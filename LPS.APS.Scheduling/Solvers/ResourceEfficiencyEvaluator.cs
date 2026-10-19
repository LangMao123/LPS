using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// R-2「资源效率 ＝ **设备有效稼动**」的**有界候选增量评价**（0号位 2026-10-14
/// 《APS V1｜致1号位综合正式审核回复 v1.1》§一 已明确的业务口径；本轮**不再**向 0号位 重复提问）。
///
/// 【业务口径（§一.2）】
///   · 有效加工计入有效稼动；不得靠**人为缩小分母 / 扩大生产量 / 填充不必要 Task** 抬比率；
///   · **Setup 是真实设备占用，但不是有效加工** ⇒ 作效率损失与产能占用计入，**不得与有效加工混计**；
///   · 可用时间以 **2号位合成的真实三级 Resource Calendar** 为准（1号位 **不自造** 8h / 7×24）；
///   · **交付与硬约束始终优先** ⇒ 本层只做**合法候选内**的次级比较，**绝不**参与可行性/履约判定；
///   · **有效稼动 / 资源负荷 / Setup 损失三者不可混同** —— 既有
///     `PhaseThreeDiagnostics.CalculateResourceUtilization` 的分子是 `PlannedEnd − PlannedStart`
///     （**占用率**，含 Setup 与跨日历中断），**不得**改个名当作「有效加工稼动率」。
///
/// 【关键算例（§一.2，本类设计的判定基准）】
///   可用窗口均 8h；两方案有效加工均 6h；A 换型 1.5h、空闲 0.5h；B 换型 0.5h、空闲 1.5h
///   ⇒ **A、B 的有效稼动均为 75%**，**不得**谎称 B 更高。
///   B 的优势是**真实 Setup 少 1h、释放 1h 可利用产能** ⇒ 该改善只能经**真实 Setup（第 ④ 层）**体现，
///   **不得**在资源效率层再记一次（否则即 §一.2.5 禁止的「同一 Setup 改善重复记两次收益」）。
///
/// 【本类给出的三件事】
///   ① **来源拆分**（§一.3.1）：有效加工 / Setup / 等待与跨日历中断 / 非生产 各自取数；
///   ② **可比窗口**（§一.3.1、§一.3.2）：真实资源集合取两侧候选任务的**并集**（**不是**对称差），
///      窗口取两侧任务的**公共包络**与真实日历求交 ⇒ **两侧分母逐字相同**，杜绝「人为缩小分母」；
///   ③ **有界增量排序键**（§一.3.2）：<see cref="Occupancy.NonSetupOccupiedMinutes"/>
///      —— **已扣 Setup** ⇒ 与第 ④ 层（Setup 总量）**结构性不重复计分**。
///
/// 【为什么排序键用「除 Setup 外占用」而不是「有效稼动率」】
///   · §一.2 算例已判：等量需求 + 等 Setup 下比率**本就相同**，拿比率排序会诱使实现去「缩小分母」；
///   · ④.6 只在第 ④ 层（Setup 总量）**判平**时才被执行 ⇒ 两侧 `SetupMinutes` **相等** ⇒
///     `Occupied` 与 `NonSetupOccupied` 在此层**同序**。取后者是**显式声明**「不与 ④ 重复计分」，
///     取前者则在语义上把 Setup 又算了一遍。⇒ 排序键取 `NonSetupOccupiedMinutes`，**可证不重复**。
///   · 比率只作**解释与留痕**（<see cref="Breakdown.EffectiveUtilization"/>）。
///
/// 【§3.2 准确命名（0号位 2026-10-14《致1号位第四轮整改回执正式复核意见 v1.0》§3.2 逐字要求）】
///   ④.6 的排序键 **不是**「有效稼动率」。它的**准确称谓**是：
///     **「相同有效产出下的非换型占用／释放产能代理比较」**（`NonSetupOccupiedMinutes` 越小越好）。
///   两者**方向可以相反**：0号位 §3.2 逐字算例 —— 窗口均 480min、合格产出均 100 件、Setup 均 0；
///     A 加工 360min ⇒ 有效稼动 **75%**；B 加工 300min ⇒ 有效稼动 **62.5%**；
///     ④.6 选 **B**（300 < 360）⇒ 含义是「**同需求更省 60min 设备分钟、释放 60min 合法可用产能**」，
///     **不是**「B 的稼动率更高」（它**更低**）。该反例由 <c>ResourceEfficiencyTests.RE_11</c> 钉住。
///   ⇒ 对外文本（<see cref="Describe"/>）**只**宣称「除 Setup 外占用更少 ⇒ 释放产能」，
///     并把两侧比率**原样**列出，**不得**反向宣称比率更高。
///
/// 【§3.6 / R2-E 可达性：**已限定前提**的惰性结论（如实登记，不降目标）】
///   ⚠ **R2-E（0号位 2026-10-14《致1号位第五轮代码审核正式复核回复 v1.0》§三 R2-E，`RISK_UNVERIFIED`）**：
///     早先「V1 全局 `Δ ≡ 0`、自然无法触发」的断言**过宽，已撤回**。正确前提是：
///     设占用 `O = Setup + Σ(StandardDuration × PlannedProcessQty ÷ CapacityFactor)`（见上文 §3.3③）。
///     若**两侧候选把同一工序放在同一资源（同一 `CapacityFactor`）**，则 `StandardDuration` 与
///     `CapacityFactor` 对数量为**常数** ⇒ `O` 对数量线性 ⇒
///       `Δ = O(锚+本) − O(锚) − O(本) = [Setup 项相消] + [线性项相消] = 0`。
///   ⇒ **「同资源、同工序集合」是 Δ ≡ 0 的前提**，**不是**「V1 不可能 Δ ≠ 0」。
///     一旦**候选资源选择变化**（不同资源 / 不同 `CapacityFactor`），线性部分**不再相消**：
///       反例（0号位 §R2-E 逐字）：同一 Operation 单位工时 2min；锚批 10 件选资源 A（`CapacityFactor=1`）
///         需 20min；新需求 10 件独立选资源 B（`CapacityFactor=2`）需 10min；合批 20 件改在 A 加工需 40min
///         ⇒ `Δ = 40 − 20 − 10 = +10`；若合批合法改在 B 则 `Δ = 20 − 20 − 10 = −10`。
///   ✅ **本号位已用 V1 既有合法载荷（Eligibility 替代设备 + 不同 `CapacityFactor` + 真实 Calendar）
///     构造出 Solver 级正工时反例**，见 `StageBatchMergeTests.M_17`（④.6 自然决定采纳）与
///     `M_18`（上层短路、④.6 未被执行）⇒ **不是** Solver 覆盖缺口，**不降 R-2 范围**。
///   ⚠ 「非线性每任务固定开销 / 随批量变的 `CapacityFactor`」只是**另一族**使 Δ ≠ 0 的成因，
///     **不是**充要条件（早先表述已更正）。
///
/// 【两级 API（**热路径与留痕分离**）】
///   · <see cref="Evaluate"/> → <see cref="Occupancy"/>：**O(任务数)、不读日历**，供**逐锚点擂台比较**（热路径）；
///   · <see cref="ComputeComparableWindow"/> + <see cref="BreakdownFor"/>：**O(资源数×窗口数)**，
///     只在**落定后留痕**时各算**一次**，**不进入逐候选循环**。
///
/// 【数据来源与假设（§一.3.1「说明为何可从既有冻结载荷取得」）】
///   · **占用**：`FinalTaskDraft.PlannedStartTime/PlannedEndTime`（既有字段）—— 这是**占用**，不是加工；
///   · **Setup**：`FinalTaskDraft.SetupTime`（既有字段，含 4 态来源 `SetupSource`）；
///   · **日历**：`ConstraintContext.ResourceCalendars`（2号位装载的真实三级日历）；
///   · **有效加工**：`占用 − Setup − 跨日历中断`。⚠ **V1 明确假设**：本求解器把 Task 紧贴放置在
///     合法窗口内（`FindForwardSlot`），窗口内不插入等待 ⇒ 扣掉窗口间空隙后即为真实加工。
///     **该假设由 `ResourceEfficiencyTests.RE_06` 钉住**；若将来引入窗口内等待，本式必须改为按
///     `RoutingOperation.StandardDuration × PlannedProcessQty ÷ CapacityFactor` 重算。
///   · **非生产**：`ResourceId == null` 的 Task **不占设备** ⇒ **既不进分子也不进分母**。
///
/// 【§3.3 代码核验答复（0号位 2026-10-14《致1号位第四轮整改回执正式复核意见 v1.0》§3.3 逐字要求
///   「请基于完整代码说明…」；以下四条均为**读码实测**，非推断）】
///   ① **`PlannedStartTime/EndTime` 是否涵盖 Setup？** —— **涵盖**。
///      `PhaseTwoInitialScheduler.ScheduleDemandOperations`（:2318/:2322）令
///      `totalDuration = processDuration + setupMinutes`、`candidateStart = candidateEnd − totalDuration`，
///      即**占用窗 = Setup + 加工**；`CreateTask`（:4485-4487）把同一 `start/end` 与 `setupMinutes`
///      分别写进 `PlannedStartTime/EndTime` 与 `SetupTime`。
///   ② **Setup 在 Task 时间窗内部还是独立占用段？** —— **在窗口内部**（同一连续占用段的前缀），
///      **不是**独立占用段。故 `占用 = Setup + 加工` ⇒ `NonSetupOccupied = Occupied − Setup` 即加工。
///   ③ **加工计划工时来自何处？** —— `operation.StandardDuration × demand.PlannedProcessQty ÷ CapacityFactor`
///      （:2304；Phase4 :1118 同式）。`StandardDuration` 源自 `RoutingOperation.StandardDuration`
///      （`PhaseOneConstraintBuilder.cs:489`）；`CapacityFactor` 源自
///      `ConstraintContext.ResourceCapacityFactors[(EligibilityLookupKey, ResourceId)]`
///      （`PhaseOneConstraintBuilder.cs:783-794`）。**缺失/非法 ⇒ 该资源直接跳过**（fail-closed，
///      **不静默用 1.0**，见 `PhaseFourLocalRepair.cs:1500`）。
///   ④ **跨日历停机：减去，还是由硬合法日历先保证？** —— **由硬合法日历先保证**：
///      `FindForwardSlot`（`PhaseTwoInitialScheduler.cs:2800-2820`、`PhaseFourLocalRepair.cs:1366-1387`）
///      只在**单个**日历窗内取槽（`slot.Value.End <= windowEnd`）⇒ 合法 Task **不跨窗** ⇒
///      `CrossCalendarInterrupt` 在合法 Task 上恒为 `0`。该字段是**防御性**的（对非合法/历史 Task 仍会算出正值，
///      见 `RE_04`），**不是**常规路径的扣减项。
///   ⑤ **`ResourceId == null` 不自动等于「非生产」** —— 冻结判别器是**工序的资源模式**
///      （`UNCONSTRAINED`/`WAIT_ONLY` ⇒ 非资源工序；见 `FinalTaskDraft.ResourceId` 冻结注释与
///      0号位 2026-09-22 裁决），而 `FinalTaskDraft` **不携带**该模式 ⇒ 本类**无法**自证。
///      故本类**不再静默忽略**：把无资源身份 Task 计数**显式暴露**在
///      <see cref="Occupancy.NonResourceTaskCount"/> / <see cref="Breakdown.NonResourceTaskCount"/>
///      并写入留痕文本；**真实 Operation Task 缺资源身份**这一契约违背因此**可见**（不再被吞）。
///
/// 【命名消歧】本类的追溯码前缀 `RESOURCE_EFFICIENCY_` 是**求解器内部决策说明层**的字符串，
///   **不是**冻结 DDL `ScenarioObjectiveScore.ObjectiveName` 里的场景级指标名 `RESOURCE_EFFICIENCY`
///   （后者由下游按场景汇总，非本层产出），两者**不得**互相赋值或混用。
/// </summary>
internal static class ResourceEfficiencyEvaluator
{
    /// <summary>追溯码（**决策说明层**，非冻结 `ReasonCode` 字典）：④.6 层**真的改变了择优**。</summary>
    internal const string TraceIncrement = "RESOURCE_EFFICIENCY_INCREMENT";

