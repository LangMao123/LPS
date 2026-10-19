using System.Data;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;

namespace LPS.APS.Application.Services;

/// <summary>
/// 工厂/部门上级工作日历维护服务（5号位=业务 Owner；表模型/三级解析=2号位）
/// 条款来源：Resource Calendar 专项 v1.4（G1 候选）+ DDL v5.1.8.5 ProductionWorkCalendarSlot。
/// 治理：G4 公告前本服务可用于联调与备料，真实业务班次数据写入与解析消费等公告生效。
/// DDL 尾注四项发布强校验在此落地：①同级时间重叠/星期日期冲突 ②部门归属 Factory 匹配
/// ③操作权限（Controller 策略 + S5-01 业务范围二次校验） ④授权加班证据完整性（Aao=1 须 WORK+SourceReference）。
///
/// 【S5-01 整改（2026-10-14）】写路径经 ValidateScopeAsync/EnsureRowScopeAsync 做业务范围二次校验：
/// 非 Global 用户目标 Factory.Code 必在 Factory 授权内；DEPARTMENT 行另须 DeptCode 在 Department 授权内
/// （不同维度 AND，照 StageLeadTimeParamGovernanceService P0-03 先例）；越界抛 ScopeViolationException
/// → Controller 映射 403。读路径（Query）按授权 Factory/Department 过滤结果（fail-closed 空页）。
/// </summary>
public interface IProductionWorkCalendarService
{
    Task<PageResult<ProductionWorkCalendarDto>> QueryAsync(
        string? scopeType = null, int? factoryId = null, int? departmentId = null,
        bool includeInactive = false, int pageIndex = 1, int pageSize = 20,
        string? sortBy = null, string? sortDir = null,
        int actorUserId = 0,
        CancellationToken ct = default);

    Task<int> UpsertWeeklyAsync(WeeklyCalendarUpsertRequest request, int actorUserId, string @operator, CancellationToken ct = default);

    Task<long> CreateExceptionAsync(WorkCalendarExceptionRequest request, int actorUserId, string @operator, CancellationToken ct = default);

    Task<int> CloseOpenAsync(CalendarCloseOpenRequest request, int actorUserId, string @operator, CancellationToken ct = default);

    /// <summary>单行闭区间（撤销已生效例外行的正规姿势：历史保留、审计完整）</summary>
    Task CloseRowAsync(long id, DateTime effectiveTo, int actorUserId, string @operator, CancellationToken ct = default);

    /// <summary>物理删除——仅限未生效行（EffectiveFrom > 今天）；已生效行拒绝，提示走 CloseRow</summary>
    Task DeleteRowAsync(long id, int actorUserId, CancellationToken ct = default);
}

