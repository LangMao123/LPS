using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

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

    public async Task<OverviewActivePlanDto?> GetActivePlanAsync(
        string? domainKey = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null)
    {
        var sql = @"
SELECT TOP 1
    Id AS PlanVersionId,
    VersionCode,
    DomainKey,
    PlanHorizonStart,
    PlanHorizonEnd,
    ActivatedAt,
    SourceScheduleRunId,
    TotalTasks,
    TotalOrders,
    CreatedAt
FROM PlanVersion
WHERE Status = 'ACTIVE'
    AND (@DomainKey IS NULL OR DomainKey = @DomainKey)
    AND (@AllowedDomains IS NULL OR DomainKey IN @AllowedDomains)
ORDER BY ActivatedAt DESC";

        var parameters = new { DomainKey = domainKey, AllowedDomains = allowedDomains };

        var results = await _connectionManager.QueryAsync<OverviewActivePlanDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        return results.FirstOrDefault();
    }

    public async Task<OverviewTaskSummaryDto> GetTaskSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    @PlanVersionId AS PlanVersionId,
    COUNT(*) AS TotalTasks,
    SUM(CASE WHEN Status = 'COMPLETED' AND PlannedEndTime <= GETDATE() THEN 1 ELSE 0 END) AS OnTimeCount,
    SUM(CASE WHEN Status = 'COMPLETED' AND PlannedEndTime > GETDATE() THEN 1 ELSE 0 END) AS DelayedCount,
    SUM(CASE WHEN Status = 'IN_PROGRESS' AND IsCriticalPath = 1 THEN 1 ELSE 0 END) AS RiskCount,
    SUM(CASE WHEN Status = 'PENDING' OR Status = 'QUEUED' THEN 1 ELSE 0 END) AS UnscheduledCount,
    SUM(CASE WHEN Status = 'ESTIMATED' THEN 1 ELSE 0 END) AS EstimatedOnlyCount,
    SUM(CASE WHEN Status NOT IN ('COMPLETED', 'IN_PROGRESS', 'PENDING', 'QUEUED', 'ESTIMATED') THEN 1 ELSE 0 END) AS OtherCount
FROM Task
WHERE PlanVersionId = @PlanVersionId";

        var parameters = new { PlanVersionId = planVersionId };

        var results = await _connectionManager.QueryAsync<OverviewTaskSummaryDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.FirstOrDefault() ?? new OverviewTaskSummaryDto { PlanVersionId = planVersionId };
    }

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
    SUM(ISNULL(t.Duration, 0)) / 3600.0 AS TotalPlannedHours,
    NULL AS UtilizationRate,
    0 AS IsBottleneck
FROM Task t
INNER JOIN Resource r ON r.Id = t.ResourceId
WHERE t.PlanVersionId = @PlanVersionId
    AND t.ResourceId IS NOT NULL
