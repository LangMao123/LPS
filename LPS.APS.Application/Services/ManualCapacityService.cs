using System.Data;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;

namespace LPS.APS.Application.Services;

/// <summary>
/// 人工能力槽业务服务（5号位；主档软删，窗口物理删）
/// 数据源：dbo.ManualCapacitySlot（主档）+ dbo.ManualCapacitySlotCalendar（窗口）
///
/// 【S5-01 整改（2026-10-14）】写/读路径按 Department 维度做业务范围二次校验：
/// 目标槽的 ProductionDepartmentId → ProductionDepartment.DeptCode →
/// EnsureInScopeAsync(Department)（照 RoutingOperationGovernanceService / SetupRuleService 先例）。
/// 列表查询按授权部门 Id 集合过滤（Global 不过滤；无授权 fail-closed 空结果）。
/// 越界抛 ScopeViolationException → Controller 映射 403。
/// </summary>
public interface IManualCapacityService
{
    /// <summary>新增人工槽主档（唯一键 Dept+OperationName+SlotCode；目标部门须在业务范围内）</summary>
    Task<ManualCapacitySlotDto> CreateSlotAsync(ManualCapacitySlotDto slot, int actorUserId, CancellationToken ct = default);

    /// <summary>查询人工槽主档（分页 PageResult；可按部门/工序过滤，支持含软删；结果按授权部门过滤）</summary>
    Task<PageResult<ManualCapacitySlotDto>> GetSlotsAsync(
        int? departmentId = null, string? operationName = null, bool includeInactive = false,
        int pageIndex = 1, int pageSize = 20, int actorUserId = 0, CancellationToken ct = default);

    /// <summary>软删人工槽主档（IsActive=0；目标槽所属部门须在业务范围内）</summary>
    Task<bool> SoftDeleteSlotAsync(int manualSlotId, int actorUserId, CancellationToken ct = default);

    /// <summary>批量铺人工槽窗口（覆盖式；目标槽所属部门须在业务范围内）</summary>
    Task<int> BulkCreateCalendarAsync(ManualSlotCalendarBulkRequest request, int actorUserId, CancellationToken ct = default);

    /// <summary>单条窗口追加（方向2：只插不删；同日重叠 409 拒绝；不跨天；目标槽所属部门须在业务范围内）</summary>
    Task<long> AddCalendarSingleAsync(ManualSlotCalendarSingleRequest request, int actorUserId, CancellationToken ct = default);

    /// <summary>查询某人工槽窗口（分页 PageResult，字段 page；可按时间范围过滤；目标槽所属部门须在业务范围内）</summary>
    Task<PageResult<ManualSlotCalendarDto>> GetCalendarBySlotAsync(
        int manualSlotId, DateTime? from, DateTime? to,
        int pageIndex, int pageSize, int actorUserId, CancellationToken ct = default);

    /// <summary>物理删除某人工槽窗口（DELETE；窗口所属槽的部门须在业务范围内）</summary>
    Task<bool> DeleteCalendarAsync(long id, int actorUserId, CancellationToken ct = default);
}

