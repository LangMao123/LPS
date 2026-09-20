using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Dto;

/// <summary>
/// 2号位从 ScheduleContext 裁剪后传给1号位的纯内存请求。
/// 不含 SupplyPool、BOM 原始快照、Ledger、PSA 或任何数据库对象。
/// 符合1↔2接口冻结文档 v1.0_20260814 §2.3 九类输入要求
/// </summary>
public sealed class DomainSolveRequest
{
    public long? ScheduleRunId { get; init; }
    public int PlanVersionId { get; init; }
    public string DomainKey { get; init; } = string.Empty;
    public DateTime? DataCutoffTime { get; init; }
    public DateTime PlanningStart { get; init; }
    public DateTime PlanningEnd { get; init; }

    public IReadOnlyList<LogicalProductionDemand> LogicalProductionDemands { get; init; }
        = Array.Empty<LogicalProductionDemand>();

    /// <summary>
    /// 多层 BOM「任务喂任务」血缘输入（PM 2026-09-10 裁决 R2）：父 LogicalProductionDemand → 子 LogicalProductionDemand
    /// 运行时关系。1号位据此 + 拆批/合批 + Routing 生成真实 TaskDependency（FinalTaskPeggingDraft）。
    /// 子件全库存 / 全 PI 时无子 NEW_REQUIREMENT，不产对应 link。
    /// </summary>
    public IReadOnlyList<MaterialRequirementLink> MaterialRequirementLinks { get; init; }
        = Array.Empty<MaterialRequirementLink>();

    public IReadOnlyList<AllocationLineage> AllocationLineage { get; init; }
        = Array.Empty<AllocationLineage>();

    public IReadOnlyList<RoutingOperation> RoutingOperations { get; init; }
        = Array.Empty<RoutingOperation>();

    public IReadOnlyList<RoutingDependency> RoutingDependencies { get; init; }
        = Array.Empty<RoutingDependency>();

    public IReadOnlyList<OperationResourceEligibility> OperationResourceEligibility { get; init; }
        = Array.Empty<OperationResourceEligibility>();

    /// <summary>
    /// 物料×阶段→默认生产部门 上下文（PM 裁定：最小 B）。
    /// 2号位裁剪当前 Domain 涉及的 (MaterialId, StageCode) 传入；1号位按 (MaterialId, StageCode)
    /// 锁 ProductionDepartmentId 后过滤 Routing 三件套，不得重新推导部门。
    /// </summary>
    public IReadOnlyList<MaterialStageDepartmentContextDto> MaterialStageDepartmentContexts { get; init; }
        = Array.Empty<MaterialStageDepartmentContextDto>();

    public IReadOnlyList<MaterialAvailabilitySlice> MaterialConstraints { get; init; }
        = Array.Empty<MaterialAvailabilitySlice>();

    public IReadOnlyList<ResourceDefinition> Resources { get; init; }
        = Array.Empty<ResourceDefinition>();

    public IReadOnlyList<ResourceCalendarSlot> CalendarSlots { get; init; }
        = Array.Empty<ResourceCalendarSlot>();

    public IReadOnlyList<ResourceEligibilityDefinition> ResourceEligibility { get; init; }
        = Array.Empty<ResourceEligibilityDefinition>();

    public IReadOnlyList<ExecutionConstraint> ExecutionConstraints { get; init; }
        = Array.Empty<ExecutionConstraint>();

    public SolverStrategySnapshot StrategySnapshot { get; init; } = new();

    public CandidateContext? CandidateContext { get; init; }

    /// <summary>
    /// 前序 Domain 成功后的共享 Resource 占用块（FULL §9）。
    /// 1号位将其作为不可用时间窗阻挡后续 Domain 在真实共享 Resource 上重叠占用。
    /// Candidate 的对应物是 CandidateContext.ExternalDomainResourceBlocks（§11）；本字段 FULL 专用、Candidate 时为 null。
    /// </summary>
    public IReadOnlyList<ResourceBlock> UpstreamDomainResourceBlocks { get; init; }
        = Array.Empty<ResourceBlock>();
}