    /// <summary>追溯码：④.6 层两侧**指标相同／无增量差异**（§一.3.4 要求的显式记录，**不得**沉默略过）。</summary>
    internal const string TraceNoIncrement = "RESOURCE_EFFICIENCY_NO_INCREMENT";

    /// <summary>追溯码：④.6 层**因数据缺失无法评价**（§一.3.4：记录真实缺项及影响，**不得**编造比率）。</summary>
    internal const string TraceDataMissing = "RESOURCE_EFFICIENCY_DATA_MISSING";

    /// <summary>
    /// 追溯码（R2-C 第六轮 §3.3「最低限度区分『最终被选方案的主决定层』和『本轮实际进行过的④.6比较』
    ///   两个事实」）：④.6 层**被执行过**（有比较到达本层），但**没有**决定最终采纳方案。
    ///
    /// ⚠ 与本层其余三个码**语义互斥**：那三个码回答「本层对这次比较怎么判」，
    ///   本码回答「本层被跑过、但最终采纳方案由**更靠前**的层决出」⇒ **不得**据此宣称「因资源效率择优」，
    ///   也**不得**反过来因最终未记本层的增量码而宣称「本层没生效」。
    /// </summary>
    internal const string TraceEvaluatedNotDecisive = "RESOURCE_EFFICIENCY_EVALUATED_NOT_DECISIVE";

