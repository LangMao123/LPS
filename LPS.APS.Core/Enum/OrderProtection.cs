namespace LPS.APS.Core.Enum;

/// <summary>
/// **订单保护事实**的保护类型（2号位 → 1号位，契约②；载体 <c>DomainSolveRequest.OrderProtections</c>）。
///
/// 【为什么独立于契约①】载荷分两条契约，**语义不得混同**（《APS V1当前有效冻结规则清单》§十四 L-001
/// 「Firm/Frozen 保护 Task 移动边界；Demand Protection/Strict Binding 保护供需份额，语义不得混同」）：
///   · **契约① `ExecutionConstraint[]`** = 硬锚点，**锁物理位置**（Resource/Start/End/数量）。
///     收 **Frozen（窗口层 + 执行层）** 与执行事实 —— 依据《跨版本排程连续性…业务裁决建议稿 v1.0 20260908》
///     §7.2 窗口公式、§14「Anchor 保留完整 Task 语义」、§15「必须先建立 Execution Anchors + Frozen Anchors 作为硬约束」、
///     §20「`Execution > Frozen > Firm > Movable`」；以及 <c>PM回复0908.md</c> §六/§十八「Frozen → 固定Task物理位置」。
///   · **契约② `OrderProtection[]`** = **订单级保护事实**，**不锁位置**，供**影响分析/业务通报**。
///     **Firm 走这条** —— 依据 <c>PM回复0923-1.md</c> §三.2 逐字「**Firm 不是锁。Firm 不是不可移动。**」
///     ＋§四「Firm 的作用不是禁止，而是：提供业务信息」＋§八「2号位 应该提供：**订单保护事实**
///     （<c>ProtectionType</c> = Firm/Frozen/None）」。
///
/// 【为什么 Firm 不在契约①】同上 §三.2/§四。且 §八 明令 2号位 **不需要**提供「禁止移动 Task 列表」
///   ——「这会把业务判断提前固化」。系统不替业务决定牺牲谁（§四/§六）。
///
/// 【时间序说明（判据取舍）】《…业务裁决建议稿 v1.0 20260908》§15 曾把 Firm Anchors 也列为硬约束；
///   但 **2026-09-23 的 <c>PM回复0923-1</c> 是更晚、且专答此问的「统一裁决回复」**，
///   其 §一 自称同时覆盖「夜间全量排程 / 白天 Candidate」两场景，§三.2 明文否掉「Firm 是锁」
///   ⇒ **以 <c>PM回复0923-1</c> 为准：Firm 不进契约①。**（建议稿 &lt; 裁决；09-08 &lt; 09-23。）
/// </summary>
public enum OrderProtectionType
{
    /// <summary>
    /// 无保护。
    /// ⚠ **不投影**（<c>APS_V1_订单保护事实载体_2号位致1号位_收敛回执_v1.0_20260923</c> §五：
    /// 「<c>ProtectionType = None</c> 不投影」）—— 只投「有保护」的订单，载荷不留噪声行。
    /// </summary>
    None = 0,

    /// <summary>
    /// Firm：已形成计划承诺、希望保持稳定。
    /// **不是锁、不是不可移动**；变化时须让业务人员知道（延期几天 + 原因），由业务判断是否接受。
    /// 判据 = 计划落在 **Firm 窗口** 内（<c>FrozenEnd &lt;= PlannedStartTime &lt; FirmEnd</c>）。
    /// </summary>
    Firm = 1,

    /// <summary>
    /// Frozen：已进入执行保护阶段（接近生产窗口 / 已下发 / 已进入执行准备）。
    /// 「Frozen 不是普通计划稳定问题。它代表：执行风险。因此 V1 普通 Candidate 不能直接破坏 Frozen。」
    /// —— <c>PM回复0923-1.md</c> §三.3。
    /// ⇒ 本类型在**契约② 里只作事实通报**；其**物理位置保护**由**契约①**（同订单 Task 的
    ///   <c>ExecutionConstraint</c>，<c>ConstraintType = FROZEN</c>）承担，两者同源、各司其职。
    /// </summary>
    Frozen = 2
}

/// <summary>
/// **订单保护事实的原因域**（独立枚举，不再复用 <c>FrozenReasonType</c> —— 后者是"MES 已下发/手工锁定/…"的
/// 执行侧原因，无「窗口」语义；依 <c>APS_V1_订单保护事实载体_2号位致1号位_收敛回执_v1.0_20260923</c> §五
/// 「<c>ProtectionReason</c> 独立成域」）。
///
/// **口径：投结论、不投判据。** 1号位 纯内存、零 I/O、不持有 Base 旧计划 ⇒ **结构上做不了窗口内/外判定**
///（<c>APS_V1_白天候选各号位配合事项_1号位致2号位_回执_v1.0_20260924</c> §三 预提2）
/// ⇒ 窗口判定一律由 2号位 在装载侧算完，1号位 只读结论。
/// </summary>
public enum OrderProtectionReason
{
    /// <summary>无原因（与 <see cref="OrderProtectionType.None"/> 配对，同样不投影）。</summary>
    None = 0,

    /// <summary>
    /// 计划开始时间落 **Frozen 窗口** 内：<c>PlannedStartTime &lt; FenceBasisTime + FrozenDays</c>。
    /// 依据《…业务裁决建议稿 v1.0 20260908》§7.2 公式 + <c>PM回复0908.md</c> §六 算例。
    /// </summary>
    FROZEN_WINDOW = 1,

    /// <summary>
    /// 计划开始时间落 **Firm 窗口** 内：<c>FrozenEnd &lt;= PlannedStartTime &lt; FrozenEnd + FirmDays</c>。
    /// 同上依据。
    /// </summary>
    FIRM_WINDOW = 2,

    /// <summary>
    /// 已下发 MES（<c>[Task].MESWorkOrderNo</c> 非空，或 <c>Status = RELEASED</c>）——**执行事实**。
    /// 执行事实**覆盖**窗口判定（§20「<c>Execution &gt; Frozen &gt; Firm &gt; Movable</c>」）。
    /// </summary>
    MES_DISPATCHED = 3,

    /// <summary>
    /// 已开工 / 已完成（<c>[Task].Status</c> ∈ {<c>IN_PROGRESS</c>, <c>COMPLETED</c>}）——**执行事实**。
    /// 同上，覆盖窗口判定。
    /// </summary>
    IN_EXECUTION = 4
}
