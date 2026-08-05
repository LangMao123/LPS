namespace LPS.APS.Core.Dto;

/// <summary>
/// 2号位从 ScheduleContext 裁剪后传给1号位的纯内存请求。
/// 不含 SupplyPool、BOM 原始快照、Ledger、PSA 或任何数据库对象。
/// </summary>
public sealed class DomainSolveRequest
{
    public int PlanVersionId { get; init; }
    public string DomainKey { get; init; } = string.Empty;
    public DateTime PlanningStart { get; init; }
    public DateTime PlanningEnd { get; init; }

    public IReadOnlyList<TaskDraft> TaskDrafts { get; init; }
        = Array.Empty<TaskDraft>();

    public IReadOnlyList<TaskDependencyDraft> Dependencies { get; init; }
        = Array.Empty<TaskDependencyDraft>();

    public IReadOnlyList<ResourceDefinition> Resources { get; init; }
        = Array.Empty<ResourceDefinition>();

    public IReadOnlyList<ResourceCalendarSlot> CalendarSlots { get; init; }
        = Array.Empty<ResourceCalendarSlot>();

    public IReadOnlyList<ResourceEligibilityDefinition> ResourceEligibility { get; init; }
        = Array.Empty<ResourceEligibilityDefinition>();

    public IReadOnlyList<ExecutionConstraint> ExecutionConstraints { get; init; }
        = Array.Empty<ExecutionConstraint>();

    public FiniteCapacityParameters Parameters { get; init; } = new();
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
    public string ConstraintType { get; init; } = string.Empty; // HARD_LOCK | EXECUTION_LOCK
}

public sealed class FiniteCapacityParameters
{
    public bool AllowSplit { get; init; } = false;
    public bool AllowMerge { get; init; } = false;
    public int MaxIterations { get; init; } = 1000;
    public string SchedulingDirection { get; init; } = "BACKWARD"; // FORWARD | BACKWARD
}
