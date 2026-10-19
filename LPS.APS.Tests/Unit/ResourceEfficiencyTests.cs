using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R-2「资源效率 ＝ 设备有效稼动」的**可执行反例**（0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》
/// §一.3.3 逐字要求的 5 条）＋ 若干口径钉桩（<c>RE_06</c> 假设、<c>RE_09</c> 不重复计 Setup）。
///
/// 【§一.3.3 逐字对照】
///   ① 「加工量同换型不同 ⇒ 稼动率数值相同，不得宣称差异来自稼动率」 → <c>RE_01</c>
///   ② 「设备相同但窗口占用不同 ⇒ 不能因资源ID集合相同判『不可评价』」 → <c>RE_02</c>
///   ③ 「表面占用率高但多做无需求生产或妨碍紧急履约 ⇒ 不能胜出」 → <c>RE_03</c>
///   ④ 「合法日历含停机/跨班 ⇒ 不能统一按 8 小时」 → <c>RE_04</c>
///   ⑤ 「若某候选确实提高有效稼动 ⇒ 应有可解释正例」 → <c>RE_05</c>
///
/// ⚠ 本文件是**纯函数/纯内存**级反例：直接喂 <see cref="ResourceEfficiencyEvaluator"/> 与
///   <see cref="PhaseTwoInitialScheduler.PreferMergeByFrozenLayers"/>，不建全量 `ConstraintContext`、
///   不触库、不跑端到端。
/// </summary>
public class ResourceEfficiencyTests
{
    private static readonly DateTime Day = new DateTime(2026, 9, 1, 0, 0, 0);

    private static FinalTaskDraft Task(
        int? resourceId,
        DateTime start,
        DateTime end,
        decimal setup = 0m,
        string draftId = "T")
        => new FinalTaskDraft
        {
            FinalDraftId = draftId,
            SourceDraftId = draftId,
            ResourceId = resourceId,
            Quantity = 1m,
            PlannedProcessQty = 1m,
            PlannedStartTime = start,
            PlannedEndTime = end,
            SetupTime = setup,
            ExecutionBatchDraftKey = draftId
        };