    /// <summary>
    /// ④.6 排序所需的**最小事实**（O(任务数)，**不读日历** ⇒ 可安全放在逐锚点擂台的热路径里）。
    /// </summary>
    /// <param name="NonResourceTaskCount">
    /// 本侧**无资源身份**（`ResourceId == null`）的 Task 数（§3.3：**不得静默忽略**）。
    /// 冻结口径下这些是非资源工序（`UNCONSTRAINED`/`WAIT_ONLY`）⇒ 不占设备；
    /// 但若出现「真实 Operation Task 缺资源身份」，该计数与留痕文本**使其可见**。
    /// </param>
    internal sealed record Occupancy(
        decimal SetupMinutes,
        decimal OccupiedMinutes,
        IReadOnlyList<int> AffectedResources,
        int NonResourceTaskCount = 0)
    {
        /// <summary>
        /// ④.6 的**排序键**：在真实资源集合上**除 Setup 之外**占用的分钟数（**越小越好**）。
        /// ⚠ **已扣 Setup** ⇒ 与第 ④ 层（Setup 总量）**不重复计分**（§一.2.5）。
        /// </summary>
        public decimal NonSetupOccupiedMinutes => OccupiedMinutes - SetupMinutes;
    }

    /// <summary>
    /// 留痕用的**完整拆分**（读日历，**只在落定时算一次**，不进热路径）。
    /// </summary>
    internal sealed record Breakdown(
        decimal EffectiveProcessingMinutes,
        decimal SetupMinutes,
        decimal OccupiedMinutes,
        decimal WaitingOrInterruptMinutes,
        decimal ComparableWindowMinutes,
        IReadOnlyList<int> AffectedResources,
        bool WindowAvailable,
        int NonResourceTaskCount = 0)
    {
        /// <summary>排序键（与 <see cref="Occupancy.NonSetupOccupiedMinutes"/> 同式，便于留痕对照）。</summary>
        public decimal NonSetupOccupiedMinutes => OccupiedMinutes - SetupMinutes;

        /// <summary>
        /// 有效稼动率（**解释/留痕用**，`null` = 缺日历事实不可算）。
        /// ⚠ **不作为排序键** —— 见类注释 §一.2 算例。
        /// </summary>
        public decimal? EffectiveUtilization =>
            WindowAvailable && ComparableWindowMinutes > 0m
                ? EffectiveProcessingMinutes / ComparableWindowMinutes
                : null;

        /// <summary>
        /// 可比窗口内的**真实日历未占用量** = `可比窗口 − 实际占用`（`null` = 缺日历事实）。
        ///
        /// 【R2-A（0号位 2026-10-14《致1号位第五轮代码审核正式复核回复 v1.0》§三 R2-A，P1 CONFIRMED）】
        ///   旧字段 `ReleasedLegalCapacityMinutes = 可比窗口 − NonSetupOccupiedMinutes` **已删除** ——
        ///   它把**已被 Setup 实际占用**的分钟也算成「可释放的合法产能」：对「窗口 480 / 有效加工 360 /
        ///   Setup 90 / 真实占用 450」的合法设备，旧式给 `480 − (450−90) = 120`，而**真实物理未占用**
        ///   只有 `480 − 450 = 30`，**夸大正好 90 分钟 = 已发生的 Setup 占用**。
        ///   ⇒ 本属性改取 `可比窗口 − OccupiedMinutes`（**扣真实占用**），物理自洽。
        ///
        /// ⚠ **「未占用」不等于「已可排给其他任务」** —— 碎片化剩余窗口能否被其它工序实际利用，
        ///   还受 **Eligibility（资格）/ 最小合法时间段 / 工序依赖 / 资源日历** 制约。
        ///   ⇒ 本类**只**给出「日历未占用」这一**物理事实**，**不**宣称「可复用能力」；
        ///     真正可复用能力**不由本层单方面断言**（需上层候选构造）。
        /// </summary>
        public decimal? UnoccupiedMinutes =>
            WindowAvailable ? ComparableWindowMinutes - OccupiedMinutes : null;
    }

