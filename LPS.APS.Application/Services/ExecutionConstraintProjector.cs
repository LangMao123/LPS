using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;

namespace LPS.APS.Application.Services;

/// <summary>
/// 上一 ACTIVE 版本的 `[Task]` 行（硬锚点**候选**，2号位 → 1号位 装载用）。
/// 只装「造锚点所需」的列，不装落库/查询用的全部列。
/// </summary>
public sealed record BasePlanAnchorRow
{
    /// <summary>`[Task].Id`（仅用于诊断定位）。</summary>
    public long TaskId { get; init; }

    /// <summary>`[Task].OrderId`（连接轴之一）。</summary>
    public long? OrderId { get; init; }

    /// <summary>`[Task].MaterialId`（连接轴之一）。</summary>
    public int MaterialId { get; init; }

    /// <summary>`[Task].StageCode`（连接轴之一 + 锚点复合键之一）。</summary>
    public string? StageCode { get; init; }

    /// <summary>`[Task].OperationCode`（连接轴之一 + 锚点复合键之一）。</summary>
    public string? OperationCode { get; init; }

    /// <summary>`[Task].ResourceId`。NULL = 人工能力槽（`ManualSlotId`）或历史行 ⇒ 本行不作硬锚点。</summary>
    public int? ResourceId { get; init; }

    /// <summary>`[Task].PlannedStartTime`。NULL = 无计划时间 ⇒ 本行不作硬锚点。</summary>
    public DateTime? PlannedStartTime { get; init; }

    /// <summary>`[Task].PlannedEndTime`。NULL = 无计划时间 ⇒ 本行不作硬锚点。</summary>
    public DateTime? PlannedEndTime { get; init; }

    /// <summary>`[Task].Quantity`（净产出量口径，见 §5.2）。</summary>
    public decimal Quantity { get; init; }

    /// <summary>`[Task].PlannedProcessQty`（产能加工量）。</summary>
    public decimal? PlannedProcessQty { get; init; }

    /// <summary>`[Task].TaskNo`（**仅供诊断**；不得当 `ExecutionConstraint.TaskKey`，见 §5.3）。</summary>
    public string? TaskNo { get; init; }

    /// <summary>
    /// `[Task].MESWorkOrderNo` —— **「已下发」事实的活载体**。
    /// 非空 ⇔ 该 Task 对应一张 MES 侧**已存在**的工单（值由 `PeggingOrchestrator` 落库时
    /// 从 `FinalTaskDraft.SourceDraftId` 的 `/WO:` 后缀反解，该后缀来自 5号位 `ExistingExecutionSlice`）。
    /// </summary>
    public string? MESWorkOrderNo { get; init; }

    /// <summary>
    /// `[Task].Status`（冻结 DDL `07` §4.4 值域：`PLANNED/RELEASED/IN_PROGRESS/COMPLETED/CANCELLED`）。
    /// ⚠ 当前 2号位 落库处**硬编码写 `PLANNED`** ⇒ 实库只有 `PLANNED`；本列是「已开工/已完成」的
    /// **设计载体**，待 MES 执行事实回写后自动生效。
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// 该行的约束种类（由装载侧资格判定给出，见 <see cref="ExecutionConstraintTypes"/>）。
    /// 1号位 当前**不消费**该值（只透传进 `LockedTaskConstraint`）。
    /// </summary>
    public string ConstraintType { get; init; } = ExecutionConstraintTypes.DispatchedImmovable;
}

/// <summary>
/// 锚点诊断（不产锚点时归档原因，**不静默丢弃**）。
/// </summary>
public sealed record AnchorDiagnostic(long TaskId, string Reason, string Detail);

/// <summary>
/// 投影诊断集（供装载层日志/审计使用）。
/// </summary>
public sealed record ExecutionConstraintProjection
{
    public IReadOnlyList<ExecutionConstraint> Constraints { get; init; } = Array.Empty<ExecutionConstraint>();
    public IReadOnlyList<AnchorDiagnostic> Diagnostics { get; init; } = Array.Empty<AnchorDiagnostic>();

