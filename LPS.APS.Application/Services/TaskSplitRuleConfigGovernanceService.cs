using System.Data;
using System.Text;
using System.Text.Json;
using Dapper;
using LPS.APS.Application.Models;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using TaskSplitRuleConfig = LPS.APS.Core.Entities.APS.TaskSplitRuleConfig;

namespace LPS.APS.Application.Services;

/// <summary>
/// 执行批拆分规则治理服务实现（TaskSplitRuleConfig / Batch Policy，3号位 规则参数体系，0号位 2026-10-07 裁决本轮落码）。
/// 直接持有 DatabaseConnectionManager 写 APS_Production.TaskSplitRuleConfig（DML，无 DDL，红线 #6 不违）；
/// 审计写统一 AuditLog 表（IAuditLogRepository）。载体「两级承接」：主题表 CRUD 即最终态 + 发布时投影进快照第⑧块。
/// 冻结依据（2026-10-09 候选）：实施包 v1.9 L51（后端校验 MaterialId/ProductionDepartmentId/PreferredBatchQty>0/硬 Min/Max/唯一有效期）
/// + L54（Department Scope、有效期冲突、审计校验；4 技术预算列归 1号位 Solver，不纳入 Batch Policy 业务配置）
/// + 字段说明 v5.1.10（正式业务字段 = Min/Max、AllowSplit/AllowMerge、有效发布必填 PreferredBatchQty）+ DDL v5.1.8.4。
/// </summary>
public sealed class TaskSplitRuleConfigGovernanceService : ITaskSplitRuleConfigGovernanceService
{
    /// <summary>统一审计实体类型常量（AuditLog.EntityType）</summary>
    public const string EntityTypeTaskSplitRuleConfig = "TaskSplitRuleConfig";

    private const string ActionCreate = "Create";
    private const string ActionUpdate = "Update";
    private const string ActionDeactivate = "Deactivate";

    /// <summary>
    /// 治理投影列（v5.1.10 收口①：不含 MaxOptimizationSplitCount/MaxBatchCandidates（1号位 Solver 技术预算）
    /// 与 BottleneckSplitStrategy/NonBottleneckStrategy（历史兼容列，V1 主链不得消费拆/合批倾向）——物理列保留历史，治理 API 不再暴露。
    /// </summary>
    private const string SelectColumns =
        "Id, MaterialId, ProductionDepartmentId, MinExecutionBatchQty, MaxExecutionBatchQty, PreferredBatchQty, " +
        "AllowSplit, AllowMerge, ResourceGroupId, MinimumOrderQuantity, EconomicOrderQuantity, " +
        "IsActive, EffectiveFrom, EffectiveTo, CreatedAt, UpdatedAt";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<TaskSplitRuleConfigGovernanceService> _logger;