    /// <summary>
    /// **热路径**：只读任务 DTO，产出 Setup / 占用 / 真实资源集合。O(任务数)、无副作用、不读日历。
    /// </summary>
    internal static Occupancy Evaluate(IReadOnlyList<FinalTaskDraft> tasks)
    {
        var setup = 0m;
        var occupied = 0m;
        var affected = new List<int>();
        var nonResource = 0;

        foreach (var task in tasks)
        {
            // 非资源 Task（`ResourceId == null`）：不占设备 ⇒ 既不进分子也不进分母。
            //   ⚠ §3.3：**不得静默忽略** —— 计数并暴露（冻结判别器是工序的资源模式，
            //     `FinalTaskDraft` 不携带 ⇒ 本类无法自证；真实 Operation Task 缺资源身份须**可见**）。
            if (!task.ResourceId.HasValue)
            {
                nonResource++;
                continue;
            }

            setup += task.SetupTime;
            occupied += (decimal)(task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;

            if (!affected.Contains(task.ResourceId.Value))
            {
                affected.Add(task.ResourceId.Value);
            }
        }

        affected.Sort();
        return new Occupancy(setup, occupied, affected, nonResource);
    }

    /// <summary>
    /// **热路径**：比较两侧排序键。`+1` = **合批侧更优**（占用更少），`-1` = 独立侧更优，
    /// `0` = **指标相同／无增量差异**。
    /// ⚠ 符号约定与 <see cref="Describe"/> 的 `comparison > 0 ⇒ 合批更优` **一致**。
    /// ⚠ 只比较**除 Setup 外占用** ⇒ 不读日历 ⇒ 数据缺失**不会**把本层伪装成「不可评价」
    ///   （§一.3.2 逐字：「即使两方案用同一设备，负荷时间、日历空隙、Setup 也可能不同」⇒ 不得因集合相同就判不可评价）。
    /// </summary>
    internal static int Compare(Occupancy separate, Occupancy merge)
        => separate.NonSetupOccupiedMinutes.CompareTo(merge.NonSetupOccupiedMinutes);

    /// <summary>
    /// ④.6 层的**评价结论**（R2-B：把「有符号比较参数」换成**不可反用的枚举** —— 方向错了**表达不出来**）。
    ///
    /// 增量定义 = `合批侧排序键 − 独立侧排序键`（**负值 = 合批更省**）。
    /// 换算（<see cref="VerdictFromMergeIncrement"/> / <see cref="VerdictFromComparison"/>）是本类**唯一**的
    /// 方向换算口；调用点**不得**再自行 `CompareTo`（R2-B 缺陷正是调用点自行换算所致）。
    /// </summary>
    internal enum ResourceEfficiencyVerdict
    {
        /// <summary>未评价：真实输入缺项（`null` 增量）⇒ 只能记 `DATA_MISSING`，**不得**记「判平」。</summary>
        NotEvaluated = 0,

        /// <summary>已评价：**合批释放产能**（增量 &lt; 0 ⇒ 合批侧除 Setup 外占用更少）。</summary>
        MergeReleasesCapacity = 1,

        /// <summary>已评价：**合批多占产能**（增量 &gt; 0 ⇒ 独立排程侧除 Setup 外占用更少）。</summary>
        MergeConsumesCapacity = 2,

        /// <summary>已评价：**两侧指标相同**（增量 = 0）。</summary>
        Tie = 3
    }

    /// <summary>
    /// **增量 → 评价结论**（**唯一换算口**；R2-B）。
    ///   `null` ⇒ <see cref="ResourceEfficiencyVerdict.NotEvaluated"/>（真实缺项）；
    ///   `&lt; 0` ⇒ 合批释放产能；`&gt; 0` ⇒ 合批多占产能；`= 0` ⇒ 判平。
    /// </summary>
    internal static ResourceEfficiencyVerdict VerdictFromMergeIncrement(decimal? mergeIncrement)
    {
        if (!mergeIncrement.HasValue)
        {
            return ResourceEfficiencyVerdict.NotEvaluated;
        }

        var value = mergeIncrement.Value;
        if (value < 0m) return ResourceEfficiencyVerdict.MergeReleasesCapacity;
        if (value > 0m) return ResourceEfficiencyVerdict.MergeConsumesCapacity;
        return ResourceEfficiencyVerdict.Tie;
    }

    /// <summary>
    /// **比较器符号 → 评价结论**（与 <see cref="VerdictFromMergeIncrement"/> **同源同向**）。
    ///   <see cref="Compare"/> 的契约是「**正值 = 合批更优**」= 「合批侧占用更少」= 增量 &lt; 0。
    /// </summary>
    internal static ResourceEfficiencyVerdict VerdictFromComparison(int comparison)
    {
        if (comparison > 0) return ResourceEfficiencyVerdict.MergeReleasesCapacity;
        if (comparison < 0) return ResourceEfficiencyVerdict.MergeConsumesCapacity;
        return ResourceEfficiencyVerdict.Tie;
    }

    /// <summary>
    /// **候选释放量**（R2-A 要求的「候选释放量」项）：合批相对独立排程**少占**的除 Setup 外设备分钟数
    ///   = `独立侧排序键 − 合批侧排序键`（**正值 = 合批释放产能**）。
    ///
    /// ⚠ 这是**排序键之差**（§3.2 准确称谓：「相同有效产出下的非换型占用／释放产能代理比较」），
    ///   **不是**「真正可复用能力」—— 见 <see cref="Breakdown.UnoccupiedMinutes"/> 的同类告诫。
    /// </summary>
    internal static decimal ReleasedCapacityMinutes(Breakdown separate, Breakdown merge)
        => separate.NonSetupOccupiedMinutes - merge.NonSetupOccupiedMinutes;

    /// <summary>
    /// **R-2 设备口径**（1号位 2026-10-16 回执 §四 点名载体 `DomainSolveRequest.DeviceResourceIds`）：
    ///   同一侧任务在「**纯设备**」白名单口径下的排序键与任务计数。
    ///
    /// 【为什么需要第二个键】冻结 R-2 口径 =「**设备**有效稼动」，但 1号位 侧资源集合**含人工槽合成键**
    ///   （`ManualSlotResourceOffset + ManualSlotId`，由 2号位 装载侧注入）⇒ 单一排序键分不出
    ///   「含人工槽」与「纯设备」两个口径。白名单由 2号位 给出 —— 判据属**合成键规则**，
    ///   1号位 侧 `ManualSlot` / `1_000_000_000` grep = **0 命中** ⇒ 本类**不做资源身份判别、不硬编码任何阈值**
    ///   （守住 B-2.1 ②段「`ResourceId` 当不透明 int 消费」）。
    ///
    /// 【★ 该键**进入择优**（0号位 2026-10-16《第十三轮跨号位联合技术契约 v1.2 与 E1/E2/E3 正式复核意见
    ///   v1.0》§5.1 ／处置矩阵 **J13-05**，判定 **P1／合同内部冲突**，处置「按设备有效稼动边界收口」）】
    ///   判词逐字：「仅提供一个可空 `DeviceResourceIds` 载体，**不能证明 1号位 评分器在正式 Run 接收
    ///   非 NULL 纯设备集合并排除了人工槽**。R-2 的业务含义是**设备**有效加工与 Setup，**不应因兼容旧行为
    ///   将人工能力槽打分为设备稼动**。**应改**：Solver `Resources` 仍完整保留设备及人工两类合法容量；……
    ///   由 1号位 在**评分分支**使用该子集而非全量 `AffectedResources`；对旧 `null` 后备语义说明兼容使用的
    ///   安全边界，**不得默认让新正式 Run 一直走含人工路径**。」
    ///   ⇒ ④.6 的**排序键**三态（调用点见 `PhaseTwoInitialScheduler` 擂台循环）：
    ///     · `null`（**未投影**）⇒ 取 <see cref="Occupancy.NonSetupOccupiedMinutes"/>（**含人工槽**）—— 兼容边界；
    ///     · 非 `null` ⇒ 取本口径的**纯设备键**（<see cref="DeviceScopeNonSetupOccupiedMinutes"/>）；
    ///     · **空集 ≠ `null`** ⇒ 纯设备键恒 `0` ⇒ ④.6 判平（由 ⑤ Tie-breaker 接管），**不得**回落成「未投影」。
    ///   ⚠ 只收窄**评分子集**：`Resources` **仍完整保留**设备＋人工两类合法容量，人工槽**继续参与**
    ///     资格（Eligibility）与真实 Calendar —— 候选构造、可行性、履约判定**一律不动**。
    /// </summary>
    /// <param name="NonSetupOccupiedMinutes">白名单内任务的「除 Setup 外占用」分钟数（与默认键同式，越小越好）。</param>
    /// <param name="InScopeTaskCount">`ResourceId ∈ 白名单` 的 Task 数（**纯设备** Task 数）。</param>
    /// <param name="OutOfScopeTaskCount">`ResourceId != null` 但**不在**白名单的 Task 数（含人工槽合成键）。</param>
    internal sealed record DeviceScopeOccupancy(
        decimal NonSetupOccupiedMinutes,
        int InScopeTaskCount,
        int OutOfScopeTaskCount);

    /// <summary>
    /// **留痕路径（含任务计数）**：算「纯设备」口径排序键。**三态**（与 `DomainSolveRequest.DeviceResourceIds` 契约逐字对齐）：
    ///   · `null` ⇒ **未投影** ⇒ 返回 `null`（调用方**不产**留痕，行为与改造前**逐字一致**，零回归）；
    ///   · 非 `null` ⇒ 按白名单筛。**空集 ≠ `null`**：空集 ⇒ 纯设备键 = `0`、全部 Task 计**越界**，
    ///     **不得**回落成「未投影」语义（空集本身有信息量：本 Run 已投影、但一个纯设备都没有）。
    /// ⚠ `ResourceId == null`（非资源工序）在**两个口径都**不计 —— 与 <see cref="Evaluate"/> 同口径。
    /// ⚠ O(任务数)、不读日历、无副作用 ⇒ 用于落定后的留痕段。
    /// ⚠ **择优热路径**用 <see cref="DeviceScopeNonSetupOccupiedMinutes"/>（**同口径、不分配对象**）——
    ///   本方法**每调用一次新建一个 `HashSet`**，**不得**放进逐锚点擂台。
    /// </summary>
    internal static DeviceScopeOccupancy? EvaluateDeviceScope(
        IReadOnlyList<FinalTaskDraft> tasks,
        IReadOnlyCollection<int>? deviceResourceIds)
    {
        if (deviceResourceIds is null)
        {
            return null;   // 未投影 ⇒ 不留痕（零回归）
        }

        var scope = deviceResourceIds as HashSet<int> ?? new HashSet<int>(deviceResourceIds);
        var setup = 0m;
        var occupied = 0m;
        var inScope = 0;
        var outOfScope = 0;

        foreach (var task in tasks)
        {
            // 非资源 Task（`ResourceId == null`）：不占设备 ⇒ 两个口径都**不计**（同 `Evaluate`）。
            if (!task.ResourceId.HasValue)
            {
                continue;
            }

            if (!scope.Contains(task.ResourceId.Value))
            {
                outOfScope++;   // 含人工槽合成键（及任何非白名单身份）—— 只计数、不判别身份
                continue;
            }

            inScope++;
            setup += task.SetupTime;
            occupied += (decimal)(task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;
        }

        return new DeviceScopeOccupancy(occupied - setup, inScope, outOfScope);
    }

    /// <summary>
    /// **择优热路径的预建白名单**（J13-05）：每个 Run **只建一次**，供擂台循环里反复查询。
    /// 调用方**必须**先判 `DomainSolveRequest.DeviceResourceIds is not null` —— `null` 表示**未投影**，
    /// 本方法**不接受** `null`（避免把「未投影」静默归一成空集，破坏「空集 ≠ `null`」这条三态契约）。
    /// </summary>
    internal static HashSet<int> BuildDeviceScopeSet(IReadOnlyCollection<int> deviceResourceIds)
        => deviceResourceIds as HashSet<int> ?? new HashSet<int>(deviceResourceIds);

    /// <summary>
    /// **择优热路径**：纯设备口径的「除 Setup 外占用」分钟数。
    /// 与 <see cref="EvaluateDeviceScope"/> **同口径**（`ResourceId == null` 的非资源工序不计、
    /// 白名单外身份**含人工槽合成键**不计），只是**不产出计数**（计数只在留痕段需要）。
    ///
    /// ⚠ O(任务数)、**不分配对象**、不读日历 ⇒ 可安全放进逐锚点擂台（每锚点 3 次调用：
    ///   合批后完整链 / 锚批原量 / 本需求独立成批）。
    /// ⚠ **空集** ⇒ 恒返回 `0`（本 Run 无纯设备）⇒ 该层判平，**不得**回落成「未投影」语义。
    /// </summary>
    internal static decimal DeviceScopeNonSetupOccupiedMinutes(
        IReadOnlyList<FinalTaskDraft> tasks,
        HashSet<int> deviceScope)
    {
        var setup = 0m;
        var occupied = 0m;

        foreach (var task in tasks)
        {
            // 非资源 Task（`ResourceId == null`）：不占设备 ⇒ 不计（同 `Evaluate` / `EvaluateDeviceScope`）。
            if (!task.ResourceId.HasValue)
            {
                continue;
            }

            // 白名单外身份（真实形态 = 人工槽合成键）：**不判别身份、只按集合筛** ⇒ 不计。
            if (!deviceScope.Contains(task.ResourceId.Value))
            {
                continue;
            }

            setup += task.SetupTime;
            occupied += (decimal)(task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;
        }

        return occupied - setup;
    }

    /// <summary>
    /// **留痕文本**：把「纯设备」口径键与（`null` 时才入比较的）含人工槽键**并列**写出，
    ///   并**显式声明 ④.6 择优取纯设备键**（J13-05）。
    /// 两侧均 `null`（未投影）⇒ 返回**空串** ⇒ 既有留痕文本**逐字不变**（零回归）。
    /// </summary>
    internal static string DescribeDeviceScope(
        DeviceScopeOccupancy? incumbentDevice,
        decimal incumbentAllNonSetup,
        string incumbentLabel,
        DeviceScopeOccupancy? challengerDevice,
        decimal challengerAllNonSetup,
        string challengerLabel)
    {
        if (incumbentDevice is null && challengerDevice is null)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder("R-2 设备口径（`DeviceResourceIds` 已投影）：");
        AppendDeviceSide(text, incumbentLabel, incumbentDevice, incumbentAllNonSetup);
        text.Append("；");
        AppendDeviceSide(text, challengerLabel, challengerDevice, challengerAllNonSetup);
        text.Append(" ⇒ **④.6 择优取纯设备键**（0号位 J13-05）；含人工槽键仅作对照留痕。");
        return text.ToString();
    }

    private static void AppendDeviceSide(
        System.Text.StringBuilder text,
        string label,
        DeviceScopeOccupancy? device,
        decimal allNonSetup)
    {
        if (device is null)
        {
            text.Append($"{label}=未投影");
            return;
        }

        // 空集（已投影但本 Run 无纯设备）**显式写明**，避免被读成「键为 0 = 没占设备」。
        var empty = device.InScopeTaskCount == 0
            ? "（该侧**无纯设备**：纯设备键 0，全部 Task 计越界）"
            : string.Empty;
        text.Append(
            $"{label}=纯设备键 {device.NonSetupOccupiedMinutes} 分钟"
            + $"（含人工槽 {allNonSetup} 分钟；纯设备 Task {device.InScopeTaskCount} 个／越界 {device.OutOfScopeTaskCount} 个）{empty}");
    }

    /// <summary>
    /// **留痕路径**：两侧候选的**公共可比窗口**（分钟）与可用性。取**真实资源集合的并集**，
    /// 逐资源把真实日历与「两侧任务公共包络」求交后相加 ⇒ **两侧分母逐字相同**。
    /// </summary>
    internal static (decimal Minutes, bool Available) ComputeComparableWindow(
        IReadOnlyList<FinalTaskDraft> separateTasks,
        IReadOnlyList<FinalTaskDraft> mergeTasks,
        ConstraintContext constraints)
    {
        var affected = UnionResources(separateTasks, mergeTasks);

        // 公共包络 = 两侧全部任务的最早开始 → 最晚完成。两侧**同一**跨度 ⇒ 分母逐字相同。
        var all = separateTasks.Concat(mergeTasks).ToList();
        if (all.Count == 0 || affected.Count == 0)
        {
            return (0m, false);
        }

        var spanStart = all.Min(t => t.PlannedStartTime);
        var spanEnd = all.Max(t => t.PlannedEndTime);

        var minutes = 0m;
        var available = true;
        foreach (var resourceId in affected)
        {
            if (!constraints.ResourceCalendars.TryGetValue(resourceId, out var windows) || windows.Count == 0)
            {
                // 缺该资源的真实日历 ⇒ 窗口不可用 ⇒ 如实记「数据缺失」，**不编造**分母。
                available = false;
                continue;
            }

            foreach (var window in windows)
            {
                // 与公共包络**有交集**的整扇日历窗**全额**计入（不是只算交集部分）：
                //   · 与 §一.2 算例的「可用窗口均 8h」同口径 ⇒ 6h 有效加工 = 75%（而非按包络算出的其它数）；
                //   · 两侧**共用**同一窗口 ⇒ **不因某候选更早完工而缩小分母**（§一.2 明令禁止的「人为缩小分母」）；
                //   · 窗口仍**受包络约束**（只取与包络相交者）⇒ 不扩张到整个规划期。
                if (window.End > spanStart && window.Start < spanEnd)
                {
                    minutes += (decimal)(window.End - window.Start).TotalMinutes;
                }
            }
        }

        return (minutes, available);
    }

    /// <summary>
    /// **留痕路径**：单侧的完整拆分（含有效加工与等待/中断）。读日历，**只在落定时调用**。
    /// </summary>
    internal static Breakdown BreakdownFor(
        IReadOnlyList<FinalTaskDraft> tasks,
        decimal comparableWindowMinutes,
        bool windowAvailable,
        ConstraintContext constraints)
    {
        var processing = 0m;
        var setup = 0m;
        var occupied = 0m;
        var nonResource = 0;

        foreach (var task in tasks)
        {
            if (!task.ResourceId.HasValue)
            {
                nonResource++;   // §3.3：**不得静默忽略**（见 Evaluate 同注）。
                continue;
            }

            var span = (decimal)(task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;
            setup += task.SetupTime;
            occupied += span;

            // 有效加工 = 占用 − Setup − **跨日历中断**（窗口之间的空隙）。
            //   ⚠ 窗口内不插入等待 —— 见类注释「数据来源与假设」，由 `RE_06` 钉住。
            //   ⚠ 合法 Task 不跨窗（`FindForwardSlot` 只在单窗内取槽）⇒ 常规路径该项为 0（见类注释 §3.3④）。
            processing += span - task.SetupTime - CrossCalendarInterrupt(task, constraints);
        }

        var waiting = occupied - processing - setup;
        if (waiting < 0m)
        {
            waiting = 0m;
        }

        return new Breakdown(
            processing,
            setup,
            occupied,
            waiting,
            comparableWindowMinutes,
            UnionResources(tasks, Array.Empty<FinalTaskDraft>()),
            windowAvailable,
            nonResource);
    }

    /// <summary>
    /// ④.6 的**落痕判定**：由**评价结论**给出应当记录的追溯码（R2-C：**不再**用「差额非零」冒充
    ///   「本层决定了胜负」—— 是否「决定了胜负」由**调用点**用「比较器落点层」判定，本方法只判「评价结论」）。
    ///
    /// 三值**穷尽**且互斥：
    ///   · <see cref="ResourceEfficiencyVerdict.NotEvaluated"/> ⇒ `DATA_MISSING`（**真实输入缺项**）；
    ///   · 合批释放产能 / 合批多占产能 ⇒ `INCREMENT`（④.6 **确有增量差异**，且本层被比较器落到）；
    ///   · <see cref="ResourceEfficiencyVerdict.Tie"/> ⇒ `NO_INCREMENT`（**确实评价且判平**）。
    ///
    /// ⚠ `windowAvailable` **不再是**本判定的输入（§3.4：排序键**只读 DTO 不读日历**）——
    ///   日历缺项**不得**把「本层不可评价」伪装出来；它只影响留痕文本里的**比率**是否可算。
    /// </summary>
    internal static string VerdictTraceCode(ResourceEfficiencyVerdict verdict)
    {
        switch (verdict)
        {
            case ResourceEfficiencyVerdict.MergeReleasesCapacity:
            case ResourceEfficiencyVerdict.MergeConsumesCapacity:
                return TraceIncrement;
            case ResourceEfficiencyVerdict.Tie:
                return TraceNoIncrement;
            default:
                return TraceDataMissing;
        }
    }

    /// <summary>
    /// 留痕的一行人类可读说明（§一.3.4：指标相同时必须写明「指标相同／无增量差异」）。
    ///
    /// ⚠ 入参是**评价结论枚举**（R2-B）：胜者文案由枚举唯一决定 ⇒ 「方向反了写成另一个胜者」在类型上不可表达。
    ///
    /// <para>
    /// R2-C（第六轮 §3.2：留痕须含**锚点身份**）：<paramref name="separateLabel"/> /
    /// <paramref name="mergeLabel"/> 允许调用方把两侧的**真实对手身份**（如
    /// 「统一基线（未合批）」/「合批到锚#2[BATCH-7]」）写进胜者文案，避免读者把「合批」误读成
    /// 「相对**独立基线**」，而真实比较对手其实是**上一位合批擂主**。
    /// **默认值保持与原实现逐字一致**（既有调用点与既有断言零回归）。
    /// </para>
    /// </summary>
    internal static string Describe(
        ResourceEfficiencyVerdict verdict,
        Breakdown separate,
        Breakdown merge,
        string separateLabel = "独立排程",
        string mergeLabel = "合批")
    {
        var suffix = separate.WindowAvailable && merge.WindowAvailable
            ? $"有效稼动率 {Format(separate.EffectiveUtilization)} vs {Format(merge.EffectiveUtilization)}"
            : "有效稼动率不可算：真实资源日历缺项（**未编造比率**）";

        // §3.3：无资源身份 Task **不得静默忽略** ⇒ 只要任一侧存在，就**显式记进留痕文本**。
        //   （冻结口径下它们是非资源工序，不占设备；此处只把该事实**写明**，使其可被下游核对。）
        var nonResource = separate.NonResourceTaskCount > 0 || merge.NonResourceTaskCount > 0
            ? $"；两侧含无资源身份 Task（独立 {separate.NonResourceTaskCount} / 合批 {merge.NonResourceTaskCount}），"
              + "按冻结口径（UNCONSTRAINED/WAIT_ONLY 非资源工序）不计设备占用"
            : string.Empty;

        // R2-A：真实日历未占用是**物理事实**；「未占用 ≠ 可复用」须在文本里写明，不得被读成可用余量。
        var physical = separate.UnoccupiedMinutes.HasValue && merge.UnoccupiedMinutes.HasValue
            ? $"；真实日历未占用 {separate.UnoccupiedMinutes.Value} vs {merge.UnoccupiedMinutes.Value} 分钟"
              + "（**未占用 ≠ 可复用**：受 Eligibility／最小合法段／依赖／日历制约）"
            : string.Empty;

        switch (verdict)
        {
            case ResourceEfficiencyVerdict.NotEvaluated:
                return $"资源效率：**未评价**（真实输入缺项，不得记为判平）（{suffix}）{nonResource}";

            case ResourceEfficiencyVerdict.Tie:
                return $"资源效率：指标相同／无增量差异（除Setup外占用 {separate.NonSetupOccupiedMinutes} vs "
                     + $"{merge.NonSetupOccupiedMinutes} 分钟；{suffix}）{physical}{nonResource}";

            case ResourceEfficiencyVerdict.MergeReleasesCapacity:
            {
                var released = ReleasedCapacityMinutes(separate, merge);
                // ⚠ §3.2：本句**只**宣称「除 Setup 外占用更少 ⇒ 释放产能」，**不得**反向宣称其有效稼动率更高
                //   （两侧比率按**原样**列出，方向可能与本句相反 —— 见 `RE_11`）。
                return $"资源效率：{mergeLabel}在真实资源集合上除Setup外占用更少"
                     + $"（{merge.NonSetupOccupiedMinutes} < {separate.NonSetupOccupiedMinutes} 分钟）"
                     + $" ⇒ 少占 {released} 分钟设备时间（{suffix}）{physical}{nonResource}";
            }

            default:
            {
                // 合批**多占** ⇒ 差值取相反号（`ReleasedCapacityMinutes` 在此为负）⇒ 文案写「独立排程少占 |差|」。
                var extra = merge.NonSetupOccupiedMinutes - separate.NonSetupOccupiedMinutes;
                return $"资源效率：{separateLabel}在真实资源集合上除Setup外占用更少"
                     + $"（{separate.NonSetupOccupiedMinutes} < {merge.NonSetupOccupiedMinutes} 分钟）"
                     + $" ⇒ 少占 {extra} 分钟设备时间（{suffix}）{physical}{nonResource}";
            }
        }
    }

    private static string Format(decimal? value)
        => value.HasValue ? value.Value.ToString("0.####") : "n/a";

    private static List<int> UnionResources(
        IReadOnlyList<FinalTaskDraft> left,
        IReadOnlyList<FinalTaskDraft> right)
    {
        // 真实资源集合 = 两侧候选任务的**并集**。
        //   ⚠ 0号位 §一.3.2 逐字：「"资源ID集合的对称差"不能定义受影响资源：即使两方案用同一设备，
        //     负荷时间、日历空隙、Setup 也可能不同」⇒ 取并集，且**不因两侧集合相同就判"不可评价"**。
        var affected = new List<int>();
        foreach (var task in left.Concat(right))
        {
            if (task.ResourceId.HasValue && !affected.Contains(task.ResourceId.Value))
            {
                affected.Add(task.ResourceId.Value);
            }
        }

        affected.Sort();
        return affected;
    }

    /// <summary>两区间重叠分钟数（无重叠 ⇒ 0）。</summary>
    private static decimal OverlapMinutes(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        return end > start ? (decimal)(end - start).TotalMinutes : 0m;
    }

    /// <summary>
    /// Task 区间内**不在任何合法日历窗**中的分钟数（跨日历中断）。
    /// 无该资源日历事实 ⇒ 返回 0（由 <see cref="Breakdown.WindowAvailable"/> 另行标记为不可评价）。
    /// </summary>
    private static decimal CrossCalendarInterrupt(FinalTaskDraft task, ConstraintContext constraints)
    {
        if (!constraints.ResourceCalendars.TryGetValue(task.ResourceId!.Value, out var windows)
            || windows.Count == 0)
        {
            return 0m;
        }

        var covered = 0m;
        foreach (var window in windows)
        {
            covered += OverlapMinutes(window.Start, window.End, task.PlannedStartTime, task.PlannedEndTime);
        }

        var span = (decimal)(task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;
        var interrupt = span - covered;
        return interrupt > 0m ? interrupt : 0m;
    }
}
