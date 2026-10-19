using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 订单查询Repository接口
/// </summary>
public interface IOrderQueryRepository
{
    /// <summary>
    /// 查询订单列表（分页 + total）
    /// </summary>
    Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null);

    /// <summary>
    /// 查询订单详情（含Pegging和生产计划）
    /// </summary>
    Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default);

    /// <summary>
    /// 查询订单状态汇总（按 DelayStatus 聚合）
    /// </summary>
    Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default);
}
