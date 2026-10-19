using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// 订单查询业务服务
///
/// 5号位提供给4号位的订单/需求计划查询
/// 直接读取APS_Production事实表，不重算Pegging
/// </summary>
public class OrderQueryService
{
    private readonly IOrderQueryRepository _repository;

    public OrderQueryService(IOrderQueryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 查询订单列表
    /// </summary>
    public async Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
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
        IReadOnlySet<string>? allowedDomains = null)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        // take 越界不再静默回落 50（同类静默错数据；4号位 10-17 R3 在 PI 端点发现）。
        // 非正数 → 默认 50；超上限 → 夹到 200。
        if (take <= 0) take = 50;
        if (take > 200) take = 200;

        return await _repository.QueryOrdersAsync(
            planVersionId, orderNo, materialCode, customerName,
            factoryCode, domainKey, delayStatus, status,
            productionInstructionNo, productFamilyCode, dueDateFrom, dueDateTo,
            skip, take, ct, allowedFactories, allowedDomains);
    }

    /// <summary>
    /// 查询订单详情（含Pegging和生产计划）
    /// </summary>
    public async Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (orderId <= 0) throw new ArgumentException("orderId is required");

        return await _repository.GetOrderDetailAsync(planVersionId, orderId, ct);
    }

    /// <summary>
    /// 查询订单状态汇总（按 DelayStatus 聚合）
    /// </summary>
    public async Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");

        return await _repository.GetSummaryAsync(planVersionId, allowedFactories, allowedDomains, ct);
    }
}