    public int UnmatchedCount { get; init; }
    public int AmbiguousCount { get; init; }
    public int DuplicateKeyCount { get; init; }
    public int IncompleteRowCount { get; init; }
}

/// <summary>
/// `ExecutionConstraint.ConstraintType` 取值（**常量由我方给**，与 `PM回复0907.md` §三.2 六类最小范围对齐）。
/// ⚠ 1号位 侧当前**不消费**该值；命名待与 0号位/1号位 对齐。
/// </summary>
public static class ExecutionConstraintTypes
{
    /// <summary>已执行 / 已开工（MES 执行事实）。</summary>
    public const string Executed = "EXECUTED";

    /// <summary>已下发且业务上不可移动。</summary>
    public const string DispatchedImmovable = "DISPATCHED_IMMOVABLE";

    /// <summary>
    /// Frozen —— **执行保护**（`PM回复0923-1` §三.3「Frozen 不是普通计划稳定问题。它代表：执行风险。
    /// 因此 V1 普通 Candidate 不能直接破坏 Frozen」）。
    ///
    /// **判据 = 计划开始时间落 Frozen 窗口内**，公式见 <see cref="V1FenceCalculator"/>。
    /// **本值是 V1 的主要产出**（实库当前 `[Task].Status` 恒 `PLANNED`、`MESWorkOrderNo` 多空
    /// ⇒ 窗口层是唯一能真正命中 Frozen 的入口）。
    ///
    /// ⚠ **不要**与 Firm 混：Firm **不是锁**（`PM回复0923-1` §三.2），走**契约②**
    /// （`DomainSolveRequest.OrderProtections`），**不产本值**。
    /// </summary>
    public const string Frozen = "FROZEN";

    /// <summary>已知固定 Resource / Start / End。</summary>
    public const string FixedResourceStartEnd = "FIXED_RES_START_END";

    /// <summary>Demand Protection / Lock。</summary>
    public const string DemandProtection = "DEMAND_PROTECTION";
}

/// <summary>
/// **Fence（保护窗口）时间带** —— `PM回复0908.md` §六 与
/// 《APS_V1_跨版本排程连续性_FirmFrozen_MES执行事实衔接业务裁决建议稿_v1.0_20260908》§7.2 的三带划分。
/// </summary>
public enum FenceBand
{
    /// <summary>可移动：`PlannedStartTime &gt;= FirmEnd`（含无窗口配置时的兜底）。</summary>
    Movable = 0,

    /// <summary>Firm：`FrozenEnd &lt;= PlannedStartTime &lt; FirmEnd`。「已形成计划承诺，希望保持稳定」——**不是锁**。</summary>
    Firm = 1,

    /// <summary>Frozen：`PlannedStartTime &lt; FrozenEnd`。执行保护，**不可移动**。</summary>
    Frozen = 2
}

/// <summary>
/// 一次 ScheduleRun 的 **Fence 参数快照**（3号位 治理、2号位 只读；
/// 《…业务裁决建议稿 v1.0 20260908》§7.1 职责表「**2号位：每次 ScheduleRun 运行时读取冻结参数快照**」）。
/// </summary>
/// <param name="BasisTime">`FenceBasisTime` = 本次 FULL 排程基准时间。</param>
/// <param name="FrozenDays">`FenceConfig.FrozenDays`。</param>
/// <param name="FirmDays">`FenceConfig.FirmDays`。</param>
public sealed record FenceParameters(DateTime BasisTime, int FrozenDays, int FirmDays)
{
    /// <summary>`FrozenEnd = FenceBasisTime + FrozenDays`（§7.2）。</summary>
    public DateTime FrozenEnd => BasisTime.AddDays(FrozenDays);

    /// <summary>`FirmEnd = FrozenEnd + FirmDays`（§7.2，**连续不重叠**：Firm 从 FrozenEnd 起算，不是从基准起算）。</summary>
    public DateTime FirmEnd => FrozenEnd.AddDays(FirmDays);
}

