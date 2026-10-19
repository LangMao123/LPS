using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
// `LPS.APS.Core.Entities.APS` 亦含 `Task` 类型 ⇒ 显式消歧，避免 CS0104。
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R2-F：`ResourceId == null` 的合法性必须在 Solver 的「生产入口」校验
///   （第五轮判词逐字：「仅在评分诊断处增加计数并不足以关闭错误路径」）。
///
/// 本文件即该入口校验（<see cref="PhaseOneConstraintBuilder.ValidateOperationPlanningModes"/>，
/// `BuildConstraints` 步骤 2.6）的**正反例**。纯内存、不触库、不读文件。
///
/// ── **第六轮整改（0号位 2026-10-14《致1号位第六轮代码审核正式复核意见 v1.0》§四 + §七.1）** ──
/// §四 R2-F1 判词逐字：「上述异常**不是**『只跳过当前非法候选Path』……**合法的A也没有机会被排程**」
///   「必须严格区分：**①输入值域损坏**；**②某一合法候选Path缺可用资格**；**③一个需求全部Path均无法
///    合法排下**；**④整个运行契约不可组装**」「**这里不要求放过非法Task，也不要求给非法资源兜底1.0；
///    要求停止无差别传播失败。**」
/// §七.1 逐字：「**R2-F先处理**：对当前全图校验的失败传播范围给出已冻结合同依据，补**两个Path一合法
///    一缺资格+另一个正常需求**的反例，修正资源ID与CapacityFactor交集校验；验证缺Calendar与候选不可行
///    不会误扩大为整Domain失败。」
///
/// ⇒ 本文件按**四分层**重排：
///   · **① 该候选 Path 整条排除**（**不抛**、Error 级输入契约 Issue）—— 「工序资源模式值域损坏」：
///     <see cref="R2F_02_工序资源模式越域_仅该候选Path整条排除_不得升级为整域失败"/> /
///     <see cref="R2F_02b_同需求全部候选Path均越域_须走既有未排语义_不得产Task且不得置技术失败"/>；
///     **⚠ 2026-10-14 第七轮 §八.2a 口径变更**（旧行为 = 抛 `SolverInputContractException` 整域 Fail Closed，
///     判词逐字：「『任意一个候选 Path 的一个非法枚举值都必然使整个 Domain 失败』**不是**本次证据能推出的结论」）
///     —— 变更理由与逐字依据见两个用例上方的专段；
///   · **② 仅该候选 Path 不成立**（**不抛**、**留痕**）—— <see cref="R2F_01_..." /> / <see cref="R2F_03_..." />
///     / <see cref="R2F_04_..." /> / <see cref="R2F_08_..." /> / <see cref="R2F_09a_..." /> / <see cref="R2F_09b_..." />
///     / <see cref="R2F_09c_..." />（谓词级）/ <see cref="R2F_10_..." />；
///   · **③ 全 Path 均不成立** ⇒ 既有未排语义（无 FinalTask、净量/份额/时间事实闭合）——
///     <see cref="R2F_11_..." />。
///
/// ⚠ 校验**只**判定「模式与资格是否可判定」，**不**改变「无合格资源 ⇒ 该需求不排程」的既有出口语义；
///   也**不**把「非资源工序（UNCONSTRAINED/WAIT_ONLY）」判为非法 —— 后者**合法地**产出 `ResourceId == null`。
/// </summary>
public class OperationPlanningModeContractTests
{
    private static readonly DateTime Day = new DateTime(2026, 9, 1, 0, 0, 0);
    private const int MaterialId = 1;
    private const int DeptId = 100;

    private const string GapCode = PhaseOneConstraintBuilder.TraceResourceCandidateAbsent;

    /// <summary>① 类留痕码：候选 Path 因 `OperationPlanningMode` 越域被**整条排除**（R2-F ①，第七轮 §八.2a）。</summary>
    private const string OutOfDomainCode = PhaseOneConstraintBuilder.TraceOperationPlanningModeOutOfDomain;

    private readonly FiniteCapacitySolver _solver = new();

    // ════════════════════════════════════════════════════════════════════
    // ② 类：某一合法候选 Path 缺可用资格 —— **仅该候选不成立**，不升级整域失败
    // ════════════════════════════════════════════════════════════════════

