using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **第七轮 §八.3**（0号位 2026-10-14《致1号位第七轮代码审核正式复核回复 v1.0》）：
///   「`DemandCompletions`对外安全性：补本轮 **Solver 出口**完整正反证（三场景＋部分净量），
///    写明当前 DTO 中**权威/未排状态**的区分方式；**不接受只写一条 Warning 或只证明没有 FinalTask**。」
///
/// 判词逐字给出的三态：
///   (i)  **权威已排完成**；(ii) **真正纯无 Routing 且经合法 `StageLeadTime` 形成的时间约束**；
///   (iii) **未排占位 / 候选诊断**。
///
/// 本文件的立论方式（**不是**「只证明没有 FinalTask」）：
///   ① 由 **DTO 自身字段**给出可判定判据，并在 **Solver 出口**逐态实测正例/反例；
///   ② 对 (ii) 给出**结构性不可达证明**（不是「没测到」，而是「与 (iii) 的判据互斥」）——
///      见 <see cref="Solver出口_纯无Routing物料_其三态(ii)结构性不可达_且链提前期不进入CompletionTime"/>；
///   ③ 对「部分净量」给出**实测出口行为**（N8-02 整改后为**合法落定 + 三量可读回**）——
///      见 <see cref="Solver出口_部分净量_合法落定_三量QS_U可读回"/>，
///      及其**校验侧鉴别性反证** <see cref="Solver出口_部分净量_未排量漏记时_第1项三量等式必须红"/>。
///
/// ⚠ 本文件只读 `LPS.APS.Scheduling` 的公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>；
///   纯内存、不触库、不读文件、不写文件。
/// </summary>
public class DemandCompletionSolverExitTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;
    private const string UnavailableCode = "DEMAND_COMPLETION_TIME_UNAVAILABLE";

    private readonly FiniteCapacitySolver _solver = new();

    // ════════════════════════════════════════════════════════════════════
    //  (i) 权威已排完成 —— 且**含**「选定路径之外的无 Routing Stage」片段
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判据 (i)：`TerminalFinalDraftId != null`。
    ///
    /// 载荷：同 Route `RT` 两条 Path —— `Path1 = {OP10@STAGE1}`、`Path2 = {OP10@STAGE1 → OP20@STAGE2}`；
    ///   需求固定 `RT/Path1`。⇒ 求解范围内可达 Stage = {STAGE1, STAGE2}，但**选定路径** `RT/Path1`
    ///   在 `STAGE2` 上**没有**工序 ⇒ `STAGE2` 对该需求是**无 Routing Stage**，
    ///   按 Stage 链 `STAGE1(1)→STAGE2(2)` 取其合法提前期 `7h` 接在末端真实 Task 之后。
    ///
    /// 断言：末端 Task 存在 ⇒ **权威**；`StageTimings` **非空**且为 `STAGE2`；
    ///   `CompletionTime == AvailableTime == Task.End + 7h`（时间由**已选定 Path** 决定，非候选并集）。
    /// </summary>
    [Fact]
    public async Task Solver出口_已排下_为权威_且选定路径外的无RoutingStage按合法提前期接续()
    {
        var result = await _solver.SolveAsync(Build(
            ops: new[]
            {
                Op("RT", 1, "OP10", "STAGE1"),
                Op("RT", 2, "OP10", "STAGE1"),
                Op("RT", 2, "OP20", "STAGE2")
            },
            deps: new[] { Dep("RT", 2, "OP10", "OP20") },
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP10", ResourceId: 1) },
            calendarEnd: Day.AddDays(5),
            leadTimes: new[] { ("STAGE2", 7m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: "RT",
            demandPathId: 1));

        Assert.True(result.Success, result.ErrorMessage);

        var task = Assert.Single(result.FinalTasks);
        var fact = Assert.Single(result.DemandCompletions);

        // (i) 的判据：DTO 自身字段即可判定「权威已排完成」。
        Assert.Equal("D1", fact.LogicalDemandKey);
        Assert.Equal(task.FinalDraftId, fact.TerminalFinalDraftId);
        Assert.NotNull(fact.TerminalFinalDraftId);

        // 无 Routing Stage（STAGE2 在选定 Path1 上无工序）按**合法提前期**接在末端 Task 之后。
        var seg = Assert.Single(fact.StageTimings);
        Assert.Equal("STAGE2", seg.StageCode);
        Assert.Equal(7m, seg.LeadTimeHours);
        Assert.Equal(task.PlannedEndTime, seg.StartTime);
        Assert.Equal(task.PlannedEndTime.AddHours(7), seg.EndTime);

        Assert.Equal(task.PlannedEndTime.AddHours(7), fact.CompletionTime);
        Assert.Equal(fact.CompletionTime, fact.AvailableTime);

        // 已排下 ⇒ 不得被「未排占位」留痕误标。
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == UnavailableCode);
    }

    // ════════════════════════════════════════════════════════════════════
    //  (iii) 未排占位 / 候选诊断
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判据 (iii)：`TerminalFinalDraftId == null` **且** `StageTimings.Count == 0`。
    ///
    /// 载荷：该物料**有**真实 Routing（`RT/1` 上 `OP10@STAGE1`），但**无任何资格行** ⇒ 排不下；
    ///   Stage 链 `STAGE1(1)→STAGE2(2)` 且 `STAGE2` 有合法提前期 `7h`。
    ///
    /// 断言：无末端 Task ⇒ 该需求的 `STAGE2` 提前期是**候选口径**（无选定 Path 可依据）
    ///   ⇒ 按第七轮 §八.3 门控**不产出** `StageTimings`（落为 (iii)）；
    ///   `CompletionTime == AvailableTime == PlanningStart`（退化值），且经既有追溯通道
    ///   **显式留痕** `DEMAND_COMPLETION_TIME_UNAVAILABLE`（**不静默**）。
    /// </summary>
    [Fact]
    public async Task Solver出口_有Routing但未排下_为占位_须抑制候选提前期且显式留痕()
    {
        var result = await _solver.SolveAsync(Build(
            ops: new[] { Op("RT", 1, "OP10", "STAGE1") },
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("STAGE2", 7m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: null,
            demandPathId: null));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

        var fact = Assert.Single(result.DemandCompletions);

        // (iii) 的判据：两字段联合，**且**二者皆无 —— 与 (ii) 的判据互斥。
        Assert.Null(fact.TerminalFinalDraftId);
        Assert.Empty(fact.StageTimings);

        Assert.Equal(Day, fact.CompletionTime);
        Assert.Equal(fact.CompletionTime, fact.AvailableTime);

        // **不静默**：退化的 CompletionTime 必须显式留痕（这正是「不接受只写一条 Warning」的另一面：
        //   该 Warning 是**附加证据**，不是判据本身 —— 判据是上面两行字段）。
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == UnavailableCode && n.Key == "D1");

        // 无末端真实 Task ⇒ 不得有孤儿份额（净量闭合不变式）。
        Assert.All(result.AllocationShares,
            s => Assert.Contains(result.FinalTasks, t => t.FinalDraftId == s.FinalDraftId));
    }

    // ════════════════════════════════════════════════════════════════════
    //  (ii) 在 Solver 出口 **已可达**（N8-01 修复后）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **N8-01 反证 ①（0号位 2026-10-15 第九轮 §3.3 联合最低反证）**：
    ///   零 Routing 且合法 `S1→S2` 提前期 `3h + 7h` ⇒ 完成时间约束 **10h**、**0 假 Task**。
    ///
    /// 载荷 = 该物料**一条 Routing 工序都没有**（`RoutingOperations` 空），
    ///   但 Stage 链 `STAGE1(1)→STAGE2(2)` 与两条合法 `StageLeadTime`（`DEPT_EXACT`）齐全
    ///   —— 即 0号位 §10.3 明文认可「整条无真实 Operation Task 时自起点纯提前期累计」的最纯形态。
    ///
    /// **修复前（r13703 及以前）实测**：出口落 (iii) —— `TerminalFinalDraftId == null`
    ///   **且** `StageTimings` 空 ⇒ 链提前期**未**进入 `CompletionTime`（`CompletionTime == PlanningStart`）。
    ///   根因 = `PhaseOneConstraintBuilder.BuildReachableStages` 唯一取源是 `RoutingOperations`，
    ///   无 Routing 的物料直接 `continue` ⇒ 这类 Stage 永远进不了 `EffectiveStages`
    ///   ⇒ `ResolveRoutelessStages` 恒返回空 ⇒ 时间链**结构性不可达**。
    ///
    /// **修复后（N8-01）**：`EffectiveStages` = Routing 可达集 **∪** 按起点裁剪后的 BOM/Stage 链
    ///   ⇒ 出口落 (ii)「真正纯无 Routing 且经合法 `StageLeadTime` 形成的时间约束」。
    /// 冻结依据：规则 **S-006**（现行 v1.7，Owner 1/2）+ 核心走查 V3.28《04》场景7。
    /// </summary>
    [Fact]
    public async Task Solver出口_纯无Routing物料_其三态ii已可达_链提前期进入CompletionTime_且0假Task()
    {
        var request = Build(
            ops: Array.Empty<RoutingOperation>(),          // **一条 Routing 工序都没有**
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("STAGE1", 3m), ("STAGE2", 7m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: null,
            demandPathId: null);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        // ① 0 假 Task：无 Routing 的 Stage **不生成** Task / TaskNo / MES 工单（S-006 逐字）。
        Assert.Empty(result.FinalTasks);

        var fact = Assert.Single(result.DemandCompletions);

        // ② 出口落 (ii)：无末端真实 Task，但**有时间段**，且时间来自合法提前期（3h + 7h = 10h）。
        Assert.Null(fact.TerminalFinalDraftId);
        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal(Day.AddHours(3), fact.StageTimings[0].EndTime);
        Assert.Equal(Day.AddHours(10), fact.StageTimings[1].EndTime);
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);

        // ③ 时间**有真实依据** ⇒ 不得再落「时间不可用」留痕（那是 (iii) 的判据）。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == UnavailableCode && n.Key == "D1");
    }

    // ════════════════════════════════════════════════════════════════════
    //  N8-01 反证 ②：缺参数 ⇒ 只出 Issue，**不虚造 Time**（0h / 72h 都不许）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **N8-01 反证 ②-a（0号位 2026-10-15 第九轮 §3.3）**：缺参数 → Issue 不虚造 Time。
    ///
    /// 载荷：纯无 Routing，链 `S1(1)→S2(2)`，**只有** `S1` 有合法提前期 `3h`，`S2` 三级参数全未命中。
    ///
    /// 断言：`StageTimings` **只有 1 段**（`S1`），`CompletionTime == Day + 3h`；
    ///   且 `STAGE_LEADTIME_MISSING` 的 `SolveTraceNote` 落在 `D1` 上（显式进 Issue，不静默）。
    ///
    /// **判别性**（这是本条反证的要害）：段数 **1 vs 2** 同时钉死两种臆造：
    ///   · 若回落到 `0h` ⇒ 段数 2、`CompletionTime == Day + 3h`（时间对不上，段数抓得住）；
    ///   · 若回落到 `72h`（本仓此前的 `FallbackLeadTimeHours = 72m`）⇒ 段数 2、`CompletionTime == Day + 75h`。
    /// 冻结依据：S-006「缺参报 `STAGE_LEADTIME_MISSING`」；《04》场景7；0号位 2026-09-29 §4.4
    ///   「不得默认 0 小时 …… 无法证明时显式进入数据/参数 Issue」。
    /// </summary>
    [Fact]
    public async Task Solver出口_纯无Routing_某Stage缺参数_只出Issue不虚造时间()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("STAGE1", 3m) },      // **只有 STAGE1 有参数**
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: null,
            demandPathId: null,
            startStageCode: null));

        Assert.True(result.Success, result.ErrorMessage);

        var fact = Assert.Single(result.DemandCompletions);

        // 缺参数的 STAGE2 **不产出时间段**（也就没有 0h / 72h 段）。
        var seg = Assert.Single(fact.StageTimings);
        Assert.Equal("STAGE1", seg.StageCode);
        Assert.Equal(3m, seg.LeadTimeHours);
        Assert.Equal(Day.AddHours(3), seg.EndTime);
        Assert.Equal(Day.AddHours(3), fact.CompletionTime);

        // 显式进 Issue（不静默）：Key = 该 Demand。
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == "STAGE_LEADTIME_MISSING" && n.Key == "D1");
    }

    /// <summary>
    /// **N8-01 反证 ②-b**：整条链**一个参数都没有** ⇒ **零**时间段、`CompletionTime` 退化、
    ///   且**每个** Stage 各出一条 `STAGE_LEADTIME_MISSING`。
    ///
    /// **判别性**：若回落到 `0h`/`72h`，`StageTimings.Count` 会是 **2**（本用例钉 `Assert.Empty`）。
    /// 同时仍走既有 `DEMAND_COMPLETION_TIME_UNAVAILABLE` 通道（(iii) 判据）—— 两条留痕语义不同：
    ///   前者说「这个 Stage 缺参数」，后者说「该 Demand 的 `CompletionTime` 无真实依据」。
    /// </summary>
    [Fact]
    public async Task Solver出口_纯无Routing_整链无参数_零时间段且每Stage各出一条Issue()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: Array.Empty<(string Stage, decimal Hours)>(),
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: null,
            demandPathId: null,
            startStageCode: null));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Empty(fact.StageTimings);
        Assert.Equal(Day, fact.CompletionTime);

        // 两个 Stage 各一条（**不合并**、**不吞**）——Stage 保留在求解范围内、只是没有时长。
        Assert.Equal(2, result.SolveTraceNotes.Count(
            n => n.ReasonCode == "STAGE_LEADTIME_MISSING" && n.Key == "D1"));
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == UnavailableCode && n.Key == "D1");
    }

    // ════════════════════════════════════════════════════════════════════
    //  N8-01 反证 ③：同物料仅某 Stage 有 Operation ⇒ Task 与其他 Stage 不重计
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **N8-01 反证 ③（0号位 2026-10-15 第九轮 §3.3）**：同物料仅某 Stage 有 Operation
    ///   ⇒ Task 与其他 Stage 的合法 `LeadTime` **不重计**。
    ///
    /// 载荷：物料只有 `OP10@STAGE1` 一条 Routing 工序（且有资格 ⇒ 真排下），
    ///   链 `S1(1)→S2(2)→S3(3)` 且**三个 Stage 都有合法提前期** `3h/5h/7h`。
    ///
    /// 断言：末端 Task 在 `STAGE1` ⇒ 只累加**它之后**的 `S2`/`S3`（`5h + 7h = 12h`），
    ///   `STAGE1` 自己的 `3h` **不计**（该 Stage 的产出已由真实 Task 表达，再计一遍就是重计）。
    ///
    /// **判别性**：段数 **2 vs 3**、增量 **12h vs 15h** 双钉 —— 若忽略末端 Stage 位置而把
    ///   整条链的提前期都加一遍，两条断言同时红。
    /// </summary>
    [Fact]
    public async Task Solver出口_同物料仅某Stage有Operation_末端Task与其余Stage合法提前期不重计()
    {
        var result = await _solver.SolveAsync(Build(
            ops: new[] { Op("RT", 1, "OP10", "STAGE1") },   // 只有 STAGE1 有工序
            deps: Array.Empty<RoutingDependency>(),
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP10", ResourceId: 1) },
            calendarEnd: Day.AddDays(5),
            leadTimes: new[] { ("STAGE1", 3m), ("STAGE2", 5m), ("STAGE3", 7m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2), ("STAGE3", 3) },
            demandRoute: "RT",
            demandPathId: 1,
            startStageCode: "STAGE1"));   // 起点就在有工序的那个 Stage（续排的常态）

        Assert.True(result.Success, result.ErrorMessage);

        var task = Assert.Single(result.FinalTasks);
        var fact = Assert.Single(result.DemandCompletions);

        Assert.Equal(task.FinalDraftId, fact.TerminalFinalDraftId);

        // 只累加末端 Task **之后**的 Stage —— STAGE1 的 3h 不计（不重计）。
        Assert.Equal(new[] { "STAGE2", "STAGE3" },
            fact.StageTimings.Select(s => s.StageCode).ToArray());
        Assert.Equal(task.PlannedEndTime.AddHours(5), fact.StageTimings[0].EndTime);
        Assert.Equal(task.PlannedEndTime.AddHours(12), fact.StageTimings[1].EndTime);
        Assert.Equal(task.PlannedEndTime.AddHours(12), fact.CompletionTime);
        Assert.NotEqual(task.PlannedEndTime.AddHours(15), fact.CompletionTime);
    }

    // ════════════════════════════════════════════════════════════════════
    //  N8-01 反证 ④：来源/起点维度的隔离反例
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **N8-01 反证 ④-a（EDGE vs ROOT）**：一条 Stage 链上**同时**来自 `ROOT` 与 `EDGE` 两类
    ///   记录的 Stage，**一个都不能丢**（`APS_BOM_STAGE_PATH_RAW` 的 `StageScopeType` 两值，
    ///   见 <c>ApsBomStagePathRaw</c>：`EDGE`=子件供给路径 / `ROOT`=根产品完工路径）。
    ///
    /// 载荷：无起点信息（新单从首工序起）⇒ 整条链在范围内；链 `ROOT_A(10)→EDGE_B(20)→EDGE_C(30)`，
    ///   三段都有合法提前期 `3h/5h/7h`。
    ///
    /// 断言：**3 段全在**、`CompletionTime == Day + 15h`。
    ///
    /// ⚠ **本层能证与不能证**（写清楚，别越界）：
    ///   · 能证：链上 Stage **按 `StageSeq` 统一消费、不因来源不同而丢**；
    ///   · **不能证**：`EDGE`/`ROOT` **各自的**隔离 —— 入站 DTO `StageSequenceStep` 只承载
    ///     `(StageCode, StageSeq, ProductionDepartmentId)`，**没有 Scope 来源字段**；且 2号位 装载
    ///     SQL（`PeggingOrchestrator.cs` 取 `APS_BOM_STAGE_PATH_RAW`）**没有 `StageScopeType` 谓词**，
    ///     直接 `GROUP BY ChildMaterialCode, StageCode` 取 `MIN(StageSeq)` ⇒ 到 Solver 时
    ///     两类记录**已被并成一条链、不可区分**。
    ///   ⇒ 该维度的真正隔离必须在**装载侧**完成，属 0号位 §3.3 的 1/2 号位联合证据项。
    /// </summary>
    [Fact]
    public async Task Solver出口_EDGE与ROOT两类Stage_链上并集全消费_但来源本身本层不可区分()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("ROOT_A", 3m), ("EDGE_B", 5m), ("EDGE_C", 7m) },
            chain: new[] { ("ROOT_A", 10), ("EDGE_B", 20), ("EDGE_C", 30) },
            demandRoute: null,
            demandPathId: null,
            startStageCode: null));    // 无起点信息 ⇒ 整条链在范围内

        Assert.True(result.Success, result.ErrorMessage);

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(new[] { "ROOT_A", "EDGE_B", "EDGE_C" },
            fact.StageTimings.Select(s => s.StageCode).ToArray());
        Assert.Equal(Day.AddHours(15), fact.CompletionTime);
    }

    /// <summary>
    /// **N8-01 反证 ④-b（StartStage 裁剪 / 已完成上游 Stage 隔离）**：
    ///   `StartStageCode` 之前的已完成 Stage **不得**再产出提前期。
    ///
    /// 载荷：纯无 Routing，链 `S0(1)→S1(2)→S2(3)`，三段都有合法提前期 `3h/5h/7h`；
    ///   `StartStageCode = "STAGE1"`（续排：`STAGE0` 已完成）。
    ///
    /// 断言：`StageTimings` **只有** `S1`/`S2`（`5h + 7h = 12h`），`STAGE0` **不在**，
    ///   `CompletionTime == Day + 12h`。
    ///
    /// **判别性**：段数 **2 vs 3**、完成时间 **12h vs 15h** —— 若取源不做起点裁剪（把整条链
    ///   无条件当范围），两条断言同时红。这正是 P1-06 的「不得凭空多出提前期」。
    /// </summary>
    [Fact]
    public async Task Solver出口_StartStage之前的已完成Stage_不得再产出提前期()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("STAGE0", 3m), ("STAGE1", 5m), ("STAGE2", 7m) },
            chain: new[] { ("STAGE0", 1), ("STAGE1", 2), ("STAGE2", 3) },
            demandRoute: null,
            demandPathId: null,
            startStageCode: "STAGE1"));

        Assert.True(result.Success, result.ErrorMessage);

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(new[] { "STAGE1", "STAGE2" },
            fact.StageTimings.Select(s => s.StageCode).ToArray());
        Assert.Equal(Day.AddHours(5), fact.StageTimings[0].EndTime);
        Assert.Equal(Day.AddHours(12), fact.StageTimings[1].EndTime);
        Assert.Equal(Day.AddHours(12), fact.CompletionTime);
        Assert.DoesNotContain("STAGE0", fact.StageTimings.Select(s => s.StageCode));
    }

    /// <summary>
    /// **N8-01 反证 ④-c（A/B Position 隔离：Stage 定位 vs Operation 定位）**：
    ///   同一物理起点、两种定位口径 ⇒ 裁剪结果必须**逐字一致**。
    ///
    /// A 口径 = `StartStageCode = "STAGE1"`；B 口径 = `StartOperationCode = "OP20"`（`OP20` 落在 `STAGE1`）。
    ///   两者载荷**除起点字段外完全相同**：链 `S0(1)→S1(2)→S2(3)`、`OP20@STAGE1` 有资格 ⇒ 真排下。
    ///
    /// 断言：两口径的 `StageTimings`（Stage 码序列）与 `CompletionTime` **相等**，
    ///   且 `STAGE0` 在两边都**不在**（两种口径都要裁剪）。
    ///
    /// **判别性**：若两条定位路径的裁剪下界不一致（例如 Operation 口径按工序码单键命中、
    ///   或忘了把 Operation 映射回 Stage），两边结果会分叉 ⇒ 本用例红。
    /// </summary>
    [Fact]
    public async Task Solver出口_起点按Stage定位与按Operation定位_裁剪结果必须一致()
    {
        var byStage = await _solver.SolveAsync(Build(
            ops: new[] { Op("RT", 1, "OP20", "STAGE1") },
            deps: Array.Empty<RoutingDependency>(),
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP20", ResourceId: 1) },
            calendarEnd: Day.AddDays(5),
            leadTimes: new[] { ("STAGE0", 3m), ("STAGE1", 5m), ("STAGE2", 7m) },
            chain: new[] { ("STAGE0", 1), ("STAGE1", 2), ("STAGE2", 3) },
            demandRoute: "RT",
            demandPathId: 1,
            startStageCode: "STAGE1"));

        var byOperation = await _solver.SolveAsync(Build(
            ops: new[] { Op("RT", 1, "OP20", "STAGE1") },
            deps: Array.Empty<RoutingDependency>(),
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP20", ResourceId: 1) },
            calendarEnd: Day.AddDays(5),
            leadTimes: new[] { ("STAGE0", 3m), ("STAGE1", 5m), ("STAGE2", 7m) },
            chain: new[] { ("STAGE0", 1), ("STAGE1", 2), ("STAGE2", 3) },
            demandRoute: "RT",
            demandPathId: 1,
            startStageCode: "STAGE1",
            startOperationCode: "OP20"));

        Assert.True(byStage.Success, byStage.ErrorMessage);
        Assert.True(byOperation.Success, byOperation.ErrorMessage);

        var a = Assert.Single(byStage.DemandCompletions);
        var b = Assert.Single(byOperation.DemandCompletions);

        // 两口径的裁剪结果逐字一致。
        Assert.Equal(a.StageTimings.Select(s => s.StageCode), b.StageTimings.Select(s => s.StageCode));
        Assert.Equal(a.CompletionTime, b.CompletionTime);

        // 且都必须裁掉 STAGE0、都不重计 STAGE1 自身。
        Assert.Equal(new[] { "STAGE2" }, a.StageTimings.Select(s => s.StageCode).ToArray());
    }

    /// <summary>
    /// **N8-01 反证 ④-d（起点定位不到 ⇒ 不放行，不得退化成「整条链无条件当范围」）**：
    ///   给了起点信息（`StartStageCode`），但该 Stage **不在本批 BOM/Stage 链里** ⇒ 定位不到。
    ///
    /// 载荷：纯无 Routing（`RoutingOperations` 空）；链 `S1(1)→S2(2)`，两段都有合法提前期 `3h/7h`；
    ///   `StartStageCode = "STAGE_X"`（链内没有、部门上下文里也没有）。
    ///
    /// 断言：链**一个 Stage 都不放行**（下界 = `int.MaxValue`）⇒ `StageTimings` 为空、
    ///   `CompletionTime == Day`。
    ///
    /// **判别性**：若「定位不到」被当成「没有起点信息 ⇒ 整条链放行」，这里会得到 **2 段 / 10h**。
    ///   冻结口径：起点信息**有但不明确** ≠ **没有起点信息** —— 前者必须 Fail Closed（与
    ///   「BFS 起点集为空 ⇒ 可达集为空」同口径），否则会把整条历史链无条件注入求解。
    ///   同一条下界规则也覆盖 `StartOperationCode` 消解不到的形态（`ResolveStartStageSeq` 同一处代码）。
    ///
    /// ⚠ 本用例刻意用**纯无 Routing**：带 Routing 的需求会先走 `PhaseTwoInitialScheduler` 的
    ///   Batch Policy 键控（`:585`，键含 `StartStageCode→Dept`），而 `STAGE_X` 反查不到部门会
    ///   先落 `BATCH_POLICY_STATE_UNAVAILABLE` Fail Closed —— 那是**另一个**判据，会掩盖本条要证的
    ///   Stage 范围取源行为。纯无 Routing 的需求在 `:528-533` 就已 `continue`，不经过该分支。
    /// </summary>
    [Fact]
    public async Task Solver出口_起点Stage不在链内_链不放行任何Stage_不得退化成整链放行()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            deps: Array.Empty<RoutingDependency>(),
            eligibility: Array.Empty<(string Route, int PathId, string Op, int ResourceId)>(),
            calendarEnd: null,
            leadTimes: new[] { ("STAGE1", 3m), ("STAGE2", 7m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            demandRoute: null,
            demandPathId: null,
            startStageCode: "STAGE_X"));   // 链内定位不到

        Assert.True(result.Success, result.ErrorMessage);

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Empty(fact.StageTimings);
        Assert.Equal(Day, fact.CompletionTime);
    }

    // ════════════════════════════════════════════════════════════════════
    //  「部分净量」在 Solver 出口的**实测**行为
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §八.3 要求的「＋部分净量」：**N8-02 整改后**（2026-10-17）实测结论 = **部分落定合法落定**，
    ///   出口同时给出 Q/S/U 三量 —— 即 0号位 第二十轮 §三 保留的受控例 `Q=10 / S=3.3333 / U=6.6667`。
    ///
    /// 载荷：净量 `10`、`AllowSplit = true`、批策略 `MaxExecutionBatchQty = 6`（⇒ 拆批）、
    ///   资源日历仅 `6` 分钟 ⇒ 3 批中只落定 1 批（`10 / 3 = 3.3333`）。
    ///
    /// ⚠ 本用例在 2026-10-17 前锁定的是**旧行为**：`closureTarget ≡ 声明量` ⇒ `ΣShare=10 > 3.3333`
    ///   ⇒ Phase5 第 3 项 `TaskShare数量不闭合` Fail Closed、整载荷清零。
    ///   0号位 第二十轮 §三 推翻该口径（原件 `:61`「该等式**不能用声明应排 Q 强行补齐未落定 Task 份额**，
    ///   也**不能反向抹去未排 U**」；`:61` `AllocationQty` 是**已构成正式 Allocation 的承接量**）
    ///   ⇒ 本用例**随裁决反向**：由「必须失败」改为「必须成功且三量可读回」。
    ///
    /// 鉴别力（两侧任一回归本用例即失败）：
    ///   · 若生成侧仍按**声明量**分摊（S=10）⇒ 第 3 项 `ΣShare ≤ Task.Quantity` 红；
    ///   · 若**未排量漏记**（U=0）⇒ 第 1 项三量等式 `ΣShare + ΣU == ΣQ` 红。
    /// </summary>
    [Fact]
    public async Task Solver出口_部分净量_合法落定_三量QS_U可读回()
    {
        var result = await _solver.SolveAsync(Build(
            ops: new[] { Op("RT", 1, "OP10", "STAGE1") },
            deps: Array.Empty<RoutingDependency>(),
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP10", ResourceId: 1) },
            calendarEnd: Day.AddMinutes(6),
            leadTimes: Array.Empty<(string Stage, decimal Hours)>(),
            chain: new[] { ("STAGE1", 1) },
            demandRoute: null,
            demandPathId: null,
            batchMax: 6m,
            allowSplit: true));

        // ① 不再 Fail Closed：部分落定是**合法**业务结果（`Success=true` + 未排量如实记录）。
        Assert.True(result.Success, result.ErrorMessage);

        // ── ② 已排量 S（2026-10-15 第八轮锁定的批数量口径，沿用）──
        //   批数量 = `BuildBatchFormation` 的**均分** `NetOutputQty / count`（`PhaseTwoInitialScheduler.cs:4206`），
        //   而 `MaxExecutionBatchQty = 6` 只决定**批数**（`DecideExecutionBatchPlan:3476` `nMin = ceil(10/6) = 2`）。
        //   实测批数 = **3**（非基线 nMin=2）⇒ 每批 `10 / 3 = 3.3333`；日历只容首批 ⇒ 只落定 1 个 Task。
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(3.3333m, task.Quantity);

        // ③ 份额严格闭合到**已落定量**，不再被声明量撑破（第 3 项 `ΣShare ≤ Task.Quantity` 取等）。
        var totalShare = result.AllocationShares.Sum(s => s.ComponentQty);
        Assert.Equal(3.3333m, totalShare);

        // ④ 未排量 U：`Q − S = 10 − 3.3333 = 6.6667`（= 0号位 受控例的第三个数）。
        //    `DraftId` 的值域是 **`LogicalDemandKey`**，不是 `FinalDraftId`（2号位 r13771 已写死注释）。
        var unscheduled = Assert.Single(result.UnscheduledTasks);
        Assert.Equal("D1", unscheduled.DraftId);
        Assert.Equal(6.6667m, unscheduled.UnscheduledQty);

        // ⑤ 三量守恒：`ΣShare + ΣU == ΣQ` —— 第 1 项新判据的端到端镜像。
        Assert.Equal(10m, totalShare + unscheduled.UnscheduledQty);
    }

    /// <summary>
    /// **校验侧鉴别性反证（N8-02，2026-10-17）**：把上一条**合法**出口的份额**原样**喂给硬校验，
    ///   只把 `UnscheduledTasks` **抽空**（模拟「未排量漏记」）⇒ 第 1 项三量等式必须**红**。
    ///
    /// 这条要证明的是：第 1 项**不是**「Σ份额 == Σ份额」式的恒真护栏 —— 份额一个数都不动，
    ///   只抽掉 U，它就翻脸。否则「部分落定合法化」等于把闭合闸**做空**。
    ///
    /// ⚠ 为什么必须由**外部**喂不一致的 U 才算证明：生成侧 `closureTarget = Q − U` 与校验侧用的是
    ///   **同一个 U 来源** ⇒ 端到端跑通**不足以**证明本项有鉴别力（那是设计使然，不是证明）。
    ///   生成侧「份额被放大」的真鉴别器是**第 3 项**（`ΣShare ≤ Task.Quantity`）。
    /// </summary>
    [Fact]
    public async Task Solver出口_部分净量_未排量漏记时_第1项三量等式必须红()
    {
        var request = Build(
            ops: new[] { Op("RT", 1, "OP10", "STAGE1") },
            deps: Array.Empty<RoutingDependency>(),
            eligibility: new[] { (Route: "RT", PathId: 1, Op: "OP10", ResourceId: 1) },
            calendarEnd: Day.AddMinutes(6),
            leadTimes: Array.Empty<(string Stage, decimal Hours)>(),
            chain: new[] { ("STAGE1", 1) },
            demandRoute: null,
            demandPathId: null,
            batchMax: 6m,
            allowSplit: true);

        var result = await _solver.SolveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        // 前置：合法出口下三量守恒 —— 否则「只抽掉 U」这个受控变量就不干净。
        Assert.Equal(10m, result.AllocationShares.Sum(s => s.ComponentQty)
            + result.UnscheduledTasks.Sum(u => u.UnscheduledQty));

        // ★ 受控变量：份额原样，U 抽空。
        var (isValid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            result.FinalTasks.ToList(),
            result.AllocationShares.ToList(),
            new List<FinalTaskPeggingDraft>(),
            request,
            new ConstraintContext(),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(),
            Array.Empty<UnscheduledTaskResult>());

        Assert.False(isValid, "未排量漏记（U=0）必须被第 1 项三量等式拦下，否则闭合闸被做空");
        Assert.Contains("数量闭合失败", error);
        Assert.Contains("ΣUnscheduledQty=0", error);
        Assert.Contains("期望NetOutputQty=10", error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    private static RoutingOperation Op(string route, int pathId, string op, string stage)
        => new()
        {
            MaterialId = M, ProductionDepartmentId = Dept, RouteCode = route, PathId = pathId,
            OperationCode = op, StageCode = stage, StandardDuration = 1m,
            OperationPlanningMode = "FINITE_RESOURCE"
        };

    private static RoutingDependency Dep(string route, int pathId, string from, string to)
        => new()
        {
            MaterialId = M, ProductionDepartmentId = Dept, RouteCode = route, PathId = pathId,
            FromOperationCode = from, ToOperationCode = to
        };

    /// <summary>
    /// 单需求（`D1` / 物料 `M` / 净量 10）夹具。**唯一变量**：路由工序集、资格行、日历窗、
    ///   Stage 链与提前期、需求是否固定 Route/Path、批策略 Min/Max/AllowSplit。
    /// </summary>
    private static DomainSolveRequest Build(
        IReadOnlyList<RoutingOperation> ops,
        IReadOnlyList<RoutingDependency> deps,
        IReadOnlyList<(string Route, int PathId, string Op, int ResourceId)> eligibility,
        DateTime? calendarEnd,
        IReadOnlyList<(string Stage, decimal Hours)> leadTimes,
        IReadOnlyList<(string Stage, int Seq)> chain,
        string? demandRoute,
        int? demandPathId,
        decimal? batchMax = null,
        bool allowSplit = false,
        string? startStageCode = "STAGE1",
        string? startOperationCode = null)
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "D1",
                    MaterialId = M, FactoryId = 1,
                    StartStageCode = startStageCode, StartOperationCode = startOperationCode,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = demandRoute, PathId = demandPathId
                }
            },
            RoutingOperations = ops.ToList(),
            RoutingDependencies = deps.ToList(),
            OperationResourceEligibility = eligibility
                .Select(e => new OperationResourceEligibility
                {
                    MaterialId = M, ProductionDepartmentId = Dept,
                    RouteCode = e.Route, PathId = e.PathId, OperationCode = e.Op,
                    ResourceId = e.ResourceId, Priority = 1, CapacityFactor = 1m
                })
                .ToList(),
            MaterialStageDepartmentContexts = chain
                .Select(s => new MaterialStageDepartmentContextDto
                {
                    MaterialId = M, StageCode = s.Stage, ProductionDepartmentId = Dept
                })
                .ToList(),
            StageLeadTimes = leadTimes
                .Select(l => new StageLeadTimeFact
                {
                    MaterialId = M, StageCode = l.Stage, LeadTimeHours = l.Hours, MatchLevel = "DEPT_EXACT"
                })
                .ToList(),
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = M,
                    Stages = chain
                        .Select(s => new StageSequenceStep { StageCode = s.Stage, StageSeq = s.Seq })
                        .ToList()
                }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = eligibility
                .Select(e => e.ResourceId).Distinct()
                .Select(rid => new ResourceDefinition
                {
                    ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m
                })
                .ToList(),
            CalendarSlots = calendarEnd is DateTime ce
                ? eligibility.Select(e => e.ResourceId).Distinct()
                    .Select(rid => new ResourceCalendarSlot
                    {
                        ResourceId = rid, Start = Day, End = ce, IsAvailable = true
                    })
                    .ToList()
                : new List<ResourceCalendarSlot>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD", AllowMerge = true, AllowSplit = allowSplit
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = M, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = batchMax,
                        AllowSplit = allowSplit, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
}
