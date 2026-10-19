using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// Overview查询Repository接口
/// </summary>
public interface IOverviewQueryRepository
{
    /// <summary>
    /// 查询当前ACTIVE计划版本信息（R1-a：按 Domain 列表，每 Domain 至多一条）
    /// </summary>
    Task<List<OverviewActivePlanDto>> GetActivePlanAsync(
        string? domainKey = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null);

    /// <summary>
    /// 查询资源负荷/Bottleneck
    /// </summary>
    Task<List<OverviewResourceBottleneckDto>> GetResourceBottleneckAsync(
        int planVersionId,
        int topN = 10,
        CancellationToken ct = default);

    /// <summary>
    /// 查询Candidate待处理摘要
    /// </summary>
    Task<OverviewCandidateSummaryDto> GetCandidateSummaryAsync(
        CancellationToken ct = default);
}