/// <summary>
/// **Fence 判定**（纯函数，`《…业务裁决建议稿 v1.0 20260908》§7.2/§7.3` 逐字实现）。
///
/// ```text
/// FenceBasisTime = 本次FULL排程基准时间
/// FrozenEnd = FenceBasisTime + FrozenDays
/// FirmEnd   = FrozenEnd + FirmDays
///
/// Task.PlannedStartTime &lt;  FrozenEnd                  → Frozen
/// FrozenEnd &lt;= Task.PlannedStartTime &lt; FirmEnd       → Firm
/// Task.PlannedStartTime &gt;= FirmEnd                    → Movable
/// ```
///
/// §7.3「只以 `PlannedStartTime` 判定。Task 开始时间落入 Frozen，则整个 Task 按 Frozen 处理，**不从中间切 Task**。」
///
/// 【基准之前（`PlannedStartTime &lt; FenceBasisTime`）】§7.2 把 `Task.PlannedStartTime &gt;= FenceBasisTime`
///   写作**前提**，未定义基准之前如何处理。2号位 **取 Frozen**（判据：这类 Task 已到期/已越过基准仍在
///   上一 ACTIVE 里，说明它既没被执行事实覆盖、又已经"该开工了"——把它当 Movable 会让求解器把已到期的
///   计划随手搬走，与 §十七「Frozen Anchor 是当前总生产需求的已排部分」的保护意图相反）。
///   ⚠ 这是**文档未覆盖处的 2号位 判断**，已登记待 0号位/3号位 确认；判据集中在本类，改口径只改此处。
///
/// 【无窗口配置时】`parameters is null` ⇒ 一律 <see cref="FenceBand.Movable"/>（**不产窗口锚点**、
///   **不自造天数**——§7.1 明令 2号位 只读快照、§15「不得自己重判 FrozenDays/FirmDays」）。
///   调用侧必须对此**显式留痕告警**，不得静默。
/// </summary>
public static class V1FenceCalculator
{
    /// <summary>判定 `plannedStartTime` 落在哪一带。</summary>
    public static FenceBand Classify(DateTime plannedStartTime, FenceParameters? parameters)
    {
        if (parameters is null)
            return FenceBand.Movable;

        if (plannedStartTime < parameters.FrozenEnd)
            return FenceBand.Frozen;          // 含「基准之前」，见类注释

        if (plannedStartTime < parameters.FirmEnd)
            return FenceBand.Firm;

        return FenceBand.Movable;
    }

    /// <summary>
    /// 由起始时间与天数直接判定（供无 <see cref="FenceParameters"/> 对象的场合/单测使用）。
    /// </summary>
    public static FenceBand Classify(DateTime plannedStartTime, DateTime basisTime, int frozenDays, int firmDays)
        => Classify(plannedStartTime, new FenceParameters(basisTime, frozenDays, firmDays));
}

