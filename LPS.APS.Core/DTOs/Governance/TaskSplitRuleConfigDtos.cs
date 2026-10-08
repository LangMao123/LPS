namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 执行批拆分规则（Batch Policy / 批量策略）治理投影——供 4号位 配置页面列表 / 维护展示。
/// 0号位 2026-10-07 裁决「本轮落码」：3号位 治理 CRUD，1号位 经 FrozenStrategySnapshot.BatchPolicy 消费。
/// 正式匹配粒度 = Material + ProductionDepartment（ProductionDepartmentId NULL = Material 级默认）。
/// 兼容字段（ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity）仅历史兼容，不参与 V1 正式匹配、不投快照。
/// </summary>
public sealed class TaskSplitRuleConfigDto
{
    public int Id { get; init; }
    public int MaterialId { get; init; }
    public int? ProductionDepartmentId { get; init; }
    public decimal? MinExecutionBatchQty { get; init; }
    public decimal? MaxExecutionBatchQty { get; init; }
    public decimal? PreferredBatchQty { get; init; }
    public bool AllowSplit { get; init; }
    public bool AllowMerge { get; init; }
    public int? MaxOptimizationSplitCount { get; init; }
    public int? MaxBatchCandidates { get; init; }
    public string? BottleneckSplitStrategy { get; init; }
    public string? NonBottleneckStrategy { get; init; }
    public bool IsActive { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 新增/编辑执行批拆分规则请求（治理侧直维护；PUT 整对象替换，不改 IsActive）。
/// 粒度主键 = MaterialId + ProductionDepartmentId（NULL 部门 = Material 级默认，同一物料最多一条 NULL 部门规则）。
/// </summary>
public sealed class SaveTaskSplitRuleConfigRequest
{
    public int MaterialId { get; init; }
    public int? ProductionDepartmentId { get; init; }
    public decimal? MinExecutionBatchQty { get; init; }
    public decimal? MaxExecutionBatchQty { get; init; }
    public decimal? PreferredBatchQty { get; init; }
    public bool? AllowSplit { get; init; }
    public bool? AllowMerge { get; init; }
    public int? MaxOptimizationSplitCount { get; init; }
    public int? MaxBatchCandidates { get; init; }
    public string? BottleneckSplitStrategy { get; init; }
    public string? NonBottleneckStrategy { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
}