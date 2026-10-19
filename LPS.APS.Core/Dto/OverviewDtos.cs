namespace LPS.APS.Core.Dto;

/// <summary>
/// Overview - 当前ACTIVE计划版本信息（按 Domain 一行；R1-a：列表元素）
/// </summary>
public sealed class OverviewActivePlanDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>版本号</summary>
    public string VersionCode { get; init; } = string.Empty;

    /// <summary>DomainKey</summary>
    public string? DomainKey { get; init; }

    /// <summary>
    /// 版本生命周期状态（R1-a 补充字段）
    /// 词表以冻结文档为准：BUILDING / CANDIDATE / ACTIVE / ARCHIVED / FAILED（+ 实现子状态 Computing）
    /// 本端点只返 Status='ACTIVE' 的行，该字段供前端徽章渲染（ACTIVE = 已生效/已采用）
    /// </summary>
    public string Status { get; init; } = string.Empty;

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

    /// <summary>
    /// 是否瓶颈。<c>true</c>=判定为瓶颈；<c>false</c>=判定为非瓶颈；<c>null</c>=未判定。
    /// ⚠️ Bottleneck 判定应由 2号位 正式 Query 提供，5号位不做二次计算；当前无数据源 ⇒
    /// SQL 返回 <c>CAST(NULL AS BIT)</c>，叠加 WhenWritingNull ⇒ **键不出现**（与 UtilizationRate 同型，诚实表达"未判定"）。
    /// 原 `bool` + `0 AS IsBottleneck` 会把"未判定"谎报成"判定为否"（4号位 10-19 C2-a，同 MesEligible 修法）。
    /// C2-b（availableHours/plannedHours/loadRate 完整负荷率契约）待 Calendar 事实源排期。JSON: isBottleneck
    /// </summary>
    public bool? IsBottleneck { get; init; }

    /// <summary>
    /// 不可用窗口（R6，2026-10-16 定：口径 = **本级 OFF**）
    /// 来源：资源级日历 `ResourceCalendarSlot.AvailableFlag = 0`（该设备明确停机时段），
    /// 限定在该 PlanVersion 的计划范围内。无 OFF 行时为空数组（非 null）。
    /// 注意：**不含**部门级/工厂级的继承 OFF（"三级合并 OFF"口径另议）。
    /// </summary>
    public List<OverviewUnavailableWindowDto> UnavailableWindows { get; init; } = new();
}

/// <summary>
/// Overview - 资源不可用窗口（R6）
/// 形状对齐 4号位 `ResourceSummaryItem.unavailableWindows`（from/to），并附 reason（原窗口 Remark）
/// </summary>
public sealed class OverviewUnavailableWindowDto
{
    /// <summary>窗口开始（含）</summary>
    public DateTime From { get; init; }

    /// <summary>窗口结束（不含）</summary>
    public DateTime To { get; init; }

    /// <summary>不可用原因（ResourceCalendarSlot.Remark，可空）</summary>
    public string? Reason { get; init; }
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
/// Candidate简要信息（candidate-summary 列表项，仅候选元信息）
/// 注意：Base vs Candidate 差集/Diff 统一由 3号位 ScheduleQueryService.GetCandidateComparisonAsync 提供，
/// 5号位 Overview 列表示不重复计算，也不在列表项内承载 diff/平均完成时间等统计字段。
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
}