/// <summary>
/// **V1 硬锚点资格判定 —— 「Frozen 窗口 ＋ 执行事实」（Firm 不在其中）。**
///
/// 【口径】上一 ACTIVE 的 `[Task]` 行进 `ExecutionConstraints`，当且仅当：
///   · **Fence 判定为 Frozen**（`PlannedStartTime &lt; FenceBasisTime + FrozenDays`）；**或**
///   · 已是**执行事实**：`[Task].Status` ∈ {`RELEASED`,`IN_PROGRESS`,`COMPLETED`}，或 `MESWorkOrderNo` 非空。
///
///   执行事实**覆盖**窗口判定（《…业务裁决建议稿》§20「`Execution &gt; Frozen &gt; Firm &gt; Movable`」）——
///   故同时命中时 `ConstraintType` 取执行侧。
///
/// 【为什么 Frozen 窗口进契约①（**2026-10-13 二次更正**）】上一版把「窗口层 Frozen」整个踢出契约①、
///   只留执行事实，**过头了**。逐字依据：
///   · `PM回复0908.md` §六：「T101 9/9 开始，**距离排程基准2天，因此进入 Frozen**」——该例里 T101
///     **既未下发也未开工**，纯由**时间窗**判成 Frozen；§十八「**Frozen → 固定Task物理位置**」。
///   · 同件 §十八末：「2号位 读取上一 ACTIVE 中的仍有效 Firm/Frozen Task。旧 Task 不参加重新拆合，
///     而作为**当前同一 PI、同一工序生产需求中的已固定排程份额**进入 `DomainSolveRequest`。」
///   · 《…业务裁决建议稿 v1.0 20260908》§14：「Anchor 保留完整生产 Task 语义…**不允许退化成只有
///     Resource+Start/End 的匿名黑块**」；§15：「必须先建立 Execution Anchors + **Frozen Anchors** …
///     作为**硬约束**」；§7.2 给出窗口公式。
///   · 1号位 消费端实查：`PhaseFourLocalRepair.IdentifyImmovableTasks` 的**唯一**来源就是
///     `request.ExecutionConstraints`（其注释「Execution/Firm/Frozen/Protection」）；**没有**独立的
///     `immovableTasks` 入参 ⇒ 1号位 侧所谓「窗口层走 immovableTasks」，物理上仍需由 2号位 经本通道供给。
///
/// 【为什么 Firm 仍然不在里面】`PM回复0923-1.md`（**2026-09-23，更晚且专答此问的统一裁决回复**）
///   §三.2 逐字「**Firm 不是锁。Firm 不是不可移动。**」§四「Firm 的作用不是禁止，而是：提供业务信息」。
///   ⇒ 纯计划承诺（未进执行、且在 Frozen 窗外）**不进契约①**，改走**契约②**
///   （`DomainSolveRequest.OrderProtections`，`OrderProtectionType.Firm`）。
///   ⚠ 张力登记：09-08 的建议稿 §15 曾把 Firm Anchors 也列为硬约束；以更晚的 09-23 裁决为准。
///
/// 【为什么弃用 `PeggingExecutionRequest.FrozenWindowStart/End` 作判据】实查该窗为 `now .. now+2h`
///   （`SchedulingOrchestrator` 建 Run 处，注释「§2.3 滑动冻结窗口」）——它是**从当前时刻起 2 小时的滑动窗**，
///   不是「天数窗」；真正的天数窗是 `FenceConfig.FrozenDays/FirmDays`（§7.1 归 3号位）。
///   拿滑动窗当判据 = 用错误的参数算正确的公式。
///
/// 【V1 实产范围】`FenceConfig` 实库 **0 行** ⇒ `FenceParameters` 为 null ⇒ 窗口分支一律 Movable；
///   叠加实库 `[Task].Status` 恒 `PLANNED`（落库处硬编码）、`MESWorkOrderNo` 多空
///   ⇒ **当前实际产出为空**。这是**数据未铺**，不是设计缺口；口径已按文档落定，
///   3号位 铺 `FenceConfig` / MES 执行事实回写后自动生效。调用侧对此**显式留痕**，不静默。
///
/// 【规则一变只改本方法】投影器与 1号位 消费端均不受影响。
/// </summary>
public static class V1AnchorQualifier
{
    /// <summary>「已下发」状态值（`[Task].Status`，冻结 DDL `07` §4.4 值域）。</summary>
    private const string StatusReleased = "RELEASED";

    /// <summary>「已开工」状态值（`[Task].Status`）。</summary>
    private const string StatusInProgress = "IN_PROGRESS";

    /// <summary>「已完成」状态值（`[Task].Status`）。</summary>
    private const string StatusCompleted = "COMPLETED";

    /// <summary>
    /// 「已取消」状态值（`[Task].Status`，冻结 DDL `07` §4.4 值域）。
    /// **终态、非事实** —— 取消的 Task 既不占设备也不该被锚定，**任何带位下都不产锚点**
    /// （否则落 Frozen 窗的取消行会被"复活"成硬约束）。
    /// </summary>
    private const string StatusCancelled = "CANCELLED";