/// <summary>
/// Pegging Allocation到FinalTask的追溯信息（接口冻结§2.3第3类）
/// 不等同于PeggingSupplyAllocation持久化表
/// </summary>
public sealed class AllocationLineage
{
    public long AllocationSequence { get; init; }
    public string DemandKey { get; init; } = string.Empty;
    public int MaterialId { get; init; }
    public string SupplyType { get; init; } = string.Empty;
    public string SupplyKey { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public DateTime? AvailableTime { get; init; }
}

/// <summary>
/// 某个逻辑生产需求的材料，在什么时间有多少数量真正可用（接口冻结§2.3第6类）
/// 必须支持多段Quantity-Time：40件15日+60件17日，不能压成100件17日
/// </summary>
public sealed class MaterialAvailabilitySlice
{
    public long AllocationSequence { get; init; }
    public int MaterialId { get; init; }
    public int FactoryId { get; init; }
    public decimal Quantity { get; init; }
    public DateTime AvailableTime { get; init; }
    public string? SourceType { get; init; }
    public string? SourceKey { get; init; }
    public string? Commitment { get; init; }
    public string? Confidence { get; init; }
}

/// <summary>
/// 一次ScheduleRun冻结给1号位使用的Solver参数包（接口冻结§2.3第7类）
/// 1号位不需要Demand排序、库存规则等，那些已由2号位执行完成
/// </summary>
public sealed class SolverStrategySnapshot
{
    public long? StrategyProfileVersionId { get; init; }
    public long? ParameterSetVersionId { get; init; }

    /// <summary>已激活消费点用的裁剪参数（执行用子集：AllowSplit/AllowMerge/SchedulingDirection + P1-02 B 组）。</summary>
    public FiniteCapacityParameters Parameters { get; init; } = new();

    // ── P1-02：⑤⑥ 全字段整块透传（PM/2号位 2026-09-14 拍板：整块引用，避免同 ⑤⑥ 逐批平铺）──
    /// <summary>P1-02：⑤ Solver 策略整块（Mode/Bottleneck/OnTimeTarget/Split/Setup/StageOverlap/AllowMerge）。<br/>
    /// 1号位从整块读取正式参数（瓶颈阈值 Bottleneck/OnTimeTarget/Split原始值/Setup/StageOverlap），
    /// 替换换不了的硬编码。强类型零漂移，2号位不再逐批投影。</summary>
    public SolverStrategyBlock SolverStrategy { get; init; } = new();

    /// <summary>P1-02：⑥ Candidate 技术 Guardrail 整块（限时/传播/警告/TopN/拆分候选）。<br/>
    /// 1号位从整块读取（NormalMs/SoftMs/LocalHardMs/MaxRepairAttempts/MaxPropagationRounds/ResourceTopN/WarnOnlyOnMaxImpacted）。
    /// 强类型零漂移。</summary>
    public CandidateGuardrailBlock CandidateGuardrail { get; init; } = new();
}

/// <summary>
/// Candidate Run专用上下文（接口冻结§2.3第9类）
/// FULL Run时为null
/// </summary>
public sealed class CandidateContext
{
    /// <summary>
    /// Base 稳定锚点 = ScheduleRun.BasePlanVersionId（创建 Run 时由 3号位冻结的当前 ACTIVE PlanVersion）。
    /// PM 2026-09-07 P0-04 2.1：Candidate 前后比较基线，2号位运行期必须始终用此值，不得中途再查「此刻最新 ACTIVE」。
    /// </summary>
    public int? BasePlanVersionId { get; init; }

    /// <summary>变化 Seed：Candidate Pegging 相对 Base ACTIVE 发生变化的逻辑生产需求键（DemandKey/AllocationSequence/LogicalDemandKey 之一）</summary>
    public IReadOnlyList<string> ChangeSeedKeys { get; init; } = Array.Empty<string>();

    /// <summary>其它 Domain 当前 ACTIVE 在共享 Resource 上的不可移动占用（PM 0907：不是 Quantity-Time）</summary>
    public IReadOnlyList<ResourceBlock> ExternalDomainResourceBlocks { get; init; } = Array.Empty<ResourceBlock>();
}

/// <summary>
/// 其它Domain ACTIVE共享资源占用的不可用时间窗
/// PM 2026-09-07 P0-04：Candidate 的外部 Domain 阻挡块必须携带来源域/版本/不可移动语义，
/// 与 FULL 的 UpstreamDomainResourceBlocks 共用本结构（FULL 时 SourceDomainKey/SourcePlanVersionId 可空）。
/// </summary>
public sealed class ResourceBlock
{
    public int ResourceId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string Reason { get; init; } = string.Empty;