GROUP BY t.ResourceId, r.ResourceCode, r.ResourceName, r.ResourceType
ORDER BY TotalPlannedHours DESC";

        var parameters = new { PlanVersionId = planVersionId, TopN = topN };

        var results = await _connectionManager.QueryAsync<OverviewResourceBottleneckDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    public async Task<OverviewCandidateSummaryDto> GetCandidateSummaryAsync(
        CancellationToken ct = default)
    {
        // 1. Candidate 列表 + 反查 ScheduleRun（Base 依 P0-04 权威落盘：SourceScheduleRunId → Run.BasePlanVersionId）
        //    CanActivate = §12.3 前置：Candidate.BasePlanVersionId 仍为当前 Domain ACTIVE。
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
WHERE pv.VersionCategory = 'CANDIDATE'
ORDER BY pv.CreatedAt DESC", null, CommandType.Text, DatabaseId.APS, commandTimeout: 10)).ToList();

        var list = new List<CandidateBriefDto>(candidates.Count);
        foreach (var cand in candidates)
        {
            var cmp = cand.BasePlanVersionId is int baseId
                ? await ComputeCandidateComparisonAsync(cand.PlanVersionId, baseId, ct)
                : default(CandidateComparisonValues);

            list.Add(new CandidateBriefDto
            {
                PlanVersionId = cand.PlanVersionId,
                VersionCode = cand.VersionCode,
                DomainKey = cand.DomainKey,
                Status = cand.Status,
                CreatedAt = cand.CreatedAt,
                CreatedByUserName = cand.CreatedByUserName,
                SourceScheduleRunId = cand.SourceScheduleRunId,
                RunType = cand.RunType,
                BasePlanVersionId = cand.BasePlanVersionId,
                CanActivate = cand.CanActivate,
                BaseAvgCompletionTime = cmp.BaseAvgCompletionTime,
                CandidateAvgCompletionTime = cmp.CandidateAvgCompletionTime,
                AvgDeltaHours = cmp.AvgDeltaHours,
                TaskAddedCount = cmp.TaskAddedCount,
                TaskRemovedCount = cmp.TaskRemovedCount,
                TaskTimeShiftedCount = cmp.TaskTimeShiftedCount,
                TaskResourceChangedCount = cmp.TaskResourceChangedCount,
                ImpactedOrderCount = cmp.ImpactedOrderCount,
                NewDelayCount = cmp.NewDelayCount,
                // 需落盘真值：0号位 裁决前占位 null（归 2号位/1号位）
                EstimatedOnlyCount = null,
                CrossDomainImpacted = null,
                CrossDomainBlockedCount = null
            });
        }

        return new OverviewCandidateSummaryDto
        {
            PendingCount = list.Count,
            Candidates = list
        };
    }

    /// <summary>候选 vs 基础 差集比较聚合（从 ScheduleQueryService 差集逻辑移植，5号位只读 [Task]/[Order] 落库事实，不重算业务）</summary>
    private async Task<CandidateComparisonValues> ComputeCandidateComparisonAsync(
        int candidateId, int baseId, CancellationToken ct)
    {
        var p = new { CandidateId = candidateId, BaseId = baseId };
        const string key = @"
        AND t_b.OrderId         = t_c.OrderId
        AND t_b.MaterialId      = t_c.MaterialId
        AND t_b.OperationSeq    = t_c.OperationSeq
        AND t_b.OperationCode   = t_c.OperationCode
        AND t_b.RouteCode       = t_c.RouteCode
        AND t_b.PathId          = t_c.PathId";

        var added = await CountScalarAsync($@"
            SELECT COUNT(*) FROM [Task] t_c
            WHERE t_c.PlanVersionId = @CandidateId
              AND NOT EXISTS (SELECT 1 FROM [Task] t_b WHERE t_b.PlanVersionId = @BaseId{key})", p);

        var removed = await CountScalarAsync($@"
            SELECT COUNT(*) FROM [Task] t_b
            WHERE t_b.PlanVersionId = @BaseId
              AND NOT EXISTS (SELECT 1 FROM [Task] t_c WHERE t_c.PlanVersionId = @CandidateId{key})", p);

        var timeShifted = await CountScalarAsync($@"
            SELECT COUNT(*) FROM [Task] t_c
            WHERE t_c.PlanVersionId = @CandidateId
              AND EXISTS (SELECT 1 FROM [Task] t_b
                WHERE t_b.PlanVersionId = @BaseId{key}
                  AND ((t_c.PlannedStartTime IS NULL) <> (t_b.PlannedStartTime IS NULL)
                    OR t_c.PlannedStartTime <> t_b.PlannedStartTime
                    OR (t_c.PlannedEndTime IS NULL) <> (t_b.PlannedEndTime IS NULL)
                    OR t_c.PlannedEndTime <> t_b.PlannedEndTime))", p);

        var resourceChanged = await CountScalarAsync($@"
            SELECT COUNT(*) FROM [Task] t_c
            WHERE t_c.PlanVersionId = @CandidateId
              AND EXISTS (SELECT 1 FROM [Task] t_b
                WHERE t_b.PlanVersionId = @BaseId{key}
                  AND ((t_c.ResourceId IS NULL) <> (t_b.ResourceId IS NULL)
                    OR t_c.ResourceId <> t_b.ResourceId))", p);

        var impactedOrders = await CountScalarAsync($@"
            SELECT COUNT(DISTINCT OrderId) FROM (
              SELECT t_c.OrderId FROM [Task] t_c
              WHERE t_c.PlanVersionId = @CandidateId
                AND (NOT EXISTS (SELECT 1 FROM [Task] t_b WHERE t_b.PlanVersionId = @BaseId{key})
                  OR EXISTS (SELECT 1 FROM [Task] t_b
                    WHERE t_b.PlanVersionId = @BaseId{key}
                      AND ((t_c.PlannedStartTime IS NULL) <> (t_b.PlannedStartTime IS NULL)
                        OR t_c.PlannedStartTime <> t_b.PlannedStartTime
                        OR (t_c.PlannedEndTime IS NULL) <> (t_b.PlannedEndTime IS NULL)
                        OR t_c.PlannedEndTime <> t_b.PlannedEndTime
                        OR (t_c.ResourceId IS NULL) <> (t_b.ResourceId IS NULL)
                        OR t_c.ResourceId <> t_b.ResourceId)))
              UNION
              SELECT t_b.OrderId FROM [Task] t_b
              WHERE t_b.PlanVersionId = @BaseId
                AND NOT EXISTS (SELECT 1 FROM [Task] t_c WHERE t_c.PlanVersionId = @CandidateId{key})
            ) changed", p);

        var newDelays = await CountScalarAsync($@"
            SELECT COUNT(*) FROM [Task] t_c
            INNER JOIN [Order] o ON o.Id = t_c.OrderId
            WHERE t_c.PlanVersionId = @CandidateId
              AND t_c.PlannedEndTime IS NOT NULL
              AND o.CustomerDueDate IS NOT NULL
              AND t_c.PlannedEndTime > o.CustomerDueDate
              AND NOT EXISTS (SELECT 1
                FROM [Task] t_b INNER JOIN [Order] ob ON ob.Id = t_b.OrderId
                WHERE t_b.PlanVersionId = @BaseId{key}
                  AND t_b.PlannedEndTime IS NOT NULL
                  AND ob.CustomerDueDate IS NOT NULL
                  AND t_b.PlannedEndTime > ob.CustomerDueDate)", p);

        var baseAvg = await QueryAvgCompletionAsync(baseId);
        var candAvg = await QueryAvgCompletionAsync(candidateId);

        return new CandidateComparisonValues
        {
            TaskAddedCount = added,
            TaskRemovedCount = removed,
            TaskTimeShiftedCount = timeShifted,
            TaskResourceChangedCount = resourceChanged,
            ImpactedOrderCount = impactedOrders,
            NewDelayCount = newDelays,
            BaseAvgCompletionTime = baseAvg,
            CandidateAvgCompletionTime = candAvg,
            AvgDeltaHours = candAvg.HasValue && baseAvg.HasValue
                ? (decimal?)(candAvg.Value - baseAvg.Value).TotalHours
                : null
        };
    }

    /// <summary>版本平均完成时间 = AVG(per-Order MAX(PlannedEndTime))；无已排任务返回 null</summary>
    private async Task<DateTime?> QueryAvgCompletionAsync(int planVersionId)
        => await _connectionManager.QueryFirstOrDefaultAsync<DateTime?>(
            @"SELECT DATEADD(SECOND, AVG(DATEDIFF(SECOND, '19000101', mx)), '19000101')
              FROM (SELECT MAX(t.PlannedEndTime) mx
                    FROM [Task] t
                    WHERE t.PlanVersionId = @Id AND t.PlannedEndTime IS NOT NULL
                    GROUP BY t.OrderId) g",
            new { Id = planVersionId },
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

    private async Task<int> CountScalarAsync(string sql, object parameters)
        => await _connectionManager.QueryFirstOrDefaultAsync<int?>(sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30) ?? 0;

    /// <summary>候选差集比较聚合值（Repository 内部中转）</summary>
    private readonly struct CandidateComparisonValues
    {
        public DateTime? BaseAvgCompletionTime { get; init; }
        public DateTime? CandidateAvgCompletionTime { get; init; }
        public decimal? AvgDeltaHours { get; init; }
        public int TaskAddedCount { get; init; }
        public int TaskRemovedCount { get; init; }
        public int TaskTimeShiftedCount { get; init; }
        public int TaskResourceChangedCount { get; init; }
        public int ImpactedOrderCount { get; init; }
        public int NewDelayCount { get; init; }
    }
}