    private static ConstraintContext Ctx(params (int ResourceId, DateTime Start, DateTime End)[] windows)
    {
        var ctx = new ConstraintContext();
        foreach (var group in windows.GroupBy(w => w.ResourceId))
        {
            ctx.ResourceCalendars[group.Key] =
                group.Select(w => new TimeWindow(w.Start, w.End)).ToList();
        }

        return ctx;
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_01（§一.3.3①）：加工量同、换型不同 ⇒ **稼动率数值相同**，不得宣称差异来自稼动率
    //   §一.2 算例逐字：可用窗口均 8h、有效加工均 6h；A 换型 1.5h、B 换型 0.5h ⇒ **A、B 均为 75%**。
    //   A 的额外 1h 占用是**换型**，B 省下的 1h 是**释放的产能** —— 二者**只能**记在 Setup 层（④）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_01_加工量同换型不同_稼动率必须相同_差异只能记在Setup层()
    {
        var ctx = Ctx((1, Day, Day.AddHours(8)));            // 可用窗口均 8h（480 min）

        var a = new[] { Task(1, Day, Day.AddMinutes(450), setup: 90m, draftId: "A") };   // 换型 1.5h
        var b = new[] { Task(1, Day, Day.AddMinutes(390), setup: 30m, draftId: "B") };   // 换型 0.5h

        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(a, b, ctx);
        Assert.True(available);
        Assert.Equal(480m, window);                          // **整扇窗**计入 ⇒ 与算例「可用窗口 8h」同口径

        var breakdownA = ResourceEfficiencyEvaluator.BreakdownFor(a, window, available, ctx);
        var breakdownB = ResourceEfficiencyEvaluator.BreakdownFor(b, window, available, ctx);

        Assert.Equal(360m, breakdownA.EffectiveProcessingMinutes);   // 两侧有效加工均 6h
        Assert.Equal(360m, breakdownB.EffectiveProcessingMinutes);
        Assert.Equal(0.75m, breakdownA.EffectiveUtilization);        // **均为 75%**
        Assert.Equal(0.75m, breakdownB.EffectiveUtilization);
        Assert.Equal(breakdownA.EffectiveUtilization, breakdownB.EffectiveUtilization);

        // ⚠ 反证判词：**不得**把 B 的优势写成「稼动率更高」—— 两侧**逐字相同**。
        //   真实差异是 Setup：A 90min vs B 30min ⇒ 只能由第 ④ 层消费。
        Assert.Equal(90m, breakdownA.SetupMinutes);
        Assert.Equal(30m, breakdownB.SetupMinutes);
        Assert.NotEqual(breakdownA.SetupMinutes, breakdownB.SetupMinutes);
        // 且「除 Setup 外占用」两侧也相同 ⇒ ④.6 在此**不得**再记一次收益（§一.2.5 禁止重复计分）。
        Assert.Equal(breakdownA.NonSetupOccupiedMinutes, breakdownB.NonSetupOccupiedMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_02（§一.3.3②）：**设备相同、窗口占用不同** ⇒ 资源集合相同**不构成**「不可评价」
    //   §一.3.2 逐字：「"资源ID集合的对称差"不能定义受影响资源：即使两方案用同一设备，
    //     负荷时间、日历空隙、Setup 也可能不同」。对称差在两侧同设备时恒为空 ⇒ 旧判据会误判「不可评价」。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_02_两侧同一设备但占用不同_资源集合相同不得判不可评价()
    {
        // 两侧**只用** R1 ⇒ 对称差 = ∅。若按对称差判「不可评价」，本层将永远沉默。
        var separate = new[] { Task(1, Day, Day.AddMinutes(100), draftId: "S") };
        var merge = new[] { Task(1, Day, Day.AddMinutes(40), draftId: "M") };

        var separateOccupancy = ResourceEfficiencyEvaluator.Evaluate(separate);
        var mergeOccupancy = ResourceEfficiencyEvaluator.Evaluate(merge);

        Assert.Equal(new[] { 1 }, separateOccupancy.AffectedResources);
        Assert.Equal(new[] { 1 }, mergeOccupancy.AffectedResources);

        var comparison = ResourceEfficiencyEvaluator.Compare(separateOccupancy, mergeOccupancy);
        Assert.NotEqual(0, comparison);                      // **可评价**，且合批更优
        Assert.Equal(1, comparison);
        Assert.Equal(60m, separateOccupancy.NonSetupOccupiedMinutes - mergeOccupancy.NonSetupOccupiedMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_03（§一.3.3③）：**表面占用更少但妨碍紧急履约 ⇒ 不得胜出**
    //   合批在 ④.6 更优（占用 50 < 100），但它在第 ② 层就延期 ⇒ 层序保证它**必须**落败。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_03_占用更少但妨碍履约_不得胜出()
    {
        var mergeWins = PhaseTwoInitialScheduler.PreferMergeByFrozenLayers(
            separateFeasible: true,
            mergeFeasible: true,
            delaySeparate: 10,                               // 独立排程延期 10min
            delayMerge: 0,                                   // 合批按期
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: Day.AddMinutes(100),
            mergeEnd: Day.AddMinutes(50),
            separateSetup: 0m,
            mergeSetup: 0m,
            separateEconDev: default,
            mergeEconDev: default,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 50m);               // ④.6 偏合批，但 ② 已定胜负

        Assert.True(mergeWins);                              // ② 在先 ⇒ 合批胜出

        // 反向：合批**延期** ⇒ 即便 ④.6 更优也必须落败。
        Assert.False(PhaseTwoInitialScheduler.PreferMergeByFrozenLayers(
            separateFeasible: true,
            mergeFeasible: true,
            delaySeparate: 0,
            delayMerge: 10,
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: Day.AddMinutes(50),
            mergeEnd: Day.AddMinutes(100),
            separateSetup: 0m,
            mergeSetup: 0m,
            separateEconDev: default,
            mergeEconDev: default,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 50m));
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_04（§一.3.3④）：**合法日历含停机/跨班 ⇒ 不得统一按 8 小时**
    //   真实三级日历 = 两扇 6h 窗（中间 4h 停机）。Task 跨窗 ⇒ 那 4h **不是**加工。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_04_日历含停机跨班_不得统一按8小时_跨窗中断须从有效加工扣除()
    {
        // 窗① 08:00-14:00、窗② 18:00-24:00（中间 14:00-18:00 = 停机 4h）。
        var ctx = Ctx(
            (1, Day.AddHours(8), Day.AddHours(14)),
            (1, Day.AddHours(18), Day.AddHours(24)));

        // Task 从 13:00 排到 19:00（跨过停机）：跨度 6h，真实加工 = 1h + 1h = 2h。
        var crossing = new[] { Task(1, Day.AddHours(13), Day.AddHours(19), draftId: "X") };
        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(crossing, crossing, ctx);
        Assert.True(available);
        Assert.Equal(720m, window);                          // 6h + 6h = 12h（**不是** 8h）

        var breakdown = ResourceEfficiencyEvaluator.BreakdownFor(crossing, window, available, ctx);
        Assert.Equal(360m, breakdown.OccupiedMinutes);       // 占用（PlannedEnd − PlannedStart）= 6h
        Assert.Equal(240m, breakdown.WaitingOrInterruptMinutes);   // 停机 4h **单列**
        Assert.Equal(120m, breakdown.EffectiveProcessingMinutes);  // 有效加工仅 2h
        // ⚠ 若有人把 `PlannedEnd − PlannedStart` 当有效加工 ⇒ 会得 360min（**红**）。
        Assert.NotEqual(breakdown.OccupiedMinutes, breakdown.EffectiveProcessingMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_05（§一.3.3⑤）：**确有正例** —— 占用更少者胜出，且说明可解释
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_05_确有增量差异时_占用更少者胜出且说明可解释()
    {
        // 两侧同设备、同 Setup（④ 判平）、同完工（③ 判平）⇒ 胜负**只能**由 ④.6 决定。
        var separate = new[] { Task(1, Day, Day.AddMinutes(100), draftId: "S") };
        var merge = new[] { Task(1, Day, Day.AddMinutes(40), draftId: "M") };

        var separateOccupancy = ResourceEfficiencyEvaluator.Evaluate(separate);
        var mergeOccupancy = ResourceEfficiencyEvaluator.Evaluate(merge);
        var comparison = ResourceEfficiencyEvaluator.Compare(separateOccupancy, mergeOccupancy);

        Assert.True(PhaseTwoInitialScheduler.PreferMergeByFrozenLayers(
            separateFeasible: true,
            mergeFeasible: true,
            delaySeparate: 0,
            delayMerge: 0,
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: Day,
            mergeEnd: Day,
            separateSetup: 0m,
            mergeSetup: 0m,
            separateEconDev: default,
            mergeEconDev: default,
            separateNonSetupOccupiedMinutes: separateOccupancy.NonSetupOccupiedMinutes,
            mergeNonSetupOccupiedMinutes: mergeOccupancy.NonSetupOccupiedMinutes));

        // 可解释：必须点出**真实资源集合**上的占用差与释放产能，而不是空话。
        var ctx = Ctx((1, Day, Day.AddHours(8)));
        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(separate, merge, ctx);
        // R2-B：入参改为**评价结论枚举**（由比较器符号唯一换算，调用点不得自行 `CompareTo`）。
        var verdict = ResourceEfficiencyEvaluator.VerdictFromComparison(comparison);
        var message = ResourceEfficiencyEvaluator.Describe(
            verdict,
            ResourceEfficiencyEvaluator.BreakdownFor(separate, window, available, ctx),
            ResourceEfficiencyEvaluator.BreakdownFor(merge, window, available, ctx));

        Assert.Contains("合批", message);
        Assert.Contains("少占 60 分钟设备时间", message);
        Assert.Equal(ResourceEfficiencyEvaluator.TraceIncrement,
            ResourceEfficiencyEvaluator.VerdictTraceCode(verdict));
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_06：钉住 V1 的**明确假设** —— 「Task 紧贴放置在合法窗口内，窗口内不插入等待」
    //   若将来引入窗口内等待，本用例会**红** ⇒ 必须把有效加工改为按
    //   `StandardDuration × PlannedProcessQty ÷ CapacityFactor` 重算（见类注释「数据来源与假设」）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_06_窗口内紧贴放置假设_有效加工等于占用扣Setup扣跨窗中断()
    {
        var ctx = Ctx(
            (1, Day, Day.AddHours(6)),
            (1, Day.AddHours(10), Day.AddHours(16)));

        // 完全落在窗① 内（无跨窗中断）⇒ 有效加工 = 占用 − Setup。
        var insideWindow = new[] { Task(1, Day.AddHours(1), Day.AddHours(3), setup: 30m, draftId: "W") };
        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(insideWindow, insideWindow, ctx);
        var breakdown = ResourceEfficiencyEvaluator.BreakdownFor(insideWindow, window, available, ctx);

        Assert.Equal(120m, breakdown.OccupiedMinutes);
        Assert.Equal(30m, breakdown.SetupMinutes);
        Assert.Equal(90m, breakdown.EffectiveProcessingMinutes);
        Assert.Equal(0m, breakdown.WaitingOrInterruptMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_07：**非资源 Task**（`ResourceId == null`）不占设备 ⇒ 既不进分子也不进分母
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_07_非资源Task不占设备_不进分子也不进分母()
    {
        var withManual = new[]
        {
            Task(1, Day, Day.AddMinutes(60), draftId: "R"),
            Task(null, Day, Day.AddHours(5), draftId: "MANUAL")   // 非资源工序
        };

        var occupancy = ResourceEfficiencyEvaluator.Evaluate(withManual);
        Assert.Equal(new[] { 1 }, occupancy.AffectedResources);   // 资源集合只含 1
        Assert.Equal(60m, occupancy.OccupiedMinutes);             // 人工工序的 5h **不计入**

        var ctx = Ctx((1, Day, Day.AddHours(8)));
        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(withManual, withManual, ctx);
        Assert.Equal(480m, window);                               // 分母只由真实资源日历决定
        Assert.True(available);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_08：**缺真实日历 ⇒ 不编造比率**（§一.3.4）
    //   但**排序键仍可算**（它只读 DTO）⇒ 数据缺失**不得**被用来把本层伪装成「不可评价」而沉默。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_08_缺日历不得编造比率_但排序键仍可算且须留痕()
    {
        var ctx = new ConstraintContext();                        // **无任何日历事实**
        var separate = new[] { Task(7, Day, Day.AddMinutes(100), draftId: "S") };
        var merge = new[] { Task(7, Day, Day.AddMinutes(40), draftId: "M") };

        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(separate, merge, ctx);
        Assert.False(available);

        var breakdown = ResourceEfficiencyEvaluator.BreakdownFor(separate, window, available, ctx);
        Assert.Null(breakdown.EffectiveUtilization);              // **不得**编造比率
        Assert.Null(breakdown.UnoccupiedMinutes);                 // R2-A：缺日历 ⇒ 真实未占用亦**不编造**

        // 排序键不依赖日历 ⇒ 仍可评价 ⇒ 仍能识别合批更省占用。
        var comparison = ResourceEfficiencyEvaluator.Compare(
            ResourceEfficiencyEvaluator.Evaluate(separate),
            ResourceEfficiencyEvaluator.Evaluate(merge));
        Assert.Equal(1, comparison);

        // ── R2-C：追溯码**只**由「评价结论」决定，**不再**由「日历是否可用」决定 ──
        //   （§3.4：排序键只读 DTO 不读日历 ⇒ 日历缺项**不得**把本层伪装成「不可评价」）
        //   真实缺项（`null` 增量）⇒ `DATA_MISSING`；判平 ⇒ `NO_INCREMENT`；有差异 ⇒ `INCREMENT`。
        Assert.Equal(ResourceEfficiencyEvaluator.TraceDataMissing,
            ResourceEfficiencyEvaluator.VerdictTraceCode(
                ResourceEfficiencyEvaluator.VerdictFromMergeIncrement(null)));
        Assert.Equal(ResourceEfficiencyEvaluator.TraceNoIncrement,
            ResourceEfficiencyEvaluator.VerdictTraceCode(
                ResourceEfficiencyEvaluator.VerdictFromMergeIncrement(0m)));
        Assert.Equal(ResourceEfficiencyEvaluator.TraceIncrement,
            ResourceEfficiencyEvaluator.VerdictTraceCode(
                ResourceEfficiencyEvaluator.VerdictFromMergeIncrement(-60m)));
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_09：**不重复计 Setup**（§一.2.5）—— ④.6 排序键**已扣 Setup**
    //   ⚠ 可证：④.6 只在第 ④ 层（Setup 总量）**判平**时才被执行 ⇒ 两侧 Setup **相等** ⇒
    //     在此层 `Occupied` 与 `NonSetupOccupied` **同序**；取后者是**显式声明**不与 ④ 重复计分。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_09_排序键已扣Setup_不重复计分()
    {
        var t = new[] { Task(1, Day, Day.AddMinutes(100), setup: 40m, draftId: "S") };
        var occupancy = ResourceEfficiencyEvaluator.Evaluate(t);

        Assert.Equal(100m, occupancy.OccupiedMinutes);
        Assert.Equal(40m, occupancy.SetupMinutes);
        Assert.Equal(60m, occupancy.NonSetupOccupiedMinutes);     // **已扣 Setup**

        // 反证：若排序键误用 `Occupied`，则「靠加大 Setup 抬占用」会被当成低效 ⇒ 与第 ④ 层同向重复。
        Assert.NotEqual(occupancy.OccupiedMinutes, occupancy.NonSetupOccupiedMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_10：追溯码**取值钉住**且三码互异（防将来被改名/合并）
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_10_追溯码取值钉住且互异()
    {
        Assert.Equal("RESOURCE_EFFICIENCY_INCREMENT", ResourceEfficiencyEvaluator.TraceIncrement);
        Assert.Equal("RESOURCE_EFFICIENCY_NO_INCREMENT", ResourceEfficiencyEvaluator.TraceNoIncrement);
        Assert.Equal("RESOURCE_EFFICIENCY_DATA_MISSING", ResourceEfficiencyEvaluator.TraceDataMissing);

        var codes = new[]
        {
            ResourceEfficiencyEvaluator.TraceIncrement,
            ResourceEfficiencyEvaluator.TraceNoIncrement,
            ResourceEfficiencyEvaluator.TraceDataMissing
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());

        // ⚠ 与冻结 DDL `ScenarioObjectiveScore.ObjectiveName` 的场景级指标名 `RESOURCE_EFFICIENCY`
        //   **不是同一个东西**（后者由下游按场景汇总）⇒ 三个追溯码都**不等于**它。
        Assert.DoesNotContain(codes, c => c == "RESOURCE_EFFICIENCY");
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_11（0号位 2026-10-14《致1号位第四轮整改回执正式复核意见 v1.0》§3.2 逐字算例）：
    //   **④.6 排序键 ≠ 有效稼动率**，且两者**方向相反**。
    //     合法可用窗口均 480min、合格产出均 100 件、Setup 均 0；A 加工 360min、B 加工 300min。
    //     · 有效稼动率：A = **75%**、B = **62.5%**（**A 更高**）；
    //     · ④.6 排序键（除 Setup 外占用，**越小越好**）：A 360 > B 300 ⇒ **选 B**。
    //   ⇒ ④.6 选 B 的含义是「**同需求更省 60min 设备分钟、释放 60min 合法可用产能**」，
    //     **不是**「B 的稼动率更高」。本用例钉住三件事：
    //       ① 实际选优方向 = 释放可用产能；② 对外文本**不反向宣称**比率更高；③ 不影响上层层序。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_11_同净产出下_第46层按释放产能选优_但不得反向宣称有效稼动率更高()
    {
        var ctx = Ctx((1, Day, Day.AddHours(8)));                                  // 480 min

        var a = new[] { Task(1, Day, Day.AddMinutes(360), draftId: "A") };         // 加工 360
        var b = new[] { Task(1, Day, Day.AddMinutes(300), draftId: "B") };         // 加工 300

        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(a, b, ctx);
        Assert.True(available);
        Assert.Equal(480m, window);                                                // 两侧**同一**分母

        var ba = ResourceEfficiencyEvaluator.BreakdownFor(a, window, available, ctx);
        var bb = ResourceEfficiencyEvaluator.BreakdownFor(b, window, available, ctx);

        // ① 有效稼动率：**A 更高**（75% > 62.5%）—— 与排序键方向**相反**（本用例核心）。
        Assert.Equal(360m, ba.EffectiveProcessingMinutes);
        Assert.Equal(300m, bb.EffectiveProcessingMinutes);
        Assert.Equal(0.75m, ba.EffectiveUtilization);
        Assert.Equal(0.625m, bb.EffectiveUtilization);
        Assert.True(ba.EffectiveUtilization > bb.EffectiveUtilization);

        // ② ④.6 排序键「除 Setup 外占用」**越小越好** ⇒ 选 B（300 < 360）。
        var comparison = ResourceEfficiencyEvaluator.Compare(
            ResourceEfficiencyEvaluator.Evaluate(a),
            ResourceEfficiencyEvaluator.Evaluate(b));
        Assert.Equal(1, comparison);                                               // +1 = 合批（B 侧）更优

        // ── R2-A：真实日历未占用 = 窗口 − **实际占用**（**不是** 窗口 − 非Setup占用）──
        //   A 占用 360 ⇒ 未占用 120；B 占用 300 ⇒ 未占用 180。
        Assert.Equal(120m, ba.UnoccupiedMinutes!.Value);
        Assert.Equal(180m, bb.UnoccupiedMinutes!.Value);
        // 候选释放量 = 排序键之差（独立侧 − 合批侧）= 60min —— 这才是选 B 的**真实理由**。
        Assert.Equal(60m, ResourceEfficiencyEvaluator.ReleasedCapacityMinutes(ba, bb));
        // ⚠ 反证：旧式 `窗口 − 非Setup占用` 在**有 Setup** 时会把 Setup 分钟也算成可释放（见 RE_15）。

        // ③ 层序：①②③④④.5 全平 ⇒ 胜负**只能**由 ④.6 决定（B 胜出）。
        Assert.True(PhaseTwoInitialScheduler.PreferMergeByFrozenLayers(
            separateFeasible: true, mergeFeasible: true,
            delaySeparate: 0, delayMerge: 0,
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: Day, mergeEnd: Day,
            separateSetup: 0m, mergeSetup: 0m,
            separateEconDev: default, mergeEconDev: default,
            separateNonSetupOccupiedMinutes: 360m,                                 // A（独立侧）
            mergeNonSetupOccupiedMinutes: 300m));                                  // B（合批侧）

        // ④ 对外文本**不作反向宣称**：只讲「除 Setup 外占用更少 ⇒ 少占 60 分钟设备时间」，
        //    并把两侧比率**原样**列出（0.75 vs 0.625 —— 与胜出方向**相反**，正是要点）。
        var message = ResourceEfficiencyEvaluator.Describe(
            ResourceEfficiencyEvaluator.VerdictFromComparison(comparison), ba, bb);
        Assert.Contains("除Setup外占用更少", message);
        Assert.Contains("少占 60 分钟设备时间", message);
        Assert.Contains("0.75 vs 0.625", message);
        Assert.DoesNotContain("稼动率更高", message);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_12（0号位 §3.4）：**真实 Calendar 缺失时硬合法阻断** —— ④.6 **永远看不到**该候选
    //   0号位逐字：「若 Calendar 缺失导致实际候选并未经过完整硬合法验证，不能先'排序成功'再事后写
    //     `RESOURCE_EFFICIENCY_DATA_MISSING`，更不能据此生成正式排程」。
    //   本用例证明这在现行实现里**结构上不可能**：无日历 ⇒ `FindForwardSlot` 返回 `null`
    //     ⇒ 该资源上**排不出 Task** ⇒ 候选根本不进 ④.6（与 `RE_08` 的「留痕不可算」是两件事）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_12_真实日历缺失_硬合法槽查找先阻断_第46层永远看不到该候选()
    {
        var noCalendar = new ConstraintContext();                 // **无任何日历事实**

        var blocked = PhaseFourLocalRepair.FindForwardSlot(
            Day, TimeSpan.FromMinutes(60), 7, noCalendar,
            new Dictionary<int, List<TimeWindow>>(), Day.AddDays(90));
        Assert.Null(blocked);                                     // **硬合法阻断**

        // 正例对照：同一调用在**有**真实日历时必须成功 ⇒ 证明上面的 null 来自「缺日历」而非别的原因。
        var withCalendar = Ctx((7, Day, Day.AddHours(8)));
        var ok = PhaseFourLocalRepair.FindForwardSlot(
            Day, TimeSpan.FromMinutes(60), 7, withCalendar,
            new Dictionary<int, List<TimeWindow>>(), Day.AddDays(90));
        Assert.NotNull(ok);
        Assert.Equal(Day, ok!.Value.Start);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_13（0号位 §3.4）：Calendar **存在**时，④.6 对「加工节时**相等**」与「**不等**」必须给出不同判定。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_13_日历存在时_加工节时相等与不等_分别记无增量与有增量()
    {
        var equal = ResourceEfficiencyEvaluator.Compare(
            ResourceEfficiencyEvaluator.Evaluate(new[] { Task(1, Day, Day.AddMinutes(60), draftId: "P") }),
            ResourceEfficiencyEvaluator.Evaluate(new[] { Task(1, Day, Day.AddMinutes(60), draftId: "Q") }));
        Assert.Equal(0, equal);
        Assert.Equal(ResourceEfficiencyEvaluator.TraceNoIncrement,
            ResourceEfficiencyEvaluator.VerdictTraceCode(
                ResourceEfficiencyEvaluator.VerdictFromComparison(equal)));

        var unequal = ResourceEfficiencyEvaluator.Compare(
            ResourceEfficiencyEvaluator.Evaluate(new[] { Task(1, Day, Day.AddMinutes(60), draftId: "P") }),
            ResourceEfficiencyEvaluator.Evaluate(new[] { Task(1, Day, Day.AddMinutes(30), draftId: "Q") }));
        Assert.Equal(1, unequal);
        Assert.Equal(ResourceEfficiencyEvaluator.TraceIncrement,
            ResourceEfficiencyEvaluator.VerdictTraceCode(
                ResourceEfficiencyEvaluator.VerdictFromComparison(unequal)));
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_14（0号位 §3.3 第二段）：`ResourceId == null` **不自动等于**「非生产」⇒ **不得静默忽略**。
    //   本类无法自证工序模式（`FinalTaskDraft` 不携带）⇒ 至少必须把该事实**显式暴露**
    //   （计数 + 留痕文本），使「真实 Operation Task 缺资源身份」这一契约违背**可见**。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_14_无资源身份Task不得静默忽略_计数与留痕须显式暴露()
    {
        var mixed = new[]
        {
            Task(1, Day, Day.AddMinutes(60), draftId: "R"),
            Task(null, Day, Day.AddHours(5), draftId: "NOID")     // 无资源身份
        };

        var occupancy = ResourceEfficiencyEvaluator.Evaluate(mixed);
        Assert.Equal(1, occupancy.NonResourceTaskCount);          // **显式计数**（不再沉默）
        Assert.Equal(60m, occupancy.OccupiedMinutes);             // 仍不计入设备占用（冻结口径）

        var ctx = Ctx((1, Day, Day.AddHours(8)));
        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(mixed, mixed, ctx);
        var breakdown = ResourceEfficiencyEvaluator.BreakdownFor(mixed, window, available, ctx);
        Assert.Equal(1, breakdown.NonResourceTaskCount);

        // 留痕文本必须**写明**该事实（下游可核对），而不是无声略过。
        var message = ResourceEfficiencyEvaluator.Describe(
            ResourceEfficiencyEvaluator.ResourceEfficiencyVerdict.Tie, breakdown, breakdown);
        Assert.Contains("无资源身份 Task", message);
        Assert.Contains("独立 1 / 合批 1", message);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_15（R2-A，0号位 2026-10-14《致1号位第五轮代码审核正式复核回复 v1.0》§三 R2-A 逐字算例）：
    //   **绝对「可释放合法产能」必须扣实际 Setup** —— 旧式 `窗口 − 非Setup占用` 夸大 90 分钟。
    //
    //   0号位 逐字场景：合法可用窗口 **480** 分钟、有效加工 **360** 分钟、Setup **90** 分钟、真实占用 **450**。
    //     · 真实物理未占用 = `480 − 450 = 30`；
    //     · 旧式「可释放合法产能」= `480 − (450 − 90) = 120` ⇒ **夸大 90 分钟 = 已发生的 Setup 占用**。
    //   ⇒ 本用例钉住：`UnoccupiedMinutes = 窗口 − Occupied`（物理自洽），并**证明**它不等于旧式。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_15_绝对未占用必须扣实际Setup_旧式窗口减非Setup占用夸大90分钟()
    {
        var ctx = Ctx((1, Day, Day.AddHours(8)));                                  // 480 min 窗
        var tasks = new[] { Task(1, Day, Day.AddMinutes(450), setup: 90m, draftId: "A") };

        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(tasks, tasks, ctx);
        Assert.True(available);
        Assert.Equal(480m, window);

        var breakdown = ResourceEfficiencyEvaluator.BreakdownFor(tasks, window, available, ctx);
        Assert.Equal(360m, breakdown.EffectiveProcessingMinutes);   // 有效加工 6h
        Assert.Equal(90m, breakdown.SetupMinutes);                  // Setup 1.5h
        Assert.Equal(450m, breakdown.OccupiedMinutes);              // 真实占用 7.5h

        // **真实物理未占用 = 30**（R2-A 修法）
        Assert.Equal(30m, breakdown.UnoccupiedMinutes!.Value);

        // **反证**：旧式 `窗口 − 非Setup占用` = 120 ⇒ 夸大 90（= Setup 分钟），**不得**再用作可用余量。
        var legacyOverstated = window - breakdown.NonSetupOccupiedMinutes;
        Assert.Equal(120m, legacyOverstated);
        Assert.Equal(90m, legacyOverstated - breakdown.UnoccupiedMinutes!.Value);
        Assert.NotEqual(legacyOverstated, breakdown.UnoccupiedMinutes!.Value);

        // 「未占用 ≠ 可复用」须**写明**在留痕文本里（不得被读成可排给其他任务的余量）。
        var message = ResourceEfficiencyEvaluator.Describe(
            ResourceEfficiencyEvaluator.ResourceEfficiencyVerdict.Tie, breakdown, breakdown);
        Assert.Contains("真实日历未占用", message);
        Assert.Contains("未占用 ≠ 可复用", message);
    }

    // ════════════════════════════════════════════════════════════════════
    // RE_16（R2-A 要求的第二组反例）：**加工相同、Setup 不同** ⇒
    //   · 有效加工、未占用**逐字相同**（Setup 不是加工）；
    //   · 排序键（除 Setup 外占用）**逐字相同** ⇒ ④.6 **不得**再记一次收益（§一.2.5 不重复计分）；
    //   · 但**真实占用**与**真实未占用**不同 ⇒ 差异**只能**由第 ④ 层（Setup 总量）消费。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void RE_16_加工相同Setup不同_未占用随Setup变_但排序键相同()
    {
        var ctx = Ctx((1, Day, Day.AddHours(8)));                                  // 480 min 窗

        var heavy = new[] { Task(1, Day, Day.AddMinutes(450), setup: 90m, draftId: "H") };   // 加工 360 + Setup 90
        var light = new[] { Task(1, Day, Day.AddMinutes(390), setup: 30m, draftId: "L") };   // 加工 360 + Setup 30

        var (window, available) = ResourceEfficiencyEvaluator.ComputeComparableWindow(heavy, light, ctx);
        var bh = ResourceEfficiencyEvaluator.BreakdownFor(heavy, window, available, ctx);
        var bl = ResourceEfficiencyEvaluator.BreakdownFor(light, window, available, ctx);

        // ① 有效加工**相同**（Setup 不是加工）⇒ 稼动率**相同**。
        Assert.Equal(360m, bh.EffectiveProcessingMinutes);
        Assert.Equal(360m, bl.EffectiveProcessingMinutes);
        Assert.Equal(bh.EffectiveUtilization, bl.EffectiveUtilization);

        // ② 排序键**相同** ⇒ ④.6 判平（**不**重复计 Setup 收益）。
        Assert.Equal(bh.NonSetupOccupiedMinutes, bl.NonSetupOccupiedMinutes);

        // ③ 但真实占用/真实未占用**不同** ⇒ 差异真实存在，只能由第 ④ 层消费。
        Assert.Equal(450m, bh.OccupiedMinutes);
        Assert.Equal(390m, bl.OccupiedMinutes);
        Assert.Equal(30m, bh.UnoccupiedMinutes!.Value);
        Assert.Equal(90m, bl.UnoccupiedMinutes!.Value);
        Assert.Equal(60m, bl.UnoccupiedMinutes!.Value - bh.UnoccupiedMinutes!.Value);   // 恰为 Setup 差 90−30
    }
}