    /// <summary>来源 DomainKey（外部 ACTIVE 阻挡块的归属域；FULL 前序 Domain 时也有值）</summary>
    public string? SourceDomainKey { get; init; }

    /// <summary>来源 PlanVersionId（外部 ACTIVE 阻挡块的归属版本）</summary>
    public int? SourcePlanVersionId { get; init; }

    /// <summary>不可移动标记（PM 0907：Candidate 外 Domain 阻挡块 Immutable=true，1号位不得挤动）</summary>
    public bool Immutable { get; init; } = true;
}

/// <summary>Task 间依赖意图（排程前保留，用于排程后生成 PhysicalPeggingDraft）</summary>
public sealed class TaskDependencyDraft
{
    public string FromDraftId { get; init; } = string.Empty;
    public string ToDraftId { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public long AllocationSequence { get; init; }
}

public sealed class ResourceDefinition
{
    public int ResourceId { get; init; }
    public string ResourceCode { get; init; } = string.Empty;
    public string FactoryCode { get; init; } = string.Empty;
    public decimal Capacity { get; init; }
}

public sealed class ResourceCalendarSlot
{
    public int ResourceId { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool IsAvailable { get; init; }
}

public sealed class ResourceEligibilityDefinition
{
    public int ResourceId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string RouteKey { get; init; } = string.Empty;
    public int Priority { get; init; }
}

public sealed class ExecutionConstraint
{
    public string DraftId { get; init; } = string.Empty;
    public int ResourceId { get; init; }
    public DateTime LockedStart { get; init; }
    public DateTime LockedEnd { get; init; }
    public string ConstraintType { get; init; } = string.Empty;

    // 第4轮Anchor补充：Stage/Operation信息，用于原地继承锁定Task
    public string? StageCode { get; init; }
    public string? OperationCode { get; init; }

    // 第4轮Anchor补充：锁定数量，原地继承该份额，只排剩余可移动份额
    public decimal? LockedQuantity { get; init; }

    // P1-01：净合格数量（锁定 Task 的净产出，YIELD 场景 != 产能加工数量）
    public decimal? LockedNetOutputQty { get; init; }

    // P1-01：产能加工数量（锁定 Task 的计划加工量）
    public decimal? LockedPlannedProcessQty { get; init; }

    // 第4轮Anchor补充：稳定TaskKey，用于跨轮次识别同一Task
    public string? TaskKey { get; init; }
}

public sealed class FiniteCapacityParameters
{
    public bool AllowSplit { get; init; } = false;
    public bool AllowMerge { get; init; } = false;
    public string SchedulingDirection { get; init; } = "BACKWARD";

    // ── P1-02 B 组 live 硬编码（3号位 已冻结、2号位 投影；字段先行、1号位 换读后逐项激活）──
    public int ImpactedTaskWarningPercent { get; init; } = 30;   // 传播警戒（PhaseFourLocalRepair maxAffectedRatio）
    public int MaxPropagationRounds { get; init; } = 10;          // 传播轮数（PhaseFourLocalRepair maxPropagationRounds）
    public int SplitAlternatives { get; init; } = 3;              // Split 候选（PhaseFourLocalRepair {2,3}）
    public decimal MinBatchQty { get; init; } = 0.1m;             // 拆分下限（PhaseFourLocalRepair qtyPerSplit<0.1）
}

/// <summary>
/// SolverStrategyMode ↔ SchedulingDirection 字符串固定映射（P1-02 §五-3：1↔2 契约正式化）。
/// 由 2号位 在投影处唯一使用；1号位 消费 SchedulingDirection 字符串（PhaseTwoInitialScheduler）。
/// </summary>
public static class SolverStrategyModeMap
{
    public static string ToDirection(SolverStrategyMode mode) => mode switch
    {
        SolverStrategyMode.Forward  => "FORWARD",
        SolverStrategyMode.Backward => "BACKWARD",
        SolverStrategyMode.Mixed    => "MIXED",
        _                            => "BACKWARD"    // 防御未知枚举，等效 Backward（与历史行为一致）
    };
}
