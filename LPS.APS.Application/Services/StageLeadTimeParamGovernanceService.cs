using System.Text;
using Dapper;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 阶段提前期参数治理服务实现（StageLeadTimeParam，3号位 规则参数体系）。
/// 直接持有 DatabaseConnectionManager 写 APS_Production.StageLeadTimeParam（DML，无 DDL，红线 #6 不违）；
/// 审计写统一 AuditLog 表（IAuditLogRepository）。载体「直接生效（时间窗）」：不进策略包/快照，CRUD 即最终态。
/// </summary>
public sealed class StageLeadTimeParamGovernanceService : IStageLeadTimeParamGovernanceService
{
    /// <summary>统一审计实体类型常量（AuditLog.EntityType）</summary>
    public const string EntityTypeStageLeadTimeParam = "StageLeadTimeParam";

    private const string ActionCreate = "Create";
    private const string ActionUpdate = "Update";
    private const string ActionDeactivate = "Deactivate";

    private const string SelectColumns =
        "Id, FactoryCode, StageCode, ProductionDeptCode, MaterialCode, ProductFamilyCode, " +
        "LeadTimeDays, LeadTimeHours, Priority, EffectiveFrom, EffectiveTo, IsDefault, IsActive, CreatedAt, UpdatedAt";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<StageLeadTimeParamGovernanceService> _logger;

