using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 产品转换换型规则（SetupTransitionRule）仓储实现（Dapper + APS_Production）。
/// 由 2号位（数据引擎）实现，供 3号位治理服务 <see cref="ISetupTransitionRuleService"/> 消费。
/// 对应表：[dbo].[SetupTransitionRule]（冻结 DDL v5.1.8 §3.10.x，2026-09-17）。
/// 审计时间戳兜底：3号位治理服务仅负责 CRUD 编排 + 冲突校验 + AuditLog 落库，不填实体
/// 的 CreatedAt/UpdatedAt；本仓储在 INSERT/UPDATE 时以 ISNULL + SYSDATETIME() 兜底，保证非空列稳妥落库。
/// </summary>
/// <remarks>开发者：2号位（数据引擎）</remarks>
public class SetupTransitionRuleRepository : ISetupTransitionRuleRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public SetupTransitionRuleRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SetupTransitionRule>> GetByRuleSetVersionAsync(
        long ruleSetVersionId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[SetupTransitionRule]
            WHERE [RuleSetVersionId] = @RuleSetVersionId
            ORDER BY [StageCode], [OperationCode], [ResourceId], [FromMaterialId]";

        var results = await _connectionManager.QueryAsync<SetupTransitionRule>(
            sql, new { RuleSetVersionId = ruleSetVersionId }, db: DatabaseId.APS);
        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<SetupTransitionRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM [dbo].[SetupTransitionRule] WHERE [Id] = @Id";
        return await _connectionManager.QueryFirstOrDefaultAsync<SetupTransitionRule>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task<long> AddAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[SetupTransitionRule]
                ([RuleSetVersionId], [ProductionDepartmentId], [StageCode], [OperationCode], [ResourceId],
                 [FromMaterialId], [ToMaterialId], [RuleType], [SetupMinutes], [IsActive],
                 [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy])
            VALUES
                (@RuleSetVersionId, @ProductionDepartmentId, @StageCode, @OperationCode, @ResourceId,
                 @FromMaterialId, @ToMaterialId, @RuleType, @SetupMinutes, @IsActive,
                 ISNULL(@CreatedAt, SYSDATETIME()), @CreatedBy, @UpdatedAt, @UpdatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, rule, db: DatabaseId.APS);

        rule.Id = id;
        return id;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE [dbo].[SetupTransitionRule]
            SET [ProductionDepartmentId] = @ProductionDepartmentId,
                [StageCode]              = @StageCode,
                [OperationCode]          = @OperationCode,
                [ResourceId]             = @ResourceId,
                [FromMaterialId]         = @FromMaterialId,
                [ToMaterialId]           = @ToMaterialId,
                [RuleType]               = @RuleType,
                [SetupMinutes]           = @SetupMinutes,
                [IsActive]               = @IsActive,
                [UpdatedAt]              = ISNULL(@UpdatedAt, SYSDATETIME()),
                [UpdatedBy]              = @UpdatedBy
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, rule, db: DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM [dbo].[SetupTransitionRule] WHERE [Id] = @Id";
        await _connectionManager.ExecuteAsync(sql, new { Id = id }, db: DatabaseId.APS);
    }
}