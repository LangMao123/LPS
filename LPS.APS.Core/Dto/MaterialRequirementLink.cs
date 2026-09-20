namespace LPS.APS.Core.Dto;

/// <summary>
/// 多层 BOM「任务喂任务」血缘输入（PM 2026-09-10 裁决 v1.0 落地 R2）。
/// 父 LogicalProductionDemand（消费侧）→ 子 LogicalProductionDemand（生产侧）的运行时关系。
/// 由 2号位 Pegging 遍历 BOM 时产出，随 LogicalProductionDemands 一起透传 1号位；
/// 1号位据此 + 拆批/合批 + Routing 生成真实 TaskDependency（FinalTaskPeggingDraft，只记生产 Task 实际份额）。
/// 子件全库存 / 全 PI 时无子 NEW_REQUIREMENT，不产对应 link（PM Case B）。
/// </summary>
public sealed class MaterialRequirementLink
{
    /// <summary>
    /// 父需求键（消费侧 LogicalProductionDemand.LogicalDemandKey）。
    /// </summary>
    public string ConsumerLogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 子需求键（生产侧 LogicalProductionDemand.LogicalDemandKey）。
    /// </summary>
    public string ProducerLogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 父物料（消费侧）——供 1号位 判别跨物料（R4：UpstreamMaterialId != DownstreamMaterialId）。
    /// </summary>
    public int ConsumerMaterialId { get; init; }

    /// <summary>
    /// 子物料（生产侧）。
    /// </summary>
    public int ProducerMaterialId { get; init; }

    /// <summary>
    /// 完整子需求数量（父对子的总需求 = BOM 展开后的子需求，非子件新增生产缺口量）。
    /// </summary>
    public decimal RequiredQty { get; init; }

    /// <summary>
    /// 子件 NEW_REQUIREMENT 分配的 AllocationSequence（= 子 LogicalProductionDemand.AllocationSequence），1号位零推断。
    /// </summary>
    public long ProducerAllocationSequence { get; init; }

    /// <summary>
    /// 父件消费相对子件完工的滞后时间（分钟，P1-12）。
    /// 同 Domain 普通 BOM 父-子 = 0（Consumer floor = Producer completion）；
    /// 跨厂边 = InterFactoryLT（3号位 CrossFactoryLeadTime 三元组 TransportDays/InspectionDays/TransferDays 天数 × 1440，
    /// 由 2号位装载时换算）。与输出侧 FinalTaskPeggingDraft.LagTime 同单位（分钟）。
    /// </summary>
    public decimal LagMinutes { get; init; }
}