    // ── ②-a：`FINITE_RESOURCE` 工序**没有任何**资源资格行 ⇒ **不抛**；该 Path 不成立 + 留痕 ──
    [Fact]
    public async Task R2F_01_资源模式工序无资源资格_仅该候选不成立_不得升级整域失败()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "FINITE_RESOURCE",
            eligibility: Array.Empty<(int ResourceId, int Priority, decimal CapacityFactor)>()));

        // ① 整域**没有**失败：合法兄弟 Path / 无关需求仍须有机会被排程（§四「合法的A也没有机会被排程」）。
        Assert.True(result.Success, result.ErrorMessage);

        // ② 该需求全部候选 Path 均不成立 ⇒ 既有未排语义：**不伪造批次**。
        Assert.Empty(result.FinalTasks);

        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
        Assert.Equal("A", note.Key);
        Assert.Equal("Warning", note.Level);
        Assert.Contains("无任何资源资格行", note.Message);
        Assert.Contains("不**升级为整域输入失败", note.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // ① 类（**2026-10-14 第七轮 §八.2a 口径变更**）：`OperationPlanningMode` **越域**
    //   ⇒ 该候选 Path **整条排除**（不得产 Task）+ 登记 **Error 级**输入契约 Issue；
    //   **不**传播为整域失败 —— 同需求的合法兄弟 Path 与无关需求仍须被求解。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 0号位 2026-10-14 第七轮正式复核 §八.2a 判词逐字：
    ///   「『任意一个候选 Path 的一个非法枚举值都必然使整个 Domain 失败』**不是**本次证据能推出的结论」
    ///   「若能**可靠识别该 Path/Demand**，**禁止此非法 Path 产生 Task、登记输入契约 Issue**，
    ///     其他完整合法 Path 及无关 Demand 仍应被求解；若整个请求所必需的**全局类型/结构**确实无法解释
    ///     和安全组装，再走整域 Fail Closed」。
    ///
    /// **口径变更**：旧行为（`R2F_02` 整改前）为**逐节点**判定越域即 `throw SolverInputContractException`
    ///   ⇒ 整域 `Success=false`、合法兄弟 Path 与无关需求一并被拒。变更依据（读码实测）：模式是**逐节点**事实，
    ///   而 `OperationNode` 自带 `RouteCode/PathId/StageCode/OperationCode` ⇒ 损害面**可可靠定位**到单条
    ///   候选 Path，不构成「全局类型/结构不可解释」。
    ///
    /// 夹具（**双向反证**）：`D1`（物料 1，未固定路径 ⇒ 两条业务候选）
    ///   · `RTA/1` = **模式越域**（`SOMETHING_ELSE`），Routing 完整、资格与系数**齐全**
    ///     ⇒ 排除的依据只能是**模式**，不是缺资格/缺日历；
    ///   · `RTB/1` = 完整合法（同工序、同资格）；
    ///   另加与 `D1` 无关且完整合法的 `D2`（物料 2）。
    ///
    /// 断言：整域仍成功；`RTA` **零 Task**；`D1` 经 `RTB` 排下；`D2` 不受牵连；
    ///   留痕**一条** `OPERATION_PLANNING_MODE_OUT_OF_DOMAIN_PATH_EXCLUDED`（`Level = "Error"`、只键 `D1`）；
    ///   且**不**与 ② 类的「无合法资源候选」混淆。
    /// </summary>
    [Fact]
    public async Task R2F_02_工序资源模式越域_仅该候选Path整条排除_不得升级为整域失败()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RTA", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 1m) }, Day, Day.AddDays(5),
                    PlanningMode: "SOMETHING_ELSE"),
                new PathPlan(1, "RTB", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 1m) }, Day, Day.AddDays(5)),
                new PathPlan(2, "RT2", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 2, CapacityFactor: 1m) }, Day, Day.AddDays(5))
            },
            demands: new[]
            {
                new DemandPlan("D1", 1, 10m, RouteCode: null, PathId: null, Sequence: 1),
                new DemandPlan("D2", 2, 10m, RouteCode: null, PathId: null, Sequence: 2)
            }));

        // ① **整域不得失败**（旧行为在整改前此处必红：`Assert.False(result.Success)` 曾为真）。
        Assert.True(result.Success, result.ErrorMessage);

        // ② 非法 Path **零 Task**；同需求经**合法兄弟 Path** 排下（不是「整条需求被拒」）。
        Assert.DoesNotContain(result.FinalTasks, t => t.RouteCode == "RTA");
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 1 && t.RouteCode == "RTB");

        // ③ **不牵连**无关需求。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 2 && t.ResourceId == 2);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D2");

        // ④ **不静默**：Error 级输入契约 Issue，逐需求键、含非法值与该 Path 身份。
        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == OutOfDomainCode);
        Assert.Equal("D1", note.Key);
        Assert.Equal("Error", note.Level);
        Assert.Contains("SOMETHING_ELSE", note.Message);
        Assert.Contains("RTA", note.Message);
        Assert.Contains("整条排除", note.Message);

        // ⑤ **两类留痕不混**：模式越域（输入契约违背）**不**得被降级登记成「无合法资源候选」（业务不可行）。
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
    }

    /// <summary>
    /// ① 类的**边界**：该需求**全部**候选 Path 均越域 ⇒ 走既有未排语义（③ 类）：
    ///   不产 Task、`UnscheduledTasks` 收下该需求、`Success` **仍为 true**（**不**置 `TechnicalFailure`）。
    ///
    /// 与 <see cref="R2F_02_工序资源模式越域_仅该候选Path整条排除_不得升级为整域失败"/> 成对 ——
    ///   证明「排除」不会退化成「整域失败」，也不会退化成「技术失败」两种变相的全域传播。
    ///   逐 Path 各一条 Error 留痕（2 条，均键 `D1`），无关需求 `D2` 照常排下。
    /// </summary>
    [Fact]
    public async Task R2F_02b_同需求全部候选Path均越域_须走既有未排语义_不得产Task且不得置技术失败()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RTA", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 1m) }, Day, Day.AddDays(5),
                    PlanningMode: "SOMETHING_ELSE"),
                new PathPlan(1, "RTB", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 1m) }, Day, Day.AddDays(5),
                    PlanningMode: "ALSO_BAD"),
                new PathPlan(2, "RT2", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 2, CapacityFactor: 1m) }, Day, Day.AddDays(5))
            },
            demands: new[]
            {
                new DemandPlan("D1", 1, 10m, RouteCode: null, PathId: null, Sequence: 1),
                new DemandPlan("D2", 2, 10m, RouteCode: null, PathId: null, Sequence: 2)
            }));

        // 未排 = **业务结果**，不是输入失败 ⇒ `Success` 仍为 true（对应「不置 TechnicalFailure」）。
        Assert.True(result.Success, result.ErrorMessage);

        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == 1);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

        var notes = result.SolveTraceNotes.Where(n => n.ReasonCode == OutOfDomainCode).ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n =>
        {
            Assert.Equal("D1", n.Key);
            Assert.Equal("Error", n.Level);
        });
        Assert.Contains(notes, n => n.Message!.Contains("RTA"));
        Assert.Contains(notes, n => n.Message!.Contains("RTB"));

        // **不误扩大**：无关需求 D2 照常排下。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 2 && t.ResourceId == 2);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D2");
    }

    // ── ②-b：资格行存在但 `CapacityFactor == 0` ⇒ 该 Path 无合法资源候选（**不**静默回落 1.0）──
    [Fact]
    public async Task R2F_03_资源模式工序产能系数为零_仅该候选不成立_不得升级整域失败()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "FINITE_RESOURCE",
            eligibility: new[] { (1, 1, 0m) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
        Assert.Equal("A", note.Key);
        Assert.Contains("无同 ID 正产能系数", note.Message);
    }

    // ── ②-c：`CapacityFactor` 为负同样非法（边界：`> 0` 是唯一合法域）──
    [Fact]
    public async Task R2F_04_产能系数为负_仅该候选不成立_不得升级整域失败()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "FINITE_RESOURCE",
            eligibility: new[] { (1, 1, -0.5m) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == GapCode && n.Message!.Contains("无同 ID 正产能系数"));
    }

    // ── 正例①（**零回归**）：合法 `FINITE_RESOURCE`（有资格行、`CapacityFactor > 0`）⇒ 正常排程，Task 带资源身份 ──
    [Fact]
    public async Task R2F_05_合法资源模式工序_不得被本校验拒绝()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "FINITE_RESOURCE",
            eligibility: new[] { (1, 1, 1m) }));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(1, task.ResourceId);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
    }

    // ── 正例②：**非资源工序**（`UNCONSTRAINED`）**无**资源资格 ⇒ **合法**，不得被本校验拒绝；
    //   其 Task 的 `ResourceId == null` 是**合法非生产**，不是契约违背。 ──
    [Fact]
    public async Task R2F_06_非资源工序无资格_属合法_不得被本校验拒绝()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "UNCONSTRAINED",
            eligibility: Array.Empty<(int ResourceId, int Priority, decimal CapacityFactor)>()));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Null(task.ResourceId);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
    }

    // ── 正例③：`WAIT_ONLY` 同为非资源模式，口径与 `UNCONSTRAINED` 一致。 ──
    [Fact]
    public async Task R2F_07_WAIT_ONLY同为非资源模式_不得被本校验拒绝()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "WAIT_ONLY",
            eligibility: Array.Empty<(int ResourceId, int Priority, decimal CapacityFactor)>()));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(Assert.Single(result.FinalTasks).ResourceId);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
    }

    // ════════════════════════════════════════════════════════════════════
    // §七.1 反例 A：**两个 Path 一合法一缺资格 + 另一个正常需求**
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §七.1 逐字：「补**两个Path一合法一缺资格+另一个正常需求**的反例……检查Solver仍能按已冻结Routing
    /// 合法性选择A安排D1，并不影响D2」。
    ///
    /// 夹具：`D1`（物料 1，**未固定路径** ⇒ 两条业务候选 `RTA/1` 与 `RTB/1`）
    ///   · `RTA/1` = **完整合法**（资格 `R1`，`CapacityFactor = 1`，日历开）；
    ///   · `RTB/1` = 该唯一工序**无任何资格行**（缺资格）；
    /// 另有与 `D1` 无关且完整合法的 `D2`（物料 2，资格 `R2`）。
    ///
    /// 断言：**入口不抛** ⇒ `D1` 仍经合法 Path `RTA/1` 排下（`R1`）、`D2` 照常排下（`R2`）；
    ///   缺口只留**一条**、且只键 `D1`（**不**牵连 `D2`）。
    /// </summary>
    [Fact]
    public async Task R2F_08_两个Path一合法一缺资格_合法Path仍须可排_且不牵连无关需求()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RTA", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 1m) }, Day, Day.AddDays(5)),
                // 缺资格侧：**照旧出现在 RoutingOperations 里**（它是业务候选 Path），只是**没有任何资格行**。
                new PathPlan(1, "RTB", 1, new[] { ("OP10", "STAGE1") },
                    Array.Empty<(int ResourceId, decimal CapacityFactor)>(), Day, Day.AddDays(5)),
                new PathPlan(2, "RT2", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 2, CapacityFactor: 1m) }, Day, Day.AddDays(5))
            },
            demands: new[]
            {
                new DemandPlan("D1", 1, 10m, RouteCode: null, PathId: null, Sequence: 1),
                new DemandPlan("D2", 2, 10m, RouteCode: null, PathId: null, Sequence: 2)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // D1 经**合法**候选 Path 排下，且**没有**被逼到缺资格的那条。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 1 && t.ResourceId == 1);
        Assert.All(result.FinalTasks.Where(t => t.MaterialId == 1), t => Assert.Equal("RTA", t.RouteCode));

        // 无关需求 D2 不受牵连。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 2 && t.ResourceId == 2);

        // 缺口只留一条，且**只**键 D1。
        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
        Assert.Equal("D1", note.Key);
        Assert.Contains("PathId=1", note.Message);
        Assert.Contains("RTB", note.Message);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode && n.Key == "D2");
    }

    // ════════════════════════════════════════════════════════════════════
    // §四 R2-F2：资格资源与产能系数必须**同 ID** 对上（交集，不是「两边各自非空」）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R2-F2 判词逐字：「**不得因为一个非法替代资源就把其它合法资源一起封杀。**」
    /// 资格集合含**同一工序的两行**：`R1`（`CapacityFactor = 0`，非法替代）与 `R2`（`= 1`，合法）。
    /// 断言：**不**登记缺口、需求照常排到 `R2`。
    /// </summary>
    [Fact]
    public async Task R2F_09a_资格集合含非法替代资源_合法替代资源不得被一并封杀()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RT", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 0m), (ResourceId: 2, CapacityFactor: 1m) },
                    Day, Day.AddDays(5))
            },
            demands: new[] { new DemandPlan("D1", 1, 10m, RouteCode: "RT", PathId: 1, Sequence: 1) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
        Assert.Equal(2, Assert.Single(result.FinalTasks).ResourceId);
    }

    /// <summary>
    /// 反身：**全部**资格行的 `CapacityFactor ≤ 0` ⇒ 该 Path 确实无合法资源候选 ⇒ 登记缺口。
    /// （与 <see cref="R2F_09a_资格集合含非法替代资源_合法替代资源不得被一并封杀"/> 成对 —— 证明判据是
    ///   **逐 ID 交集**，**不是**「字典里随便有个正值就算合法」。）
    /// </summary>
    [Fact]
    public async Task R2F_09b_资格集合全部无正系数_须登记候选缺口()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RT", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 0m), (ResourceId: 2, CapacityFactor: 0m) },
                    Day, Day.AddDays(5))
            },
            demands: new[] { new DemandPlan("D1", 1, 10m, RouteCode: "RT", PathId: 1, Sequence: 1) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == GapCode);
        Assert.Contains("无同 ID 正产能系数", note.Message);
    }

    /// <summary>
    /// **谓词级差异反例**（R2-F2 判词逐字要求的三组差异之一：「增加资格 `R1`／系数只有 `R2`」）。
    ///
    /// ⚠ **为什么必须直调谓词**：`BuildOperationResourceEligibility`（`PhaseOneConstraintBuilder.cs:925-949`）
    ///   用**同一个 `eligibilities` 列表、同一个 `GroupBy` 键**同时产出 `OperationResourceEligibility` 与
    ///   `ResourceCapacityFactors` ⇒ 两张映射的**键集恒等**，「资格 `{R1}` / 系数只有 `{R2}`」
    ///   经 `DomainSolveRequest` **结构上不可达**（唯一残留可能是同 ID 重复行的 `ToDictionary` 抛异常，
    ///   那本身也是 Fail Closed）。⇒ 只能**直接构造 <see cref="ConstraintContext"/>** 喂入该不可达输入。
    ///
    /// 本用例是 R2-F2 整改的**鉴别性反证**：
    ///   · 旧判据 `resourceIds.Count > 0 && factors.Values.Any(cf => cf > 0m)` 对本输入返回 **true**
    ///     ⇒ **静默放过**（资格 `R1` 与系数 `R2` 根本不是同一个资源，`R1` 实际无系数可用）；
    ///   · 新判据（逐 ID 交集）返回 **false** ⇒ 登记缺口。
    /// </summary>
    [Fact]
    public void R2F_09c_谓词级_资格R1而系数只有R2_须登记缺口_旧式ValuesAny会放过()
    {
        var (request, context) = SingleNodeContext();
        var key = new EligibilityLookupKey(MaterialId, DeptId, "RT", 1, "OP10");

        // ── 差异输入：资格 `{R1}`，系数字典只有 `{R2: 1.5}` ──
        context.OperationResourceEligibility[key] = new List<int> { 1 };
        context.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [2] = 1.5m };

        // 旧判据在此输入上的取值（**整改前红**的可读证据，非生产代码）：
        Assert.True(context.ResourceCapacityFactors[key].Values.Any(cf => cf > 0m),
            "旧判据 `Values.Any(cf > 0m)` 对本输入为 true ⇒ 整改前会静默放过该 Path。");

        PhaseOneConstraintBuilder.ValidateOperationPlanningModes(request, context);

        var note = Assert.Single(context.TraceNotes);
        Assert.Equal(GapCode, note.ReasonCode);
        Assert.Equal("D1", note.Key);
        Assert.Contains("无同 ID 正产能系数", note.Message);

        // ── 配对反证：把系数改成**与资格同 ID** ⇒ 缺口消失（证明判据看的是 ID 对不对得上）──
        var (request2, context2) = SingleNodeContext();
        context2.OperationResourceEligibility[key] = new List<int> { 1 };
        context2.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [1] = 1.5m };

        PhaseOneConstraintBuilder.ValidateOperationPlanningModes(request2, context2);

        Assert.Empty(context2.TraceNotes);
    }

    /// <summary>
    /// R2-F2 判词逐字：「**不得因为一个非法替代资源就把其它合法资源一起封杀。**」
    ///
    /// ⚠ 本用例是该判词的 **Solver 级端到端鉴别性反证**，针对
    ///   <see cref="R2F_09a_资格集合含非法替代资源_合法替代资源不得被一并封杀"/> **遮住**的一条真实缺陷：
    ///   `PhaseTwoInitialScheduler` 在**资源循环内**遇到 `CapacityFactor` 缺失/≤0 时走的是
    ///   **`return new List&lt;FinalTaskDraft&gt;()`**（倒排 `:2302` / 正排 `:2521`），**不是** `continue`
    ///   —— 一个非法替代资源会把**该工序其余全部合法资源**一起弃掉，整条需求在 Phase2 直接不排。
    ///   `Phase4` 同判据用的是 **`continue`**（`:1116` / `:1572`）⇒ 这笔账会被 Phase4 局部修复**补排**回来
    ///   ⇒ 只看最终结果的 `R2F_09a` **看不出**该缺陷（**「结果一致性」≠ 鉴别性反证**）。
    ///
    /// 夹具（R2 **合法**、窗口只容得下一件；R1 为同一工序的**非法替代**）：
    ///   · `D1`（先到）资格 = `{R1(CF=0), R2(CF=1)}`；
    ///   · `D2`（后到）资格 = `{R2(CF=1)}`；
    ///   · R2 日历 = 12 分钟，单件工时 = `1 × 10 ÷ 1 = 10` 分钟 ⇒ **只装得下一件**。
    ///
    /// 整改前（`return`）：Phase2 在 `R1` 处直接放弃 `D1` ⇒ `R2` 被后到的 `D2` 占走 ⇒ **`D1` 未排**。
    /// 整改后（`continue`）：Phase2 跳过 `R1`、把 `D1` 排在 `R2` ⇒ **`D1` 落 Task**，`D2` 未排。
    /// ⇒ 「`D1` 必须拿到 `R2`」这条断言**在整改前必红**，本用例即该缺陷的回归锁。
    /// </summary>
    [Fact]
    public async Task R2F_09d_非法替代资源不得封杀合法资源_Phase2即须避免_退回Phase4补排不算()
    {
        var windowEnd = Day.AddMinutes(12);

        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RT", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 1, CapacityFactor: 0m), (ResourceId: 2, CapacityFactor: 1m) },
                    Day, windowEnd),
                new PathPlan(2, "RT", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 2, CapacityFactor: 1m) },
                    Day, windowEnd)
            },
            demands: new[]
            {
                new DemandPlan("D1", 1, 10m, RouteCode: "RT", PathId: 1, Sequence: 1),
                new DemandPlan("D2", 2, 10m, RouteCode: "RT", PathId: 1, Sequence: 2)
            }));

        Assert.True(result.Success, result.ErrorMessage);

        // 先到的 D1 必须拿到**合法替代** R2 —— 不得被同工序的**非法替代** R1 封杀。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 1 && t.ResourceId == 2);

        // 夹具自证：R2 窗口确实只容得下一件（否则上面的断言不具鉴别性）。
        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == 2);
    }

    // ════════════════════════════════════════════════════════════════════
    // §七.1 反例 C：缺 Calendar / 候选不可行 —— 保持未排，**不得**误扩大为整域失败
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §七.1 逐字：「验证缺Calendar与候选不可行不会误扩大为整Domain失败」＋ §四 R2-F3 逐字：
    /// 「无法证明**候选有资格但所有日历窗实际关闭**时保持未排而不是补8h/7×24」。
    ///
    /// 夹具：资格与系数**完全合法**，但资源日历：(a) **完全缺失**（无 `ResourceCalendarSlot` 行）；
    ///   (b) **存在但窗口窄于所需工时**（5 分钟 &lt; 10 分钟）。二者都**不抛**，需求保持未排。
    ///
    /// 「**不补 8h/7×24**」的可证形式：整条需求产出的 `FinalTask` 数 **= 0** ——
    ///   若实现偷偷按 8h/7×24 虚拟产能补窗，本处就必然出现 Task；`Assert.Empty` 是它的**否证**。
    ///   （补窗属 2号位 装载侧口径，1号位 侧只负责「排不下就不排」。）
    /// </summary>
    [Fact]
    public async Task R2F_10_候选有资格但日历缺失或窗口不足_须保持未排_不得误扩大为整域失败()
    {
        foreach (var label in new[] { "日历完全缺失", "窗口窄于所需工时" })
        {
            var noCalendar = label == "日历完全缺失";
            var result = await _solver.SolveAsync(BuildMulti(
                paths: new[]
                {
                    new PathPlan(1, "RT", 1, new[] { ("OP10", "STAGE1") },
                        new[] { (ResourceId: 1, CapacityFactor: 1m) },
                        // 需求 10 件 × 标准工时 1 分钟 = 10 分钟；窗口 5 分钟 ⇒ 放不下。
                        CalStart: noCalendar ? null : Day,
                        CalEnd: noCalendar ? null : Day.AddMinutes(5))
                },
                demands: new[] { new DemandPlan("D1", 1, 10m, RouteCode: "RT", PathId: 1, Sequence: 1) }));

            // 入口**不抛** ⇒ 整域成功（②/③ 类，不是 ①/④）。
            Assert.True(result.Success, $"[{label}] {result.ErrorMessage}");

            // **不是**资格问题 ⇒ **无**候选缺口留痕。
            Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == GapCode);

            // 保持未排：**不伪造批次**（也就没有被 8h/7×24 补齐出来的 Task）。
            Assert.Empty(result.FinalTasks);
            Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

            // 净量/时间事实闭合：无末端真实 Task ⇒ 不生成份额、完成时间退化为 PlanningStart。
            var completion = Assert.Single(result.DemandCompletions);
            Assert.Null(completion.TerminalFinalDraftId);
            Assert.Equal(Day, completion.CompletionTime);
            Assert.Empty(result.AllocationShares);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // ③ 类：一个需求**全部** Path 均无法合法排下
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §四 R2-F1 逐字：「另测全部Path都不具备合法资格时**不可产生FinalTask**、未满足净量与Issue保持闭合。」
    ///
    /// 夹具：`D1` 两条业务候选 Path `RTA/1`、`RTB/1`，**两条都缺资格**；另加与 `D1` 无关且合法的 `D2`。
    /// 断言：整域仍成功、**无** FinalTask、**无**份额、`TerminalFinalDraftId == null`（净量闭合）；
    ///   缺口**逐 Path 各留一条**（共 2 条，均键 `D1`）；`D2` 照常排下（**不误扩大**）。
    /// </summary>
    [Fact]
    public async Task R2F_11_全部Path均缺资格_须无FinalTask且净量闭合_仍不牵连无关需求()
    {
        var result = await _solver.SolveAsync(BuildMulti(
            paths: new[]
            {
                new PathPlan(1, "RTA", 1, new[] { ("OP10", "STAGE1") },
                    Array.Empty<(int ResourceId, decimal CapacityFactor)>()),
                new PathPlan(1, "RTB", 1, new[] { ("OP10", "STAGE1") },
                    Array.Empty<(int ResourceId, decimal CapacityFactor)>()),
                new PathPlan(2, "RT2", 1, new[] { ("OP10", "STAGE1") },
                    new[] { (ResourceId: 2, CapacityFactor: 1m) }, Day, Day.AddDays(5))
            },
            demands: new[]
            {
                new DemandPlan("D1", 1, 10m, RouteCode: null, PathId: null, Sequence: 1),
                new DemandPlan("D2", 2, 10m, RouteCode: null, PathId: null, Sequence: 2)
            }));

        Assert.True(result.Success, result.ErrorMessage);

        // ③：**不产生 FinalTask**（D1 侧）。
        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == 1);

        // 净量闭合：无末端真实 Task ⇒ 无份额、完成时间退化、净量维持未满足（不进 Supply 可用）。
        var d1 = Assert.Single(result.DemandCompletions, c => c.LogicalDemandKey == "D1");
        Assert.Null(d1.TerminalFinalDraftId);
        // 既有闭合不变式：每条份额都必须指向一条**真实** Task（D1 无 Task ⇒ 不得有孤儿份额）。
        Assert.All(result.AllocationShares,
            s => Assert.Contains(result.FinalTasks, t => t.FinalDraftId == s.FinalDraftId));
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

        // 缺口**逐 Path**各一条，共 2 条（RTA/1 与 RTB/1），且**只**键 D1。
        var gapNotes = result.SolveTraceNotes.Where(n => n.ReasonCode == GapCode).ToList();
        Assert.Equal(2, gapNotes.Count);
        Assert.All(gapNotes, n => Assert.Equal("D1", n.Key));

        // **不误扩大**：无关需求 D2 照常排下。
        Assert.Contains(result.FinalTasks, t => t.MaterialId == 2 && t.ResourceId == 2);
    }

    // ════════════════════════════════════════════════════════════════════
    // 副作用锁定：**Demand 级时间事实必须真的到达端到端出口**
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本次整改**实测发现**的真缺陷锁定（与 R2-F 无因果关系，因同批直调 `SolveAsync` 而暴露）：
    ///   `FiniteCapacitySolver.SolveAsync`（`:145-166`）**逐字段重组装** `DomainSolveResult` 时
    ///   漏拷 `DemandCompletions` ⇒ `PhaseFiveCompression.cs:210` 已构建的时间事实被**静默丢弃**，
    ///   端到端出口恒为空（全仓 `.cs` 此前**零引用** ⇒ 无任何用例可发现）。
    ///   ⇒ 本用例是它的**回归锁**：已排需求必须真的带回 `DemandCompletionFact` 且能对上末端真实 Task。
    /// </summary>
    [Fact]
    public async Task R2F_12_已排需求_Demand级时间事实须到达端到端出口_不得在重组装处静默丢弃()
    {
        var result = await _solver.SolveAsync(Request(
            planningMode: "FINITE_RESOURCE",
            eligibility: new[] { (1, 1, 1m) }));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);

        var completion = Assert.Single(result.DemandCompletions);
        Assert.Equal("A", completion.LogicalDemandKey);
        Assert.Equal(task.FinalDraftId, completion.TerminalFinalDraftId);
        Assert.Equal(task.PlannedEndTime, completion.CompletionTime);
        Assert.Equal(completion.CompletionTime, completion.AvailableTime);
    }

    // ════════════════════════════════════════════════════════════════════
    // 最小夹具构造
    //   · Request      —— 单工序、单需求；**唯一变量 = 工序资源模式 + 资格行**（R2F_01～07）
    //   · BuildMulti   —— 多 Path / 多需求（§七.1 反例）
    //   · SingleNodeContext —— 谓词级直调（R2F_09c）
    // ════════════════════════════════════════════════════════════════════
    private static DomainSolveRequest Request(
        string planningMode,
        IReadOnlyList<(int ResourceId, int Priority, decimal CapacityFactor)> eligibility)
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "A", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "A", MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    DemandSequence = 1,
                    RouteCode = "RT", PathId = 1
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 1m, OperationPlanningMode = planningMode
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = eligibility
                .Select(e => new OperationResourceEligibility
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", ResourceId = e.ResourceId,
                    Priority = e.Priority, CapacityFactor = e.CapacityFactor
                })
                .ToList(),
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = eligibility
                .Select(e => e.ResourceId)
                .Distinct()
                .Select(rid => new ResourceDefinition
                {
                    ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m
                })
                .ToList(),
            CalendarSlots = eligibility
                .Select(e => e.ResourceId)
                .Distinct()
                .Select(rid => new ResourceCalendarSlot
                {
                    ResourceId = rid, Start = Day, End = Day.AddDays(5), IsAvailable = true
                })
                .ToList(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = true,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId, allowMerge: true, allowSplit: false),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };

    /// <summary>一条候选路径：路由身份 + 工序链 + **该路径的资格行**（空数组 = 整条 Path 缺资格）
    ///   + 该路径资源的日历窗（`CalStart == null` ⇒ **不造日历行**）
    ///   + **工序资源模式**（默认合法；越域值用于 ① 类反例）。</summary>
    private sealed record PathPlan(
        int MaterialId,
        string RouteCode,
        int PathId,
        (string Op, string Stage)[] Ops,
        (int ResourceId, decimal CapacityFactor)[] Eligibility,
        DateTime? CalStart = null,
        DateTime? CalEnd = null,
        string PlanningMode = "FINITE_RESOURCE");

    /// <summary>一个逻辑需求。`RouteCode`/`PathId` 为 null ⇒ **未固定路径**（由 C 桶逐合法完整 Path 择优）。</summary>
    private sealed record DemandPlan(
        string Key, int MaterialId, decimal Quantity, string? RouteCode, int? PathId, int Sequence);

    private static DomainSolveRequest BuildMulti(
        IReadOnlyList<PathPlan> paths,
        IReadOnlyList<DemandPlan> demands)
    {
        var ops = new List<RoutingOperation>();
        var deps = new List<RoutingDependency>();
        var elig = new List<OperationResourceEligibility>();
        var resources = new List<ResourceDefinition>();
        var calendars = new List<ResourceCalendarSlot>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var p in paths)
        {
            foreach (var (op, stage) in p.Ops)
            {
                ops.Add(new RoutingOperation
                {
                    MaterialId = p.MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    OperationCode = op, StageCode = stage,
                    StandardDuration = 1m, OperationPlanningMode = p.PlanningMode
                });

                foreach (var (rid, cf) in p.Eligibility)
                {
                    elig.Add(new OperationResourceEligibility
                    {
                        MaterialId = p.MaterialId, ProductionDepartmentId = DeptId,
                        RouteCode = p.RouteCode, PathId = p.PathId,
                        OperationCode = op, ResourceId = rid, Priority = 1, CapacityFactor = cf
                    });

                    if (resources.All(r => r.ResourceId != rid))
                    {
                        resources.Add(new ResourceDefinition
                        {
                            ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m
                        });
                    }

                    if (p.CalStart is DateTime cs && p.CalEnd is DateTime ce
                        && calendars.All(c => c.ResourceId != rid))
                    {
                        calendars.Add(new ResourceCalendarSlot
                        {
                            ResourceId = rid, Start = cs, End = ce, IsAvailable = true
                        });
                    }
                }

                if (stageDepts.All(s => s.MaterialId != p.MaterialId || s.StageCode != stage))
                {
                    stageDepts.Add(new MaterialStageDepartmentContextDto
                    {
                        MaterialId = p.MaterialId, StageCode = stage, ProductionDepartmentId = DeptId
                    });
                }
            }

            for (var i = 1; i < p.Ops.Length; i++)
            {
                deps.Add(new RoutingDependency
                {
                    MaterialId = p.MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    FromOperationCode = p.Ops[i - 1].Op, ToOperationCode = p.Ops[i].Op
                });
            }
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = demands
                .Select(d => new LogicalProductionDemand
                {
                    LogicalDemandKey = d.Key, PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = d.Sequence, DemandKey = d.Key,
                    MaterialId = d.MaterialId, FactoryId = 1,
                    StartStageCode = paths.First(p => p.MaterialId == d.MaterialId).Ops[0].Stage,
                    NetOutputQty = d.Quantity, PlannedProcessQty = d.Quantity,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = d.Sequence,
                    RouteCode = d.RouteCode, PathId = d.PathId
                })
                .ToList(),
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = resources,
            CalendarSlots = calendars,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = true,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(
                    demands.Select(d => d.MaterialId), DeptId, allowMerge: true, allowSplit: false),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    /// <summary>
    /// R2F_09c 专用：**手工装配**一个只含单节点单需求的 <see cref="ConstraintContext"/>
    ///   （`BuildConstraints` 的正常装配路径**造不出**「资格与系数不同键集」——见该用例说明）。
    /// </summary>
    private static (DomainSolveRequest Request, ConstraintContext Context) SingleNodeContext()
    {
        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1",
                    MaterialId = MaterialId, FactoryId = 1, StartStageCode = "STAGE1",
                    NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = 1,
                    RouteCode = "RT", PathId = 1
                }
            }
        };

        var nodeKey = new OperationNodeKey("STAGE1", "OP10");
        var context = new ConstraintContext
        {
            RoutingGraphs = new Dictionary<int, Dictionary<RoutePathKey, RoutingGraph>>
            {
                [MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
                {
                    [new RoutePathKey("RT", 1)] = new RoutingGraph
                    {
                        Operations = new Dictionary<OperationNodeKey, OperationNode>
                        {
                            [nodeKey] = new OperationNode
                            {
                                OperationCode = "OP10", StageCode = "STAGE1",
                                RouteCode = "RT", PathId = 1,
                                ProductionDepartmentId = DeptId,
                                StandardDuration = 1m,
                                OperationPlanningMode = "FINITE_RESOURCE"
                            }
                        },
                        RootOperations = new List<OperationNodeKey> { nodeKey }
                    }
                }
            }
        };

        return (request, context);
    }
}