    public StageLeadTimeParamGovernanceService(
        DatabaseConnectionManager connectionManager,
        IAuditLogRepository auditLogRepository,
        ILogger<StageLeadTimeParamGovernanceService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StageLeadTimeParamDto>> ListAsync(
        string? factoryCode, string? stageCode, string? productionDeptCode, bool? isActive, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM StageLeadTimeParam WHERE 1=1");
        var p = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(factoryCode)) { sql.Append(" AND FactoryCode = @FactoryCode"); p.Add("FactoryCode", factoryCode); }
        if (!string.IsNullOrWhiteSpace(stageCode)) { sql.Append(" AND StageCode = @StageCode"); p.Add("StageCode", stageCode); }
        if (!string.IsNullOrWhiteSpace(productionDeptCode)) { sql.Append(" AND ProductionDeptCode = @ProductionDeptCode"); p.Add("ProductionDeptCode", productionDeptCode); }
        if (isActive.HasValue) { sql.Append(" AND IsActive = @IsActive"); p.Add("IsActive", isActive.Value); }

        sql.Append(" ORDER BY FactoryCode, StageCode, Priority, Id");

        var rows = await _connectionManager.QueryAsync<StageLeadTimeParamDto>(sql.ToString(), p, db: DatabaseId.APS);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<StageLeadTimeParamDto> CreateAsync(
        SaveStageLeadTimeParamRequest input, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        Validate(input);
        await EnsureNotDuplicateAsync(input.FactoryCode, input.StageCode, input.ProductionDeptCode, input.MaterialCode, input.ProductFamilyCode, excludeId: 0);

        var now = DateTime.UtcNow;
        const string insertSql = @"
INSERT INTO StageLeadTimeParam
(FactoryCode, StageCode, ProductionDeptCode, MaterialCode, ProductFamilyCode,
 LeadTimeDays, LeadTimeHours, Priority, EffectiveFrom, EffectiveTo, IsDefault, IsActive, CreatedAt, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@FactoryCode, @StageCode, @ProductionDeptCode, @MaterialCode, @ProductFamilyCode,
 @LeadTimeDays, @LeadTimeHours, @Priority, @EffectiveFrom, @EffectiveTo, @IsDefault, 1, @CreatedAt, @UpdatedAt)";

        var p = new DynamicParameters();
        p.Add("FactoryCode", input.FactoryCode);
        p.Add("StageCode", input.StageCode);
        p.Add("ProductionDeptCode", (object?)input.ProductionDeptCode ?? DBNull.Value);
        p.Add("MaterialCode", (object?)input.MaterialCode ?? DBNull.Value);
        p.Add("ProductFamilyCode", (object?)input.ProductFamilyCode ?? DBNull.Value);
        p.Add("LeadTimeDays", input.LeadTimeDays);
        p.Add("LeadTimeHours", input.LeadTimeHours);
        p.Add("Priority", input.Priority);
        p.Add("EffectiveFrom", input.EffectiveFrom);
        p.Add("EffectiveTo", (object?)input.EffectiveTo ?? DBNull.Value);
        p.Add("IsDefault", input.IsDefault);
        p.Add("CreatedAt", now);
        p.Add("UpdatedAt", now);

        var id = await _connectionManager.QueryFirstOrDefaultAsync<int>(insertSql, p, db: DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionCreate,
            EntityType = EntityTypeStageLeadTimeParam,
            EntityId = id.ToString(),
            OldValue = null,
            NewValue = input.StageCode,
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"新增阶段提前期参数 [{input.FactoryCode}/{input.StageCode}] LeadTimeDays={input.LeadTimeDays}",
        }, ct);

        return new StageLeadTimeParamDto
        {
            Id = id,
            FactoryCode = input.FactoryCode,
            StageCode = input.StageCode,
            ProductionDeptCode = input.ProductionDeptCode,
            MaterialCode = input.MaterialCode,
            ProductFamilyCode = input.ProductFamilyCode,
            LeadTimeDays = input.LeadTimeDays,
            LeadTimeHours = input.LeadTimeHours,
            Priority = input.Priority,
            EffectiveFrom = input.EffectiveFrom,
            EffectiveTo = input.EffectiveTo,
            IsDefault = input.IsDefault,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<StageLeadTimeParamDto> UpdateAsync(
        int id, SaveStageLeadTimeParamRequest input, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("id 须大于 0。", nameof(id));
        }

        Validate(input);

        var existing = await GetByIdAsync(id);
        if (existing is null)
        {
            throw new ResourceNotFoundException($"阶段提前期参数（StageLeadTimeParam.Id={id}）不存在。");
        }

        await EnsureNotDuplicateAsync(input.FactoryCode, input.StageCode, input.ProductionDeptCode, input.MaterialCode, input.ProductFamilyCode, excludeId: id);

        var now = DateTime.UtcNow;
        const string updateSql = @"
UPDATE StageLeadTimeParam SET
  FactoryCode = @FactoryCode, StageCode = @StageCode, ProductionDeptCode = @ProductionDeptCode,
  MaterialCode = @MaterialCode, ProductFamilyCode = @ProductFamilyCode,
  LeadTimeDays = @LeadTimeDays, LeadTimeHours = @LeadTimeHours, Priority = @Priority,
  EffectiveFrom = @EffectiveFrom, EffectiveTo = @EffectiveTo, IsDefault = @IsDefault,
  UpdatedAt = @UpdatedAt
WHERE Id = @Id";

        var p = new DynamicParameters();
        p.Add("Id", id);
        p.Add("FactoryCode", input.FactoryCode);
        p.Add("StageCode", input.StageCode);
        p.Add("ProductionDeptCode", (object?)input.ProductionDeptCode ?? DBNull.Value);
        p.Add("MaterialCode", (object?)input.MaterialCode ?? DBNull.Value);
        p.Add("ProductFamilyCode", (object?)input.ProductFamilyCode ?? DBNull.Value);
        p.Add("LeadTimeDays", input.LeadTimeDays);
        p.Add("LeadTimeHours", input.LeadTimeHours);
        p.Add("Priority", input.Priority);
        p.Add("EffectiveFrom", input.EffectiveFrom);
        p.Add("EffectiveTo", (object?)input.EffectiveTo ?? DBNull.Value);
        p.Add("IsDefault", input.IsDefault);
        p.Add("UpdatedAt", now);

        await _connectionManager.ExecuteAsync(updateSql, p, db: DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionUpdate,
            EntityType = EntityTypeStageLeadTimeParam,
            EntityId = id.ToString(),
            OldValue = existing.StageCode,
            NewValue = input.StageCode,
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"更新阶段提前期参数 [{input.FactoryCode}/{input.StageCode}]",
        }, ct);

        return new StageLeadTimeParamDto
        {
            Id = id,
            FactoryCode = input.FactoryCode,
            StageCode = input.StageCode,
            ProductionDeptCode = input.ProductionDeptCode,
            MaterialCode = input.MaterialCode,
            ProductFamilyCode = input.ProductFamilyCode,
            LeadTimeDays = input.LeadTimeDays,
            LeadTimeHours = input.LeadTimeHours,
            Priority = input.Priority,
            EffectiveFrom = input.EffectiveFrom,
            EffectiveTo = input.EffectiveTo,
            IsDefault = input.IsDefault,
            IsActive = existing.IsActive,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<StageLeadTimeParamDto> DeactivateAsync(
        int id, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("id 须大于 0。", nameof(id));
        }

        var existing = await GetByIdAsync(id);
        if (existing is null)
        {
            throw new ResourceNotFoundException($"阶段提前期参数（StageLeadTimeParam.Id={id}）不存在。");
        }

        var now = DateTime.UtcNow;
        await _connectionManager.ExecuteAsync(
            "UPDATE StageLeadTimeParam SET IsActive = 0, UpdatedAt = @UpdatedAt WHERE Id = @Id",
            new { UpdatedAt = now, Id = id },
            db: DatabaseId.APS);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ActionDeactivate,
            EntityType = EntityTypeStageLeadTimeParam,
            EntityId = id.ToString(),
            OldValue = existing.IsActive ? "Active" : "Inactive",
            NewValue = "Inactive",
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = now,
            Remark = $"停用阶段提前期参数 [{existing.FactoryCode}/{existing.StageCode}]",
        }, ct);