public class ProductionWorkCalendarService : IProductionWorkCalendarService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDataScopeService _dataScopeService;

    public ProductionWorkCalendarService(DatabaseConnectionManager connectionManager, IDataScopeService dataScopeService)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
    }

    // ══════════════ 查询（分页 PageResult，同既定契约；深页 skip 已按 10-10 教训直落 SQL） ══════════════
    public async Task<PageResult<ProductionWorkCalendarDto>> QueryAsync(
        string? scopeType = null, int? factoryId = null, int? departmentId = null,
        bool includeInactive = false, int pageIndex = 1, int pageSize = 20,
        string? sortBy = null, string? sortDir = null,
        int actorUserId = 0,
        CancellationToken ct = default)
    {
        if (pageIndex <= 0) pageIndex = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;
        var skip = (pageIndex - 1) * pageSize;

        // S5-01：读路径业务范围过滤——Global 不过滤；非 Global：FACTORY 行须厂授权，
        // DEPARTMENT 行须厂授权+部门授权（无 Department 授权时仅 FACTORY 行可见）；无 Factory 授权 fail-closed 空页。
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        var scopeFilter = string.Empty;
        var scopeFacs = new List<string>();
        var scopeDepts = new List<string>();
        if (!scope.IsGlobal)
        {
            var facs = scope.GetValues(DataScopeTypes.Factory);
            if (facs is null || facs.Count == 0)
                return new PageResult<ProductionWorkCalendarDto>
                {
                    Items = new List<ProductionWorkCalendarDto>(),
                    Total = 0,
                    Page = pageIndex,
                    PageSize = pageSize
                };
            scopeFacs = facs.ToList();
            var depts = scope.GetValues(DataScopeTypes.Department);
            if (depts is not null && depts.Count > 0)
            {
                scopeDepts = depts.ToList();
                scopeFilter = "\n  AND f.Code IN @ScopeFacs\n  AND (p.ProductionDepartmentId IS NULL OR pd.DeptCode IN @ScopeDepts)";
            }
            else
            {
                scopeFilter = "\n  AND f.Code IN @ScopeFacs\n  AND p.ProductionDepartmentId IS NULL";
            }
        }

        // 排序（4号位 10-13 PWC 函）：不传 sortBy=原固定 5 字段 ASC（向后兼容零回归）；
        // 白名单映射防注入；EffectiveTo 可空按 NULLS LAST；一律 p.Id DESC 收尾保证翻页确定性。
        string orderBy;
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            orderBy = "\nORDER BY p.ScopeType, p.FactoryId, p.ProductionDepartmentId, p.WorkDate, p.WeekdayIso";
        }
        else
        {
            if (!SortWhitelist.TryGetValue(sortBy.Trim(), out var col))
                throw new ArgumentException($"sortBy 非法：{sortBy}（可选 Id/CreatedAt/EffectiveFrom/EffectiveTo/UpdatedAt）");
            var dir = string.IsNullOrWhiteSpace(sortDir)
                ? col.DefaultDir
                : sortDir.Trim().ToUpperInvariant() switch
                {
                    "ASC" => "ASC",
                    "DESC" => "DESC",
                    _ => throw new ArgumentException($"sortDir 非法：{sortDir}（ASC/DESC）")
                };
            // tie-break p.Id DESC 保翻页确定性；但 sortBy=Id 本身时不得重复列（SQL Server ORDER BY 列须唯一）
            orderBy = col.Col switch
            {
                "p.EffectiveTo" => $"\nORDER BY CASE WHEN p.EffectiveTo IS NULL THEN 1 ELSE 0 END, p.EffectiveTo {dir}, p.Id DESC",
                "p.Id" => $"\nORDER BY p.Id {dir}",
                _ => $"\nORDER BY {col.Col} {dir}, p.Id DESC"
            };
        }

        var where = @"
WHERE (@Scope IS NULL OR p.ScopeType = @Scope)
  AND (@Fid IS NULL OR p.FactoryId = @Fid)
  AND (@Dpid IS NULL OR p.ProductionDepartmentId = @Dpid)
  AND (@Inc = 1 OR p.IsActive = 1)" + scopeFilter;
        var p = new
        {
            Scope = string.IsNullOrWhiteSpace(scopeType) ? null : scopeType.Trim().ToUpperInvariant(),
            Fid = factoryId,
            Dpid = departmentId,
            Inc = includeInactive ? 1 : 0,
            Skip = skip,
            Take = pageSize,
            ScopeFacs = scopeFacs,
            ScopeDepts = scopeDepts
        };

        // scopeFilter 引用 f./pd. 时 COUNT 也须带同 JOIN（各列表参数在整条 SQL 中仅展开一次，避免 Dapper 双引用）
        var countJoins = scopeFilter.Length > 0
            ? "\nLEFT JOIN dbo.Factory f ON f.Id = p.FactoryId\nLEFT JOIN dbo.ProductionDepartment pd ON pd.Id = p.ProductionDepartmentId"
            : string.Empty;

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM dbo.ProductionWorkCalendarSlot p" + countJoins + where,
            p, CommandType.Text, DatabaseId.APS, 30);

        var sql = SelectColumns + @"
