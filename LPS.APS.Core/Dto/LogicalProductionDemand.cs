namespace LPS.APS.Core.Dto;

/// <summary>
/// 逻辑生产需求（V1.2）
/// Pegging阶段形成，交给1号位Solver决定FinalTask
/// 不持久化到数据库，仅运行时内存DTO
/// </summary>
public sealed class LogicalProductionDemand
{
    /// <summary>
    /// 逻辑需求唯一键
    /// </summary>
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 计划版本ID
    /// </summary>
    public long PlanVersionId { get; init; }

    /// <summary>
    /// Domain键
    /// </summary>
    public string DomainKey { get; init; } = string.Empty;

    /// <summary>
    /// 与Pegging Allocation建立追溯
    /// </summary>
    public long AllocationSequence { get; init; }

    /// <summary>
    /// 需求键
    /// </summary>
    public string DemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 订单ID（可选）
    /// </summary>
    public long? OrderId { get; init; }

    /// <summary>
    /// 物料ID
    /// </summary>
    public int MaterialId { get; init; }

    /// <summary>
    /// 工厂ID
    /// </summary>
    public int FactoryId { get; init; }

    /// <summary>
    /// 从哪里开始继续生产（大工艺阶段码，机加工/氧化级，对应 RoutingOperation.StageCode）。
    /// 2号位在 PeggingLoop 后据 Routing 有向图「无入边源结点」回填（原 init 改为 set 以支持回填，见 PeggingOrchestrator.FillStartStageCodes）。
    /// </summary>
    public string StartStageCode { get; set; } = string.Empty;

    /// <summary>
    /// 续排起点工序码（工序级，比 StartStageCode 更细，对应 5号位 的 NextOperation/StartOperation）
    /// 2号位 Pegging 不扩展 Operation，此字段由 5号位 按执行进度交付；null = 尚无工序级起点（新单从第一道工序起 / 未接 5号位交付）。
    /// </summary>
    public string? StartOperationCode { get; init; }

    /// <summary>
    /// 净产出数量
    /// </summary>
    public decimal NetOutputQty { get; init; }

    /// <summary>
    /// 计划加工数量
    /// </summary>
    public decimal PlannedProcessQty { get; init; }

    /// <summary>
    /// 数量单位（P1-08 方案a）：来自需求侧订单 Order.UOM，2号位装载时透传；
    /// 1号位 FinalTaskDraft.UOM 据此原样回填，2号位落盘不再反查订单补 UOM。null = 无单位来源（旧订单/无 Order 场景）。
    /// </summary>
    public string? UOM { get; init; }

    /// <summary>
    /// 下游要求的可用时间
    /// </summary>
    public DateTime RequiredAvailableTime { get; init; }

    /// <summary>
    /// 已按冻结规则排好的业务顺序
    /// 不是全局PriorityScore，而是计算层→Priority Segment→段内排序的结果
    /// </summary>
    public int DemandSequence { get; init; }

    /// <summary>
    /// 生产指示号（PI类需求使用）
    /// </summary>
    public string? ProductionInstructionNo { get; init; }

    /// <summary>
    /// 是否未定位（PI Position为UNLOCATED）
    /// </summary>
    public bool IsUnlocated { get; init; }

    /// <summary>
    /// 软偏好资源（P1-11）：上一 ACTIVE Task 的 ResourceId。非硬锁——1号位在合法资源集内优先尝试，
    /// 不满足则回落其它合法资源；硬锁走 ExecutionConstraint。null = 无上一 ACTIVE 资源偏好（自由/新单）。
    /// </summary>
    public int? PreferredResourceId { get; init; }

    /// <summary>
    /// 备选软偏好资源（P1-11）：PreferredResourceId 不可用时的次优偏好。null = 无。
    /// </summary>
    public int? FallbackResourceId { get; init; }

    /// <summary>
    /// 是否连续份额（跨版本连续性的输入标记）。
    /// true        = 逐工单连续份额（B类 已有APS Task连续 / C类 无TaskNo外部MES连续），
    ///               1号位 Solver 走「不拆合(P0-07) + 连续先行/单活跃资源(P0-08)」语义；
    /// false/缺省  = 普通自由需求（A类硬约束 / D类自由），行为不变。
    /// 连续份额的身份（旧TaskNo / 旧MES工单）复用 LogicalDemandKey 承载，不另设 ContinuationKey 字段。
    /// </summary>
    public bool IsContinuation { get; init; }
}
