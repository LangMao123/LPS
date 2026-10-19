using System.Data;
using Dapper;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 参数集版本仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.ParameterSetVersion
/// </summary>
public class ParameterSetVersionRepository : IParameterSetVersionRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<ParameterSetVersionRepository> _logger;

    public ParameterSetVersionRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<ParameterSetVersionRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<ParameterSetVersion?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[ParameterSetVersion]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<ParameterSetVersion>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<ParameterSetVersion>> GetByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[ParameterSetVersion]
            WHERE [ParameterSetId] = @ParameterSetId
            ORDER BY [VersionCode]";

        var results = await _connectionManager.QueryAsync<ParameterSetVersion>(
            sql, new { ParameterSetId = parameterSetId }, db: DatabaseId.APS);

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParameterSetVersionSummaryDto>> GetSummariesByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default)
    {
        // 2026-10-15 4号位 来文：列表端点原 SELECT * 整列搬 ContentSnapshotJson 大 LOB（PSV 1305 稳定 37s）。
        // 显式列仅取摘要字段（冻结 DDL v5.1.8.3 已确认列；ContentSnapshotJson/五主题 JSON/Remarks 为方案 A 实表新增列
        // 或代码实体独有，列表不消费故不取——避免大 LOB 搬运与列存在性耦合）。
        const string sql = @"
            SELECT [Id], [ParameterSetId], [VersionCode], [Status],
                   [EffectiveFrom], [EffectiveTo], [PublishedAt], [PublishedBy],
                   [ApprovedAt], [ApprovedBy], [CreatedAt], [CreatedBy]
            FROM [dbo].[ParameterSetVersion]
            WHERE [ParameterSetId] = @ParameterSetId
            ORDER BY [VersionCode]";

        var results = await _connectionManager.QueryAsync<ParameterSetVersionSummaryDto>(
            sql, new { ParameterSetId = parameterSetId }, db: DatabaseId.APS);

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<ParameterSetVersion?> GetDetailByIdAsync(long id, CancellationToken ct = default)
    {
        // 方案 A 落点（2026-10-16）：详情端点原 SELECT * 整列搬 ContentSnapshotJson 大 LOB（PSV 1305 ≈ 2.15 亿字符 → 39s）。
        // 治理显式列同摘要方法（冻结 DDL v5.1.8.3 已确认列）；五主题经 OPENJSON 单次解析顶级键按块提取原始 JSON 文本，
        // ContentSnapshotJson 不读回应用层；损坏/空快照（ISJSON=0 或 NULL）按块返回 null（与 ExtractBlockJson 容错等价）。
        const string sql = @"
            SELECT ps.[Id], ps.[ParameterSetId], ps.[VersionCode], ps.[Status],
                   ps.[EffectiveFrom], ps.[EffectiveTo], ps.[PublishedAt], ps.[PublishedBy],
                   ps.[ApprovedAt], ps.[ApprovedBy], ps.[CreatedAt], ps.[CreatedBy],
                   MAX(CASE WHEN j.[key] = N'Lock' THEN j.[value] END) AS [LockJson],
                   MAX(CASE WHEN j.[key] = N'Supply' THEN j.[value] END) AS [SupplyJson],
                   MAX(CASE WHEN j.[key] = N'Procurement' THEN j.[value] END) AS [ProcurementJson],
                   MAX(CASE WHEN j.[key] = N'SolverStrategy' THEN j.[value] END) AS [SolverStrategyJson],
                   MAX(CASE WHEN j.[key] = N'CandidateGuardrail' THEN j.[value] END) AS [CandidateGuardrailJson]
            FROM [dbo].[ParameterSetVersion] ps
            OUTER APPLY OPENJSON(CASE WHEN ISJSON(ps.[ContentSnapshotJson]) = 1 THEN ps.[ContentSnapshotJson] END) AS j
            WHERE ps.[Id] = @Id
            GROUP BY ps.[Id], ps.[ParameterSetId], ps.[VersionCode], ps.[Status],
                     ps.[EffectiveFrom], ps.[EffectiveTo], ps.[PublishedAt], ps.[PublishedBy],
                     ps.[ApprovedAt], ps.[ApprovedBy], ps.[CreatedAt], ps.[CreatedBy]";

        return await _connectionManager.QueryFirstOrDefaultAsync<ParameterSetVersion>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParameterSetVersion>> GetDetailByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default)
    {
        // published-version 读路径（方案 A 落点）：语义同 GetDetailByIdAsync，返回列表供 SelectCurrentPublished 0/1/多三态判定。
        const string sql = @"
            SELECT ps.[Id], ps.[ParameterSetId], ps.[VersionCode], ps.[Status],
                   ps.[EffectiveFrom], ps.[EffectiveTo], ps.[PublishedAt], ps.[PublishedBy],
                   ps.[ApprovedAt], ps.[ApprovedBy], ps.[CreatedAt], ps.[CreatedBy],
                   MAX(CASE WHEN j.[key] = N'Lock' THEN j.[value] END) AS [LockJson],
                   MAX(CASE WHEN j.[key] = N'Supply' THEN j.[value] END) AS [SupplyJson],
                   MAX(CASE WHEN j.[key] = N'Procurement' THEN j.[value] END) AS [ProcurementJson],
                   MAX(CASE WHEN j.[key] = N'SolverStrategy' THEN j.[value] END) AS [SolverStrategyJson],
                   MAX(CASE WHEN j.[key] = N'CandidateGuardrail' THEN j.[value] END) AS [CandidateGuardrailJson]
            FROM [dbo].[ParameterSetVersion] ps
            OUTER APPLY OPENJSON(CASE WHEN ISJSON(ps.[ContentSnapshotJson]) = 1 THEN ps.[ContentSnapshotJson] END) AS j
            WHERE ps.[ParameterSetId] = @ParameterSetId
            GROUP BY ps.[Id], ps.[ParameterSetId], ps.[VersionCode], ps.[Status],
                     ps.[EffectiveFrom], ps.[EffectiveTo], ps.[PublishedAt], ps.[PublishedBy],
                     ps.[ApprovedAt], ps.[ApprovedBy], ps.[CreatedAt], ps.[CreatedBy]
            ORDER BY ps.[VersionCode]";

        var results = await _connectionManager.QueryAsync<ParameterSetVersion>(
            sql, new { ParameterSetId = parameterSetId }, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<ParameterSetVersion> AddAsync(ParameterSetVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[ParameterSetVersion]
                ([ParameterSetId], [VersionCode], [Status], [ContentSnapshotJson],
                 [EffectiveFrom], [EffectiveTo],
                 [PublishedAt], [PublishedBy], [ApprovedAt], [ApprovedBy],
                 [CreatedAt], [CreatedBy])
            VALUES
                (@ParameterSetId, @VersionCode, @Status, @ContentSnapshotJson,
                 @EffectiveFrom, @EffectiveTo,
                 @PublishedAt, @PublishedBy, @ApprovedAt, @ApprovedBy,
                 @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, version, db: DatabaseId.APS);

        version.Id = id;
        return version;
    }

    public async Task UpdateAsync(ParameterSetVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE [dbo].[ParameterSetVersion]
            SET [VersionCode] = @VersionCode,
                [Status] = @Status,
                [ContentSnapshotJson] = @ContentSnapshotJson,
                [EffectiveFrom] = @EffectiveFrom,
                [EffectiveTo] = @EffectiveTo,
                [PublishedAt] = @PublishedAt,
                [PublishedBy] = @PublishedBy,
                [ApprovedAt] = @ApprovedAt,
                [ApprovedBy] = @ApprovedBy
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, version, db: DatabaseId.APS);
    }
}