FROM dbo.ProductionWorkCalendarSlot p
LEFT JOIN dbo.Factory f ON f.Id = p.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = p.ProductionDepartmentId"
    + where + orderBy + @"
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

        var rows = await _connectionManager.QueryAsync<ProductionWorkCalendarDto>(
            sql, p, CommandType.Text, DatabaseId.APS, 30);

        return new PageResult<ProductionWorkCalendarDto>
        {
            Items = rows.ToList(),
            Total = total,
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    // ══════════════ 周基准铺设（同范围同星期：未生效旧行覆盖替换、已生效闭区间换版） ══════════════
    public async Task<int> UpsertWeeklyAsync(WeeklyCalendarUpsertRequest request, int actorUserId, string @operator, CancellationToken ct = default)
    {
        var (scopeType, factoryId, deptId) = await ValidateScopeAsync(request.ScopeType, request.FactoryId, request.ProductionDepartmentId, actorUserId, ct);
        if (request.Entries is null || request.Entries.Count == 0)
            throw new ArgumentException("Entries 不能为空");
        if (request.EffectiveFrom.Date == default)
            throw new ArgumentException("EffectiveFrom 必填");

        foreach (var e in request.Entries)
        {
            if (e.WeekdayIso is < 1 or > 7)
                throw new ArgumentException($"WeekdayIso 须 1..7：{e.WeekdayIso}");
            ValidateStateAndTimes(e.WorkState, e.StartAt, e.EndAt, e.AllowLowerOverride, false);
        }

        // 校验①（多窗口版）：同星期允许多条并存（白班/小夜/大夜多班制），但窗口不得重叠；
        // OFF 全天（双 NULL）视作 00:00-24:00，与同日任何窗口均判重叠。相切（17:00 收尾接 17:00 起）合法。
        foreach (var g in request.Entries.GroupBy(e => e.WeekdayIso))
        {
            var items = g.ToList();
            for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (Overlaps(
                        (NormalizeState(items[i].WorkState), items[i].StartAt, items[i].EndAt),
                        (NormalizeState(items[j].WorkState), items[j].StartAt, items[j].EndAt)))
                    throw new ArgumentException(
                        $"星期 {g.Key} 批内窗口重叠：{FmtEntry(items[i])} vs {FmtEntry(items[j])}（同星期多窗口仅允许不重叠并存）");
        }

        var eff = request.EffectiveFrom.Date;
        var inserted = 0;
        // 按星期分组"整组替换"：未生效旧行覆盖删、已生效旧行闭区间换版，再插入该星期全部新窗口
        foreach (var g in request.Entries.GroupBy(e => e.WeekdayIso).OrderBy(x => x.Key))
        {
            var wd = (byte)g.Key;

            await _connectionManager.ExecuteAsync(@"
DELETE FROM dbo.ProductionWorkCalendarSlot
WHERE ScopeType=@st AND FactoryId=@fid
  AND ((@dpid IS NULL AND ProductionDepartmentId IS NULL) OR ProductionDepartmentId=@dpid)
  AND WeekdayIso=@wd AND WorkDate IS NULL AND EffectiveFrom >= @eff;",
                new { st = scopeType, fid = factoryId, dpid = deptId, wd, eff },
                CommandType.Text, DatabaseId.APS, 30);

            await _connectionManager.ExecuteAsync(@"
UPDATE dbo.ProductionWorkCalendarSlot
SET EffectiveTo = @closeTo, UpdatedBy = @op, UpdatedAt = SYSUTCDATETIME()
WHERE ScopeType=@st AND FactoryId=@fid
  AND ((@dpid IS NULL AND ProductionDepartmentId IS NULL) OR ProductionDepartmentId=@dpid)
  AND WeekdayIso=@wd AND WorkDate IS NULL
  AND EffectiveTo IS NULL AND EffectiveFrom < @eff AND IsActive = 1;",
                new { st = scopeType, fid = factoryId, dpid = deptId, wd, eff, closeTo = eff.AddDays(-1), op = @operator },
                CommandType.Text, DatabaseId.APS, 30);

            foreach (var e in g.OrderBy(x => x.StartAt ?? TimeSpan.Zero))
            {
                await _connectionManager.QueryFirstOrDefaultAsync<long>(InsertSql, new
                {
                    st = scopeType, fid = factoryId, dpid = deptId,
                    wd = (byte?)wd, workDate = (DateTime?)null,
                    s = e.StartAt, en = e.EndAt,   // OFF 时段行的时间同样保留（全天 OFF 双 NULL）
                    stt = NormalizeState(e.WorkState),
                    alo = e.AllowLowerOverride ? 1 : 0, aao = 0,
                    ss = request.SourceSystem, sr = request.SourceReference,
                    eff, effTo = (DateTime?)null, op = @operator
                }, CommandType.Text, DatabaseId.APS, 30);
                inserted++;
            }
        }
        return inserted;
    }

    // ══════════════ 日期例外铺设（同级同日重叠即拒） ══════════════
    public async Task<long> CreateExceptionAsync(WorkCalendarExceptionRequest request, int actorUserId, string @operator, CancellationToken ct = default)
    {
        var (scopeType, factoryId, deptId) = await ValidateScopeAsync(request.ScopeType, request.FactoryId, request.ProductionDepartmentId, actorUserId, ct);
        ValidateStateAndTimes(request.WorkState, request.StartAt, request.EndAt, request.AllowLowerOverride, request.IsAuthorizedOverride);
        // 校验④ 授权加班证据
        if (request.IsAuthorizedOverride && string.IsNullOrWhiteSpace(request.SourceReference))
            throw new ArgumentException("IsAuthorizedOverride=1 必须携 SourceReference（审批单号等证据）");
        if (request.EffectiveTo is not null && request.EffectiveTo < request.EffectiveFrom)
            throw new ArgumentException("EffectiveTo 不得早于 EffectiveFrom");

        var workDate = request.WorkDate.Date;
        if (workDate == default) throw new ArgumentException("WorkDate 必填");

        // 校验① 同级同日冲突：该日"开放中"的例外行（含跨日 Effective 区间）
        var existing = (await _connectionManager.QueryAsync<(long Id, string WorkState, TimeSpan? StartAt, TimeSpan? EndAt)>(@"
SELECT Id, WorkState, StartAt, EndAt FROM dbo.ProductionWorkCalendarSlot
WHERE ScopeType=@st AND FactoryId=@fid
  AND ((@dpid IS NULL AND ProductionDepartmentId IS NULL) OR ProductionDepartmentId=@dpid)
  AND WorkDate = @wd AND IsActive = 1 AND (EffectiveTo IS NULL OR EffectiveTo >= @wd)",
            new { st = scopeType, fid = factoryId, dpid = deptId, wd = workDate },
            CommandType.Text, DatabaseId.APS, 30)).ToList();

        foreach (var row in existing)
        {
            if (Overlaps((NormalizeState(request.WorkState), request.StartAt, request.EndAt), (row.WorkState, row.StartAt, row.EndAt)))
                throw new InvalidOperationException($"与已有例外行(Id={row.Id}, {row.WorkState} {row.StartAt:hh\\:mm}-{row.EndAt:hh\\:mm}) 时间重叠，同级同日冲突");
        }

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(InsertSql, new
        {
            st = scopeType, fid = factoryId, dpid = deptId,
            wd = (byte?)null, workDate = workDate,
            s = request.StartAt, en = request.EndAt,   // OFF 时段行的时间同样保留（全天 OFF 双 NULL）
            stt = NormalizeState(request.WorkState),
            alo = request.AllowLowerOverride ? 1 : 0, aao = request.IsAuthorizedOverride ? 1 : 0,
            ss = request.SourceSystem, sr = request.SourceReference,
            eff = request.EffectiveFrom == default ? workDate : request.EffectiveFrom.Date,
            effTo = request.EffectiveTo?.Date, op = @operator
        }, CommandType.Text, DatabaseId.APS, 30);
        return id;
    }

    // ══════════════ 闭区间换版 ══════════════
    public async Task<int> CloseOpenAsync(CalendarCloseOpenRequest request, int actorUserId, string @operator, CancellationToken ct = default)
    {
        var (scopeType, factoryId, deptId) = await ValidateScopeAsync(request.ScopeType, request.FactoryId, request.ProductionDepartmentId, actorUserId, ct);
        return await _connectionManager.ExecuteAsync(@"
UPDATE dbo.ProductionWorkCalendarSlot
SET EffectiveTo = @closeTo, UpdatedBy = @op, UpdatedAt = SYSUTCDATETIME()
WHERE ScopeType=@st AND FactoryId=@fid
  AND ((@dpid IS NULL AND ProductionDepartmentId IS NULL) OR ProductionDepartmentId=@dpid)
  AND EffectiveTo IS NULL AND EffectiveFrom < @newFrom AND IsActive = 1;",
            new { st = scopeType, fid = factoryId, dpid = deptId, newFrom = request.NewEffectiveFrom.Date, closeTo = request.NewEffectiveFrom.Date.AddDays(-1), op = @operator },
            CommandType.Text, DatabaseId.APS, 30);
    }

    // ══════════════ 单行闭区间（撤销已生效行） ══════════════
    public async Task CloseRowAsync(long id, DateTime effectiveTo, int actorUserId, string @operator, CancellationToken ct = default)
    {
        var row = await _connectionManager.QueryFirstOrDefaultAsync<(long Id, DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive, string? FactoryCode, string? DeptCode)?>(
            @"SELECT p.Id, p.EffectiveFrom, p.EffectiveTo, p.IsActive, f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.ProductionWorkCalendarSlot p
LEFT JOIN dbo.Factory f ON f.Id = p.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = p.ProductionDepartmentId
WHERE p.Id = @id",
            new { id }, CommandType.Text, DatabaseId.APS, 30);
        if (row is null)
            throw new KeyNotFoundException($"日历行不存在：{id}");

        // S5-01：行级业务范围校验先于业务校验（越界 403，不落任何更新）
        await EnsureRowScopeAsync(row.Value.FactoryCode, row.Value.DeptCode, actorUserId, ct);

        if (!row.Value.IsActive)
            throw new InvalidOperationException($"行 {id} 已停用（IsActive=0），不可再闭区间");
        if (row.Value.EffectiveTo is not null)
            throw new InvalidOperationException($"行 {id} 已闭区间（EffectiveTo={row.Value.EffectiveTo:yyyy-MM-dd}），不可重复闭");
        if (effectiveTo.Date < row.Value.EffectiveFrom.Date)
            throw new ArgumentException($"EffectiveTo({effectiveTo:yyyy-MM-dd}) 不得早于该行 EffectiveFrom({row.Value.EffectiveFrom:yyyy-MM-dd})");

        await _connectionManager.ExecuteAsync(@"
UPDATE dbo.ProductionWorkCalendarSlot
SET EffectiveTo = @to, UpdatedBy = @op, UpdatedAt = SYSUTCDATETIME()
WHERE Id = @id AND EffectiveTo IS NULL;",
            new { id, to = effectiveTo.Date, op = @operator }, CommandType.Text, DatabaseId.APS, 30);
    }

    // ══════════════ 物理删除（仅限未生效行，纯纠错通道） ══════════════
    public async Task DeleteRowAsync(long id, int actorUserId, CancellationToken ct = default)
    {
        var row = await _connectionManager.QueryFirstOrDefaultAsync<(DateTime EffectiveFrom, string? FactoryCode, string? DeptCode)?>(
            @"SELECT p.EffectiveFrom, f.Code AS FactoryCode, pd.DeptCode AS DeptCode
FROM dbo.ProductionWorkCalendarSlot p
LEFT JOIN dbo.Factory f ON f.Id = p.FactoryId
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = p.ProductionDepartmentId
WHERE p.Id = @id",
            new { id }, CommandType.Text, DatabaseId.APS, 30);
        if (row is null)
            throw new KeyNotFoundException($"日历行不存在：{id}");

        // S5-01：行级业务范围校验先于业务校验（越界 403，不落任何删除）
        await EnsureRowScopeAsync(row.Value.FactoryCode, row.Value.DeptCode, actorUserId, ct);

        if (row.Value.EffectiveFrom.Date <= DateTime.Today)
            throw new InvalidOperationException($"行 {id} 已生效（EffectiveFrom={row.Value.EffectiveFrom:yyyy-MM-dd} ≤ 今天），不可物理删除——请走 POST /rows/{id}/close 闭区间保留审计");

        await _connectionManager.ExecuteAsync(
            "DELETE FROM dbo.ProductionWorkCalendarSlot WHERE Id = @id;",
            new { id }, CommandType.Text, DatabaseId.APS, 30);
    }

    // ══════════════ 共用校验/工具 ══════════════
    /// <summary>GET 排序白名单（sortBy→列 + 该列默认方向）；键大小写不敏感，值不拼 SQL 原文防注入</summary>
    private static readonly Dictionary<string, (string Col, string DefaultDir)> SortWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = ("p.Id", "ASC"),
        ["CreatedAt"] = ("p.CreatedAt", "DESC"),
        ["EffectiveFrom"] = ("p.EffectiveFrom", "ASC"),
        ["EffectiveTo"] = ("p.EffectiveTo", "ASC"),
        ["UpdatedAt"] = ("p.UpdatedAt", "DESC"),
    };

    private const string SelectColumns = @"