/// <summary>
/// 人工能力槽业务服务实现（5号位）
/// </summary>
public class ManualCapacityService : IManualCapacityService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDataScopeService _dataScopeService;

    public ManualCapacityService(DatabaseConnectionManager connectionManager, IDataScopeService dataScopeService)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
    }

    public async Task<ManualCapacitySlotDto> CreateSlotAsync(ManualCapacitySlotDto slot, int actorUserId, CancellationToken ct = default)
    {
        if (slot.ProductionDepartmentId <= 0)
            throw new ArgumentException("ProductionDepartmentId must be positive", nameof(slot));
        if (string.IsNullOrWhiteSpace(slot.OperationName))
            throw new ArgumentException("OperationName is required", nameof(slot));
        if (string.IsNullOrWhiteSpace(slot.SlotCode))
            throw new ArgumentException("SlotCode is required", nameof(slot));

        // S5-01：目标部门业务范围校验（部门不存在 → 404；越界 → ScopeViolationException/403）
        await EnsureDepartmentScopeAsync(slot.ProductionDepartmentId, actorUserId, ct);

        // 唯一键校验：同一 (Dept, OperationName, SlotCode) 不得重复
        var dup = await _connectionManager.QueryFirstOrDefaultAsync<long?>(
            @"SELECT Id FROM dbo.ManualCapacitySlot
              WHERE ProductionDepartmentId=@Dept AND OperationName=@Op AND SlotCode=@Slot
                AND IsActive=1;",
            new { Dept = slot.ProductionDepartmentId, Op = slot.OperationName, Slot = slot.SlotCode },
            CommandType.Text, DatabaseId.APS, 30);
        if (dup != null)
            throw new InvalidOperationException($"人工槽已存在：(部门 {slot.ProductionDepartmentId}, 工序 {slot.OperationName}, 槽 {slot.SlotCode})");

        var newId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"INSERT INTO dbo.ManualCapacitySlot (ProductionDepartmentId, OperationName, SlotCode, IsActive, CreatedAt, UpdatedAt)
              VALUES (@Dept, @Op, @Slot, 1, SYSDATETIME(), SYSDATETIME());
              SELECT SCOPE_IDENTITY();",
            new { Dept = slot.ProductionDepartmentId, Op = slot.OperationName, Slot = slot.SlotCode },
            CommandType.Text, DatabaseId.APS, 30);

        return new ManualCapacitySlotDto
        {
            ManualSlotId = newId,
            ProductionDepartmentId = slot.ProductionDepartmentId,
            OperationName = slot.OperationName,
            SlotCode = slot.SlotCode,
            IsActive = true
        };
    }

    public async Task<PageResult<ManualCapacitySlotDto>> GetSlotsAsync(
        int? departmentId = null, string? operationName = null, bool includeInactive = false,
        int pageIndex = 1, int pageSize = 20, int actorUserId = 0, CancellationToken ct = default)
    {
        if (pageIndex <= 0) pageIndex = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;
        var skip = (pageIndex - 1) * pageSize;

        // S5-01：读路径按授权部门过滤（Global → 不过滤；非 Global 无 Department 授权 → fail-closed 空页）
        var allowedDeptIds = await ResolveAllowedDepartmentIdsAsync(actorUserId, ct);
        if (allowedDeptIds is { Count: 0 })
            return new PageResult<ManualCapacitySlotDto>
            {
                Items = new List<ManualCapacitySlotDto>(),
                Total = 0,
                Page = pageIndex,
                PageSize = pageSize
            };

        var where = @"
WHERE (@Dept IS NULL OR ms.ProductionDepartmentId = @Dept)
  AND (@Op IS NULL OR ms.OperationName = @Op)
  AND (@IncludeInactive = 1 OR ms.IsActive = 1)
  AND (@NoScopeFilter = 1 OR ms.ProductionDepartmentId IN @AllowedDeptIds)";
        var p = new
        {
            Dept = departmentId,
            Op = operationName,
            IncludeInactive = includeInactive ? 1 : 0,
            NoScopeFilter = allowedDeptIds is null ? 1 : 0,
            AllowedDeptIds = allowedDeptIds?.ToList() ?? new List<int>(),
            Skip = skip,
            Take = pageSize
        };

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM dbo.ManualCapacitySlot ms" + where,
            p, CommandType.Text, DatabaseId.APS, 30);

        var sql = @"
SELECT ms.ManualSlotId, ms.ProductionDepartmentId, pd.DeptName AS DepartmentName,
       ms.OperationName, ms.SlotCode, ms.IsActive,
       CASE WHEN EXISTS(SELECT 1 FROM dbo.ManualCapacitySlotCalendar mc WHERE mc.ManualSlotId = ms.ManualSlotId)
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS HasCalendar
FROM dbo.ManualCapacitySlot ms
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = ms.ProductionDepartmentId"
    + where + @"