    /// <param name="mesWorkOrderNo">`[Task].MESWorkOrderNo`（非空 ⇒ 已下发）。</param>
    /// <param name="status">`[Task].Status`。</param>
    /// <param name="band">该行 `PlannedStartTime` 的 Fence 判定（`null` 参数时传 <see cref="FenceBand.Movable"/>）。</param>
    /// <param name="constraintType">命中时给出契约① 约束种类。</param>
    /// <param name="reason">命中时给出契约② 原因域取值（与 `ConstraintType` 同源的同一判断）。</param>
    public static bool IsHardAnchor(
        string? mesWorkOrderNo,
        string? status,
        FenceBand band,
        out string constraintType,
        out OrderProtectionReason reason)
    {
        constraintType = string.Empty;
        reason = OrderProtectionReason.None;

        // ⓪ 已取消 = 终态、非事实 ⇒ 任何带位都不产锚点（含 Frozen 窗内，见 StatusCancelled 注释）。
        if (IsCancelled(status))
            return false;

        // ① 已开工 / 已完成 —— MES 执行事实。**覆盖**窗口判定（§20 Execution > Frozen > Firm > Movable）。
        if (IsAny(status, StatusInProgress, StatusCompleted))
        {
            constraintType = ExecutionConstraintTypes.Executed;
            reason = OrderProtectionReason.IN_EXECUTION;
            return true;
        }

        // ② 已下发 —— 挂着 MES 侧已存在的工单，或任务状态已下达。同样覆盖窗口判定。
        if (!string.IsNullOrWhiteSpace(mesWorkOrderNo) || IsAny(status, StatusReleased))
        {
            constraintType = ExecutionConstraintTypes.DispatchedImmovable;
            reason = OrderProtectionReason.MES_DISPATCHED;
            return true;
        }

        // ③ Frozen 窗口（**纯计划、未下发未开工**，靠时间窗判成执行保护）。
        if (band == FenceBand.Frozen)
        {
            constraintType = ExecutionConstraintTypes.Frozen;
            reason = OrderProtectionReason.FROZEN_WINDOW;
            return true;
        }

        // ④ Firm / Movable ⇒ **不产契约①锚点**（Firm 走契约②）。
        return false;
    }

    /// <summary>
    /// 该行是否**已取消**（终态、非事实）。取消行不产契约① 锚点，也**不参与**契约② 的订单级带位判定。
    /// </summary>
    public static bool IsCancelled(string? status) => IsAny(status, StatusCancelled);

    private static bool IsAny(string? value, params string[] candidates)
        => value is not null
           && candidates.Any(c => string.Equals(value.Trim(), c, StringComparison.OrdinalIgnoreCase));
}


