namespace LPS.APS.Core.Dto;

/// <summary>
/// 订单状态汇总DTO（5号位提供给4号位 Order.vue 顶部 KPI）
/// </summary>
public sealed class OrderSummaryDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>总订单数</summary>
    public int TotalCount { get; init; }

    /// <summary>按时交付（<c>DelayStatus = 'ON_TIME'</c>；NULL = 未计算，不计入按期）</summary>
    public int OnTimeCount { get; init; }

    /// <summary>已延期（<c>DelayStatus IN ('FIRST_DELAY','REPEATED_DELAY')</c>；V1 简化：超期一律 FIRST_DELAY）</summary>
    public int DelayedCount { get; init; }

    /// <summary>有风险（取自 <c>OrderScheduleSummary.RiskLevel IN ('AT_RISK','DELAYED')</c>，非 <c>[Order].DelayStatus</c>）</summary>
    public int RiskCount { get; init; }

    /// <summary>未排程：无权威字段口径，当前恒 0（<c>Status='UNSCHEDULED'</c> 不在 <c>[Order].Status</c> 值域内）</summary>
    public int UnscheduledCount { get; init; }
}