ORDER BY ms.ProductionDepartmentId, ms.OperationName, ms.SlotCode, ms.ManualSlotId
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

        var rows = await _connectionManager.QueryAsync<ManualCapacitySlotDto>(
            sql, p, CommandType.Text, DatabaseId.APS, 60);

        return new PageResult<ManualCapacitySlotDto>
        {
            Items = rows.ToList(),
            Total = total,
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<bool> SoftDeleteSlotAsync(int manualSlotId, int actorUserId, CancellationToken ct = default)
    {
        // S5-01：槽 → 所属部门 → 业务范围校验；槽不存在保持原 false 语义（无副作用）
        var deptId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT ProductionDepartmentId FROM dbo.ManualCapacitySlot WHERE ManualSlotId=@Id;",
            new { Id = manualSlotId }, CommandType.Text, DatabaseId.APS, 30);
        if (deptId is null)
            return false;

        await EnsureDepartmentScopeAsync(deptId.Value, actorUserId, ct);

        var affected = await _connectionManager.ExecuteAsync(
            "UPDATE dbo.ManualCapacitySlot SET IsActive=0, UpdatedAt=SYSDATETIME() WHERE ManualSlotId=@Id AND IsActive=1;",
            new { Id = manualSlotId }, CommandType.Text, DatabaseId.APS, 30);
        return affected > 0;
    }

    public async Task<int> BulkCreateCalendarAsync(ManualSlotCalendarBulkRequest request, int actorUserId, CancellationToken ct = default)
    {
        if (request.ManualSlotId <= 0)
            throw new ArgumentException("ManualSlotId must be positive", nameof(request));
        if (request.Days <= 0 || request.Days > 370)
            throw new ArgumentException("Days must be in 1..370", nameof(request));
        if (request.EndTime <= request.StartTime)
            throw new ArgumentException("EndTime must be after StartTime", nameof(request));

        // 校验人工槽存在且未软删（保持原语义）
        var slotDeptId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT ProductionDepartmentId FROM dbo.ManualCapacitySlot WHERE ManualSlotId=@Id AND IsActive=1;",
            new { Id = request.ManualSlotId }, CommandType.Text, DatabaseId.APS, 30);
        if (slotDeptId is null)
            throw new KeyNotFoundException($"人工槽不存在或未启用：{request.ManualSlotId}");

        // S5-01：目标槽所属部门业务范围校验（越界 403，未写库）
        await EnsureDepartmentScopeAsync(slotDeptId.Value, actorUserId, ct);

        // 覆盖式铺窗：先物理删该人工槽在 [StartDate, StartDate+Days) 区间的全部旧窗，再生成新窗，保证无重叠且重复铺窗幂等（D2 回执口径）
        var rangeEnd = request.StartDate.Date.AddDays(request.Days);
        await _connectionManager.ExecuteAsync(@"
DELETE FROM dbo.ManualCapacitySlotCalendar
WHERE ManualSlotId = @ManualSlotId
  AND StartTime >= @Start AND StartTime < @End;",
            new { ManualSlotId = request.ManualSlotId, Start = request.StartDate.Date, End = rangeEnd },
            CommandType.Text, DatabaseId.APS, 30);

        // 逐天生成新窗口
        var inserted = 0;
        for (var i = 0; i < request.Days; i++)
        {
            var day = request.StartDate.Date.AddDays(i);
            var start = day + request.StartTime;
            var end = day + request.EndTime;

            inserted += await _connectionManager.ExecuteAsync(@"
INSERT INTO dbo.ManualCapacitySlotCalendar (ManualSlotId, StartTime, EndTime, AvailableFlag, Remark, CreatedAt, UpdatedAt)
VALUES (@ManualSlotId, @StartTime, @EndTime, @AvailableFlag, @Remark, SYSDATETIME(), SYSDATETIME());",
                new
                {
                    ManualSlotId = request.ManualSlotId,
                    StartTime = start,
                    EndTime = end,
                    AvailableFlag = request.AvailableFlag ? 1 : 0,
                    Remark = request.Remark
                }, CommandType.Text, DatabaseId.APS, 30);
        }

        return inserted;
    }

    /// <summary>
    /// 单条窗口追加（2026-10-14 放开"一天多条"方向2；与设备日历同口径）：
    /// 只插不删；与同日已有窗口重叠拒绝（InvalidOperationException→409）；相切合法；不跨天。
    /// </summary>
    public async Task<long> AddCalendarSingleAsync(ManualSlotCalendarSingleRequest request, int actorUserId, CancellationToken ct = default)
    {
        if (request.ManualSlotId <= 0)
            throw new ArgumentException("ManualSlotId must be positive", nameof(request));
        if (request.EndTime <= request.StartTime)
            throw new ArgumentException("EndTime must be after StartTime", nameof(request));
        if (request.StartTime.Date != request.EndTime.Date)
            throw new ArgumentException("单条窗口不得跨天（跨天夜班请拆两条：当晚段 + 次日凌晨段）");

        // 槽存在且未软删 + S5-01 部门业务范围校验（越界 403，未写库）
        var slotDeptId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT ProductionDepartmentId FROM dbo.ManualCapacitySlot WHERE ManualSlotId=@Id AND IsActive=1;",
            new { Id = request.ManualSlotId }, CommandType.Text, DatabaseId.APS, 30);
        if (slotDeptId is null)
            throw new KeyNotFoundException($"人工槽不存在或未启用：{request.ManualSlotId}");
        await EnsureDepartmentScopeAsync(slotDeptId.Value, actorUserId, ct);

        // 同级同日重叠校验（半开区间；相切合法；不看 AvailableFlag）
        var clash = await _connectionManager.QueryFirstOrDefaultAsync<(long Id, DateTime StartTime, DateTime EndTime)?>(
            @"SELECT TOP 1 Id, StartTime, EndTime FROM dbo.ManualCapacitySlotCalendar
WHERE ManualSlotId = @ManualSlotId AND StartTime < @End AND EndTime > @Start
ORDER BY StartTime;",
            new { ManualSlotId = request.ManualSlotId, Start = request.StartTime, End = request.EndTime },
            CommandType.Text, DatabaseId.APS, 30);
        if (clash is not null)
            throw new InvalidOperationException(
                $"与已有窗口(Id={clash.Value.Id} {clash.Value.StartTime:yyyy-MM-dd HH:mm}-{clash.Value.EndTime:HH:mm}) 时间重叠；如需整段重铺请用批量端点");

        return await _connectionManager.QueryFirstOrDefaultAsync<long>(@"
INSERT INTO dbo.ManualCapacitySlotCalendar (ManualSlotId, StartTime, EndTime, AvailableFlag, Remark, CreatedAt, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@ManualSlotId, @StartTime, @EndTime, @AvailableFlag, @Remark, SYSDATETIME(), SYSDATETIME());",
            new
            {
                ManualSlotId = request.ManualSlotId,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                AvailableFlag = request.AvailableFlag ? 1 : 0,
                Remark = request.Remark
            }, CommandType.Text, DatabaseId.APS, 30);
    }

    public async Task<PageResult<ManualSlotCalendarDto>> GetCalendarBySlotAsync(
        int manualSlotId, DateTime? from, DateTime? to,
        int pageIndex, int pageSize, int actorUserId, CancellationToken ct = default)
    {
        // S5-01：单目标读——槽所属部门须在业务范围内；槽不存在保持原"空结果"行为（无泄露面）
        var deptId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT ProductionDepartmentId FROM dbo.ManualCapacitySlot WHERE ManualSlotId=@Id;",
            new { Id = manualSlotId }, CommandType.Text, DatabaseId.APS, 30);
        if (deptId is not null)
            await EnsureDepartmentScopeAsync(deptId.Value, actorUserId, ct);

        if (pageIndex <= 0) pageIndex = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;
        var skip = (pageIndex - 1) * pageSize;

        var where = @"
