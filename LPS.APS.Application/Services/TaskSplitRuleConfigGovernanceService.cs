using System.Data;
using System.Text;
using System.Text.Json;
using Dapper;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using TaskSplitRuleConfig = LPS.APS.Core.Entities.APS.TaskSplitRuleConfig;

namespace LPS.APS.Application.Services;

/// <summary>
/// 执行批拆分规则治理服务实现（TaskSplitRuleConfig / Batch Policy，3号位 规则参数体系，0号位 2026-10-07 裁决本轮落码）。
/// 直接持有 DatabaseConnectionManager 写 APS_Production.TaskSplitRuleConfig（DML，无 DDL，红线 #6 不违）；
/// 审计写统一 AuditLog 表（IAuditLogRepository）。载体「两级承接」：主题表 CRUD 即最终态 + 发布时投影进快照第⑧块。
/// </summary>
public sealed class TaskSplitRuleConfigGovernanceService : ITaskSplitRuleConfigGovernanceService
{
    /// <summary>统一审计实体类型常量（AuditLog.EntityType）</summary>
    public const string EntityTypeTaskSplitRuleConfig = "TaskSplitRuleConfig";

    private const string ActionCreate = "Create";
    private const string ActionUpdate = "Update";
    private const string ActionDeactivate = "Deactivate";

    private const string SelectColumns =
        "Id, MaterialId, ProductionDepartmentId, MinExecutionBatchQty, MaxExecutionBatchQty, PreferredBatchQty, " +
        "AllowSplit, AllowMerge, MaxOptimizationSplitCount, MaxBatchCandidates, ResourceGroupId, MinimumOrderQuantity, EconomicOrderQuantity, " +
        "BottleneckSplitStrategy, NonBottleneckStrategy, IsActive, EffectiveFrom, EffectiveTo, CreatedAt, UpdatedAt";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IAuditLogRepository _auditLogRepository;