        return new StageLeadTimeParamDto
        {
            Id = existing.Id,
            FactoryCode = existing.FactoryCode,
            StageCode = existing.StageCode,
            ProductionDeptCode = existing.ProductionDeptCode,
            MaterialCode = existing.MaterialCode,
            ProductFamilyCode = existing.ProductFamilyCode,
            LeadTimeDays = existing.LeadTimeDays,
            LeadTimeHours = existing.LeadTimeHours,
            Priority = existing.Priority,
            EffectiveFrom = existing.EffectiveFrom,
            EffectiveTo = existing.EffectiveTo,
            IsDefault = existing.IsDefault,
            IsActive = false,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
        };
    }

    private async Task<StageLeadTimeParamDto?> GetByIdAsync(int id)
        => await _connectionManager.QueryFirstOrDefaultAsync<StageLeadTimeParamDto>(
            $"SELECT {SelectColumns} FROM StageLeadTimeParam WHERE Id = @Id",
            new { Id = id },
            db: DatabaseId.APS);

    private static void Validate(SaveStageLeadTimeParamRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.FactoryCode))
        {
            throw new ArgumentException("FactoryCode 不能为空。", nameof(input.FactoryCode));
        }

        if (string.IsNullOrWhiteSpace(input.StageCode))
        {
            throw new ArgumentException("StageCode 不能为空。", nameof(input.StageCode));
        }

        if (input.LeadTimeDays < 0 || input.LeadTimeHours < 0)
        {
            throw new SetupRuleDataRedLineException("提前期不允许为负（LeadTimeDays / LeadTimeHours ≥ 0）。");
        }

        if (input.LeadTimeDays == 0 && input.LeadTimeHours == 0)
        {
            throw new SetupRuleDataRedLineException("阶段提前期须大于 0（LeadTimeDays 与 LeadTimeHours 不能同时为 0）。");
        }

        if (input.Priority < 0)
        {
            throw new SetupRuleDataRedLineException("Priority 不允许为负。");
        }

        if (input.EffectiveTo.HasValue && input.EffectiveTo.Value < input.EffectiveFrom)
        {
            throw new SetupRuleDataRedLineException("EffectiveTo 不能早于 EffectiveFrom。");
        }
    }

    private async Task EnsureNotDuplicateAsync(
        string factoryCode, string stageCode, string? productionDeptCode, string? materialCode, string? productFamilyCode, int excludeId)
    {
        const string sql = @"
SELECT COUNT(1) FROM StageLeadTimeParam
WHERE FactoryCode = @FactoryCode AND StageCode = @StageCode
  AND ((@Dept IS NULL AND ProductionDeptCode IS NULL) OR ProductionDeptCode = @Dept)
  AND ((@Material IS NULL AND MaterialCode IS NULL) OR MaterialCode = @Material)
  AND ((@Family IS NULL AND ProductFamilyCode IS NULL) OR ProductFamilyCode = @Family)
  AND Id <> @ExcludeId";

        var p = new DynamicParameters();
        p.Add("FactoryCode", factoryCode);
        p.Add("StageCode", stageCode);
        p.Add("Dept", (object?)productionDeptCode ?? DBNull.Value);
        p.Add("Material", (object?)materialCode ?? DBNull.Value);
        p.Add("Family", (object?)productFamilyCode ?? DBNull.Value);
        p.Add("ExcludeId", excludeId);

        var count = await _connectionManager.QueryFirstOrDefaultAsync<int>(sql, p, db: DatabaseId.APS);
        if (count > 0)
        {
            throw new SetupRuleDataRedLineException("已存在相同（工厂+阶段+生产部门+物料+产品族）组合的阶段提前期参数，不允许重复。");
        }
    }
}