WHERE mc.ManualSlotId = @ManualSlotId
  AND (@From IS NULL OR mc.StartTime >= @From)
  AND (@To IS NULL OR mc.EndTime <= @To)";
        var p = new { ManualSlotId = manualSlotId, From = from, To = to, Skip = skip, Take = pageSize };

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM dbo.ManualCapacitySlotCalendar mc" + where,
            p, CommandType.Text, DatabaseId.APS, 30);

        // tie-break mc.Id：StartTime 不唯一（同日多窗口），保深页不重不漏（同 4号位 10-14 函 §三 口径）
        var sql = @"
SELECT mc.Id, mc.ManualSlotId, mc.StartTime, mc.EndTime, mc.AvailableFlag, mc.Remark
FROM dbo.ManualCapacitySlotCalendar mc"
    + where + @"
ORDER BY mc.StartTime, mc.Id
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

        var rows = await _connectionManager.QueryAsync<ManualSlotCalendarDto>(
            sql, p, CommandType.Text, DatabaseId.APS, 60);

        return new PageResult<ManualSlotCalendarDto>
        {
            Items = rows.ToList(),
            Total = total,
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<bool> DeleteCalendarAsync(long id, int actorUserId, CancellationToken ct = default)
    {
        // S5-01：窗口 → 所属槽 → 部门 → 业务范围校验；窗口不存在保持原 false 语义（无副作用）
        var deptId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            @"SELECT ms.ProductionDepartmentId
FROM dbo.ManualCapacitySlotCalendar mc
JOIN dbo.ManualCapacitySlot ms ON ms.ManualSlotId = mc.ManualSlotId
WHERE mc.Id = @Id;",
            new { Id = id }, CommandType.Text, DatabaseId.APS, 30);
        if (deptId is null)
            return false;

        await EnsureDepartmentScopeAsync(deptId.Value, actorUserId, ct);

        var affected = await _connectionManager.ExecuteAsync(
            "DELETE FROM dbo.ManualCapacitySlotCalendar WHERE Id = @Id;",
            new { Id = id }, CommandType.Text, DatabaseId.APS, 30);
        return affected > 0;
    }

    // ══════════════ S5-01 业务范围校验工具 ══════════════

    /// <summary>
    /// Department 维度校验（照 RoutingOperationGovernanceService P1-04 先例）：
    /// 部门不存在 → KeyNotFoundException(404)；越界 → ScopeViolationException(403)。
    /// </summary>
    private async Task EnsureDepartmentScopeAsync(int departmentId, int actorUserId, CancellationToken ct)
    {
        var deptCode = await _connectionManager.QueryFirstOrDefaultAsync<string?>(
            "SELECT DeptCode FROM dbo.ProductionDepartment WHERE Id = @Id",
            new { Id = departmentId }, CommandType.Text, DatabaseId.APS, 30);

        if (string.IsNullOrWhiteSpace(deptCode))
            throw new KeyNotFoundException($"生产部门（ProductionDepartment.Id={departmentId}）不存在。");

        await _dataScopeService.EnsureInScopeAsync(actorUserId, DataScopeTypes.Department, deptCode, ct);
    }

    /// <summary>
    /// 解析当前用户 Department 维度可查询的部门 Id 集合（照 RoutingOperationGovernanceService 同款模式）：
    /// Global → null（不过滤）；非 Global → 授权 DeptCode 映射为 Id 列表；无授权 → 空列表（fail-closed）。
    /// </summary>
    private async Task<IReadOnlyList<int>?> ResolveAllowedDepartmentIdsAsync(int actorUserId, CancellationToken ct)
    {
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (scope.IsGlobal)
            return null;

        var deptCodes = scope.GetValues(DataScopeTypes.Department);
        if (deptCodes is null || deptCodes.Count == 0)
            return new List<int>();

        var ids = await _connectionManager.QueryAsync<int>(
            "SELECT Id FROM dbo.ProductionDepartment WHERE DeptCode IN @Codes",
            new { Codes = deptCodes }, CommandType.Text, DatabaseId.APS, 30);

        return ids.ToList();
    }
}