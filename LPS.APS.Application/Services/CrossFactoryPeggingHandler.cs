using LPS.APS.Core.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 跨厂Pegging处理器（2号位职责 — 消费5号位的跨厂供给事实）
///
/// 职责边界：
/// - 5号位负责计算跨厂Transit/Received事实（数量、可用时间、去重）
/// - 2号位负责Pegging消费和Quantity-Time传播
///
/// PM冻结口径两类跨厂订单：
/// 1. STAGE_HANDOFF: 同SH的多段消费（Transit → Received → 未生产）
/// 2. INTER_FACTORY_ORDER: 跨厂采购单（上游完成时间 + LT = 下游可用时间）
/// </summary>
public sealed class CrossFactoryPeggingHandler
{
    private readonly ILogger<CrossFactoryPeggingHandler> _logger;

    public CrossFactoryPeggingHandler(ILogger<CrossFactoryPeggingHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 消费同一Stage Handoff的跨厂供给（防止重复计数）
    ///
    /// PM冻结口径：
    /// - SH内部Transit、Received、未生产属于同一SH履行状态
    /// - 不能拆成多个外部Supply重复入池
    /// - 按顺序消费：Transit → Received → 未生产份额
    /// </summary>
    public StageHandoffConsumption ConsumeStageHandoff(
        string stageHandoffNo,
        decimal shRemainingQty,
        IEnumerable<SupplyFact> transitSupplies,
        IEnumerable<SupplyFact> receivedSupplies)
    {
        var remaining = shRemainingQty;

        // 1. 消费同SH的Transit
        var transitQty = transitSupplies
            .Where(s => s.SourceKey == stageHandoffNo)
            .Sum(s => s.AvailableQuantity);

        var consumedTransit = Math.Min(remaining, transitQty);
        remaining -= consumedTransit;

        // 2. 消费同SH的Received
        var receivedQty = receivedSupplies
            .Where(s => s.SourceKey == stageHandoffNo)
            .Sum(s => s.AvailableQuantity);

        var consumedReceived = Math.Min(remaining, receivedQty);
        remaining -= consumedReceived;

        // 3. 剩余部分 = SH未生产份额（触发上游工厂生产Demand）
        var unproducedQty = remaining;

        _logger.LogInformation(
            "Stage Handoff {SH} consumption: Total={Total}, Transit={Transit}, Received={Received}, Unproduced={Unproduced}",
            stageHandoffNo, shRemainingQty, consumedTransit, consumedReceived, unproducedQty);

        return new StageHandoffConsumption
        {
            StageHandoffNo = stageHandoffNo,
            TotalRemainingQty = shRemainingQty,
            ConsumedTransitQty = consumedTransit,
            ConsumedReceivedQty = consumedReceived,
            UnproducedQty = unproducedQty
        };
    }

    /// <summary>
    /// 计算跨厂Supply的下游可用时间
    ///
    /// PM冻结口径：
    /// - 已存在的Transit/Received：直接使用5号位提供的AvailableTime
    /// - 本次Solver刚排出的上游新增生产：1号位FinalTask完成时间 + 5号位提供的LT = 下游AvailableTime
    /// </summary>
    public DateTime CalculateDownstreamAvailableTime(
        DateTime upstreamCompletionTime,
        CrossFactoryLeadTime leadTime)
    {
        // INTEGRATION TODO: 5号位应提供标准化的跨厂LT（TransportLT + InspectionLT + TransferLT）
        // 当前使用简化计算
        var totalLeadTimeDays = leadTime.TransportDays + leadTime.InspectionDays + leadTime.TransferDays;
        return upstreamCompletionTime.AddDays(totalLeadTimeDays);
    }

    /// <summary>
    /// 去重检查（防止Transit和Received重复计数）
    ///
    /// PM冻结口径：
    /// - Transit/Received自身按PhysicalSourceKey、SourceDocument、SourceLine去重
    /// - 防止一批货到货后同时还留在Transit里
    /// </summary>
    public List<SupplyFact> DeduplicateCrossFactorySupplies(IEnumerable<SupplyFact> supplies)
    {
        var deduplicated = supplies
            .GroupBy(s => new
            {
                s.SourceKey,
                s.MaterialId,
                s.FactoryId,
                PhysicalKey = $"{s.SourceKey}_{s.MaterialId}_{s.FactoryId}"
            })
            .Select(g =>
            {
                // 优先取Received（已到货），其次Transit（在途）
                var received = g.FirstOrDefault(s => s.SupplyType?.Contains("RECEIVED", StringComparison.OrdinalIgnoreCase) == true);
                if (received != null)
                {
                    return received;
                }

                var transit = g.FirstOrDefault(s => s.SupplyType?.Contains("TRANSIT", StringComparison.OrdinalIgnoreCase) == true);
                if (transit != null)
                {
                    return transit;
                }

                return g.First();
            })
            .ToList();

        if (deduplicated.Count < supplies.Count())
        {
            _logger.LogInformation(
                "Deduplicated cross-factory supplies: {Original} → {Deduplicated}",
                supplies.Count(), deduplicated.Count);
        }

        return deduplicated;
    }
}

/// <summary>
/// Stage Handoff消费结果
/// </summary>
public sealed class StageHandoffConsumption
{
    public string StageHandoffNo { get; init; } = default!;
    public decimal TotalRemainingQty { get; init; }
    public decimal ConsumedTransitQty { get; init; }
    public decimal ConsumedReceivedQty { get; init; }
    public decimal UnproducedQty { get; init; }
}

/// <summary>
/// 跨厂前置期（INTEGRATION TODO: 应由5号位标准化提供）
/// </summary>
public sealed class CrossFactoryLeadTime
{
    public int TransportDays { get; init; }
    public int InspectionDays { get; init; }
    public int TransferDays { get; init; }
}