    public TaskSplitRuleConfigGovernanceService(
        DatabaseConnectionManager connectionManager,
        IAuditLogRepository auditLogRepository,
        IDataScopeService dataScopeService,
        ILogger<TaskSplitRuleConfigGovernanceService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PageResult<TaskSplitRuleConfigDto>> ListAsync(
        int? materialId, int? productionDepartmentId, bool? isActive,
        int actorUserId, int pageIndex = 1, int pageSize = 20, CancellationToken ct = default)
    {
        // R2 标准分页契约（4号位 2026-10-08 提请，方案 A）：pageIndex 1 基 <1 归 1；pageSize 1~200 超限截断；COUNT + OFFSET/FETCH。
        pageIndex = Math.Max(pageIndex, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (pageIndex - 1) * pageSize;

        // P0-01（0号位 审核 2026-10-09）：以受信主体 Business Scope 约束列表——Global 不过滤；非 Global 按 Department 维度
        // 授权部门（DeptCode → ProductionDepartment.Id）IN 约束 COUNT + 列表；无部门授权 → fail-closed 空页（Auth §9.3 禁止先全量再前端隐藏）；
        // 显式传入越权部门 → 403（ScopeViolationException，与写路径 Scope 契约一致，防 IDOR 探测）。
        var allowedDeptIds = await ResolveAllowedDepartmentIdsAsync(actorUserId, productionDepartmentId, ct);

        var where = new StringBuilder(" FROM TaskSplitRuleConfig WHERE 1=1");
        var p = new DynamicParameters();

        if (materialId.HasValue) { where.Append(" AND MaterialId = @MaterialId"); p.Add("MaterialId", materialId.Value); }
        if (isActive.HasValue) { where.Append(" AND IsActive = @IsActive"); p.Add("IsActive", isActive.Value); }

        if (allowedDeptIds is null)
        {
            // Global 全放行：仅按调用方自选过滤。
            if (productionDepartmentId.HasValue)
            {
                where.Append(" AND ProductionDepartmentId = @ProductionDepartmentId");
                p.Add("ProductionDepartmentId", productionDepartmentId.Value);
            }
        }
        else
        {
            if (allowedDeptIds.Count == 0)
            {
                // 非 Global 且无任何 Department 授权 → fail-closed 空页（禁止先全量返回再前端隐藏）。
                return new PageResult<TaskSplitRuleConfigDto>
                {
                    Items = new List<TaskSplitRuleConfigDto>(),
                    Total = 0,
                    Page = pageIndex,
                    PageSize = pageSize
                };
            }

            where.Append(" AND ProductionDepartmentId IN @AllowedDeptIds");
            p.Add("AllowedDeptIds", allowedDeptIds.ToList());
            // 3-NEW-P1-01（0号位 再审核，v1.2 契约）：非 Global 查询同时尊重「授权集合」与「用户显式条件」——
            // 显式传入部门在授权内时补等值过滤（IN∩=，仅返回该部门，COUNT 与页结果一致）；越权部门已在 ResolveAllowedDepartmentIdsAsync 抛 403。
            if (productionDepartmentId.HasValue)
            {
                where.Append(" AND ProductionDepartmentId = @ProductionDepartmentId");
                p.Add("ProductionDepartmentId", productionDepartmentId.Value);
            }
        }

        p.Add("Offset", offset);
        p.Add("PageSize", pageSize);

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            $"SELECT COUNT(*){where}", p, db: DatabaseId.APS) ?? 0;

        var rows = await _connectionManager.QueryAsync<TaskSplitRuleConfigDto>(
            $"SELECT {SelectColumns}{where} ORDER BY MaterialId, ProductionDepartmentId, Id OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
            p, db: DatabaseId.APS);

        return new PageResult<TaskSplitRuleConfigDto>
        {
            Items = rows.ToList(),
            Total = total,
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<TaskSplitRuleConfigDto> CreateAsync(
        SaveTaskSplitRuleConfigRequest input, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        Validate(input);
        WarnIfConfirmedDegenerateOneOneOne(input);
        await EnsureDepartmentScopeAsync(input.ProductionDepartmentId!.Value, actorUserId, ct);

        // P1-03（0号位 审核 §五 P1-03）：治理 DML（APS_Production）与审计（APS_Auth）跨库，无 2PC——
        // 以审计前置预检（EnsureWritableAsync：开事务即回滚探测审计库可写，F-G4 跨库一致性兜底）先于 DML，
        // 审计库不可写则整操作失败、业务零变更（杜绝「已生效但无追溯审计」的规则）。
        await _auditLogRepository.EnsureWritableAsync(ct);

        // P1-02（0号位 审核 §五 P1-02）：原「唯一有效期检查」与 INSERT 分离存在 TOCTOU 竞态——两个并发请求
        // 同时通过「无重叠」检查后都 INSERT，导致同一 (MaterialId, Dept) 同一时点两条有效规则。
        // ⇒ 检查+写放入同一事务，并持 (MaterialId, DeptId) 独占应用锁（sp_getapplock，事务 Owner 自动释放，零 DDL）。
        // 【2026-10-14 时区整改】原为 DateTime.UtcNow —— 本服务是 2号位 辖区**唯一**的 UTC 孤岛：
        // 该 now 同时写 TaskSplitRuleConfig.CreatedAt/UpdatedAt（两列 DEFAULT 均为 (getdate())）与
        // AuditLog.OccurredAt。全仓其余治理/审计写入点（GovernanceVersionService 13 处、RunLifecycleService、
        // SetupRuleService、StageLeadTimeParamGovernanceService、AuthService、RbacManagementService，
        // 以及 AuthDbContext 拦截器）一律 DateTime.Now。UTC 与 DDL 声明的列口径相悖 ⇒ 统一回本地。
        // 依据：APS 内部口径「时间戳一律本地（DateTime.Now / GETDATE()），禁 UTC」；0号位 2026-10-13
        // PM回复1013-1 §5「涉及UTC/本地时间的差异先进行字段级核对」—— 本次核对已完成（见台账 §T-1014a）。
        var now = DateTime.Now;
        var id = await _connectionManager.ExecuteInTransactionAsync(async (conn, tx) =>
        {
            await AcquireExclusiveLockAsync(conn, tx, input.MaterialId, input.ProductionDepartmentId!.Value);
            await EnsureUniqueEffectiveWindowAsync(
                conn, tx, input.MaterialId, input.ProductionDepartmentId!.Value, input.EffectiveFrom, input.EffectiveTo, excludeId: 0, ct);

            const string insertSql = @"
INSERT INTO TaskSplitRuleConfig
(MaterialId, ProductionDepartmentId, MinExecutionBatchQty, MaxExecutionBatchQty, PreferredBatchQty,
 AllowSplit, AllowMerge,
 IsActive, EffectiveFrom, EffectiveTo, CreatedAt, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@MaterialId, @ProductionDepartmentId, @MinExecutionBatchQty, @MaxExecutionBatchQty, @PreferredBatchQty,
 @AllowSplit, @AllowMerge,
 1, @EffectiveFrom, @EffectiveTo, @CreatedAt, @UpdatedAt)";

            var p = new DynamicParameters();
            p.Add("MaterialId", input.MaterialId);
            p.Add("ProductionDepartmentId", input.ProductionDepartmentId!.Value, DbType.Int32);
            p.Add("MinExecutionBatchQty", input.MinExecutionBatchQty, DbType.Decimal);
            p.Add("MaxExecutionBatchQty", input.MaxExecutionBatchQty, DbType.Decimal);
            p.Add("PreferredBatchQty", input.PreferredBatchQty!.Value, DbType.Decimal);
            p.Add("AllowSplit", input.AllowSplit!.Value);
            p.Add("AllowMerge", input.AllowMerge!.Value);
            p.Add("EffectiveFrom", input.EffectiveFrom, DbType.DateTime);
            p.Add("EffectiveTo", input.EffectiveTo, DbType.DateTime);
            p.Add("CreatedAt", now);
            p.Add("UpdatedAt", now);

            return await conn.QueryFirstOrDefaultAsync<int>(insertSql, p, tx);
        }, DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionCreate,
            EntityType = EntityTypeTaskSplitRuleConfig,
            EntityId = id.ToString(),
            OldValue = null,
            NewValue = JsonSerializer.Serialize(input),
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"新增执行批拆分规则 [Material={input.MaterialId}/Dept={input.ProductionDepartmentId}]",
        }, ct);

        return new TaskSplitRuleConfigDto
        {
            Id = id,
            MaterialId = input.MaterialId,
            ProductionDepartmentId = input.ProductionDepartmentId,
            MinExecutionBatchQty = input.MinExecutionBatchQty,
            MaxExecutionBatchQty = input.MaxExecutionBatchQty,
            PreferredBatchQty = input.PreferredBatchQty,
            AllowSplit = input.AllowSplit!.Value,
            AllowMerge = input.AllowMerge!.Value,
            IsActive = true,
            EffectiveFrom = input.EffectiveFrom,
            EffectiveTo = input.EffectiveTo,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<TaskSplitRuleConfigDto> UpdateAsync(
        int id, SaveTaskSplitRuleConfigRequest input, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("id 须大于 0。", nameof(id));
        }

        Validate(input);
        WarnIfConfirmedDegenerateOneOneOne(input);

        var existing = await GetByIdAsync(id);
        if (existing is null)
        {
            throw new ResourceNotFoundException($"执行批拆分规则（TaskSplitRuleConfig.Id={id}）不存在。");
        }

        // P0-02（0号位 审核）：旧归属 + 新归属分别校验，防 IDOR 式跨部门记录迁移（仅授权 A 的用户不得用 B 记录 Id 改到 A）。
        if (existing.ProductionDepartmentId.HasValue)
        {
            await EnsureDepartmentScopeAsync(existing.ProductionDepartmentId.Value, actorUserId, ct);
        }

        await EnsureDepartmentScopeAsync(input.ProductionDepartmentId!.Value, actorUserId, ct);

        // P1-03：审计前置预检（同 Create，先于 DML，审计库不可写则整操作失败、业务零变更）。
        await _auditLogRepository.EnsureWritableAsync(ct);

        // P1-02：Update 与 Create 同竞态（重叠检查与 UPDATE 分离）——同样放入事务 + (MaterialId, DeptId) 应用锁串行化。
        var now = DateTime.Now;
        await _connectionManager.ExecuteInTransactionAsync(async (conn, tx) =>
        {
            await AcquireExclusiveLockAsync(conn, tx, input.MaterialId, input.ProductionDepartmentId!.Value);
            await EnsureUniqueEffectiveWindowAsync(
                conn, tx, input.MaterialId, input.ProductionDepartmentId!.Value, input.EffectiveFrom, input.EffectiveTo, excludeId: id, ct);

            const string updateSql = @"
UPDATE TaskSplitRuleConfig SET
  MaterialId = @MaterialId, ProductionDepartmentId = @ProductionDepartmentId,
  MinExecutionBatchQty = @MinExecutionBatchQty, MaxExecutionBatchQty = @MaxExecutionBatchQty, PreferredBatchQty = @PreferredBatchQty,
  AllowSplit = @AllowSplit, AllowMerge = @AllowMerge,
  EffectiveFrom = @EffectiveFrom, EffectiveTo = @EffectiveTo, UpdatedAt = @UpdatedAt
WHERE Id = @Id";

            var p = new DynamicParameters();
            p.Add("Id", id);
            p.Add("MaterialId", input.MaterialId);
            p.Add("ProductionDepartmentId", input.ProductionDepartmentId!.Value, DbType.Int32);
            p.Add("MinExecutionBatchQty", input.MinExecutionBatchQty, DbType.Decimal);
            p.Add("MaxExecutionBatchQty", input.MaxExecutionBatchQty, DbType.Decimal);
            p.Add("PreferredBatchQty", input.PreferredBatchQty!.Value, DbType.Decimal);
            p.Add("AllowSplit", input.AllowSplit!.Value);
            p.Add("AllowMerge", input.AllowMerge!.Value);
            p.Add("EffectiveFrom", input.EffectiveFrom, DbType.DateTime);
            p.Add("EffectiveTo", input.EffectiveTo, DbType.DateTime);
            p.Add("UpdatedAt", now);

            await conn.ExecuteAsync(updateSql, p, tx);
            return true;
        }, DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionUpdate,
            EntityType = EntityTypeTaskSplitRuleConfig,
            EntityId = id.ToString(),
            OldValue = JsonSerializer.Serialize(existing),
            NewValue = JsonSerializer.Serialize(input),
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"更新执行批拆分规则 [Material={input.MaterialId}/Dept={input.ProductionDepartmentId}]",
        }, ct);

        return new TaskSplitRuleConfigDto
        {
            Id = id,
            MaterialId = input.MaterialId,
            ProductionDepartmentId = input.ProductionDepartmentId,
            MinExecutionBatchQty = input.MinExecutionBatchQty,
            MaxExecutionBatchQty = input.MaxExecutionBatchQty,
            PreferredBatchQty = input.PreferredBatchQty,
            AllowSplit = input.AllowSplit!.Value,
            AllowMerge = input.AllowMerge!.Value,
            IsActive = existing.IsActive,
            EffectiveFrom = input.EffectiveFrom,
            EffectiveTo = input.EffectiveTo,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<TaskSplitRuleConfigDto> DeactivateAsync(
        int id, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("id 须大于 0。", nameof(id));
        }

        var existing = await GetByIdAsync(id);
        if (existing is null)
        {
            throw new ResourceNotFoundException($"执行批拆分规则（TaskSplitRuleConfig.Id={id}）不存在。");
        }

        // P1-01（0号位 审核）：NULL 部门历史记录无法按部门鉴权——仅 Global 授权可停用（fail-closed，保留审计；反证 #6）。
        if (existing.ProductionDepartmentId is null)
        {
            var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
            if (!scope.IsGlobal)
            {
                throw new ScopeViolationException(DataScopeTypes.Department, "<NULL>");
            }
        }
        else
        {
            await EnsureDepartmentScopeAsync(existing.ProductionDepartmentId.Value, actorUserId, ct);
        }

        // P1-03：审计前置预检（同 Create/Update，先于 DML，审计库不可写则整操作失败、业务零变更）。
        await _auditLogRepository.EnsureWritableAsync(ct);

        var now = DateTime.Now;
        await _connectionManager.ExecuteAsync(
            "UPDATE TaskSplitRuleConfig SET IsActive = 0, UpdatedAt = @UpdatedAt WHERE Id = @Id",
            new { UpdatedAt = now, Id = id },
            db: DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionDeactivate,
            EntityType = EntityTypeTaskSplitRuleConfig,
            EntityId = id.ToString(),
            OldValue = existing.IsActive ? "Active" : "Inactive",
            NewValue = "Inactive",
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"停用执行批拆分规则 [Material={existing.MaterialId}/Dept={existing.ProductionDepartmentId}]",
        }, ct);

        return new TaskSplitRuleConfigDto
        {
            Id = existing.Id,
            MaterialId = existing.MaterialId,
            ProductionDepartmentId = existing.ProductionDepartmentId,
            MinExecutionBatchQty = existing.MinExecutionBatchQty,
            MaxExecutionBatchQty = existing.MaxExecutionBatchQty,
            PreferredBatchQty = existing.PreferredBatchQty,
            AllowSplit = existing.AllowSplit,
            AllowMerge = existing.AllowMerge,
            IsActive = false,
            EffectiveFrom = existing.EffectiveFrom,
            EffectiveTo = existing.EffectiveTo,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
        };
    }


    private async Task<TaskSplitRuleConfigDto?> GetByIdAsync(int id)
        => await _connectionManager.QueryFirstOrDefaultAsync<TaskSplitRuleConfigDto>(
            $"SELECT {SelectColumns} FROM TaskSplitRuleConfig WHERE Id = @Id",
            new { Id = id },
            db: DatabaseId.APS);

    /// <summary>业务校验（v5.1.10 收口③：部门必填、Preferred 必填且>0、Min≤Preferred≤Max；DDL v5.1.8.4 §2.14）。</summary>
    private static void Validate(SaveTaskSplitRuleConfigRequest input)
    {
        if (input.MaterialId <= 0)
        {
            throw new ArgumentException("MaterialId 须大于 0。", nameof(input));
        }

        // ③ 新发布有效规则必须明确 ProductionDepartmentId（NULL 部门仅历史兼容，治理写路径拒绝——不再支持「Material 级默认」语义）。
        if (!input.ProductionDepartmentId.HasValue || input.ProductionDepartmentId.Value <= 0)
        {
            throw new SetupRuleDataRedLineException("ProductionDepartmentId 必填（NULL 部门仅历史兼容，新发布有效规则必须明确部门）。");
        }

        if (!input.AllowSplit.HasValue || !input.AllowMerge.HasValue)
        {
            throw new SetupRuleDataRedLineException("AllowSplit / AllowMerge 须显式提供（bool? 不得省略）。");
        }

        if (input.MinExecutionBatchQty.HasValue && input.MinExecutionBatchQty.Value < 0)
        {
            throw new SetupRuleDataRedLineException("MinExecutionBatchQty 不允许为负。");
        }

        if (input.MaxExecutionBatchQty.HasValue && input.MaxExecutionBatchQty.Value < 0)
        {
            throw new SetupRuleDataRedLineException("MaxExecutionBatchQty 不允许为负。");
        }

        if (input.MinExecutionBatchQty.HasValue && input.MaxExecutionBatchQty.HasValue
            && input.MaxExecutionBatchQty.Value < input.MinExecutionBatchQty.Value)
        {
            throw new SetupRuleDataRedLineException("MaxExecutionBatchQty 不能小于 MinExecutionBatchQty。");
        }

        // ③ PreferredBatchQty 业务有效发布必填且 >0（DDL v5.1.8.4 §2.14 / 字段说明 v5.1.10）。
        if (!input.PreferredBatchQty.HasValue)
        {
            throw new SetupRuleDataRedLineException("PreferredBatchQty 必填（业务有效发布必填软偏好）。");
        }

        if (input.PreferredBatchQty.Value <= 0)
        {
            throw new SetupRuleDataRedLineException("PreferredBatchQty 必须大于 0。");
        }

        // ③ Preferred 必须处于硬 Min/Max 区间内（已配时）。
        if (input.MinExecutionBatchQty.HasValue && input.PreferredBatchQty.Value < input.MinExecutionBatchQty.Value)
        {
            throw new SetupRuleDataRedLineException("PreferredBatchQty 不能小于 MinExecutionBatchQty（硬下限）。");
        }

        if (input.MaxExecutionBatchQty.HasValue && input.PreferredBatchQty.Value > input.MaxExecutionBatchQty.Value)
        {
            throw new SetupRuleDataRedLineException("PreferredBatchQty 不能大于 MaxExecutionBatchQty（硬上限）。");
        }

        if (input.EffectiveFrom.HasValue && input.EffectiveTo.HasValue
            && input.EffectiveTo.Value < input.EffectiveFrom.Value)
        {
            throw new SetupRuleDataRedLineException("EffectiveTo 不能早于 EffectiveFrom。");
        }
    }

    /// <summary>
    /// **B-006 已确认异常 `1/1/1` 的写路径告警**（Min = Max = Preferred = 1，三者都显式给出且同为 1）。
    ///
    /// 依据逐字（规则清单 **B-006**，**生效**）：「…`PreferredBatchQty`业务正数必填，经济软Min/Max及Preferred
    /// 数值须自洽并**由3号位治理**和授权车间维护。**发布数据质量校验不等于Solver对生产批量经济区间偏离的硬拒排**；
    /// 历史NULL物理兼容，**已确认异常`1/1/1`经核实后治理防重入**」。
    ///
    /// ⇒ 「**防重入**」必须落在**写的时候**。发布侧旧⑧块注入已退出、装载改由 2号位 按表直读——
    /// 若只在历史发布读取路径检出（已随某次发布进快照后才看见），「防重入」已经晚了；
    /// 若落装载直读路径则须由 2号位 在装载时按同一判据告警，3号位 写路径补的是「防重入」前置可见性。
    ///
    /// ⚠ **只告警，不阻断**：`1/1/1` 本身**自洽**（满足 Min ≤ Max 且 Preferred ∈ [Min, Max]），
    /// 属**经济区间偏离**、不是自洽性违规 ⇒ **不得**并入 <see cref="Validate"/> 的阻断项，
    /// 否则等于单方替 3号位 拒收业务数据（与 B-006「不等于硬拒排」直接冲突）。
    /// 判据复用 <see cref="TaskSplitRuleConfigProjector.IsConfirmedDegenerateOneOneOne"/> ——
    /// 与发布闸那条告警**同源一份判据**，两处不会漂移。
    /// </summary>
    private void WarnIfConfirmedDegenerateOneOneOne(SaveTaskSplitRuleConfigRequest input)
    {
        if (!TaskSplitRuleConfigProjector.IsConfirmedDegenerateOneOneOne(
                input.MinExecutionBatchQty, input.MaxExecutionBatchQty, input.PreferredBatchQty))
        {
            return;
        }

        _logger.LogWarning(
            "执行批拆分规则写入请求含 B-006 已确认异常 1/1/1（Min=Max=Preferred=1）："
            + "MaterialId={MaterialId}, ProductionDepartmentId={ProductionDepartmentId}。"
            + "按 B-006 属「经济区间偏离」，写路径**不阻断**；请 3号位 按「经核实后治理防重入」处置。",
            input.MaterialId,
            input.ProductionDepartmentId);
    }

    /// <summary>
    /// A2 Department Scope：写路径（Create/Update/Deactivate）经部门业务码（ProductionDepartment.DeptCode）调
    /// EnsureInScopeAsync(Department)。部门不存在 → 404（ResourceNotFoundException）；越界 → ScopeViolationException（Controller 映射 403）。
    /// </summary>
    private async Task EnsureDepartmentScopeAsync(int departmentId, int actorUserId, CancellationToken ct)
    {
        var deptCode = await _connectionManager.QueryFirstOrDefaultAsync<string?>(
            "SELECT DeptCode FROM ProductionDepartment WHERE Id = @Id",
            new { Id = departmentId },
            db: DatabaseId.APS);

        if (string.IsNullOrWhiteSpace(deptCode))
        {
            throw new ResourceNotFoundException($"生产部门（ProductionDepartment.Id={departmentId}）不存在。");
        }

        await _dataScopeService.EnsureInScopeAsync(actorUserId, DataScopeTypes.Department, deptCode, ct);
    }

    /// <summary>
    /// P0-01：解析当前用户在 Department 维度可查询的部门 Id 集合。
    /// Global → 返回 null（不过滤，全放行）；非 Global → 授权 DeptCode 映射为 ProductionDepartment.Id 列表；
    /// 无任何 Department 授权 → 空列表（fail-closed 空页）；显式传入越权部门 → 403（ScopeViolationException，防 IDOR 探测）。
    /// </summary>
    private async Task<IReadOnlyList<int>?> ResolveAllowedDepartmentIdsAsync(int actorUserId, int? requestedDeptId, CancellationToken ct)
    {
        var ctx = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (ctx.IsGlobal)
        {
            return null;
        }

        var deptCodes = ctx.GetValues(DataScopeTypes.Department);
        if (deptCodes is null || deptCodes.Count == 0)
        {
            if (requestedDeptId.HasValue)
            {
                throw new ScopeViolationException(DataScopeTypes.Department, requestedDeptId.Value.ToString());
            }

            return new List<int>();
        }

        if (requestedDeptId.HasValue)
        {
            var requestedCode = await _connectionManager.QueryFirstOrDefaultAsync<string?>(
                "SELECT DeptCode FROM ProductionDepartment WHERE Id = @Id",
                new { Id = requestedDeptId.Value },
                db: DatabaseId.APS);

            if (string.IsNullOrWhiteSpace(requestedCode) || !deptCodes.Contains(requestedCode))
            {
                throw new ScopeViolationException(DataScopeTypes.Department, requestedDeptId.Value.ToString());
            }
        }

        var ids = await _connectionManager.QueryAsync<int>(
            "SELECT Id FROM ProductionDepartment WHERE DeptCode IN @Codes",
            new { Codes = deptCodes },
            db: DatabaseId.APS);

        return ids.ToList();
    }

    /// <summary>
    /// P1-02（0号位 审核）：获取 (MaterialId, DeptId) 键上的 SQL Server 独占应用锁（sp_getapplock，事务 Owner）。
    /// 使「唯一有效期检查 + 写」在同一事务内串行化，杜绝并发 TOCTOU 双写；事务提交/回滚自动释放锁，零 DDL（红线 #6 不违）。
    /// 返回值 &lt; 0 表示未获锁（超时返回 -2），抛数据红线异常；30 秒等待防死锁/饿死。
    /// </summary>
    private static async Task AcquireExclusiveLockAsync(IDbConnection conn, IDbTransaction tx, int materialId, int departmentId)
    {
        const string sql = @"
DECLARE @ret int;
EXEC @ret = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @LockTimeout;
SELECT @ret;";
        var p = new DynamicParameters();
        p.Add("Resource", $"TaskSplitRuleConfig:{materialId}:{departmentId}", DbType.String, size: 255);
        p.Add("LockTimeout", 30000, DbType.Int32);

        var result = await conn.QueryFirstOrDefaultAsync<int>(sql, p, tx);
        if (result < 0)
        {
            throw new SetupRuleDataRedLineException("并发创建/更新繁忙，获取执行批拆分规则业务锁超时，请稍后重试。");
        }
    }

    /// <summary>
    /// A1 唯一有效期：同一 (MaterialId + ProductionDepartmentId) 任一时点至多一条 IsActive=1 有效规则。
    /// 新生效时间窗与既有有效规则窗口重叠 → 422 拒绝；顺序非重叠窗口 → 允许；NULL 时间边界视为无界（EffectiveFrom NULL=不限开始、
    /// EffectiveTo NULL=永久有效）；边界相切（&lt; 严格判定）不视为重叠。冻结依据：实施包 v1.9 L51「唯一有效期」+ 字段说明 v5.1.10 §7.3。
    /// P1-02：须在持有 (MaterialId, DeptId) 独占应用锁的事务内调用（conn/tx 由调用方传入），否则检查与写之间仍存在竞态窗口。
    /// </summary>
    private static async Task EnsureUniqueEffectiveWindowAsync(
        IDbConnection conn, IDbTransaction tx,
        int materialId, int departmentId, DateTime? effectiveFrom, DateTime? effectiveTo, int excludeId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1) FROM TaskSplitRuleConfig
WHERE MaterialId = @MaterialId
  AND ProductionDepartmentId = @Dept
  AND IsActive = 1 AND Id <> @ExcludeId
  AND (EffectiveFrom IS NULL OR @EffectiveTo IS NULL OR EffectiveFrom < @EffectiveTo)
  AND (@EffectiveFrom IS NULL OR EffectiveTo IS NULL OR @EffectiveFrom < EffectiveTo)";

        var p = new DynamicParameters();
        p.Add("MaterialId", materialId);
        p.Add("Dept", departmentId, DbType.Int32);
        p.Add("ExcludeId", excludeId);
        p.Add("EffectiveFrom", effectiveFrom, DbType.DateTime);
        p.Add("EffectiveTo", effectiveTo, DbType.DateTime);

        var count = await conn.QueryFirstOrDefaultAsync<int>(sql, p, tx);
        if (count > 0)
        {
            throw new SetupRuleDataRedLineException(
                "生效时间窗重叠，违反唯一有效期（同一物料+部门同一时点至多一条有效规则）。");
        }
    }
}
