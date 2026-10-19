using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 无 Routing Stage 纯内存时间节点（0号位 2026-09-29 裁决 §十四第 4/5/9 项）契约测试。
/// 纯内存，直接调 <see cref="StageTimingNodeBuilder.BuildCore"/> —— 不碰数据库、不起 Solver、不读 Context。
///
/// 覆盖：
/// ④ 节点标记恒 `STAGE_TIMING`（不得被当真实 Task / 不得生成 TaskNo）；
/// ⑤ `MatchLevel` 4 值契约：3 个合法值原样采纳；旧名 `MATERIAL`/`FAMILY` ⇒ 违例**不采纳**；
///    命中值原样透传（**含 0 / 负，我方不做符号判断**）；无 fact / `MISSING` ⇒ **不虚造时长**（只登记待写 Issue）；
/// ⑨ 只累加末端真实 Task **之后**的 Stage；整条无真实 Task 时自 `PlanningStart` 起累计；
/// 边界：无 Stage 链不猜顺序 / 求解范围外 Stage 不算 / 非 DEFAULT 路由不算有工序 / 同 `StageSeq` 定序可复现。
/// </summary>
public class StageTimingNodeBuilderTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime TerminalEnd = new(2026, 9, 10, 8, 0, 0);

    private const string Timing = "STAGE_TIMING";
    private const string Missing = "STAGE_LEADTIME_MISSING";

    // ─────────────────────────── ④ 纯内存标记 ───────────────────────────

    [Fact]
    public void Segments_AreMarkedAsStageTiming_NotRealTasks()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);
        var seg = Assert.Single(fact.StageTimings);

        Assert.Equal(Timing, seg.NodeType);
        Assert.Equal("CN_FINAL", seg.StageCode);
        Assert.Equal(4m, seg.LeadTimeHours);
        Assert.Equal("DEPT_EXACT", seg.MatchLevel);
        Assert.Equal(TerminalEnd, seg.StartTime);
        Assert.Equal(TerminalEnd.AddHours(4), seg.EndTime);

        Assert.Equal(TerminalEnd.AddHours(4), fact.CompletionTime);
        Assert.Equal(TerminalEnd.AddHours(4), fact.AvailableTime);
        Assert.Equal("F2", fact.TerminalFinalDraftId);
        Assert.Equal(1001, fact.MaterialId);
        Assert.Equal(1L, fact.AllocationSequence);
    }

    // ─────────────────────────── ⑤ 4 值契约 ───────────────────────────

    [Theory]
    [InlineData("DEPT_EXACT", 120)]
    [InlineData("FACTORY_STAGE_DEFAULT", 96)]
    [InlineData("GLOBAL_STAGE_DEFAULT", 48)]
    public void LegalLevels_AreAdoptedVerbatim(string level, int hours)
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", hours, level) },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var seg = Assert.Single(Assert.Single(result).StageTimings);

        Assert.Equal(level, seg.MatchLevel);
        Assert.Equal(hours, seg.LeadTimeHours);
        Assert.Equal(TerminalEnd.AddHours(hours), seg.EndTime);
    }

    /// <summary>
    /// 旧名 `MATERIAL` / `FAMILY` = 契约违例（0号位 §4.3 已移除；4 个之外一律不采纳）。
    /// 关键：**违例名不得向外透传** —— 否则下游会把它当合法层级审计。
    ///
    /// ⚠ 2026-10-15（0号位 第九轮 §3.1/§3.3）口径更正：违例值**不再**按 72h 兜底产出时间段
    ///   ——「不使用 0 或 72h 臆造时间」。改为**不产出时间段、不推进游标**，
    ///   只登记 `(DemandKey, StageCode)` 供装配层写 `STAGE_LEADTIME_MISSING` 的 Issue。
    /// </summary>
    [Theory]
    [InlineData("MATERIAL")]
    [InlineData("FAMILY")]
    public void LegacyLevels_AreContractViolations_NoTimeAndRecordedAsMissing(string legacyLevel)
    {
        var missing = new List<(string DemandKey, string StageCode)>();
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 120m, legacyLevel) },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)),
            missing);

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);                  // 违例值不产出时间段
        Assert.Equal(TerminalEnd, fact.CompletionTime);   // 游标未推进（**不是** +72h）
        Assert.Equal(("D1", "CN_FINAL"), Assert.Single(missing));
    }

    /// <summary>
    /// 实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00`。
    ///
    /// ⚠️ **2026-09-29 口径更正（2号位 r13519 载入 Core 契约）**：早先那条
    /// 「命中到参数行但折算值 ≤ 0 ⇒ 视同未命中 ⇒ 72h 兜底」**已作废**。
    /// 现行口径：**命中就是命中** —— 值 0（或负）**按命中产出**，`MatchLevel` 保持实际命中级。
    /// 理由：0 是参数行写下的值，装载层与消费侧都**不得替参数侧改判**。
    ///
    /// ⇒ 本用例锁的是「我方**不做符号判断**」这条决定：值原样透传，层级**不得**被改写成 `MISSING`。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ExplicitValue_IsPassedThroughVerbatim_NoSignJudgement(int hours)
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", hours, "FACTORY_STAGE_DEFAULT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);
        var seg = Assert.Single(fact.StageTimings);

        Assert.Equal((decimal)hours, seg.LeadTimeHours);
        Assert.Equal("FACTORY_STAGE_DEFAULT", seg.MatchLevel);   // 不得被改写成 MISSING
        Assert.Equal(TerminalEnd.AddHours(hours), fact.CompletionTime);
    }

    /// <summary>
    /// 2号位 装载层对「三级全不中」会产 `LeadTimeHours = 72` + `MatchLevel = MISSING`。
    ///
    /// ⚠ 2026-10-15（0号位 第九轮 §3.1/§3.3）口径更正：**1号位 不再消费该值**
    ///   —— 逐字「不使用 0 或 72h 臆造时间」。命中 `MISSING` = 没有可用合法参数
    ///   ⇒ **不产出时间段、不推进游标**，只登记待写 Issue。
    /// （旧口径「用其值、不叠加」自本条起作废。）
    /// </summary>
    [Fact]
    public void MissingLevel_FabricatedValueIsNotConsumed()
    {
        var missing = new List<(string DemandKey, string StageCode)>();
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 72m, Missing) },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)),
            missing);

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);                       // 不虚造时长
        Assert.Equal(TerminalEnd, fact.CompletionTime);        // **不是** TerminalEnd + 72h
        Assert.Equal(("D1", "CN_FINAL"), Assert.Single(missing));
    }

    /// <summary>
    /// 防御分支：该 (物料, 阶段) 一条 fact 都没有 ⇒ **仍不得静默 0 小时**（0号位 2026-09-29 §4.4 明禁），
    ///   也**不得用 72h 兜底**（0号位 2026-10-15 第九轮 §3.1）⇒ 不产出时间段，只登记待写 Issue。
    /// </summary>
    [Fact]
    public void NoFact_NoTimeAndRecordedAsMissing()
    {
        var missing = new List<(string DemandKey, string StageCode)>();
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)),
            missing);

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);
        Assert.Equal(TerminalEnd, fact.CompletionTime);
        Assert.Equal(("D1", "CN_FINAL"), Assert.Single(missing));
    }

    // ─────────────────────────── ⑨ 时间出口 ───────────────────────────

    /// <summary>
    /// 只取末端真实 Task **之后**的 Stage：`CN_PRE`(Seq=1) 在末端之前 ⇒ 不得计入，
    /// 否则会把本次不求解、已完成的上游阶段提前期凭空加进来。
    /// </summary>
    [Fact]
    public void OnlyStagesAfterTerminal_AreAccumulated()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[]
                {
                    Fact(1001, "CN_PRE", 10m, "DEPT_EXACT"),
                    Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT")
                },
                chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_PRE", "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);
        var seg = Assert.Single(fact.StageTimings);

        Assert.Equal("CN_FINAL", seg.StageCode);
        Assert.Equal(TerminalEnd, seg.StartTime);
        Assert.Equal(TerminalEnd.AddHours(4), fact.CompletionTime);
        Assert.DoesNotContain(fact.StageTimings, s => s.StageCode == "CN_PRE");
    }

    /// <summary>
    /// **真正纯无 Routing** 的物料：整条需求无真实 Operation Task ⇒ 自 `PlanningStart` 起按 Stage 链顺序
    ///   纯累计（0号位 §10.3 明文认可，0号位 2026-10-14 第七轮 §八.3 的 **(ii) 合法时间约束**）。
    ///
    /// ⚠ 夹具口径（2026-10-15 门控落地后收紧）：必须**该物料在本范围内一条 Routing 工序都没有**。
    ///   旧版夹具带 `Op(1001,"M01","CN_MACH")`（= **有** Routing 但没排下）—— 那是 §八.3 的 **(iii) 占位口径**，
    ///   已被 <see cref="StageTimingNodeBuilder.BuildCore"/> 门控抑制（见
    ///   <see cref="S2_02_有Routing但未排下_属占位口径_须门控抑制且进留痕"/>）。
    ///   ⇒ 本用例保留的是 §10.3 **真正**适用的那一半：**无 Routing 可排**、时间由合法 StageLeadTime 构成。
    /// </summary>
    [Fact]
    public void NoRealTask_AccumulatesFromPlanningStart()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[]
                {
                    Fact(1001, "CN_PRE", 10m, "DEPT_EXACT"),
                    Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT")
                },
                chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
                ops: Array.Empty<RoutingOperation>()),   // 纯无 Routing：该物料范围内零工序
            Stages(1001, "CN_PRE", "CN_FINAL"),
            Terminals());

        var fact = Assert.Single(result);

        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal("CN_PRE", fact.StageTimings[0].StageCode);
        Assert.Equal(Start, fact.StageTimings[0].StartTime);
        Assert.Equal("CN_FINAL", fact.StageTimings[1].StageCode);
        Assert.Equal(Start.AddHours(10), fact.StageTimings[1].StartTime);
        Assert.Equal(Start.AddHours(14), fact.CompletionTime);
        Assert.Null(fact.TerminalFinalDraftId);
    }

    // ─────────────────────────── 边界 ───────────────────────────

    /// <summary>无 Stage 链 ⇒ **不猜顺序**，该物料的无 Routing Stage 一律不并入（宁可少算，不可凭空多算）。</summary>
    [Fact]
    public void NoStageChain_DoesNotGuessOrder()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);
        Assert.Equal(TerminalEnd, fact.CompletionTime);
    }

    /// <summary>求解范围（`EffectiveStages`）之外的 Stage 不算 —— 否则会把本次不求解的 Stage 也算进来。</summary>
    [Fact]
    public void StageOutsideEffectiveScope_IsIgnored()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);
        Assert.Equal(TerminalEnd, fact.CompletionTime);
    }

    /// <summary>在求解范围内、但不在 Stage 链里 ⇒ 无顺序依据，不得并入。</summary>
    [Fact]
    public void StageInScopeButAbsentFromChain_IsIgnored()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "TJ_OUTS", 120m, "GLOBAL_STAGE_DEFAULT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "TJ_OUTS"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);
        Assert.Equal(TerminalEnd, fact.CompletionTime);
    }

    /// <summary>
    /// **2026-10-14 口径更正**（0号位《正式回复 v2.0》§二 S2-01 逐字：「`GetRoutelessStages` 在**未取得
    /// 选中Path**时还存在**仅接受 `DEFAULT`** 的分支」⇒「修复无Routing Stage判定的真实Path/**未排**情形」）：
    /// 末端 Task **未带选中路径**（`RouteCode/PathId` 未填 = 「未排/未填」情形）时，**不得**再按
    /// `RouteCode == "DEFAULT"` 过滤 —— 非 DEFAULT 的**真实**路径工序同样构成「该 Stage 有工序」
    /// ⇒ **不凭空多出提前期**。
    /// ⚠ 旧断言 `NonDefaultRouteOperation_DoesNotCountAsRouting`（「V1 只认 `DEFAULT`」）锚定的是
    ///   归一化 ON 时代口径，**已随本次整改作废**；归一化 ON（全部 `DEFAULT/1`）时新旧两版**逐元素相同**
    ///   ⇒ 现行生产载荷**零回归**。
    /// </summary>
    [Fact]
    public void NonDefaultRouteOperation_CountsAsRouting_NoFabricatedLeadTime()
    {
        var demand = new[] { Demand("D1", 1001) };
        var chain = new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) };
        var facts = new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") };
        var terminals = Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd));

        var withAltRoute = Run(
            Req(demand, facts, chain, new[] { Op(1001, "M01", "CN_MACH"), Op(1001, "X01", "CN_FINAL", "ALT") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            terminals);

        var withDefaultRoute = Run(
            Req(demand, facts, chain, new[] { Op(1001, "M01", "CN_MACH"), Op(1001, "X01", "CN_FINAL", "DEFAULT") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            terminals);

        Assert.Empty(Assert.Single(withAltRoute).StageTimings);      // 真实路径（ALT）⇒ 有工序，不建时间节点
        Assert.Empty(Assert.Single(withDefaultRoute).StageTimings);  // DEFAULT ⇒ 同样不建
    }

    /// <summary>同 `StageSeq` 时按 `StageCode` 定序 ⇒ 结果可复现（不得依赖数据库返回顺序）。</summary>
    [Fact]
    public void SameStageSeq_IsOrderedByStageCode_Deterministically()
    {
        var request = Req(new[] { Demand("D1", 1001) },
            facts: new[]
            {
                Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT"),
                Fact(1001, "BJ_FINAL", 6m, "DEPT_EXACT")
            },
            chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 3), ("BJ_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });
        var stages = Stages(1001, "CN_MACH", "CN_FINAL", "BJ_FINAL");
        var terminals = Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd));

        var first = Run(request, stages, terminals);
        var second = Run(request, stages, terminals);

        var segs = Assert.Single(first).StageTimings;
        Assert.Equal(new[] { "BJ_FINAL", "CN_FINAL" }, segs.Select(s => s.StageCode).ToArray());
        Assert.Equal(
            segs.Select(s => (s.StageCode, s.StartTime, s.EndTime)).ToArray(),
            Assert.Single(second).StageTimings.Select(s => (s.StageCode, s.StartTime, s.EndTime)).ToArray());
    }

    // ─────────────────────────── RequiredStageCompletionTime ───────────────────────────

    /// <summary>供给阈值 Stage 落在末端**之后**的无 Routing 段 ⇒ 取其段末。</summary>
    [Fact]
    public void RequiredStageAfterTerminal_ReportsThatSegmentEnd()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001, requiredStage: "CN_FINAL") },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Equal("CN_FINAL", fact.RequiredStageCode);
        Assert.Equal(TerminalEnd.AddHours(4), fact.RequiredStageCompletionTime);
        Assert.Equal(TerminalEnd.AddHours(4), fact.CompletionTime);
    }

    /// <summary>供给阈值 Stage 在末端真实 Task 处或之前 ⇒ 完成时间即末端 Task.End（不必等无 Routing 段）。</summary>
    [Fact]
    public void RequiredStageAtOrBeforeTerminal_ReportsTerminalEnd()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001, requiredStage: "CN_MACH") },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Equal(TerminalEnd, fact.RequiredStageCompletionTime);
        Assert.Equal(TerminalEnd.AddHours(4), fact.CompletionTime); // 整条仍算到 CN_FINAL
    }

    /// <summary>无供给阈值语义（`RequiredStageCode` 为 null）⇒ 该字段为 null，不臆造。</summary>
    [Fact]
    public void NoRequiredStage_LeavesRequiredStageCompletionTimeNull()
    {
        var result = Run(
            Req(new[] { Demand("D1", 1001) },
                facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
                chains: new[] { Chain(1001, ("CN_MACH", 1), ("CN_FINAL", 2)) },
                ops: new[] { Op(1001, "M01", "CN_MACH") }),
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)));

        var fact = Assert.Single(result);

        Assert.Null(fact.RequiredStageCode);
        Assert.Null(fact.RequiredStageCompletionTime);
    }

    // ════════════════════════════════════════════════════════════════════
    // §三.2（0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》）：
    //   **`terminal == null && segments.Count == 0` 时 `CompletionTime` 退化为 `PlanningStart`** 的可见反例。
    //
    // 判词逐字：「`BuildCore`仍用`terminal?.PlannedEndTime ?? PlanningStart`作游标，在`terminal==null`且
    //   `segments.Count==0`时仍可能生成`CompletionTime=PlanningStart`的`DemandCompletionFact`，而r13685
    //   新增`SolveTraceNote`仅在`!PathDetermined && segments.Count>0`时触发。请补此可见反例…」
    //   ⇒ 本组用例即该可见反例；并**同时**钉住「两类留痕不可混同」的边界。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **可见反例**：需求既**无末端真实 Task**、又**无任何无 Routing Stage 提前期段**
    /// ⇒ `CompletionTime`/`AvailableTime` 退化为 `PlanningStart`（**不是**已排/预计完工/供给可用时间），
    /// 该 Demand 键**必须**进 `completionTimeUnavailable` 收集器（装配层据此写 `DEMAND_COMPLETION_TIME_UNAVAILABLE`）。
    ///   · 同时**不得**进 `pathUndetermined`（那是「有提前期段、但取自候选路径并集」的另一类，见 §三.2 判词）。
    /// </summary>
    [Fact]
    public void S2_02_无末端真实Task且无任何提前期段_CompletionTime退化为PlanningStart_须进留痕收集器()
    {
        var request = Req(new[] { Demand("D1", 1001) });

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var result = StageTimingNodeBuilder.BuildCore(
            request, Stages(1001), Terminals(), pathUndetermined, completionTimeUnavailable);

        var fact = Assert.Single(result);

        // 退化事实本身（旧行为）：游标从未推进 ⇒ 时间 = PlanningStart
        Assert.Equal(Start, fact.CompletionTime);
        Assert.Equal(Start, fact.AvailableTime);
        Assert.Empty(fact.StageTimings);
        Assert.Null(fact.TerminalFinalDraftId);          // 未排下 ⇒ **不伪造** Task（0号位 §9.2）

        // 整改落点：**不静默** —— 该键必须被显式收集
        Assert.Equal(new[] { "D1" }, completionTimeUnavailable.ToArray());
        Assert.Empty(pathUndetermined);                  // 两类留痕**不可混同**
    }

    /// <summary>
    /// **第七轮 §八.3 反例(a)：有真实 Routing 但未排下** ⇒ 其无 Routing Stage 提前期是**占位/候选口径**，
    ///   **不是** 0号位 §10.3 认可的「合法时间约束」⇒ 门控**必须**抑制 `StageTimings`，
    ///   该 Fact 落为 §八.3 点名的 (iii) 态，且**不静默**（走既有 `completionTimeUnavailable` 通道）。
    ///
    /// 判据落点（读码实测，见 <see cref="StageTimingNodeBuilder.BuildCore"/>）：
    ///   `routeless.HasRoutingInScope == true`（`CN_MACH` 有工序）**且** `terminal == null`
    ///   ⇒ `suppressCandidateLeadTimes == true`。
    /// </summary>
    [Fact]
    public void S2_02_有Routing但未排下_属占位口径_须门控抑制且进留痕()
    {
        var request = Req(new[] { Demand("D1", 1001) },
            facts: new[]
            {
                Fact(1001, "CN_PRE", 10m, "DEPT_EXACT"),
                Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT")
            },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });   // **有** Routing（正是 §八.3 (iii) 的形态）

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var result = StageTimingNodeBuilder.BuildCore(
            request, Stages(1001, "CN_PRE", "CN_MACH", "CN_FINAL"), Terminals(),
            pathUndetermined, completionTimeUnavailable);

        var fact = Assert.Single(result);

        // (iii) 占位/候选诊断：**无**末端 Task、**无**时间段 ⇒ 时间退化为 PlanningStart，且**不得**被读成权威完工。
        Assert.Null(fact.TerminalFinalDraftId);
        Assert.Empty(fact.StageTimings);
        Assert.Equal(Start, fact.CompletionTime);
        Assert.Equal(Start, fact.AvailableTime);

        // **不静默**：被抑制的候选口径必须显式收集（第七轮 §八.3「不接受只写一条Warning」）。
        Assert.Equal(new[] { "D1" }, completionTimeUnavailable.ToArray());

        // 抑制后 `segments` 为空 ⇒ S2-01 的「候选口径」留痕**不再叠加**（两类留痕互斥，见 Build 内注释）。
        Assert.Empty(pathUndetermined);
    }

    /// <summary>
    /// **第七轮 §八.3 反例(b)：真正纯无 Routing**（该物料在本范围内**零** Routing 工序）**且**有 StageLeadTime 链
    ///   ⇒ 时间**有依据**（0号位 §10.3 明文认可的「自起点起的纯提前期累计」= §八.3 的 (ii) 合法时间约束）
    ///   ⇒ 门控**不得**抑制，**不得**进 `completionTimeUnavailable`。
    ///
    /// 与上一用例**只差 `ops`** —— 这正是「(ii) 与 (iii) 结构可判」的对照证据。
    /// </summary>
    [Fact]
    public void S2_02_纯无Routing有提前期段时_不得误报为无依据()
    {
        var request = Req(new[] { Demand("D1", 1001) },
            facts: new[]
            {
                Fact(1001, "CN_PRE", 10m, "DEPT_EXACT"),
                Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT")
            },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: Array.Empty<RoutingOperation>());   // 纯无 Routing ⇒ HasRoutingInScope == false

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var result = StageTimingNodeBuilder.BuildCore(
            request, Stages(1001, "CN_PRE", "CN_FINAL"), Terminals(),
            pathUndetermined, completionTimeUnavailable);

        var fact = Assert.Single(result);

        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal(Start.AddHours(14), fact.CompletionTime);   // 有依据（自起点累计 10 + 4）
        Assert.Empty(completionTimeUnavailable);                 // **不得**误报 —— 本用例的唯一主张

        // 本夹具**无末端真实 Task** ⇒ 无从取 RouteCode/PathId ⇒ S2-01 的「候选口径」留痕**照常成立**
        //   （与 `StrictRoutelessStagesAndErrorMessageTests` §② 同口径）。
        Assert.Equal(new[] { "D1" }, pathUndetermined.ToArray());
    }

    /// <summary>
    /// **对照（不得误报）**：**有**末端真实 Task、只是末端之后没有无 Routing Stage（`segments.Count == 0`）
    /// ⇒ 时间来自 `Task.End`，**有依据** ⇒ **不得**进 `completionTimeUnavailable`。
    /// （与上面「无 Task 且无段」结构不同，必须分开判 —— 这正是 §三.2 判词的分界。）
    /// </summary>
    [Fact]
    public void S2_02_有末端真实Task时_不得误报为无依据()
    {
        var request = Req(new[] { Demand("D1", 1001) },
            facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
            ops: new[] { Op(1001, "M01", "CN_MACH") });

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var result = StageTimingNodeBuilder.BuildCore(
            request, Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)),
            pathUndetermined, completionTimeUnavailable);

        var fact = Assert.Single(result);

        Assert.Empty(fact.StageTimings);
        Assert.Equal(TerminalEnd, fact.CompletionTime);
        Assert.Equal("F2", fact.TerminalFinalDraftId);
        Assert.Empty(completionTimeUnavailable);                 // **不得**误报
    }

    /// <summary>
    /// 追溯码取值即**对外契约值**（2号位 读模型 / 订单交付 / 正式 Supply 可用时间侧据此识别）
    /// ⇒ 钉住字面量，防止被无意改写；并证明与 S2-01 的追溯码**互不相同**。
    /// </summary>
    [Fact]
    public void S2_02_追溯码取值钉住_且与S2_01追溯码不同()
    {
        Assert.Equal("DEMAND_COMPLETION_TIME_UNAVAILABLE", StageTimingNodeBuilder.TraceCompletionTimeUnavailable);
        Assert.Equal("STAGE_TIMING_PATH_UNDETERMINED", StageTimingNodeBuilder.TracePathUndetermined);
        Assert.NotEqual(StageTimingNodeBuilder.TraceCompletionTimeUnavailable,
                        StageTimingNodeBuilder.TracePathUndetermined);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static List<DemandCompletionFact> Run(
        DomainSolveRequest request,
        IReadOnlyDictionary<int, HashSet<string>> stages,
        IReadOnlyDictionary<string, FinalTaskDraft> terminals)
        => StageTimingNodeBuilder.BuildCore(request, stages, terminals);

    /// <summary>
    /// 带 N8-01 缺参收集器的重载：缺参 Stage 由装配层 <see cref="StageTimingNodeBuilder.Build"/>
    /// 写 `STAGE_LEADTIME_MISSING` 的 `SolveTraceNote`（本纯逻辑入口只登记，不写留痕）。
    /// </summary>
    private static List<DemandCompletionFact> Run(
        DomainSolveRequest request,
        IReadOnlyDictionary<int, HashSet<string>> stages,
        IReadOnlyDictionary<string, FinalTaskDraft> terminals,
        ICollection<(string DemandKey, string StageCode)> missingLeadTimeStages)
        => StageTimingNodeBuilder.BuildCore(request, stages, terminals, null, null, missingLeadTimeStages);

    private static DomainSolveRequest Req(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<StageLeadTimeFact>? facts = null,
        IReadOnlyList<StageSequenceChain>? chains = null,
        IReadOnlyList<RoutingOperation>? ops = null)
        => new()
        {
            PlanningStart = Start,
            PlanningEnd = Start.AddYears(100),
            LogicalProductionDemands = demands,
            StageLeadTimes = facts ?? Array.Empty<StageLeadTimeFact>(),
            StageSequenceChains = chains ?? Array.Empty<StageSequenceChain>(),
            RoutingOperations = ops ?? Array.Empty<RoutingOperation>()
        };

    private static LogicalProductionDemand Demand(string key, int materialId, string? requiredStage = null)
        => new()
        {
            LogicalDemandKey = key,
            MaterialId = materialId,
            AllocationSequence = 1,
            RequiredStageCode = requiredStage
        };

    private static StageSequenceChain Chain(int materialId, params (string Stage, int Seq)[] steps)
        => new()
        {
            MaterialId = materialId,
            Stages = steps.Select(s => new StageSequenceStep { StageCode = s.Stage, StageSeq = s.Seq }).ToList()
        };

    private static StageLeadTimeFact Fact(int materialId, string stageCode, decimal hours, string level)
        => new()
        {
            MaterialId = materialId,
            StageCode = stageCode,
            LeadTimeHours = hours,
            MatchLevel = level
        };

    private static RoutingOperation Op(int materialId, string operationCode, string stageCode, string routeCode = "DEFAULT")
        => new()
        {
            MaterialId = materialId,
            OperationCode = operationCode,
            StageCode = stageCode,
            RouteCode = routeCode
        };

    private static FinalTaskDraft Task(
        string draftId, string demandKey, int materialId, string stageCode, string operationCode, DateTime end)
        => new()
        {
            FinalDraftId = draftId,
            SourceDraftId = demandKey,
            MaterialId = materialId,
            StageCode = stageCode,
            OperationCode = operationCode,
            PlannedStartTime = end.AddHours(-8),
            PlannedEndTime = end
        };

    private static IReadOnlyDictionary<int, HashSet<string>> Stages(int materialId, params string[] stageCodes)
        => new Dictionary<int, HashSet<string>>
        {
            [materialId] = new HashSet<string>(stageCodes, StringComparer.Ordinal)
        };

    private static IReadOnlyDictionary<string, FinalTaskDraft> Terminals(params FinalTaskDraft[] tasks)
        => tasks.ToDictionary(t => t.SourceDraftId, StringComparer.Ordinal);
}
