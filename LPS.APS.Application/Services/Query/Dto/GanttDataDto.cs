namespace LPS.APS.Application.Services.Query.Dto;

/// <summary>
/// 甘特图数据（前端一次性拉取整个版本的 Task + Resource 明细）
/// </summary>
public class GanttDataDto
{
    public int PlanVersionId { get; set; }
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>
    /// PlanVersion 状态（ACTIVE / CANDIDATE / ARCHIVED）
    ///
    /// v1.5 §16.3：Candidate 结果查询时必须返回，供4号位区分当前版本类型。
    /// </summary>
    public string? PlanVersionStatus { get; set; }

    /// <summary>
    /// Candidate 基准版本 ID（仅 CANDIDATE 状态时有值）
    ///
    /// v1.5 §16.3：Candidate 与 Base 比较所需。
    /// ACTIVE / ARCHIVED 版本为 null。
    /// </summary>
    public int? BasePlanVersionId { get; set; }

    public DateTime PlanHorizonStart { get; set; }
    public DateTime PlanHorizonEnd { get; set; }

    /// <summary>
    /// 资源行（甘特图 Y 轴）
    /// </summary>
    public IReadOnlyList<GanttResourceDto> Resources { get; set; } = Array.Empty<GanttResourceDto>();

    /// <summary>
    /// 任务条（甘特图 X 轴上的矩形）
    /// </summary>
    public IReadOnlyList<GanttTaskDto> Tasks { get; set; } = Array.Empty<GanttTaskDto>();

    /// <summary>
    /// 是否因业务范围（Domain）不足而被拒绝（fail-closed）。
    /// true = 当前用户无该 Domain 授权，返回空是"越权"而非"没数据"。
    /// </summary>
    public bool ScopeDenied { get; set; }

    /// <summary>
    /// 是否为空结果（版本不存在 / 越权 / 该版本确无 Task）。
    /// 与 <see cref="ScopeDenied"/>、<see cref="VersionExists"/> 配合区分三种空态。
    /// </summary>
    public bool IsEmpty { get; set; }

    /// <summary>
    /// 版本是否真实存在。false = 该 planVersionId 不存在（VersionCode 为空串）。
    /// 显式位，替代「<c>versionCode === ''</c>」这种隐式契约（4号位 10-17 §2.3）。
    /// 三种空态：版本不存在(<c>false</c>) / 越权(<c>scopeDenied</c>) / 存在但无任务(全 true + isEmpty)。
    /// </summary>
    public bool VersionExists { get; set; }
}