SELECT p.Id, p.ScopeType, p.FactoryId, f.Code AS FactoryCode,
       p.ProductionDepartmentId, pd.DeptName AS DepartmentName,
       p.WorkDate, p.WeekdayIso, p.StartAt, p.EndAt, p.WorkState,
       p.AllowLowerOverride, p.IsAuthorizedOverride, p.SourceSystem, p.SourceReference,
       p.EffectiveFrom, p.EffectiveTo, p.IsActive, p.CreatedBy, p.CreatedAt, p.UpdatedAt";

    private const string InsertSql = @"
INSERT INTO dbo.ProductionWorkCalendarSlot
(ScopeType, FactoryId, ProductionDepartmentId, WorkDate, WeekdayIso,
 StartAt, EndAt, WorkState, AllowLowerOverride, IsAuthorizedOverride,
 SourceSystem, SourceReference, EffectiveFrom, EffectiveTo, IsActive, CreatedBy, UpdatedBy)
OUTPUT INSERTED.Id
VALUES
(@st, @fid, @dpid, @workDate, @wd, @s, @en, @stt, @alo, @aao, @ss, @sr, @eff, @effTo, 1, @op, @op);";

    /// <summary>
    /// 范围/归属校验（DDL 尾注校验②+S5-01 校验③合一）：
    /// ① ScopeType 合法与 Factory/Department 携带规则；② 工厂/部门存在且启用、部门归属 Factory 匹配（以库内事实为准）；
    /// ③ 业务范围：非 Global 用户 Factory.Code 必在 Factory 授权内，DEPARTMENT 行另须 DeptCode 在 Department 授权内
    ///   （不同维度 AND，照 StageLeadTimeParamGovernanceService P0-03 先例）；越界抛 ScopeViolationException。
    /// </summary>
    private async Task<(string Scope, int FactoryId, int? DeptId)> ValidateScopeAsync(string? scopeType, int factoryId, int? deptId, int actorUserId, CancellationToken ct)
    {
        var st = (scopeType ?? string.Empty).Trim().ToUpperInvariant();
        if (st != "FACTORY" && st != "DEPARTMENT")
            throw new ArgumentException("ScopeType 仅 FACTORY / DEPARTMENT");
        if (st == "FACTORY" && deptId is not null)
            throw new ArgumentException("FACTORY 行不得携带 ProductionDepartmentId");
        if (st == "DEPARTMENT" && deptId is null)
            throw new ArgumentException("DEPARTMENT 行必须携带 ProductionDepartmentId");

        var factoryCode = await _connectionManager.QueryFirstOrDefaultAsync<string?>(
            "SELECT Code FROM dbo.Factory WHERE Id=@id AND IsActive=1", new { id = factoryId },
            CommandType.Text, DatabaseId.APS, 30);
        if (string.IsNullOrWhiteSpace(factoryCode)) throw new KeyNotFoundException($"工厂不存在或未启用：{factoryId}");

        string? deptCode = null;
        if (st == "DEPARTMENT")
        {
            // 校验② 部门归属 Factory 匹配（以库内事实为准，不信调用方自报）
            var dept = await _connectionManager.QueryFirstOrDefaultAsync<(int FactoryId, string DeptCode)?>(
                "SELECT FactoryId, DeptCode FROM dbo.ProductionDepartment WHERE Id=@id AND IsActive=1",
                new { id = deptId!.Value }, CommandType.Text, DatabaseId.APS, 30);
            if (dept is null) throw new KeyNotFoundException($"部门不存在或未启用：{deptId}");
            if (dept.Value.FactoryId != factoryId)
                throw new ArgumentException($"部门 {deptId} 归属工厂 {dept.Value.FactoryId}，与请求 FactoryId={factoryId} 不一致");
            deptCode = dept.Value.DeptCode;
        }

        // S5-01 校验③：业务范围二次校验（fail-closed：userId 无效→Empty 范围→拒绝全部）
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (!scope.IsGlobal)
        {
            if (!scope.Allows(DataScopeTypes.Factory, factoryCode))
                throw new ScopeViolationException(DataScopeTypes.Factory, factoryCode);
            if (deptCode is not null && !scope.Allows(DataScopeTypes.Department, deptCode))
                throw new ScopeViolationException(DataScopeTypes.Department, deptCode);
        }

        return (st, factoryId, deptId);
    }

    /// <summary>S5-01：行级业务范围校验（CloseRow/DeleteRow 用；语义同 ValidateScopeAsync ③）</summary>
    private async Task EnsureRowScopeAsync(string? factoryCode, string? deptCode, int actorUserId, CancellationToken ct)
    {
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (scope.IsGlobal)
            return;

        if (string.IsNullOrWhiteSpace(factoryCode) || !scope.Allows(DataScopeTypes.Factory, factoryCode))
            throw new ScopeViolationException(DataScopeTypes.Factory, factoryCode ?? "(unknown)");

        if (!string.IsNullOrWhiteSpace(deptCode) && !scope.Allows(DataScopeTypes.Department, deptCode))
            throw new ScopeViolationException(DataScopeTypes.Department, deptCode);
    }

    private static string NormalizeState(string state) => (state ?? string.Empty).Trim().ToUpperInvariant();

    private static string FmtEntry(WeeklyCalendarEntry e)
        => $"{NormalizeState(e.WorkState)} {e.StartAt:hh\\:mm}-{e.EndAt:hh\\:mm}";

    private static void ValidateStateAndTimes(string state, TimeSpan? start, TimeSpan? end, bool allowLower, bool authOverride)
    {
        var s = NormalizeState(state);
        if (s != "WORK" && s != "OFF")
            throw new ArgumentException("WorkState 仅 WORK / OFF（MISSING=不建行，禁止虚拟行）");
        if (s == "WORK" && (start is null || end is null || start >= end))
            throw new ArgumentException("WORK 行必须含 StartAt<EndAt");
        if (s == "OFF" && start is not null && end is not null && start >= end)
            throw new ArgumentException("OFF 时段行须 StartAt<EndAt（全天则两者均空）");
        if (allowLower && s != "OFF")
            throw new ArgumentException("AllowLowerOverride 仅可挂 OFF 行");
        if (authOverride && s != "WORK")
            throw new ArgumentException("IsAuthorizedOverride 仅可挂 WORK 行");
    }

    // 重叠判定：OFF 全天(双NULL)视作覆盖 00:00-24:00；区间半开 [start,end)，相切不重叠
    private static bool Overlaps((string State, TimeSpan? S, TimeSpan? E) a, (string State, TimeSpan? S, TimeSpan? E) b)
    {
        var (as1, ae1) = Window(a);
        var (as2, ae2) = Window(b);
        return as1 < ae2 && as2 < ae1;
    }

    private static (TimeSpan S, TimeSpan E) Window((string State, TimeSpan? S, TimeSpan? E) w)
        => (w.State == "OFF" && w.S is null && w.E is null)
            ? (TimeSpan.Zero, TimeSpan.FromHours(24))
            : (w.S!.Value, w.E!.Value);
}