using System.Text;
using Dapper;
using LPS.APS.Core.DTOs.Governance;
using ParameterSet = LPS.APS.Core.Entities.APS.ParameterSet;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 参数集主表仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.ParameterSet
/// 3-4联调接口 G1（A8）：参数集列表查询。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class ParameterSetRepository : IParameterSetRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<ParameterSetRepository> _logger;

    public ParameterSetRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<ParameterSetRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ParameterSet>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        var sql = new StringBuilder(@"
            SELECT * FROM [dbo].[ParameterSet]
            WHERE 1 = 1");

        var parameters = new DynamicParameters();
        if (activeOnly.HasValue)
        {
            sql.Append(" AND [IsActive] = @IsActive");
            parameters.Add("IsActive", activeOnly.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND ([ParameterSetCode] LIKE @Keyword OR [ParameterSetName] LIKE @Keyword)");
            parameters.Add("Keyword", $"%{keyword.Trim()}%");
        }

        sql.Append(" ORDER BY [Id] DESC");

        if (skip.HasValue)
        {
            sql.Append(" OFFSET @Skip ROWS");
            parameters.Add("Skip", skip.Value);
        }

        if (take.HasValue)
        {
            sql.Append(" FETCH NEXT @Take ROWS ONLY");
            parameters.Add("Take", take.Value);
        }

        var results = await _connectionManager.QueryAsync<ParameterSet>(
            sql.ToString(), parameters, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<IReadOnlyList<ParameterSetListItemDto>> GetListWithVersionAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        // Q1（4号位 2026-10-15）：参数集主表列表 LEFT JOIN 最新版本摘要，对齐 rule-sets（R2 同款）。
        // 版本摘要语义：
        //  - CurrentVersionId / CurrentVersionCode = 最新 PUBLISHED 版本 Id + 版本码（C-4 身份补全）
        //  - DraftVersionId   / DraftVersionCode   = 最新 DRAFT 版本 Id + 版本码（C-4 身份补全）
        //  - Status / LastChangeReason / PublishedAt = 最新版本；无版本行则版本摘要为 null
        var sql = new StringBuilder(@"
            SELECT p.[Id], p.[ParameterSetCode], p.[ParameterSetName], p.[Description], p.[IsActive],
                   p.[CreatedAt], p.[CreatedBy], p.[UpdatedAt], p.[UpdatedBy],
                   v.CurrentVersionId, v.CurrentVersionCode,
                   v.DraftVersionId, v.DraftVersionCode,
                   v.Status, v.LastChangeReason, v.PublishedAt
            FROM [dbo].[ParameterSet] p
            OUTER APPLY (
                SELECT TOP 1
                       (SELECT TOP 1 [Id] FROM [dbo].[ParameterSetVersion]
                         WHERE [ParameterSetId] = p.[Id] AND [Status] = 'PUBLISHED'
                         ORDER BY [Id] DESC) AS CurrentVersionId,
                       (SELECT TOP 1 [VersionCode] FROM [dbo].[ParameterSetVersion]
                         WHERE [ParameterSetId] = p.[Id] AND [Status] = 'PUBLISHED'
                         ORDER BY [Id] DESC) AS CurrentVersionCode,
                       (SELECT TOP 1 [Id] FROM [dbo].[ParameterSetVersion]
                         WHERE [ParameterSetId] = p.[Id] AND [Status] = 'DRAFT'
                         ORDER BY [Id] DESC) AS DraftVersionId,
                       (SELECT TOP 1 [VersionCode] FROM [dbo].[ParameterSetVersion]
                         WHERE [ParameterSetId] = p.[Id] AND [Status] = 'DRAFT'
                         ORDER BY [Id] DESC) AS DraftVersionCode,
                       [Status], [Remarks] AS LastChangeReason, [PublishedAt]
                FROM [dbo].[ParameterSetVersion]
                WHERE [ParameterSetId] = p.[Id]
                ORDER BY [Id] DESC
            ) v
            WHERE 1 = 1");

        var parameters = new DynamicParameters();
        if (activeOnly.HasValue)
        {
            sql.Append(" AND p.[IsActive] = @IsActive");
            parameters.Add("IsActive", activeOnly.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND (p.[ParameterSetCode] LIKE @Keyword OR p.[ParameterSetName] LIKE @Keyword)");
            parameters.Add("Keyword", $"%{keyword.Trim()}%");
        }

        sql.Append(" ORDER BY p.[Id] DESC");

        if (skip.HasValue)
        {
            sql.Append(" OFFSET @Skip ROWS");
            parameters.Add("Skip", skip.Value);
        }

        if (take.HasValue)
        {
            sql.Append(" FETCH NEXT @Take ROWS ONLY");
            parameters.Add("Take", take.Value);
        }

        var results = await _connectionManager.QueryAsync<ParameterSetListItemDto>(
            sql.ToString(), parameters, db: DatabaseId.APS);

        return results.ToList();
    }
}
