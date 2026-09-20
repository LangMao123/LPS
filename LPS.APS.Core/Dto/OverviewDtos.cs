namespace LPS.APS.Core.Dto;

/// <summary>
/// Overview - 当前ACTIVE计划版本信息
/// </summary>
public sealed class OverviewActivePlanDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>版本号</summary>
    public string VersionCode { get; init; } = string.Empty;

    /// <summary>DomainKey</summary>
    public string? DomainKey { get; init; }

    /// <summary>计划范围开始</summary>
    public DateTime PlanHorizonStart { get; init; }

    /// <summary>计划范围结束</summary>
    public DateTime PlanHorizonEnd { get; init; }

    /// <summary>激活时间</summary>
    public DateTime? ActivatedAt { get; init; }

    /// <summary>来源ScheduleRunId</summary>
    public int? SourceScheduleRunId { get; init; }

    /// <summary>总任务数</summary>
    public int TotalTasks { get; init; }

    /// <summary>总订单数</summary>
    public int TotalOrders { get; init; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Overview - 任务状态摘要
/// </summary>
public sealed class OverviewTaskSummaryDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>总任务数</summary>
    public int TotalTasks { get; init; }

    /// <summary>On-time（按时完成）</summary>
    public int OnTimeCount { get; init; }

    /// <summary>Delayed（延期）</summary>
    public int DelayedCount { get; init; }

    /// <summary>Risk（风险）</summary>
    public int RiskCount { get; init; }

    /// <summary>Unscheduled（未排程）</summary>
    public int UnscheduledCount { get; init; }

    /// <summary>Estimated-only（仅估算，非正式承诺）</summary>
    public int EstimatedOnlyCount { get; init; }

    /// <summary>其他状态</summary>
    public int OtherCount { get; init; }
}

/// <summary>
/// Overview - 资源负荷/Bottleneck
/// </summary>
public sealed class OverviewResourceBottleneckDto
{
    /// <summary>资源ID</summary>
    public int ResourceId { get; init; }

    /// <summary>资源编码</summary>
    public string ResourceCode { get; init; } = string.Empty;

    /// <summary>资源名称</summary>
    public string ResourceName { get; init; } = string.Empty;

    /// <summary>资源类型</summary>
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>分配任务数</summary>
    public int TaskCount { get; init; }

    /// <summary>总计划工时（小时）</summary>
    public decimal TotalPlannedHours { get; init; }

    /// <summary>负荷率（实际工时/可用工时，>1为过载）</summary>
    public decimal? UtilizationRate { get; init; }

    /// <summary>是否Bottleneck</summary>
    public bool IsBottleneck { get; init; }
}

/// <summary>
/// Overview - Candidate待处理摘要
/// </summary>
public sealed class OverviewCandidateSummaryDto
{
    /// <summary>待处理Candidate数量</summary>
    public int PendingCount { get; init; }

    /// <summary>Candidate列表</summary>
    public List<CandidateBriefDto> Candidates { get; init; } = new();
}

/// <summary>
/// Candidate简要信息（candidate-summary 列表项）
/// 补齐 4号位 §十二 17 字段中 5号位可算部分；需落盘真值字段（Estimated/跨域）在 0号位 裁决返回 null/占位
/// </summary>
public sealed class CandidateBriefDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>版本号</summary>
    public string VersionCode { get; init; } = string.Empty;

    /// <summary>DomainKey</summary>
    public string? DomainKey { get; init; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>创建人</summary>
    public string? CreatedByUserName { get; init; }

    /// <summary>候选版本状态（PlanVersion.Status）</summary>
    public string? Status { get; init; }

    /// <summary>候选来源 ScheduleRunId</summary>
    public int? SourceScheduleRunId { get; init; }

    /// <summary>候选来源 RunType</summary>
    public string? RunType { get; init; }

    /// <summary>候选冻结的 Base 计划版本（P0-04：ScheduleRun.BasePlanVersionId 权威落盘）</summary>
    public int? BasePlanVersionId { get; init; }

    /// <summary>Base 是否仍为当前 Domain ACTIVE（§12.3 激活红线前置校验）</summary>
    public bool CanActivate { get; init; }

    // ---- Base vs Candidate 完成时间差（单订单最大完工时间的版本均值）----

    /// <summary>Base 平均完成时间（AVG per-Order MAX(PlannedEndTime)）</summary>
    public DateTime? BaseAvgCompletionTime { get; init; }

    /// <summary>Candidate 平均完成时间</summary>
    public DateTime? CandidateAvgCompletionTime { get; init; }

    /// <summary>完成时间差（小时；Candidate - Base）</summary>
    public decimal? AvgDeltaHours { get; init; }

    // ---- Task 增删移 / Resource 变化（复用 comparison 差集 SQL）----

    /// <summary>新增任务数（候选存在、基础不存在）</summary>
    public int TaskAddedCount { get; init; }

    /// <summary>移除任务数（基础存在、候选不存在）</summary>
    public int TaskRemovedCount { get; init; }

    /// <summary>时间平移任务数（同主键且起止时间任一变化）</summary>
    public int TaskTimeShiftedCount { get; init; }

    /// <summary>资源变更任务数（同主键且资源变化）</summary>
    public int TaskResourceChangedCount { get; init; }

    /// <summary>受影响订单数（发生增删移/资源变更任务的去重订单数）</summary>
    public int ImpactedOrderCount { get; init; }

    /// <summary>新增延期数（候选延期且基础同主键任务未延期的任务数）</summary>
    public int NewDelayCount { get; init; }

    // ---- 需落盘真值（0号位 裁决前占位；归 2号位/1号位，5号位不重算）----

    /// <summary>估算供给订单数（⚠️ 2号位 pegging 落盘 Estimated 标志后接入；裁决前 null）</summary>
    public int? EstimatedOnlyCount { get; init; }

    /// <summary>跨域影响订单数（⚠️ 未落盘，归 2号位/1号位；裁决前 null）</summary>
    public int? CrossDomainImpacted { get; init; }

    /// <summary>跨域阻挡任务数（⚠️ 未落盘，归 1号位/2号位；裁决前 null）</summary>
    public int? CrossDomainBlockedCount { get; init; }
}
