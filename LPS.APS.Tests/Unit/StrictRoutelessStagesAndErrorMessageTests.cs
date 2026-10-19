using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **2026-10-12 整改判别性单测**（答 2号位《APS_V1_20261012_技术事项汇总回执》§一 / §九#2、#3）。
///
/// 两块：
///   · **①「严格改法」** —— <see cref="StageTimingNodeBuilder.GetRoutelessStages"/> 不再把
///     `RouteCode == "DEFAULT"` 写死；改为按**需求已选中路径**（末端真实 Task 的 `RouteCode/PathId`）过滤；
///     **无选中路径**（需求整条**未排下** / 该两列未填）⇒ **不按 RouteCode 过滤**，取该物料**任一**
///     RouteCode 上的工序并集（⇒ 不凭空多出提前期）。**2026-10-14 口径更正**（0号位《正式回复 v2.0》
///     §二 S2-01「修复无Routing Stage判定的真实Path/**未排**情形」）：早先「无选中 ⇒ 退回 `DEFAULT` 口径」
///     的写法只在**归一化 ON** 下成立，归一化 OFF + 整条未排下时会把范围内**全部** Stage 误判为无路由。
///     归一化 ON（全部 `DEFAULT/1`）时新旧两版**逐元素相同** ⇒ 现行生产载荷**严格 no-op**。
///   · **§2.3** —— <see cref="PhaseFiveCompression.ComposeErrorMessage"/> 把 Batch Policy 硬失败明细并入
///     `DomainSolveResult.ErrorMessage`，**带稳定前缀且**有界**。
///
/// 全部纯内存，**不触库**（用户红线：`LPS.APS.Tests/Integration/` 直连生产库，只跑 Unit）。
/// </summary>
public class StrictRoutelessStagesAndErrorMessageTests
{
    private const int MaterialId = 100;

    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0);

    private static DomainSolveRequest RequestWith(params RoutingOperation[] ops)
        => new() { RoutingOperations = ops };

    private static RoutingOperation Op(string routeCode, int pathId, string stageCode, int materialId = MaterialId)
        => new()
        {
            MaterialId = materialId,
            RouteCode = routeCode,
            PathId = pathId,
            StageCode = stageCode,
            OperationCode = $"{routeCode}-{stageCode}",
        };

    private static IReadOnlyDictionary<int, HashSet<string>> Effective(params string[] stages)
        => new Dictionary<int, HashSet<string>>
        {
            [MaterialId] = new HashSet<string>(stages, StringComparer.Ordinal),
        };

    // ══════════════════════════════════════════════════════════════════
    // ① 严格改法
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判别性反证：**多路径**下必须**只认选中路径**。
    /// 旧实现（写死 `RouteCode == "DEFAULT"`）在真实 RouteCode 载荷下 ⇒ `stagesWithRouting` 恒空
    /// ⇒ 两个 Stage **都被判「无路由」**（凭空多出提前期）。整改后：选中 R1 ⇒ 只有 S2 无路由。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_MultiPath_FollowsSelectedRoute_NotHardCodedDefault()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R2", 2, "S2"));

        var effective = Effective("S1", "S2");

        var selectedR1 = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", 1);
        var selectedR2 = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R2", 2);

        Assert.Equal(new[] { "S2" }, selectedR1.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "S1" }, selectedR2.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 判别性反证：**无选中路径**（需求整条**未排下** / `RouteCode` 未填）⇒ **不再退回 `DEFAULT` 口径**
    /// （2026-10-14 口径更正，见类摘要与 0号位《正式回复 v2.0》§二 S2-01）。
    /// 真实 RouteCode 载荷 + 无选中 ⇒ 该物料**任一** RouteCode 上的工序都算「有路由」
    /// ⇒ S1/S2 **都不得**被判无路由（旧行为会两个都判 ⇒ **凭空多出提前期**）。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_NoSelectedRoute_DoesNotFabricateRoutelessStages()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R2", 2, "S2"));

        var effective = Effective("S1", "S2", "S3");

        var noSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, null, null);
        var blankSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "  ", null);

        // 仅 S3（范围内无任何工序）才算无路由；S1/S2 在 R1/R2 上有工序 ⇒ 有路由。
        Assert.Equal(new[] { "S3" }, noSelection.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "S3" }, blankSelection.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// **零回归反证**：归一化 ON（全部 `DEFAULT/1`）时，「传选中路径」与「不传」结果**逐元素相同**
    /// ⇒ 本改动对当前生产载荷是**严格 no-op**。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_NormalizedDefaultPayload_SelectedAndLegacyAreIdentical()
    {
        var request = RequestWith(
            Op("DEFAULT", 1, "S1"),
            Op("DEFAULT", 1, "S2"));

        var effective = Effective("S1", "S2", "S3");

        var withSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "DEFAULT", 1);
        var legacy = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, null, null);

        Assert.Equal(new[] { "S3" }, withSelection.ToArray());
        Assert.Equal(withSelection.OrderBy(s => s, StringComparer.Ordinal), legacy.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// 边界：选中路径给定时**只按 `RouteCode` 过滤**，`PathId` 缺省（null）不参与 ⇒ 同 RouteCode 的
    /// 两条 Path 都算「有路由」（保守方向：**少**判无路由 ⇒ 不凭空多出提前期）。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_SelectedRouteWithoutPathId_MatchesAllPathsOfThatRoute()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R1", 2, "S2"),
            Op("R2", 1, "S3"));

        var effective = Effective("S1", "S2", "S3");

        var result = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", null);

        Assert.Equal(new[] { "S3" }, result.ToArray());
    }

    /// <summary>边界：范围内无该物料 / 空集 ⇒ 返回空表（不抛）。</summary>
    [Fact]
    public void GetRoutelessStages_MaterialNotInScope_ReturnsEmpty()
    {
        var request = RequestWith(Op("R1", 1, "S1"));
        var effective = new Dictionary<int, HashSet<string>>
        {
            [999] = new HashSet<string>(new[] { "S1" }, StringComparer.Ordinal),
        };

        var result = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", 1);

        Assert.Empty(result);
    }

    /// <summary>
    /// **生产入口级判别性用例**（经 <see cref="StageTimingNodeBuilder.BuildCore"/>，**两版签名通用**
    /// ⇒ 可作整改前/后的对照）：真实 `RouteCode` 载荷 + 末端 Task 带选中路径 ⇒
    /// **不得**把选中路径上、位于末端**之后**的 Stage 当成「无路由」。
    ///
    /// 载荷：`R1` 上有 `S1`(末端) 与 `S2`；`S3` 无工序；Stage 链 `S1(1)→S2(2)→S3(3)`。
    ///   · 旧实现（写死 `op.RouteCode != "DEFAULT"` ⇒ 恒假）⇒ routeless = {S1,S2,S3}
    ///     ⇒ 末端 `S1` 之后产出 **`S2`、`S3` 两段**（凭空多出 `S2` 的 5h）。
    ///   · 整改后（按选中路径 `R1` 过滤）⇒ routeless = {S3} ⇒ **恰一段**。
    /// </summary>
    [Fact]
    public void BuildCore_RealRouteCodePayload_DoesNotTreatSelectedRouteStagesAsRouteless()
    {
        var request = new DomainSolveRequest
        {
            PlanningStart = Start,
            PlanningEnd = Start.AddYears(100),
            LogicalProductionDemands = new[]
            {
                new LogicalProductionDemand
                {
                    LogicalDemandKey = "D1",
                    MaterialId = MaterialId,
                    AllocationSequence = 1,
                },
            },
            StageLeadTimes = new[]
            {
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S2", LeadTimeHours = 5m, MatchLevel = "DEPT_EXACT" },
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S3", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
            },
            StageSequenceChains = new[]
            {
                new StageSequenceChain
                {
                    MaterialId = MaterialId,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "S1", StageSeq = 1 },
                        new() { StageCode = "S2", StageSeq = 2 },
                        new() { StageCode = "S3", StageSeq = 3 },
                    },
                },
            },
            RoutingOperations = new[]
            {
                Op("R1", 1, "S1"),
                Op("R1", 1, "S2"),
            },
        };

        var stages = Effective("S1", "S2", "S3");
        var terminals = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal)
        {
            ["D1"] = new FinalTaskDraft
            {
                FinalDraftId = "F1",
                SourceDraftId = "D1",
                MaterialId = MaterialId,
                StageCode = "S1",
                OperationCode = "R1-S1",
                RouteCode = "R1",
                PathId = 1,
                PlannedStartTime = Start,
                PlannedEndTime = Start.AddHours(8),
            },
        };

        var fact = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, terminals));

        // 只有 S3 无路由 ⇒ 恰一段；S2 在选中路径 R1 上有工序 ⇒ 不得再被当无路由
        var seg = Assert.Single(fact.StageTimings);
        Assert.Equal("S3", seg.StageCode);
        Assert.Equal(Start.AddHours(8 + 7), fact.CompletionTime);
    }

    // ══════════════════════════════════════════════════════════════════
    // S2-01（2026-10-14）0号位《…再次整改回执正式审核回复》§二 要求的三类正反例
    //   判词逐字：「**1号位应以已确定的Path为依据传播可执行的Stage完成时间；对尚未选定Path或实际未排下的
    //   需求，不得把任意候选路径并集产生的时间当作权威承诺时间，也不得据此生成假Task、漏计真实StageLeadTime
    //   或静默报完整完成。**」
    //   ⇒ 本组用例即针对「**并集替代真实 Path**」这一 0号位 明确点名的待核风险（其反例：同物料合法
    //     Path A / Path B，某 Stage 只在 A 有 Operation；未选中时并集判「有 Routing」，若实际按 B 执行则结论失真）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>夹具：Stage 链 `S1(1)→S2(2)→S3(3)`；S2/S3 各有**真实**提前期参数（5h / 7h，`DEPT_EXACT`）。</summary>
    private static DomainSolveRequest PathFixture(params RoutingOperation[] ops)
        => new()
        {
            PlanningStart = Start,
            PlanningEnd = Start.AddYears(100),
            LogicalProductionDemands = new[]
            {
                new LogicalProductionDemand
                {
                    LogicalDemandKey = "D1",
                    MaterialId = MaterialId,
                    AllocationSequence = 1,
                },
            },
            StageLeadTimes = new[]
            {
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S2", LeadTimeHours = 5m, MatchLevel = "DEPT_EXACT" },
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S3", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
            },
            StageSequenceChains = new[]
            {
                new StageSequenceChain
                {
                    MaterialId = MaterialId,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "S1", StageSeq = 1 },
                        new() { StageCode = "S2", StageSeq = 2 },
                        new() { StageCode = "S3", StageSeq = 3 },
                    },
                },
            },
            RoutingOperations = ops,
        };

    /// <summary>末端真实 Task 落在 `S1`，`End = Start + 8h`，并带上**实际选中路径**（可为 null = 未选定）。</summary>
    private static Dictionary<string, FinalTaskDraft> PathTerminal(string? routeCode, long? pathId)
        => new(StringComparer.Ordinal)
        {
            ["D1"] = new FinalTaskDraft
            {
                FinalDraftId = "F1",
                SourceDraftId = "D1",
                MaterialId = MaterialId,
                StageCode = "S1",
                OperationCode = $"{routeCode ?? "NONE"}-S1",
                RouteCode = routeCode,
                PathId = pathId,
                PlannedStartTime = Start,
                PlannedEndTime = Start.AddHours(8),
            },
        };

    /// <summary>
    /// 载荷：`R1/1` 只做 `S1`；`R2/2` 做 `S1`、`S2`。⇒ **同一物料两条合法 Path，Operation 结构不一致**，
    /// 且 `S2` **只在 `R2/2` 有** Operation —— 正是 0号位 反例所构造的形态。
    /// </summary>
    private static DomainSolveRequest TwoPathFixture()
        => PathFixture(
            Op("R1", 1, "S1"),
            Op("R2", 2, "S1"),
            Op("R2", 2, "S2"));

    // ── ① Path A 有 Operation 而 Path B 无 Operation、分别选中时 StageTime 必须不同 ──

    /// <summary>
    /// **0号位 反例 #1 的正证**：同物料两条合法 Path，`S2` 只在 `R2/2` 有 Operation。
    ///   · 选中 `R1/1` ⇒ `S2` **确为**无 Routing（该路径上真没有它的工序）⇒ 末端 `S1` 之后 **S2(5h)+S3(7h)**。
    ///   · 选中 `R2/2` ⇒ `S2` **有** Routing ⇒ 末端之后 **只有 S3(7h)**。
    /// ⇒ 两者 `StageTimings` 与 `CompletionTime` **必须不同**。若「一律取并集」，两者会**同值** ⇒ 本用例即红。
    /// </summary>
    [Fact]
    public void S2_01_同一物料两路径_Operation结构不同_分别选中时StageTime不同()
    {
        var request = TwoPathFixture();
        var stages = Effective("S1", "S2", "S3");

        var viaPathA = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, PathTerminal("R1", 1)));
        var viaPathB = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, PathTerminal("R2", 2)));

        // Path A（R1/1）：S2 无工序 ⇒ 末端后 S2 → S3
        Assert.Equal(new[] { "S2", "S3" }, viaPathA.StageTimings.Select(s => s.StageCode).ToArray());
        Assert.Equal(Start.AddHours(8 + 5 + 7), viaPathA.CompletionTime);

        // Path B（R2/2）：S2 有工序 ⇒ 末端后只剩 S3
        Assert.Equal(new[] { "S3" }, viaPathB.StageTimings.Select(s => s.StageCode).ToArray());
        Assert.Equal(Start.AddHours(8 + 7), viaPathB.CompletionTime);

        Assert.NotEqual(viaPathA.CompletionTime, viaPathB.CompletionTime);
    }

    // ── ② 未选中 Path 且多候选结构不一致 ⇒ 必须区分「候选/诊断」与「已选真实完成时间」 ──

    /// <summary>
    /// **0号位 反例 #2 的正证**：`TwoPathFixture` 的两条候选路径**结构不一致**（`S2` 一条有一条没有）。
    ///   · **未选中 Path**（需求整条**未排下**，`terminal = null`）⇒ 路径未定 **且** 该物料**有**真实 Routing
    ///     ⇒ 其无 Routing Stage 提前期属**占位/候选口径** ⇒ 0号位 2026-10-14 第七轮 §八.3 门控**抑制**
    ///     `StageTimings` ⇒ 该 Fact 落为 (iii) 态：`TerminalFinalDraftId == null` **且** `StageTimings` 空
    ///     ⇒ **未伪造 Task**、也**不给出可被误读为权威的 CompletionTime**（退化为 `PlanningStart`）。
    ///     该 Demand 键**必须**进 `completionTimeUnavailable` 收集器（装配层据此写
    ///     `DEMAND_COMPLETION_TIME_UNAVAILABLE`，**不静默**）。
    ///   · **已选中 Path**（`R1/1`）⇒ `PathDetermined == true`、`terminal != null` ⇒ 门控**不**生效，
    ///     **不**进任何留痕收集器 ⇒ 才是**权威**完成时间。
    /// </summary>
    [Fact]
    public void S2_01_未选中Path且多候选结构不一致_只出候选口径并留痕_不伪造权威完成()
    {
        var request = TwoPathFixture();
        var stages = Effective("S1", "S2", "S3");

        // ── 未选中 Path：占位/候选口径（§八.3 门控后**不产出**时间段） ──
        var undetermined = new List<string>();
        var unavailable = new List<string>();
        var candidate = Assert.Single(StageTimingNodeBuilder.BuildCore(
            request, stages, new Dictionary<string, FinalTaskDraft>(), undetermined, unavailable));

        Assert.False(
            StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, null, null).PathDetermined);
        Assert.True(
            StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, null, null).HasRoutingInScope);

        Assert.Null(candidate.TerminalFinalDraftId);            // 未排下 ⇒ 不伪造 Task
        Assert.Empty(candidate.StageTimings);                   // 占位口径**被门控抑制**
        Assert.Equal(Start, candidate.CompletionTime);          // 退化值，非权威

        Assert.Equal(new[] { "D1" }, unavailable.ToArray());    // 必须留痕，不得静默
        Assert.Empty(undetermined);                             // 两类留痕互斥：抑制后不再叠加 S2-01 码

        // ── 已选中 Path：权威口径 ──
        var determined = new List<string>();
        var authoritative = Assert.Single(
            StageTimingNodeBuilder.BuildCore(request, stages, PathTerminal("R1", 1), determined));

        Assert.True(
            StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R1", 1).PathDetermined);
        Assert.Empty(determined);                               // 权威 ⇒ 不留痕
        Assert.Equal("F1", authoritative.TerminalFinalDraftId);
        Assert.Equal(Start.AddHours(8 + 5 + 7), authoritative.CompletionTime);

        // 「候选」与「权威」**必须可区分**（这正是本项整改的落点）
        Assert.NotEqual(candidate.CompletionTime, authoritative.CompletionTime);
    }

    /// <summary>
    /// **边界（确定性判据本身）**：同一 `RouteCode` 下**唯一** Path ⇒ 已确定（权威）；
    /// 该 `RouteCode` 有**多条** Path 而只给 `RouteCode` ⇒ **未确定**（候选）。
    /// 声称的 `PathId` **不在载荷内** ⇒ 亦**未确定**（契约不一致**不按"近似路径"放行**）。
    /// </summary>
    [Fact]
    public void S2_01_路径确定性判据_唯一Path才权威_多Path或越域Path均候选()
    {
        var request = TwoPathFixture();
        var stages = Effective("S1", "S2", "S3");

        // R1 唯一 Path=1 ⇒ 已确定（即便只给 RouteCode、不给 PathId，也可补全）
        Assert.True(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R1", null).PathDetermined);
        Assert.True(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R1", 1).PathDetermined);

        // R2 只有 Path=2 ⇒ 已确定
        Assert.True(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R2", 2).PathDetermined);

        // 声称的 PathId 越域（R1 只有 1）⇒ 契约不一致 ⇒ 未确定，不得静默按 R1 放行
        Assert.False(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R1", 9).PathDetermined);

        // 载荷里没有该 RouteCode 的工序 ⇒ 无从判定 ⇒ 未确定
        Assert.False(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "RX", 1).PathDetermined);

        // 未给 RouteCode ⇒ 未确定
        Assert.False(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, null, null).PathDetermined);
    }

    /// <summary>
    /// **可测的「整改前红」**（用的正是**改动前就存在**的 5 参 `GetRoutelessStages` 签名，故两版可对照）：
    /// 声称的 `PathId` **越域**（`R1` 只有 Path 1，却给了 9）⇒ 契约不一致。
    ///   · **整改前**：`restrictToSelectedRoute && selectedPathId.HasValue && op.PathId != 9` ⇒ **R1 的工序被全部滤掉**
    ///     ⇒ `stagesWithRouting` 恒空 ⇒ **范围内全部 Stage 被判「无路由」** ⇒ **凭空多出提前期**（静默、无留痕）。
    ///   · **整改后**：越域 ⇒ `PathDetermined = false` ⇒ **不按 `PathId` 过滤**（退回该 Route 的并集）
    ///     ⇒ 只有真正无工序的 `S3` 被判无路由。
    /// </summary>
    [Fact]
    public void S2_01_越域PathId_不得把全部Stage误判为无Routing_旧实现凭空多出提前期()
    {
        var request = TwoPathFixture();
        var stages = Effective("S1", "S2", "S3");

        var routeless = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, stages, request, "R1", 9);

        // 整改前会是 {S1,S2,S3}（R1 的工序被越域 PathId 全部滤掉 ⇒ 全 Stage 误判无路由）
        Assert.Equal(new[] { "S2", "S3" }, routeless.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.False(StageTimingNodeBuilder.ResolveRoutelessStages(MaterialId, stages, request, "R1", 9).PathDetermined);
    }

    // ── ③ 真实 LeadTime 保留 + 归一化桥接解除后无 DEFAULT 依赖 + A/B 固定路径与 C 桶选路均不串 ──

    /// <summary>
    /// **0号位 要求 #3 的正证**（同一夹具一次验三条）：
    ///   (a) **真实 LeadTime 保留**：`S3` 在**已选中路径**下真无 Routing ⇒ 取到**真实** `7h`
    ///       （`DEPT_EXACT`）—— 既**不是** 72h 兜底（`STAGE_LEADTIME_MISSING`），也**不是** 0；
    ///   (b) **无 DEFAULT 依赖**：载荷是真实 `RouteCode`（`R1`/`R2`，无任何 `DEFAULT`）⇒ 选中路径的结论
    ///       **必须**不同于未选中路径的结论（旧实现写死 `RouteCode != "DEFAULT"` 时两者会**同值**）；
    ///   (c) **A/B 固定路径与 C 桶选路均不串**：`R1/1` 与 `R2/2` 两种选路的 `StageTimings` **互不相同**，
    ///       且 `R2/2` 的 `S2` **不得**出现在 `R1/1` 的结果里（反向亦然）—— 即两条固定路径互不串。
    ///
    /// ⚠ **2026-10-15（第七轮 §八.3 门控落地后）**：未选中路径（`union`）**不再产出**「并集口径」的时间段
    ///   —— 该物料**有**真实 Routing ⇒ 其无 Routing Stage 提前期属**占位口径** ⇒ 被抑制为 (iii) 态
    ///   （`TerminalFinalDraftId == null` 且 `StageTimings` 空）。故 (a) 只在**已选中路径**上验；
    ///   (b) 由「选中 ⇒ 有值 vs 未选中 ⇒ 退化值」给出**更强**的分离。
    /// </summary>
    [Fact]
    public void S2_01_真实LeadTime保留_去DEFAULT依赖_固定路径与C桶选路互不串()
    {
        var request = TwoPathFixture();
        var stages = Effective("S1", "S2", "S3");

        var viaA = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, PathTerminal("R1", 1)));
        var viaB = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, PathTerminal("R2", 2)));
        var unavailable = new List<string>();
        var union = Assert.Single(StageTimingNodeBuilder.BuildCore(
            request, stages, new Dictionary<string, FinalTaskDraft>(), null, unavailable));

        // (a) 真实 LeadTime 保留：**已选中路径**下 S3 恒取 7h / DEPT_EXACT（非兜底、非 0）
        foreach (var fact in new[] { viaA, viaB })
        {
            var s3 = Assert.Single(fact.StageTimings.Where(s => s.StageCode == "S3"));
            Assert.Equal(7m, s3.LeadTimeHours);
            Assert.Equal("DEPT_EXACT", s3.MatchLevel);
        }

        // (a′) 未选中路径 ⇒ 占位口径被抑制，**不静默**（§八.3 门控的正证）
        Assert.Empty(union.StageTimings);
        Assert.Equal(Start, union.CompletionTime);
        Assert.Equal(new[] { "D1" }, unavailable.ToArray());

        // (b) 去 DEFAULT 依赖：真实 RouteCode 载荷下，选中（有值）≠ 未选中（退化值）
        Assert.NotEqual(viaA.CompletionTime, union.CompletionTime);
        Assert.NotEqual(viaB.CompletionTime, union.CompletionTime);

        // (c) 固定路径互不串：S2 只在 R2/2 有工序 ⇒ 只有在 R1/1 下才真无 Routing
        Assert.Contains(viaA.StageTimings, s => s.StageCode == "S2");
        Assert.DoesNotContain(viaB.StageTimings, s => s.StageCode == "S2");
        Assert.NotEqual(
            viaA.StageTimings.Select(s => s.StageCode).ToArray(),
            viaB.StageTimings.Select(s => s.StageCode).ToArray());
    }

    // ══════════════════════════════════════════════════════════════════
    // §2.3 ErrorMessage 组合
    // ══════════════════════════════════════════════════════════════════

    private static InitialScheduleResult ResultWith(
        IEnumerable<KeyValuePair<string, string>>? hardFailures = null,
        IEnumerable<string>? conflicts = null,
        IEnumerable<string>? missing = null,
        IEnumerable<string>? unavailable = null)
    {
        var result = new InitialScheduleResult();
        if (hardFailures is not null)
        {
            foreach (var kv in hardFailures)
            {
                result.BatchPolicyHardFailures[kv.Key] = kv.Value;
            }
        }

        if (conflicts is not null)
        {
            result.BatchPolicyConflicts.AddRange(conflicts);
        }

        if (missing is not null)
        {
            result.BatchPolicyMissingDemandKeys.AddRange(missing);
        }

        if (unavailable is not null)
        {
            result.BatchPolicyUnavailableDemandKeys.AddRange(unavailable);
        }

        return result;
    }

    /// <summary>三段皆空 ⇒ `null`（**不产出空串**，避免把「无失败」写成空 `ErrorMessage`）。</summary>
    [Fact]
    public void ComposeErrorMessage_AllSegmentsEmpty_ReturnsNull()
    {
        var result = ResultWith();

        Assert.Null(PhaseFiveCompression.ComposeErrorMessage(null, null, result));
        Assert.Null(PhaseFiveCompression.ComposeErrorMessage("  ", "  ", result));
    }

    /// <summary>前缀顺序稳定：`[Phase5] … | [Technical] … | [BatchPolicy] …`（2号位 要按前缀拆列）。</summary>
    [Fact]
    public void ComposeErrorMessage_SegmentOrderIsStableAndPrefixed()
    {
        var result = ResultWith(
            hardFailures: new Dictionary<string, string> { ["D-1"] = "BATCH_POLICY_CONFLICT" },
            conflicts: new[] { "[D-1] PI_LEGAL_QTY_CONTRACT_PENDING：缺 Stage 合法自由量上下文" });

        var text = PhaseFiveCompression.ComposeErrorMessage(
            "[Phase5] 硬约束校验失败: X", "[Technical] Y", result);

        Assert.NotNull(text);
        Assert.StartsWith("[Phase5] ", text);
        Assert.Contains(" | [Technical] ", text);
        Assert.Contains(" | [BatchPolicy] ", text);
        Assert.True(
            text!.IndexOf("[Phase5]", StringComparison.Ordinal)
                < text.IndexOf("[Technical]", StringComparison.Ordinal));
        Assert.True(
            text.IndexOf("[Technical]", StringComparison.Ordinal)
                < text.IndexOf("[BatchPolicy]", StringComparison.Ordinal));
        // 明细正文（真实原因）确实被带出来，不再只留在零读者的分列表里
        Assert.Contains("PI_LEGAL_QTY_CONTRACT_PENDING", text);
    }

    /// <summary>
    /// **有界性反证**（2号位 实测 21,772/21,787 会被拦下 ⇒ 无界拼接会写出兆字节字符串）：
    /// 2,000 条硬失败 ⇒ 输出仍**远小于**明细总量，且**示例最多 3 条**、每条截断。
    /// </summary>
    [Fact]
    public void ComposeErrorMessage_LargeBatchPolicySet_IsBounded()
    {
        const int n = 2000;
        var hardFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var key = $"D-{i:D5}";
            hardFailures[key] = "BATCH_POLICY_CONFLICT";
            conflicts.Add($"[{key}] PI_LEGAL_QTY_CONTRACT_PENDING：" + new string('X', 400));
        }

        var result = ResultWith(hardFailures: hardFailures, conflicts: conflicts);

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains($"[BatchPolicy] 硬失败需求 {n} 条", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 2000", text);
        // 有界：远小于 2000 × 400 字符的明细总量
        Assert.True(text!.Length < 2000, $"ErrorMessage 未被有界化，长度 = {text.Length}");
        // 示例恰 3 条（`" ; "` 出现 2 次分隔）
        Assert.Equal(2, text.Split(" ; ").Length - 1);
        // 单条示例被截断
        Assert.Contains("…", text);
    }

    /// <summary>分类计数逐项正确（`BATCH_POLICY_MISSING` 与 `BATCH_POLICY_CONFLICT` 分列）。</summary>
    [Fact]
    public void ComposeErrorMessage_CountsEachHardFailureClassSeparately()
    {
        var result = ResultWith(hardFailures: new Dictionary<string, string>
        {
            ["D-1"] = "BATCH_POLICY_MISSING",
            ["D-2"] = "BATCH_POLICY_MISSING",
            ["D-3"] = "BATCH_POLICY_CONFLICT",
        });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains("硬失败需求 3 条", text);
        Assert.Contains("BATCH_POLICY_MISSING 2", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 1", text);
    }

    /// <summary>
    /// **第三类单列**：`BATCH_POLICY_STATE_UNAVAILABLE`（2026-10-13 新增 —— 0号位 文件4 §3.2／§3.3
    ///   明令「状态不可获得」**不得**并入 `MISSING`）⇒ 必须与另两类**分列计数**，不得被吞进任一旧桶。
    /// </summary>
    [Fact]
    public void ComposeErrorMessage_CountsStateUnavailableSeparately()
    {
        var result = ResultWith(hardFailures: new Dictionary<string, string>
        {
            ["D-1"] = "BATCH_POLICY_MISSING",
            ["D-2"] = "BATCH_POLICY_STATE_UNAVAILABLE",
            ["D-3"] = "BATCH_POLICY_STATE_UNAVAILABLE",
            ["D-4"] = "BATCH_POLICY_CONFLICT",
        });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains("硬失败需求 4 条", text);
        Assert.Contains("BATCH_POLICY_MISSING 1", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 1", text);
        Assert.Contains("BATCH_POLICY_STATE_UNAVAILABLE 2", text);
    }

    /// <summary>
    /// 明细取回：命中 ⇒ 返回该条；未命中 / 分列表为空 ⇒ `null`（**不伪造**）。
    /// **三个**分列表都要查（缺策略走 `BatchPolicyMissingDemandKeys`；状态不可获得走
    /// `BatchPolicyUnavailableDemandKeys`）。
    /// </summary>
    [Fact]
    public void FindBatchPolicyDetail_ResolvesFromEitherList_OrReturnsNull()
    {
        var result = ResultWith(
            conflicts: new[] { "[D-1] CONFLICT-TEXT" },
            missing: new[] { "[D-2] MISSING-TEXT" },
            unavailable: new[] { "[D-4] UNAVAILABLE-TEXT" });

        Assert.Equal("[D-1] CONFLICT-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-1"));
        Assert.Equal("[D-2] MISSING-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-2"));
        Assert.Equal("[D-4] UNAVAILABLE-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-4"));
        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(result, "D-3"));

        var empty = ResultWith();
        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(empty, "D-1"));
    }

    /// <summary>
    /// 前缀匹配是**严格**的（`[D-1] ` 不得命中 `[D-10] `）—— 否则会把别的需求的明细当成自己的。
    /// </summary>
    [Fact]
    public void FindBatchPolicyDetail_PrefixMatchIsExact_NotSubstring()
    {
        var result = ResultWith(conflicts: new[] { "[D-10] TEN" });

        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(result, "D-1"));
        Assert.Equal("[D-10] TEN", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-10"));
    }

    /// <summary>只有 `[BatchPolicy]` 段时，输出**不以** `|` 开头（无多余分隔符）。</summary>
    [Fact]
    public void ComposeErrorMessage_BatchPolicyOnly_HasNoLeadingSeparator()
    {
        var result = ResultWith(
            hardFailures: new Dictionary<string, string> { ["D-1"] = "BATCH_POLICY_CONFLICT" },
            conflicts: new[] { "[D-1] X" });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.StartsWith("[BatchPolicy] ", text);
        Assert.DoesNotContain(" | ", text);
    }
}