/// <summary>
/// 把「上一 ACTIVE 的 `[Task]` 硬锚点候选行」投影为 1号位 的 <see cref="ExecutionConstraint"/> 列表。
///
/// 【为什么需要】（`PM回复0907.md` §三.2 逐字）：「当前 `ExecutionConstraints = Array.Empty&lt;...&gt;()`
///   **不能作为正式V1实现保留**。」责任链已冻结（§三.1：上一 ACTIVE 仍有效的 Firm/Frozen 由 2号位 继承，
///   已执行/已下发/固定 R·S·E 由 2号位 组装）。
///
/// 【唯一的硬身份要求 —— `DraftId`】1号位 消费端（`PhaseOneConstraintBuilder.BuildLockedTasks` →
///   `PhaseTwoInitialScheduler` 锁定继承循环 / `SchedulingDirectionResolver` / `PhaseFourLocalRepair
///   .IdentifyImmovableTasks`）**一律要求 `DraftId` == 本次 Run 的 `LogicalProductionDemand.LogicalDemandKey`**：
///   · `PhaseTwoInitialScheduler` 按 `d.LogicalDemandKey == lockedTask.DraftId` 查 demand，查不到 ⇒ 图/节点解不出
///     ⇒ **`TechnicalFailure`**（输入完整性失败，非「排不下」）；
///   · `SchedulingDirectionResolver` 按 `k.DraftId == demand.LogicalDemandKey` 判「该需求有无锁定」（有 ⇒ 正排）；
///   · `PhaseFourLocalRepair` 按 `FinalTaskDraft.SourceDraftId == DraftId` 认不可移动任务。
///   而上一 ACTIVE `[Task]` **没有 `DraftId` 列**（只有 `TaskNo`/`StageExecutionBatchDraftKey`，属**另一命名空间**）
///   ⇒ **必须按连接轴在本 Run 需求集上重算**。
///
/// 【连接轴】`(OrderId, MaterialId, StageCode, OperationCode)`。命中 0 条 ⇒ 该旧 Task 本轮无对应需求
///   （不动它、不产锚点）；命中 &gt;1 条 ⇒ **歧义 ⇒ 不产锚点**（禁止猜，避免把锚点挂错需求）。
///
/// ⚠ **【未决项 · 2026-10-13 新发现，尚未改码】连接轴口径可能应为「PI + 工序」而不是「OrderId + …」。**
///   `PM回复0908.md` §十 逐字：「那么『不动的旧 Task』怎么和今天 Pegging 关联？**不是通过昨天的 Allocation，
///   而是通过 P1001 这个当前 Supply/生产对象**」；同件 §十八末：「作为**当前同一 PI、同一工序**生产需求中的
///   已固定排程份额进入 `DomainSolveRequest`」；建议稿 §10/§12 亦以「TaskNo 稳定，**不代表昨天的 Demand 归属稳定**」
///   为核心论点（每天重新 Pegging ⇒ 旧 Task 的 `OrderId` 可能已易主）。
///   ⇒ 严格按上述口径，轴应为 `(MTS_InstructionNo, StageCode, OperationCode)`：
///   上一 ACTIVE `[Task].MTS_InstructionNo` ↔ 本轮 `LogicalProductionDemand.ProductionInstructionNo`
///   （memory「生产指示号 = `MTS_InstructionNo` 三地同键」）。
///   **未立即改的原因**：① 一个 PI 在本轮可能对应**多条** `LogicalProductionDemand`（按 AllocationSequence 展开），
///   而 1号位 `PhaseTwoInitialScheduler` 硬要求 `DraftId == d.LogicalDemandKey`（唯一）⇒ 需先定「PI 命中多需求时
///   锚点挂哪一条」（是挂全部、还是按工序唯一化）；② 该口径与现有四元组在「订单不变」时结果等价，
///   只在**换单**场景分歧 ⇒ 需真实样本验证。**改前必须把①②定死，否则会把锚点挂错需求、整解 Fail Closed。**
///
/// 【不产锚点的其它条件（全部归档进 <see cref="ExecutionConstraintProjection.Diagnostics"/>，不静默丢）】
///   · `StageCode`/`OperationCode` 空 ⇒ 无法构成复合键；
///   · `ResourceId` NULL ⇒ 人工槽/历史行，无设备锚；
///   · `PlannedStartTime`/`PlannedEndTime` NULL ⇒ 无时间锚；
///   · 投影后 `(DraftId, StageCode, OperationCode)` 撞键 ⇒ 保留首条（否则会被 1号位 判输入契约冲突、整解 Fail Closed）。
///
/// 【`TaskKey` 恒 NULL（**故意**）】1号位 `PhaseFourLocalRepair.IdentifyImmovableTasks` 拿它与本轮新生成的
///   `FinalDraftId`（`Guid.NewGuid()`）做等值匹配 ⇒ **跨 Run 恒不命中**。填 `[Task].TaskNo` 也不生效、反误导
///   ⇒ 留 null（见 §5.3：载体去留属 1↔2 契约，只按 1号位 点名字段扩）。
///
/// 纯函数、无 IO ⇒ 可单测。
/// </summary>
public static class ExecutionConstraintProjector
{
    /// <summary>
    /// 投影硬锚点。
    /// </summary>
    /// <param name="demands">**本次 Run** 的需求集（必须已分桶，`LogicalDemandKey` 为准；A/B 桶带 `/WO:` 后缀、自由桶带 `/FREE`）。</param>
    /// <param name="baseRows">上一 ACTIVE 的硬锚点候选行（已由装载侧按资格判定过滤）。</param>
    public static ExecutionConstraintProjection Project(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<BasePlanAnchorRow> baseRows)
    {
        // 需求索引：连接轴 → 该轴上的需求列表（>1 视为歧义）。
        var demandIndex = new Dictionary<(long? OrderId, int MaterialId, string Stage, string Op), List<LogicalProductionDemand>>();
        foreach (var d in demands)
        {
            var key = (d.OrderId, d.MaterialId, Norm(d.StartStageCode), Norm(d.StartOperationCode));
            if (!demandIndex.TryGetValue(key, out var list))
                demandIndex[key] = list = new List<LogicalProductionDemand>();
            list.Add(d);
        }

        var constraints = new List<ExecutionConstraint>();
        var diagnostics = new List<AnchorDiagnostic>();
        var seenKeys = new HashSet<(string DraftId, string Stage, string Op)>();

        int unmatched = 0, ambiguous = 0, dup = 0, incomplete = 0;

        foreach (var row in baseRows)
        {
            var stage = Norm(row.StageCode);
            var op = Norm(row.OperationCode);

            if (stage.Length == 0 || op.Length == 0)
            {
                incomplete++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "INCOMPLETE", "StageCode/OperationCode 为空，无法构成锚点复合键"));
                continue;
            }
            if (row.ResourceId is null)
            {
                incomplete++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "INCOMPLETE", "ResourceId 为 NULL（人工槽/历史行），无设备锚"));
                continue;
            }
            if (row.PlannedStartTime is null || row.PlannedEndTime is null)
            {
                incomplete++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "INCOMPLETE", "PlannedStartTime/PlannedEndTime 为空，无时间锚"));
                continue;
            }

            if (!demandIndex.TryGetValue((row.OrderId, row.MaterialId, stage, op), out var hits) || hits.Count == 0)
            {
                unmatched++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "UNMATCHED",
                    $"本轮需求集无 (OrderId={row.OrderId?.ToString() ?? "-"}, MaterialId={row.MaterialId}, Stage={stage}, Op={op}) 对应需求"));
                continue;
            }
            if (hits.Count > 1)
            {
                ambiguous++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "AMBIGUOUS",
                    $"连接轴命中 {hits.Count} 条需求（{string.Join(", ", hits.Select(h => h.LogicalDemandKey))}）⇒ 不猜，不产锚点"));
                continue;
            }

            var demand = hits[0];
            var keyTuple = (demand.LogicalDemandKey, stage, op);
            if (!seenKeys.Add(keyTuple))
            {
                dup++;
                diagnostics.Add(new AnchorDiagnostic(row.TaskId, "DUPLICATE_KEY",
                    $"(DraftId={demand.LogicalDemandKey}, Stage={stage}, Op={op}) 已出现 ⇒ 保留首条（重复会被 1号位 判输入契约冲突、整解 Fail Closed）"));
                continue;
            }

            constraints.Add(new ExecutionConstraint
            {
                DraftId = demand.LogicalDemandKey,
                ResourceId = row.ResourceId.Value,
                LockedStart = row.PlannedStartTime.Value,
                LockedEnd = row.PlannedEndTime.Value,
                ConstraintType = row.ConstraintType,
                StageCode = stage,
                OperationCode = op,
                // §5.2 口径：明确给三类量，避免 1号位 落到 `?? demand.NetOutputQty` / `?? demand.PlannedProcessQty` 兜底
                //   （那会把「锁定覆盖量」当成需求全量）。
                LockedQuantity = row.Quantity,
                LockedNetOutputQty = row.Quantity,
                LockedPlannedProcessQty = row.PlannedProcessQty ?? row.Quantity,
                // §5.3：恒 NULL（跨 Run 不可能命中 FinalDraftId）。
                TaskKey = null
            });
        }

        return new ExecutionConstraintProjection
        {
            Constraints = constraints,
            Diagnostics = diagnostics,
            UnmatchedCount = unmatched,
            AmbiguousCount = ambiguous,
            DuplicateKeyCount = dup,
            IncompleteRowCount = incomplete
        };
    }

    private static string Norm(string? s) => (s ?? string.Empty).Trim();
}
