using System.Data;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
// 别名引入（整命名空间引入会与 System.Threading.Tasks.Task 冲突：APS 也有 Task 实体）
using ProductionWorkCalendarSlot = LPS.APS.Core.Entities.APS.ProductionWorkCalendarSlot;

namespace LPS.APS.Application.Services;

/// <summary>
/// 资源日历业务服务（5号位，设备日历维护）
/// 数据源：dbo.ResourceCalendarSlot
///
/// 【S5-01 整改（2026-10-14）】读/写路径均做业务范围二次校验：
/// 目标 Resource 关联 Factory.Code + ProductionDepartment.DeptCode，
/// 非 Global 用户须 Factory ∈ 授权 且 Department ∈ 授权（不同维度 AND，
/// 照 StageLeadTimeParamGovernanceService P0-03 先例）；越界抛 ScopeViolationException
/// → Controller 映射 403。fail-closed：userId 无效(0) → Empty 范围 → 拒绝全部。
/// </summary>
public interface IResourceCalendarService
{
    /// <summary>批量铺窗（覆盖式：先删 [StartDate, StartDate+Days) 旧窗再生成；Days=1 覆盖逐条）</summary>
    Task<int> BulkCreateAsync(ResourceCalendarBulkRequest request, int actorUserId, CancellationToken ct = default);

    /// <summary>单条窗口追加（方向2：只插不删；同日重叠 409 拒绝；不跨天；目标设备须在业务范围内）</summary>
    Task<long> AddSingleAsync(ResourceCalendarSingleRequest request, int actorUserId, CancellationToken ct = default);

    /// <summary>查询某设备窗口（分页 PageResult，字段 page；可按时间范围过滤；目标设备须在操作者业务范围内，否则 403）</summary>
    Task<PageResult<ResourceCalendarEntryDto>> GetByResourceAsync(
        int resourceId, DateTime? from, DateTime? to,
        int pageIndex, int pageSize, int actorUserId, CancellationToken ct = default);

    /// <summary>物理删除某窗口（DELETE；窗口所属设备须在操作者业务范围内，否则 403）</summary>
    Task<bool> DeleteAsync(long id, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 三级合并解析预览（只读，接线 ResourceCalendarResolver，不写库）：
    /// 返回每台设备三级继承后的最终可排窗口与来源层级（4号位 10-14 函 R1）。
    /// 业务范围 fail-closed：越界设备从结果中剔除（不泄露存在性）。
    /// </summary>
    Task<List<ResourceCalendarResolvePreviewDto>> ResolvePreviewAsync(
        IReadOnlyList<int> resourceIds, DateTime startDate, DateTime endDate,
        int actorUserId, CancellationToken ct = default);
}

/// <summary>
/// 资源日历业务服务实现（5号位；只读+写窗，不重算排程）
/// </summary>
public class ResourceCalendarService : IResourceCalendarService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDataScopeService _dataScopeService;

