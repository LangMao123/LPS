using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// Overview查询Repository实现
/// 直接读取APS_Production事实表
/// </summary>
public class OverviewQueryRepository : IOverviewQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public OverviewQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<List<OverviewActivePlanDto>> GetActivePlanAsync(
        string? domainKey = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null)
    {
        // Dapper 列表参数只能出现一次（IN 内），不得兼作标量 "@X IS NULL" 检查——
        // 否则 Dapper 会把所有 @AllowedDomains 扩成 (@p1,@p2)，产生非法 "@(@p1,@p2) IS NULL" → SqlException 4145（4号位 pmc 视角 active-plan 500 根因）。
        // 分治：null=global(无域过滤)；空集=有 scope 无 Domain 授权(fail-closed 返空)；非空=IN 过滤。
        if (allowedDomains is { Count: 0 })
            return new List<OverviewActivePlanDto>();

        // R1-a（2026-10-15）：由 TOP 1 单条 → 按 Domain 列表（冻结模型「每 Domain 至多一个生效版本」，
        // 由 PlanVersion 的 UQ 索引（谓词 Status='ACTIVE'）保证，故 ACTIVE 行数 = 生效 Domain 数，无需去重。
        // 供 4号位「各 Domain 生效版本一览」卡片使用；单条场景取 [0]。
        var baseSql = @"SELECT
    Id AS PlanVersionId,
    VersionCode,
    DomainKey,
    Status,
    PlanHorizonStart,
    PlanHorizonEnd,
    ActivatedAt,
    SourceScheduleRunId,
    ISNULL(TotalTasks, 0) AS TotalTasks,
    ISNULL(TotalOrders, 0) AS TotalOrders,
    CreatedAt
FROM PlanVersion
WHERE Status = 'ACTIVE'
    AND (@DomainKey IS NULL OR DomainKey = @DomainKey)";

        string sql;
        object parameters;
        if (allowedDomains is null)
        {
            sql = baseSql + @"
ORDER BY ActivatedAt DESC, Id DESC";
            parameters = new { DomainKey = domainKey };
        }
        else
        {
            sql = baseSql + @"
    AND DomainKey IN @AllowedDomains
ORDER BY ActivatedAt DESC, Id DESC";
            parameters = new { DomainKey = domainKey, AllowedDomains = allowedDomains };
        }

        var results = await _connectionManager.QueryAsync<OverviewActivePlanDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        return results.ToList();
    }

    // 【2026-10-16 下线】GetTaskSummaryAsync 已移除（连同 OverviewTaskSummaryDto）。
    //   原因见 OverviewController 头注：6 字段中 4 个恒 0/不可判（Task.Status 字面量超冻结值域、
    //   Task 表无实际时间列），订单维度 KPI 已由 4号位 改走 order-query/summary。

    public async Task<List<OverviewResourceBottleneckDto>> GetResourceBottleneckAsync(
        int planVersionId,
        int topN = 10,
        CancellationToken ct = default)
    {
        // 注意：Bottleneck判定应由2号位正式Query提供，5号位不做二次计算
        // 此处仅返回资源任务数统计，不判定Bottleneck
        var sql = @"
SELECT TOP (@TopN)
    t.ResourceId,
    r.ResourceCode,
    r.ResourceName,
    r.ResourceType,
    COUNT(*) AS TaskCount,
    -- Duration 库值单位=分钟（2号位 PeggingOrchestrator 落库口径 TotalMinutes）。
    -- 原 /3600.0 按秒换算 → 少算 60 倍（5号位 10-17 发现并知会）。改为 /60.0 换算为小时。
    SUM(ISNULL(t.Duration, 0)) / 60.0 AS TotalPlannedHours,
    NULL AS UtilizationRate,
    -- C2-a（4号位 10-19）：原 0 AS IsBottleneck 把「未判定」谎报成「判定为非瓶颈」。
    -- 判定归 2号位 正式 Query（见上方注释），5号位无数据源 ⇒ 与 UtilizationRate 同型改 NULL（键省略=诚实未判定）。
    CAST(NULL AS BIT) AS IsBottleneck
FROM Task t
INNER JOIN Resource r ON r.Id = t.ResourceId
WHERE t.PlanVersionId = @PlanVersionId
    AND t.ResourceId IS NOT NULL
GROUP BY t.ResourceId, r.ResourceCode, r.ResourceName, r.ResourceType
ORDER BY TotalPlannedHours DESC";

        var parameters = new { PlanVersionId = planVersionId, TopN = topN };

        var results = (await _connectionManager.QueryAsync<OverviewResourceBottleneckDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        if (results.Count == 0)
            return results;

        // R6（2026-10-16，口径 = 本级 OFF）：
        //   资源级日历 ResourceCalendarSlot.AvailableFlag = 0 的窗口 = 该设备"明确停机"时段，
        //   限定在该 PlanVersion 的计划范围内（半开区间求交：Start < HorizonEnd AND End > HorizonStart）。
        //   一次 IN 取回后内存分组，避免 N+1。命中索引 IX_ResourceCalendarSlot_Resource_Time(ResourceId, StartTime, EndTime)。
        //   ⚠️ 不含部门/工厂级继承 OFF（口径 b"三级合并"未采用）。
        var resourceIds = results.Select(r => r.ResourceId).ToList();
        var offWindows = await _connectionManager.QueryAsync<ResourceOffWindowRow>(@"
SELECT
    rc.ResourceId,
    rc.StartTime AS [From],
    rc.EndTime   AS [To],
    rc.Remark    AS Reason
FROM ResourceCalendarSlot rc
INNER JOIN PlanVersion pv ON pv.Id = @PlanVersionId
WHERE rc.AvailableFlag = 0
    AND rc.ResourceId IN @ResourceIds
    AND rc.StartTime < pv.PlanHorizonEnd
    AND rc.EndTime   > pv.PlanHorizonStart
ORDER BY rc.ResourceId, rc.StartTime",
            new { PlanVersionId = planVersionId, ResourceIds = resourceIds },
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var byResource = offWindows
            .GroupBy(w => w.ResourceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var row in results)
        {
            if (!byResource.TryGetValue(row.ResourceId, out var list))
                continue;

            row.UnavailableWindows.AddRange(list.Select(w => new OverviewUnavailableWindowDto
            {
                From = w.From,
                To = w.To,
                Reason = w.Reason
            }));
        }

        return results;
    }

    public async Task<OverviewCandidateSummaryDto> GetCandidateSummaryAsync(
        CancellationToken ct = default)
    {
        // Candidate 列表 + 反查 ScheduleRun（Base 依 P0-04 权威落盘：SourceScheduleRunId → Run.BasePlanVersionId）
        // CanActivate = §12.3 前置：Candidate.BasePlanVersionId 仍为当前 Domain ACTIVE。
        // 边界：列表项仅承载候选元信息；Base vs Candidate 差集/Diff 由 3号位 ScheduleQueryService 提供，5号位不在此重复计算。
        var candidates = (await _connectionManager.QueryAsync<CandidateBriefDto>(@"
SELECT
    pv.Id                                      AS PlanVersionId,
    pv.VersionCode                             AS VersionCode,
    pv.DomainKey                               AS DomainKey,
    pv.Status                                  AS Status,
    pv.CreatedAt                               AS CreatedAt,
    pv.CreatedByUserName                       AS CreatedByUserName,
    pv.SourceScheduleRunId                     AS SourceScheduleRunId,
    sr.RunType                                 AS RunType,
    sr.BasePlanVersionId                       AS BasePlanVersionId,
    CAST(CASE WHEN sr.BasePlanVersionId IS NOT NULL
               AND EXISTS (SELECT 1 FROM PlanVersion b
                            WHERE b.Id = sr.BasePlanVersionId
                              AND b.Status = 'ACTIVE'
                              AND b.DomainKey = pv.DomainKey)
         THEN 1 ELSE 0 END AS BIT)             AS CanActivate
FROM PlanVersion pv
LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
WHERE pv.Status = 'CANDIDATE'   -- 生命周期判定只看 Status（2号位 10-15 口径：VersionCategory 仅用于激活门禁身份，不参与状态判定）
ORDER BY pv.CreatedAt DESC", null, CommandType.Text, DatabaseId.APS, commandTimeout: 10)).ToList();

        return new OverviewCandidateSummaryDto
        {
            PendingCount = candidates.Count,
            Candidates = candidates
        };
    }

    /// <summary>R6 内部行：资源级 OFF 窗口（Dapper 投影用，不对外）</summary>
    private sealed class ResourceOffWindowRow
    {
        public int ResourceId { get; init; }
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public string? Reason { get; init; }
    }
}