public class GanttResourceDto
{
    public int ResourceId { get; set; }
    public string ResourceCode { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public int? FactoryId { get; set; }
    public int? ProductionDepartmentId { get; set; }

    /// <summary>
    /// 资源所属域标识（G2-b 档①；U01 多 Domain 染色分组）
    /// F-G4：域不再以 FACTORY_{id} 命名规则推导，资源域 = 所属 PlanVersion.DomainKey（DomainDefinition 唯一权威源）
    /// </summary>
    public string? DomainKey { get; set; }

    /// <summary>
    /// 工厂名称（G2-b 档①；域主数据投影）
    /// </summary>
    public string? FactoryName { get; set; }

    /// <summary>
    /// 生产部门名称（G2-b 档①；域主数据投影）
    /// </summary>
    public string? ProductionDepartmentName { get; set; }

    /// <summary>
    /// 工序阶段标识/名称（G2-b 档①；ProductionDepartment.StageCode，部门 vs 阶段 1:1）
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// 设备不可用窗口（G2-b 档②；依赖排程/根因事实，未就绪返回 null，v1.3 待 2号位 对齐）
    /// </summary>
    public IReadOnlyList<GanttUnavailableWindowDto>? UnavailableWindows { get; set; }
}

public class GanttTaskDto
{
    public long TaskId { get; set; }
    public string TaskNo { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public string? OrderNo { get; set; }
    public int MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string? MaterialName { get; set; }
    public int? ResourceId { get; set; }
    public string OperationCode { get; set; } = string.Empty;
    public int OperationSeq { get; set; }
    public decimal Quantity { get; set; }
    public string UOM { get; set; } = string.Empty;
    public DateTime? PlannedStartTime { get; set; }
    public DateTime? PlannedEndTime { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 是否延期（按**日期**比较：PlannedEndTime 的日期 > CustomerDueDate）。
    /// 注：CustomerDueDate 为 DATE（到天），PlannedEndTime 为 DATETIME2。
    /// 修复前直接比较二者会被隐式提升为"完工时刻 vs 交日零点"，导致**当天完工也被判延期**。
    /// </summary>
    public bool IsDelayed { get; set; }

    /// <summary>
    /// 客户交期（[Order].CustomerDueDate，DATE）。冻结 01:1907 第 2 项要求输出。
    /// </summary>
    public DateTime? CustomerDueDate { get; set; }

    /// <summary>
    /// 延期时长（小时）= PlannedEndTime − CustomerDueDate，仅延期时为正；未延期为 null。
    /// </summary>
    public decimal? DelayHours { get; set; }

    /// <summary>
    /// 是否关键路径（Task.IsCriticalPath，冻结 07:3683 / DDL 08:4249）。
    /// </summary>
    public bool IsCriticalPath { get; set; }

    /// <summary>
    /// 计划工时（Task.Duration 库值，DDL 08:4246）。以库值为准，不用前端时间差反算。
    /// </summary>
    public decimal? Duration { get; set; }

    /// <summary>
    /// 任务所属域标识（G2-b 档①；U01，从 Order.DomainKey 投影）
    /// </summary>
    public string? DomainKey { get; set; }

    /// <summary>
    /// 净需求数量（G2-b 档①；U05，落盘 Task.Quantity = NetOutputQty 净合格产出）
    /// </summary>
    public decimal? NetQty { get; set; }

    /// <summary>
    /// 含良率补偿数量（G2-b 档①；U05 净需求 vs 含良率）
    /// 取 Task.PlannedProcessQty（DDL 08:4242 有定义、2号位 PeggingOrchestrator 落盘）。
    /// 原注释「Task 表当前无此列」有误，已更正（2026-10-17，4号位 R2）。
    /// </summary>
    public decimal? PlannedProcessQty { get; set; }

    /// <summary>
    /// MES 下发资格（G2-b 档①；U16）。
    /// <para><c>true</c>=可下发；<c>false</c>=判定为不可下发；<c>null</c>=未判定（资格判定尚未接入）。</para>
    /// ⚠️ 该字段**不是任何表的列**——数据库无 `MesEligible` 列，当前由查询层硬编码返回，属接口占位。
    /// 原为非可空 <c>bool</c> + 恒 <c>false</c>，会导致「判定为否」与「未判定」在契约上不可区分；
    /// 2026-10-17 改为可空，未接入期统一返回 <c>null</c>（前端按「未接入」展示）。
    /// </summary>
    public bool? MesEligible { get; set; }

    /// <summary>
    /// 不可下发原因（G2-b 档①；U17，硬枚举见 v1.2 §二 2.3）
    /// ⚠️ 未就绪返回 null，待 5号位/2号位 对齐
    /// </summary>
    public IReadOnlyList<string>? MesIneligibleReasons { get; set; }

    /// <summary>
    /// 跨域阻挡标记（G2-b 档①；U14）。
    /// <para><c>true</c>=被阻挡；<c>false</c>=判定为未阻挡；<c>null</c>=未判定（判定未接入）。</para>
    /// ⚠️ 该字段**不是任何表的列**——数据库无 `CrossDomainBlocked` 列，当前由查询层硬编码返回，属接口占位。
    /// 与 <see cref="MesEligible"/> 同因改可空：避免「判定为否」与「未判定」不可区分（2026-10-17）。
    /// </summary>
    public bool? CrossDomainBlocked { get; set; }

    /// <summary>
    /// 跨域阻挡原因（G2-b 档①；复用 G7 域依赖）
    /// ⚠️ 未就绪返回 null，待 2号位 对齐
    /// </summary>
    public string? CrossDomainBlockReason { get; set; }

    /// <summary>
    /// Demand Protection 锁标记（G2-b 档②；U08，未就绪返回 null，v1.3）
    /// </summary>
    public string? LockMarker { get; set; }

    /// <summary>
    /// 多 Order 共担一个 Task 的份额（G2-b 档②；U04，未就绪返回 null，v1.3）
    /// </summary>
    public IReadOnlyList<GanttTaskShareDto>? TaskShares { get; set; }

    /// <summary>
    /// 数量-时间分段（G2-b 档②；U06，40+60 不压成 100，未就绪返回 null，v1.3）
    /// </summary>
    public IReadOnlyList<GanttTaskSegmentDto>? TaskSegments { get; set; }

    /// <summary>
    /// 延期原因明细（G2-b 档②；根因优先，未就绪返回 null，v1.3 待 2号位/1号位 对齐）
    /// </summary>
    public IReadOnlyList<GanttDelayReasonDto>? DelayReasons { get; set; }

    /// <summary>
    /// 事实类型（G2-b 档②；FACT/RESULT/ESTIMATED/RECOMMENDATION，未就绪返回 null，v1.3）
    /// </summary>
    public string? FactType { get; set; }

    /// <summary>
    /// 上游工序阶段阻挡（G2-b 档②；未就绪返回 null，v1.3）
    /// </summary>
    public bool? UpstreamStageBlocked { get; set; }

    /// <summary>
    /// 上游阻挡阶段编码（G2-b 档②；未就绪返回 null，v1.3）
    /// </summary>
    public string? UpstreamStageCode { get; set; }

    /// <summary>
    /// 换型/Setup 来源（R3 SetupSource 输出链；v1.5 §9/§10）
    /// 1号位 产出 → 2号位 原样落库 [Task].[SetupSource] → 本 DTO 透传。
    /// 值契约：0号位 20260922 四态 INITIAL_SETUP_STATE / EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK（nullable string）。
    ///         内部 SetupOutcome 5 值（ExactHit/DefaultHit/SameProductZero/InitialState/RuleMissing）经 SetupOutcomeToSource 归并后落库；SAME_PRODUCT/NONE 不再是正式值。
    /// 用途：甘特每 Task 展示换型标签（EXACT/DEFAULT 正常；SETUP_RULE_MISSING_ZERO_FALLBACK 红 Tag 规则缺失；INITIAL_SETUP_STATE 班头首单普通展示）。
    /// </summary>
    public string? SetupSource { get; set; }
}

/// <summary>设备不可用窗口（G2-b 档②）</summary>
public class GanttUnavailableWindowDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? Reason { get; set; }
    public IReadOnlyList<long>? ImpactedTaskIds { get; set; }
}

/// <summary>多 Order 共担一个 Task 的份额（G2-b 档②）</summary>
public class GanttTaskShareDto
{
    public long OrderId { get; set; }
    public string? OrderNo { get; set; }
    public decimal ShareQty { get; set; }
    public decimal ShareRatio { get; set; }
}

/// <summary>数量-时间分段（G2-b 档②）</summary>
public class GanttTaskSegmentDto
{
    public int SegmentSeq { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public decimal Qty { get; set; }
    public decimal QtyRatio { get; set; }
    public string? Reason { get; set; }
}

/// <summary>延期原因明细（G2-b 档②）</summary>
public class GanttDelayReasonDto
{
    public string ReasonCode { get; set; } = string.Empty;
    public string? IssueCategory { get; set; }
    public string? Severity { get; set; }
    public string? Description { get; set; }
    public string? RelatedObjectRef { get; set; }
    public decimal? ImpactHours { get; set; }
}