    public ResourceCalendarService(DatabaseConnectionManager connectionManager, IDataScopeService dataScopeService)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
    }

    public async Task<int> BulkCreateAsync(ResourceCalendarBulkRequest request, int actorUserId, CancellationToken ct = default)
    {
        if (request.ResourceId <= 0)
            throw new ArgumentException("ResourceId must be positive", nameof(request));
        if (request.Days <= 0 || request.Days > 370)
            throw new ArgumentException("Days must be in 1..370", nameof(request));
        if (request.EndTime <= request.StartTime)
            throw new ArgumentException("EndTime must be after StartTime", nameof(request));

        // 校验设备存在（保持原语义）+ 解析目标 Factory/Department 码
        var target = await _connectionManager.QueryFirstOrDefaultAsync<(string? FactoryCode, string? DeptCode)?>(
            @"SELECT f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.Resource r
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
WHERE r.Id = @Id AND r.IsActive = 1",
            new { Id = request.ResourceId }, CommandType.Text, DatabaseId.APS, 30);
        if (target is null)
            throw new KeyNotFoundException($"设备不存在或未启用：{request.ResourceId}");

        // S5-01：写路径业务范围二次校验（越界 403，未写库）
        await EnsureTargetScopeAsync(target.Value.FactoryCode, target.Value.DeptCode, actorUserId, ct);

        // 覆盖式铺窗：先物理删该资源在 [StartDate, StartDate+Days) 区间的全部旧窗，再生成新窗，保证无重叠且重复铺窗幂等（D2 回执口径）
        // 【2026-10-17 谓词订正｜2号位 边界三】原谓词只删「StartTime 落在区间内」的行，
        //   起点在区间之前的跨区间窗（如 30 天长窗）会被漏掉 ⇒ 新窗插入后同层重叠，与上句「保证无重叠」不符。
        //   改为区间相交判定：StartTime < @End AND EndTime > @Start。
        //   正常路径行为不变（API 造不出跨日长窗：AddSingleAsync 禁跨天、本方法按天生成每窗 ≤24h），
        //   本订正用于兜住直连数据库写入产生的跨区间行。
        var rangeEnd = request.StartDate.Date.AddDays(request.Days);
        await _connectionManager.ExecuteAsync(@"
DELETE FROM dbo.ResourceCalendarSlot
WHERE ResourceId = @ResourceId
  AND StartTime < @End AND EndTime > @Start;",
            new { ResourceId = request.ResourceId, Start = request.StartDate.Date, End = rangeEnd },
            CommandType.Text, DatabaseId.APS, 30);

        // 逐天生成新窗口
        var inserted = 0;
        for (var i = 0; i < request.Days; i++)
        {
            var day = request.StartDate.Date.AddDays(i);
            var start = day + request.StartTime;
            var end = day + request.EndTime;

            inserted += await _connectionManager.ExecuteAsync(@"
INSERT INTO dbo.ResourceCalendarSlot (ResourceId, StartTime, EndTime, AvailableFlag, Remark, CreatedAt, UpdatedAt)
VALUES (@ResourceId, @StartTime, @EndTime, @AvailableFlag, @Remark, SYSDATETIME(), SYSDATETIME());",
                new
                {
                    ResourceId = request.ResourceId,
                    StartTime = start,
                    EndTime = end,
                    AvailableFlag = request.AvailableFlag ? 1 : 0,
                    Remark = request.Remark
                }, CommandType.Text, DatabaseId.APS, 30);
        }

        return inserted;
    }

    /// <summary>
    /// 单条窗口追加（2026-10-14 放开"一天多条"方向2）：
    /// 只插不删；与同日已有窗口重叠拒绝（InvalidOperationException→409）；相切合法；不跨天。
    /// 与批量端点分工：批量=整段重铺（覆盖式，语义不变），单条=按条增补。
    /// </summary>
    public async Task<long> AddSingleAsync(ResourceCalendarSingleRequest request, int actorUserId, CancellationToken ct = default)
    {
        if (request.ResourceId <= 0)
            throw new ArgumentException("ResourceId must be positive", nameof(request));
        if (request.EndTime <= request.StartTime)
            throw new ArgumentException("EndTime must be after StartTime", nameof(request));
        if (request.StartTime.Date != request.EndTime.Date)
            throw new ArgumentException("单条窗口不得跨天（跨天夜班请拆两条：当晚段 + 次日凌晨段）");

        // 设备存在 + S5-01 业务范围二次校验（越界 403，未写库）
        var target = await _connectionManager.QueryFirstOrDefaultAsync<(string? FactoryCode, string? DeptCode)?>(
            @"SELECT f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.Resource r
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
WHERE r.Id = @Id AND r.IsActive = 1",
            new { Id = request.ResourceId }, CommandType.Text, DatabaseId.APS, 30);
        if (target is null)
            throw new KeyNotFoundException($"设备不存在或未启用：{request.ResourceId}");
        await EnsureTargetScopeAsync(target.Value.FactoryCode, target.Value.DeptCode, actorUserId, ct);

        // 同级同日重叠校验（半开区间 [start,end)：相切合法；不看 AvailableFlag——WORK/OFF 混叠同样会造成解析歧义）
        var clash = await _connectionManager.QueryFirstOrDefaultAsync<(long Id, DateTime StartTime, DateTime EndTime)?>(
            @"SELECT TOP 1 Id, StartTime, EndTime FROM dbo.ResourceCalendarSlot
WHERE ResourceId = @ResourceId AND StartTime < @End AND EndTime > @Start
ORDER BY StartTime;",
            new { ResourceId = request.ResourceId, Start = request.StartTime, End = request.EndTime },
            CommandType.Text, DatabaseId.APS, 30);
        if (clash is not null)
            throw new InvalidOperationException(
                $"与已有窗口(Id={clash.Value.Id} {clash.Value.StartTime:yyyy-MM-dd HH:mm}-{clash.Value.EndTime:HH:mm}) 时间重叠；如需整段重铺请用批量端点");

        return await _connectionManager.QueryFirstOrDefaultAsync<long>(@"
INSERT INTO dbo.ResourceCalendarSlot (ResourceId, StartTime, EndTime, AvailableFlag, Remark, CreatedAt, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@ResourceId, @StartTime, @EndTime, @AvailableFlag, @Remark, SYSDATETIME(), SYSDATETIME());",
            new
            {
                ResourceId = request.ResourceId,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                AvailableFlag = request.AvailableFlag ? 1 : 0,
                Remark = request.Remark
            }, CommandType.Text, DatabaseId.APS, 30);
    }

    public async Task<PageResult<ResourceCalendarEntryDto>> GetByResourceAsync(
        int resourceId, DateTime? from, DateTime? to,
        int pageIndex, int pageSize, int actorUserId, CancellationToken ct = default)
    {
        // S5-01：单目标读——目标设备须在业务范围内；设备行不存在时保持原"空结果"行为（无泄露面）
        var target = await _connectionManager.QueryFirstOrDefaultAsync<(string? FactoryCode, string? DeptCode)?>(
            @"SELECT f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.Resource r
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
WHERE r.Id = @Id",
            new { Id = resourceId }, CommandType.Text, DatabaseId.APS, 30);
        if (target is not null)
            await EnsureTargetScopeAsync(target.Value.FactoryCode, target.Value.DeptCode, actorUserId, ct);

        if (pageIndex <= 0) pageIndex = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;
        var skip = (pageIndex - 1) * pageSize;

        var where = @"
WHERE rc.ResourceId = @ResourceId
  AND (@From IS NULL OR rc.StartTime >= @From)
  AND (@To IS NULL OR rc.EndTime <= @To)";
        var p = new { ResourceId = resourceId, From = from, To = to, Skip = skip, Take = pageSize };

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM dbo.ResourceCalendarSlot rc" + where,
            p, CommandType.Text, DatabaseId.APS, 30);

        // tie-break rc.Id：StartTime 不唯一（同日多窗口三班制），缺 tie-break 深页会漏行/重复（4号位 10-14 函 §三）
        var sql = @"
