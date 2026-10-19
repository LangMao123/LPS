using System.Data;
using Dapper;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 策略包版本仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.StrategyProfileVersion
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileVersionRepository : IStrategyProfileVersionRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<StrategyProfileVersionRepository> _logger;

    public StrategyProfileVersionRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<StrategyProfileVersionRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<StrategyProfileVersion?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[StrategyProfileVersion]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<StrategyProfileVersion>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<StrategyProfileVersion>> GetByStrategyProfileIdAsync(long strategyProfileId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[StrategyProfileVersion]
            WHERE [StrategyProfileId] = @StrategyProfileId
            ORDER BY [VersionCode]";

        var results = await _connectionManager.QueryAsync<StrategyProfileVersion>(
            sql, new { StrategyProfileId = strategyProfileId }, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<IReadOnlyList<StrategyProfileVersionListItem>> GetByStrategyProfileIdWithCodesAsync(long strategyProfileId, CancellationToken ct = default)
    {
        // 4号位 2026-10-14 审计 R2/R3：LEFT JOIN RuleSetVersion/ParameterSetVersion 带出版本编码与父级主表 Id。
        // LEFT JOIN 而非 INNER：版本可能引用已停用/被删的版本行（悬空引用不致命时列表仍应可见）。
        const string sql = @"
            SELECT v.[Id], v.[StrategyProfileId], v.[VersionCode], v.[RuleSetVersionId], v.[ParameterSetVersionId],
                   rsv.[VersionCode] AS [RuleSetVersionCode], rsv.[RuleSetId] AS [RuleSetId],
                   psv.[VersionCode] AS [ParameterSetVersionCode], psv.[ParameterSetId] AS [ParameterSetId],
                   v.[Status], v.[EffectiveFrom], v.[EffectiveTo], v.[IsDefault],
                   v.[PublishedAt], v.[PublishedBy], v.[ApprovedAt], v.[ApprovedBy],
                   v.[CreatedAt], v.[CreatedBy]
            FROM [dbo].[StrategyProfileVersion] v
            LEFT JOIN [dbo].[RuleSetVersion] rsv ON rsv.[Id] = v.[RuleSetVersionId]
            LEFT JOIN [dbo].[ParameterSetVersion] psv ON psv.[Id] = v.[ParameterSetVersionId]
            WHERE v.[StrategyProfileId] = @StrategyProfileId
            ORDER BY v.[VersionCode]";

        var results = await _connectionManager.QueryAsync<StrategyProfileVersionListItem>(
            sql, new { StrategyProfileId = strategyProfileId }, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<StrategyProfileVersion?> GetDefaultByStrategyProfileIdAsync(long strategyProfileId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[StrategyProfileVersion]
            WHERE [StrategyProfileId] = @StrategyProfileId
              AND [IsDefault] = 1
              AND [Status] = 'PUBLISHED'";

        return await _connectionManager.QueryFirstOrDefaultAsync<StrategyProfileVersion>(
            sql, new { StrategyProfileId = strategyProfileId }, db: DatabaseId.APS);
    }

    public async Task<StrategyProfileVersion> AddAsync(StrategyProfileVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[StrategyProfileVersion]
            ([StrategyProfileId], [VersionCode], [RuleSetVersionId], [ParameterSetVersionId],
             [Status], [EffectiveFrom], [EffectiveTo], [IsDefault],
             [PublishedAt], [PublishedBy], [ApprovedAt], [ApprovedBy],
             [CreatedAt], [CreatedBy])
            VALUES
            (@StrategyProfileId, @VersionCode, @RuleSetVersionId, @ParameterSetVersionId,
             @Status, @EffectiveFrom, @EffectiveTo, @IsDefault,
             @PublishedAt, @PublishedBy, @ApprovedAt, @ApprovedBy,
             @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, version, db: DatabaseId.APS);
        version.Id = id;
        return version;
    }

    public async Task UpdateAsync(StrategyProfileVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE [dbo].[StrategyProfileVersion]
            SET [VersionCode] = @VersionCode,
                [RuleSetVersionId] = @RuleSetVersionId,
                [ParameterSetVersionId] = @ParameterSetVersionId,
                [Status] = @Status,
                [EffectiveFrom] = @EffectiveFrom,
                [EffectiveTo] = @EffectiveTo,
                [IsDefault] = @IsDefault,
                [PublishedAt] = @PublishedAt,
                [PublishedBy] = @PublishedBy,
                [ApprovedAt] = @ApprovedAt,
                [ApprovedBy] = @ApprovedBy
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, version, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<StrategyProfileVersion>> GetDefaultByRunTypeAsync(string runType, CancellationToken ct = default)
    {
        // P0-06：RunType 在父表 StrategyProfile；返回所有候选默认版本全集（不 TOP 1 随机取），
        // 由 Application 层结合 EffectiveFrom/EffectiveTo 过滤后判定 0/1/多（多 → 歧义报错）
        const string sql = @"
            SELECT v.*
            FROM [dbo].[StrategyProfileVersion] v
            JOIN [dbo].[StrategyProfile] p ON p.Id = v.StrategyProfileId
            WHERE v.[IsDefault] = 1
              AND v.[Status] = 'PUBLISHED'
              AND p.[RunType] = @RunType
              AND p.[IsActive] = 1";

        var results = await _connectionManager.QueryAsync<StrategyProfileVersion>(
            sql, new { RunType = runType }, db: DatabaseId.APS);

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StrategyProfileVersion>> GetReferencingPublishedVersionsAsync(
        long? referencedRuleSetVersionId,
        long? referencedParameterSetVersionId,
        CancellationToken ct = default)
    {
        // C-5/D-2：反查引用被停用版本、且仍具合法可新Run资格的已发布策略包版本（父 Profile IsActive=1）。
        // 生效窗口过滤由 Application 层完成；本条仅过滤 PUBLISHED + 父 IsActive（镜像 GetDefaultByRunTypeAsync 判定）。
        const string sql = @"
            SELECT v.*
            FROM [dbo].[StrategyProfileVersion] v
            JOIN [dbo].[StrategyProfile] p ON p.Id = v.StrategyProfileId
            WHERE v.[Status] = 'PUBLISHED'
              AND p.[IsActive] = 1
              AND (   (@ReferencedRuleSetVersionId IS NOT NULL AND v.[RuleSetVersionId] = @ReferencedRuleSetVersionId)
                   OR (@ReferencedParameterSetVersionId IS NOT NULL AND v.[ParameterSetVersionId] = @ReferencedParameterSetVersionId))";

        var results = await _connectionManager.QueryAsync<StrategyProfileVersion>(
            sql,
            new
            {
                ReferencedRuleSetVersionId = referencedRuleSetVersionId,
                ReferencedParameterSetVersionId = referencedParameterSetVersionId
            },
            db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task ClearDefaultFlagAsync(long strategyProfileId, long exceptVersionId, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE [dbo].[StrategyProfileVersion]
            SET [IsDefault] = 0
            WHERE [StrategyProfileId] = @StrategyProfileId
              AND [Id] <> @ExceptVersionId
              AND [IsDefault] = 1";

        await _connectionManager.ExecuteAsync(
            sql,
            new { StrategyProfileId = strategyProfileId, ExceptVersionId = exceptVersionId },
            db: DatabaseId.APS);
    }
}