    public TaskSplitRuleConfigGovernanceService(
        DatabaseConnectionManager connectionManager,
        IAuditLogRepository auditLogRepository)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskSplitRuleConfigDto>> ListAsync(
        int? materialId, int? productionDepartmentId, bool? isActive, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM TaskSplitRuleConfig WHERE 1=1");
        var p = new DynamicParameters();

        if (materialId.HasValue) { sql.Append(" AND MaterialId = @MaterialId"); p.Add("MaterialId", materialId.Value); }
        if (productionDepartmentId.HasValue) { sql.Append(" AND ProductionDepartmentId = @ProductionDepartmentId"); p.Add("ProductionDepartmentId", productionDepartmentId.Value); }
        if (isActive.HasValue) { sql.Append(" AND IsActive = @IsActive"); p.Add("IsActive", isActive.Value); }

        sql.Append(" ORDER BY MaterialId, ProductionDepartmentId, Id");

        var rows = await _connectionManager.QueryAsync<TaskSplitRuleConfigDto>(sql.ToString(), p, db: DatabaseId.APS);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<TaskSplitRuleConfigDto> CreateAsync(
        SaveTaskSplitRuleConfigRequest input, int actorUserId, string actorUserCode, CancellationToken ct = default)
    {
        Validate(input);
        await EnsureNotDuplicateAsync(input.MaterialId, input.ProductionDepartmentId, excludeId: 0);

        var now = DateTime.UtcNow;
        const string insertSql = @"
INSERT INTO TaskSplitRuleConfig
(MaterialId, ProductionDepartmentId, MinExecutionBatchQty, MaxExecutionBatchQty, PreferredBatchQty,
 AllowSplit, AllowMerge, MaxOptimizationSplitCount, MaxBatchCandidates,
 BottleneckSplitStrategy, NonBottleneckStrategy, IsActive, EffectiveFrom, EffectiveTo, CreatedAt, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@MaterialId, @ProductionDepartmentId, @MinExecutionBatchQty, @MaxExecutionBatchQty, @PreferredBatchQty,
 @AllowSplit, @AllowMerge, @MaxOptimizationSplitCount, @MaxBatchCandidates,
 @BottleneckSplitStrategy, @NonBottleneckStrategy, 1, @EffectiveFrom, @EffectiveTo, @CreatedAt, @UpdatedAt)";

        var p = new DynamicParameters();
        p.Add("MaterialId", input.MaterialId);
        p.Add("ProductionDepartmentId", input.ProductionDepartmentId, DbType.Int32);
        p.Add("MinExecutionBatchQty", input.MinExecutionBatchQty, DbType.Decimal);
        p.Add("MaxExecutionBatchQty", input.MaxExecutionBatchQty, DbType.Decimal);
        p.Add("PreferredBatchQty", input.PreferredBatchQty, DbType.Decimal);
        p.Add("AllowSplit", input.AllowSplit!.Value);
        p.Add("AllowMerge", input.AllowMerge!.Value);
        p.Add("MaxOptimizationSplitCount", input.MaxOptimizationSplitCount, DbType.Int32);
        p.Add("MaxBatchCandidates", input.MaxBatchCandidates, DbType.Int32);
        p.Add("BottleneckSplitStrategy", input.BottleneckSplitStrategy, DbType.String);
        p.Add("NonBottleneckStrategy", input.NonBottleneckStrategy, DbType.String);
        p.Add("EffectiveFrom", input.EffectiveFrom, DbType.DateTime);
        p.Add("EffectiveTo", input.EffectiveTo, DbType.DateTime);
        p.Add("CreatedAt", now);
        p.Add("UpdatedAt", now);

        var id = await _connectionManager.QueryFirstOrDefaultAsync<int>(insertSql, p, db: DatabaseId.APS);

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
            Remark = $"新增执行批拆分规则 [Material={input.MaterialId}/Dept={input.ProductionDepartmentId?.ToString() ?? "(默认)"}]",
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
            MaxOptimizationSplitCount = input.MaxOptimizationSplitCount,
            MaxBatchCandidates = input.MaxBatchCandidates,
            BottleneckSplitStrategy = input.BottleneckSplitStrategy,
            NonBottleneckStrategy = input.NonBottleneckStrategy,
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

        var existing = await GetByIdAsync(id);
        if (existing is null)
        {
            throw new ResourceNotFoundException($"执行批拆分规则（TaskSplitRuleConfig.Id={id}）不存在。");
        }

        await EnsureNotDuplicateAsync(input.MaterialId, input.ProductionDepartmentId, excludeId: id);

        var now = DateTime.UtcNow;
        const string updateSql = @"
UPDATE TaskSplitRuleConfig SET
  MaterialId = @MaterialId, ProductionDepartmentId = @ProductionDepartmentId,
  MinExecutionBatchQty = @MinExecutionBatchQty, MaxExecutionBatchQty = @MaxExecutionBatchQty, PreferredBatchQty = @PreferredBatchQty,
  AllowSplit = @AllowSplit, AllowMerge = @AllowMerge,
  MaxOptimizationSplitCount = @MaxOptimizationSplitCount, MaxBatchCandidates = @MaxBatchCandidates,
  BottleneckSplitStrategy = @BottleneckSplitStrategy, NonBottleneckStrategy = @NonBottleneckStrategy,
  EffectiveFrom = @EffectiveFrom, EffectiveTo = @EffectiveTo, UpdatedAt = @UpdatedAt
WHERE Id = @Id";

        var p = new DynamicParameters();
        p.Add("Id", id);
        p.Add("MaterialId", input.MaterialId);
        p.Add("ProductionDepartmentId", input.ProductionDepartmentId, DbType.Int32);
        p.Add("MinExecutionBatchQty", input.MinExecutionBatchQty, DbType.Decimal);
        p.Add("MaxExecutionBatchQty", input.MaxExecutionBatchQty, DbType.Decimal);
        p.Add("PreferredBatchQty", input.PreferredBatchQty, DbType.Decimal);
        p.Add("AllowSplit", input.AllowSplit!.Value);
        p.Add("AllowMerge", input.AllowMerge!.Value);
        p.Add("MaxOptimizationSplitCount", input.MaxOptimizationSplitCount, DbType.Int32);
        p.Add("MaxBatchCandidates", input.MaxBatchCandidates, DbType.Int32);
        p.Add("BottleneckSplitStrategy", input.BottleneckSplitStrategy, DbType.String);
        p.Add("NonBottleneckStrategy", input.NonBottleneckStrategy, DbType.String);
        p.Add("EffectiveFrom", input.EffectiveFrom, DbType.DateTime);
        p.Add("EffectiveTo", input.EffectiveTo, DbType.DateTime);
        p.Add("UpdatedAt", now);

        await _connectionManager.ExecuteAsync(updateSql, p, db: DatabaseId.APS);

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
            Remark = $"更新执行批拆分规则 [Material={input.MaterialId}/Dept={input.ProductionDepartmentId?.ToString() ?? "(默认)"}]",
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
            MaxOptimizationSplitCount = input.MaxOptimizationSplitCount,
            MaxBatchCandidates = input.MaxBatchCandidates,
            BottleneckSplitStrategy = input.BottleneckSplitStrategy,
            NonBottleneckStrategy = input.NonBottleneckStrategy,
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

        var now = DateTime.UtcNow;
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
            Remark = $"停用执行批拆分规则 [Material={existing.MaterialId}]",
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
            MaxOptimizationSplitCount = existing.MaxOptimizationSplitCount,
            MaxBatchCandidates = existing.MaxBatchCandidates,
            BottleneckSplitStrategy = existing.BottleneckSplitStrategy,
            NonBottleneckStrategy = existing.NonBottleneckStrategy,
            IsActive = false,
            EffectiveFrom = existing.EffectiveFrom,
            EffectiveTo = existing.EffectiveTo,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BatchPolicyRuleSnapshot>> GetActiveRulesForSnapshotAsync(CancellationToken ct = default)
    {
        var rows = await _connectionManager.QueryAsync<TaskSplitRuleConfig>(
            $"SELECT {SelectColumns} FROM TaskSplitRuleConfig WHERE IsActive = 1 ORDER BY MaterialId, ProductionDepartmentId, Id",
            null,
            db: DatabaseId.APS);

        return TaskSplitRuleConfigProjector.Project(rows);
    }

    private async Task<TaskSplitRuleConfigDto?> GetByIdAsync(int id)
        => await _connectionManager.QueryFirstOrDefaultAsync<TaskSplitRuleConfigDto>(
            $"SELECT {SelectColumns} FROM TaskSplitRuleConfig WHERE Id = @Id",
            new { Id = id },
            db: DatabaseId.APS);

    private static void Validate(SaveTaskSplitRuleConfigRequest input)
    {
        if (input.MaterialId <= 0)
        {
            throw new ArgumentException("MaterialId 须大于 0。", nameof(input));
        }

        if (input.ProductionDepartmentId.HasValue && input.ProductionDepartmentId.Value <= 0)
        {
            throw new ArgumentException("ProductionDepartmentId 须大于 0（空 = Material 级默认）。", nameof(input));
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

        if (input.PreferredBatchQty.HasValue && input.PreferredBatchQty.Value < 0)
        {
            throw new SetupRuleDataRedLineException("PreferredBatchQty 不允许为负。");
        }

        if (input.MaxOptimizationSplitCount.HasValue && input.MaxOptimizationSplitCount.Value < 0)
        {
            throw new SetupRuleDataRedLineException("MaxOptimizationSplitCount 不允许为负。");
        }

        if (input.MaxBatchCandidates.HasValue && input.MaxBatchCandidates.Value < 0)
        {
            throw new SetupRuleDataRedLineException("MaxBatchCandidates 不允许为负。");
        }

        if (!string.IsNullOrWhiteSpace(input.BottleneckSplitStrategy)
            && !TaskSplitRuleConfigProjector.ValidBottleneckSplitStrategies.Contains(input.BottleneckSplitStrategy))
        {
            throw new SetupRuleDataRedLineException("BottleneckSplitStrategy 必须为 PREFER_SPLIT / PREFER_MERGE 之一。");
        }

        if (!string.IsNullOrWhiteSpace(input.NonBottleneckStrategy)
            && !TaskSplitRuleConfigProjector.ValidNonBottleneckStrategies.Contains(input.NonBottleneckStrategy))
        {
            throw new SetupRuleDataRedLineException("NonBottleneckStrategy 必须为 PREFER_LARGE_BATCH / PREFER_SMALL_BATCH 之一。");
        }

        if (input.EffectiveTo.HasValue && input.EffectiveTo.Value < input.EffectiveFrom)
        {
            throw new SetupRuleDataRedLineException("EffectiveTo 不能早于 EffectiveFrom。");
        }
    }

    private async Task EnsureNotDuplicateAsync(int materialId, int? productionDepartmentId, int excludeId)
    {
        const string sql = @"
SELECT COUNT(1) FROM TaskSplitRuleConfig
WHERE MaterialId = @MaterialId
  AND ((@Dept IS NULL AND ProductionDepartmentId IS NULL) OR ProductionDepartmentId = @Dept)
  AND Id <> @ExcludeId";

        var p = new DynamicParameters();
        p.Add("MaterialId", materialId);
        p.Add("Dept", productionDepartmentId, DbType.Int32);
        p.Add("ExcludeId", excludeId);

        var count = await _connectionManager.QueryFirstOrDefaultAsync<int>(sql, p, db: DatabaseId.APS);
        if (count > 0)
        {
            throw new SetupRuleDataRedLineException("已存在相同（物料+生产部门）组合的执行批拆分规则，不允许重复。");
        }
    }
}