SELECT rc.Id, rc.ResourceId, r.ResourceCode, r.ResourceName,
       rc.StartTime, rc.EndTime, rc.AvailableFlag, rc.Remark
FROM dbo.ResourceCalendarSlot rc
JOIN dbo.Resource r ON r.Id = rc.ResourceId"
    + where + @"
ORDER BY rc.StartTime, rc.Id
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

        var rows = await _connectionManager.QueryAsync<ResourceCalendarEntryDto>(
            sql, p, CommandType.Text, DatabaseId.APS, 60);

        return new PageResult<ResourceCalendarEntryDto>
        {
            Items = rows.ToList(),
            Total = total,
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<bool> DeleteAsync(long id, int actorUserId, CancellationToken ct = default)
    {
        // S5-01：窗口 → 所属设备 → Factory/Department 码；窗口不存在保持原 false 语义（无副作用）
        var target = await _connectionManager.QueryFirstOrDefaultAsync<(string? FactoryCode, string? DeptCode)?>(
            @"SELECT f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.ResourceCalendarSlot rc
JOIN dbo.Resource r ON r.Id = rc.ResourceId
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
WHERE rc.Id = @Id",
            new { Id = id }, CommandType.Text, DatabaseId.APS, 30);
        if (target is null)
            return false;

        await EnsureTargetScopeAsync(target.Value.FactoryCode, target.Value.DeptCode, actorUserId, ct);

        var affected = await _connectionManager.ExecuteAsync(
            "DELETE FROM dbo.ResourceCalendarSlot WHERE Id = @Id;",
            new { Id = id }, CommandType.Text, DatabaseId.APS, 30);
        return affected > 0;
    }

    /// <summary>
    /// 三级合并解析预览（4号位 10-14 函 R1；只读、不写库、不改任何日历数据）：
    /// 装载 PWC 上级事实 + 本级窗口 → 调 ResourceCalendarResolver.Resolve → 映射展示态。
    /// </summary>
    public async Task<List<ResourceCalendarResolvePreviewDto>> ResolvePreviewAsync(
        IReadOnlyList<int> resourceIds, DateTime startDate, DateTime endDate,
        int actorUserId, CancellationToken ct = default)
    {
        if (resourceIds is null || resourceIds.Count == 0)
            throw new ArgumentException("resourceIds 不能为空");
        if (resourceIds.Count > 200)
            throw new ArgumentException("resourceIds 单次最多 200 个");
        if (endDate.Date < startDate.Date)
            throw new ArgumentException("endDate 不得早于 startDate");

        // 设备归属（含 Factory/Dept 码，供 S5-01 范围过滤）
        var resources = (await _connectionManager.QueryAsync<(int Id, int? FactoryId, int? DepartmentId, string? FactoryCode, string? DeptCode)>(
            @"SELECT r.Id, r.FactoryId, r.ProductionDepartmentId, f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.Resource r
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
WHERE r.Id IN @Ids",
            new { Ids = resourceIds.ToList() }, CommandType.Text, DatabaseId.APS, 30)).ToList();

        // S5-01 fail-closed：越界设备直接剔除（多目标读不泄露存在性）
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        var scoped = resources.Where(r =>
            scope.IsGlobal
            || (!string.IsNullOrWhiteSpace(r.FactoryCode) && scope.Allows(DataScopeTypes.Factory, r.FactoryCode)
                && (string.IsNullOrWhiteSpace(r.DeptCode) || scope.Allows(DataScopeTypes.Department, r.DeptCode))))
            .ToList();
        if (scoped.Count == 0)
            return new List<ResourceCalendarResolvePreviewDto>();

        var ids = scoped.Select(r => r.Id).ToList();
        var horizonStart = startDate.Date;
        var horizonEnd = endDate.Date;                       // Resolver 语义：horizonEnd **含**（EnumerateDays 按日号闭区间）
        var fetchEndExclusive = endDate.Date.AddDays(1);     // SQL 取窗用开区间（覆盖末日整天）

        // 本级窗口（含 OFF 行）
        var ownRows = (await _connectionManager.QueryAsync<(int ResourceId, DateTime StartTime, DateTime EndTime, bool AvailableFlag)>(
            @"SELECT ResourceId, StartTime, EndTime, AvailableFlag FROM dbo.ResourceCalendarSlot
WHERE ResourceId IN @Ids AND EndTime > @Start AND StartTime < @End",
            new { Ids = ids, Start = horizonStart, End = fetchEndExclusive },
            CommandType.Text, DatabaseId.APS, 60)).ToList();

        // 上级事实（PWC 全量有效行，按计划期裁剪）
        var upperRows = (await _connectionManager.QueryAsync<ProductionWorkCalendarSlot>(
            @"SELECT Id, ScopeType, FactoryId, ProductionDepartmentId, WorkDate, WeekdayIso, StartAt, EndAt, WorkState,
       AllowLowerOverride, IsAuthorizedOverride, SourceSystem, SourceReference, EffectiveFrom, EffectiveTo, IsActive
FROM dbo.ProductionWorkCalendarSlot
WHERE IsActive = 1 AND (EffectiveTo IS NULL OR EffectiveTo >= @Start) AND EffectiveFrom <= @End",
            new { Start = horizonStart, End = horizonEnd },
            CommandType.Text, DatabaseId.APS, 60)).ToList();

        var inputs = scoped.Select(r => new ResourceCalendarResolver.ResourceCalendarInput
        {
            Key = r.Id.ToString(),
            FactoryId = r.FactoryId,
            DepartmentId = r.DepartmentId,
            OwnWindows = ownRows.Where(w => w.ResourceId == r.Id)
                .Select(w => new ResourceCalendarResolver.LevelWindow
                {
                    Start = w.StartTime,
                    End = w.EndTime,
                    IsAvailable = w.AvailableFlag
                }).ToList()
        }).ToList();

        var resolved = ResourceCalendarResolver.Resolve(upperRows, inputs, horizonStart, horizonEnd);

        return scoped.Select(r =>
        {
            var key = r.Id.ToString();
            var has = resolved.Calendars.TryGetValue(key, out var wins);
            var hasOwnOff = ownRows.Any(w => w.ResourceId == r.Id && !w.AvailableFlag);
            return new ResourceCalendarResolvePreviewDto
            {
                ResourceId = r.Id,
                HasAnyWindow = has,
                // 三态展示：有窗口=WORK；无窗口但本级有 OFF=OFF（本级明确停机）；其余=MISSING（本级未登记/上级也没开）
                Status = has ? "WORK" : (hasOwnOff ? "OFF" : "MISSING"),
                Windows = (wins ?? new List<ResourceCalendarResolver.ResolvedWindow>())
                    .Select(w => new ResolvedWindowDto { Start = w.Start, End = w.End, Source = w.Source.ToString() })
                    .ToList()
            };
        }).ToList();
    }

    /// <summary>
    /// S5-01 业务范围二次校验（照 StageLeadTimeParamGovernanceService P0-03 先例）：
    /// Global 放行；非 Global：FactoryCode 必在 Factory 授权内（缺失视为越界，fail-closed），
    /// DeptCode 非空时必在 Department 授权内（不同维度 AND）。越界抛 ScopeViolationException。
    /// </summary>
    private async Task EnsureTargetScopeAsync(string? factoryCode, string? deptCode, int actorUserId, CancellationToken ct)
    {
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (scope.IsGlobal)
            return;

        if (string.IsNullOrWhiteSpace(factoryCode) || !scope.Allows(DataScopeTypes.Factory, factoryCode))
            throw new ScopeViolationException(DataScopeTypes.Factory, factoryCode ?? "(unknown)");

        if (!string.IsNullOrWhiteSpace(deptCode) && !scope.Allows(DataScopeTypes.Department, deptCode))
            throw new ScopeViolationException(DataScopeTypes.Department, deptCode);
    }
}