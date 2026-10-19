using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using FrozenLayer = LPS.APS.Scheduling.Solvers.PhaseTwoInitialScheduler.FrozenLayer;
using ResourceEfficiencyVerdict = LPS.APS.Scheduling.Solvers.ResourceEfficiencyEvaluator.ResourceEfficiencyVerdict;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// OWN-P0-02 专项反证（0号位 2026-10-10《APS_V1_1_20261010.md》§三）：
/// **多 Operation Stage 执行批的真实合批能力**。
///
/// 复审判词（本号位核对：**成立**）：
///   · `FindMergeableTasks` 首行 `if (operations.Count != 1) return candidates;` ⇒ 正常 Stage 多 Operation
///     工艺**无法参与完整合批**；
///   · `#L3308-L3314` 先形成带 `ExecutionBatchDraftKey` 的 Task，再由 `requireIdentityPreserving` 拒绝已归批
///     Task ⇒ 当前常规多批 C 需求**缺真实合批路径**；
///   · `#L1115-L1130` 源码自认多批合并「当前实际不发生」。
///
/// 冻结模型（同文 §二.4）：C 桶可在合法条件下合批；「同一 Stage 执行批承载 N 个 Operation Task，
///   多个需求份额各自可追溯」；「若 30 件+20 件合批方案胜出，形成**一个 50 件 Stage 执行批**及该 Path 上的
///   N 条 Operation 级 FinalTask…需求 A 的 30 件与 B 的 20 件由**份额账本**追溯」；「**不能把 50 件经过
///   3 道工序错误加成 150 件**」。
///
/// 本文件六组反证（全部走**完整** <see cref="FiniteCapacitySolver.SolveAsync"/>，纯内存，不触库）：
///   · <see cref="M_01_合批应胜_三工序两需求_须形成一个50件Stage执行批与完整Operation链"/>
///   · <see cref="M_02_不合批应胜_合批更晚且不多省Setup_须取独立排程两批各合法Path"/>  ← **反向用例**
///   · <see cref="M_03_多Routing_合批候选须在完整Path维度真实选路_不得因身份冲突放弃合批"/>
///   · <see cref="M_04_AB边界_连续份额侧不得被普通C合批改写批身份"/>
///   · <see cref="M_05_量Stage路径_三工序共享同一Stage批身份_份额闭合且不重复生成工单"/>
///   · <see cref="M_06_Calendar跨90天_合法完成日第120天不得由PlanningEnd否决"/>
///   · <see cref="M_07_经济Max小于合批量_不得作为合批硬拒绝_仍须合批"/>（**2026-10-14 S1-01**：
///     经济 Max 移出**合批合法性闸**；与 M-01 构成同夹具判别对，唯一变量 = 经济 Max）
///
/// ⚠ 夹具口径常量：`PlanningEnd = PlanningStart + 90 天`（滚动**需求范围**，不是资源时间终点）。
/// ⚠ 本文件**不替代**第 91/180 天时间反证 —— 那三组在 <see cref="PlanningEndNotHardBoundTests"/>。
/// </summary>
public class StageBatchMergeTests
{
    private static readonly DateTime Day = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningStart = Day;

    /// <summary>滚动 90 天 = 需求进入本轮求解的范围（**不是**资源时间终点）。</summary>
    private static readonly DateTime PlanningEnd = Day.AddDays(90);

    private const int MaterialId = 1;
    private const int DeptId = 100;

    private readonly FiniteCapacitySolver _solver = new();

    // ════════════════════════════════════════════════════════════════════
    // M-01 合批应胜
    //   复审要求：同 Stage 同产品、需求 A30 / B20、≥2 个 Operation、存在公共合法 Route/Path、
    //   完整硬约束允许合批且冻结的目标评价**合批更优** ⇒ 最终**一个** Stage 执行批、完整 Operation 链、
    //   A/B 份额 30/20、净产出 50（按 Stage 正确计一次）。
    //
    //   本夹具让合批**真实更优**的业务机制 = **省换型**：三道工序的资源上各有一条
    //   `EXACT(M→M) = 60min` 规则（同产品连续、有显式规则时可覆盖默认 0 —— SetupOptimizer §6）。
    //     · 独立排程：B 的三道工序各自命中 M→M 换型 ⇒ 3×60 = 180min Setup，完成时间被推后；
    //     · 合批：目标批被移出后重排，三道工序在其资源上均无前产品（InitialState ⇒ Setup=0），
    //       且 50 件一次成型 ⇒ 完成更早、Setup 总量 0。
    //   ⇒ 冻结四层目标 ③（更早完成）与 ④（Setup 更小）**两层同时**指向合批。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_01_合批应胜_三工序两需求_须形成一个50件Stage执行批与完整Operation链()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 一个 Stage 执行批：恰 3 条 Operation 级 FinalTask，全部同一批键
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKeys = result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().ToList();
        var batchKey = Assert.Single(batchKeys);
        Assert.False(string.IsNullOrEmpty(batchKey));

        // ── AUD-1-002 整改（0号位 2026-10-10《APS_V1_2_20261010.md》§3，P0/CONFIRMED）──
        //   **执行批身份（跨 Stage 工序链）** 与 **Stage 执行批身份（Stage 内 MES 批）** 必须**分开**：
        //     · `ExecutionBatchDraftKey` = 该执行批**跨整个 Routing Path** 的身份 ⇒ 本夹具 1 个；
        //     · `StageExecutionBatchDraftKey` = **Stage 内** MES 批身份（T-002「TaskNo 是 Stage 内 MES 执行批身份」、
        //       T-003「MES 工单不跨 Stage」）⇒ 三道工序分属 STAGE1/2/3 ⇒ **3 个互不相同**。
        //   ⇒ 本条即「**跨 Stage 不得共用一个 MES 工单**」在 1号位 出口的反证：
        //     若三个不同 Stage 的 Task 共用一个 `StageExecutionBatchDraftKey`（旧实现），本条**红**。
        //     （最终 `TaskNo` / MES 工单归组由 2号位 在 FinalTask 之后完成，须与其联验。）
        var stageKeys = result.FinalTasks.Select(t => t.StageExecutionBatchDraftKey).ToList();
        Assert.All(stageKeys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Equal(3, stageKeys.Distinct(StringComparer.Ordinal).Count());

        // 完整 Operation 链：三道工序齐备，且**净产出 50 按 Stage 正确计一次**（不得 150）
        Assert.Equal(
            new[] { "OP10", "OP20", "OP30" },
            result.FinalTasks.Select(t => t.OperationCode).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.PlannedProcessQty));

        // 一条完整真实 Path（不得混 Path）
        Assert.All(result.FinalTasks, t => Assert.Equal("RT", t.RouteCode));
        Assert.All(result.FinalTasks, t => Assert.Equal(1, t.PathId));

        // 份额账本：A 30 / B 20，闭合到声明量
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(50m, result.AllocationShares.Sum(s => s.ComponentQty));

        // 跨 90 天承载：合并批落在第 120 天的真实日历窗内
        Assert.All(result.FinalTasks, t => Assert.True(t.PlannedStartTime >= Day.AddDays(120)));

        // ── 复审 §六.5「**硬约束及目标优于分开方案**」：同夹具对照组（`AllowMerge=false` = 候选 A「单独排」）──
        //   不能只断言「合批胜出」这一**结果**，必须在**冻结目标层**上断言它**严格优于**分开方案。
        //   `allowMerge=false` ⇒ 求解器不尝试合批 ⇒ 直接给出「单独排」候选（**候选 A 的确定取法**；
        //   M-02 则是**开着 Merge** 由 `PreferStageMerge` 判独立排胜出 —— 两者取法不同，勿混）。
        var separateRequest = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3),
            allowMerge: false);

        var separate = await _solver.SolveAsync(separateRequest);

        Assert.True(separate.Success, separate.ErrorMessage);
        // 分开方案：两条需求各自成批 ⇒ 6 条 Task / 2 个批键（即候选 A）
        Assert.Equal(6, separate.FinalTasks.Count);
        Assert.Equal(2, separate.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());

        var mergedEnd = result.FinalTasks.Max(t => t.PlannedEndTime);
        var separateEnd = separate.FinalTasks.Max(t => t.PlannedEndTime);
        var mergedSetup = result.FinalTasks.Sum(t => t.SetupTime);
        var separateSetup = separate.FinalTasks.Sum(t => t.SetupTime);

        // 冻结目标 ③（FORWARD：更早可行完成优先）—— 合批严格更早
        Assert.True(mergedEnd < separateEnd,
            $"合批完成（{mergedEnd:O}）须早于分开方案（{separateEnd:O}）");
        // 冻结目标 ④（Setup 总量小者优先）—— 合批严格更省
        Assert.True(mergedSetup < separateSetup,
            $"合批 Setup（{mergedSetup}）须小于分开方案（{separateSetup}）");
    }

    // ════════════════════════════════════════════════════════════════════
    // M-02 不合批应胜（**反向用例** —— 复审 §六.5「还需一例合法不合批更优的反向用例」）
    //   同产品、同工序集、无换型规则（合批省不下 Setup），交期宽松 ⇒ 合批**合法**但**更晚完成**：
    //     · 独立排程：B 紧接 A 之后逐工序落位 ⇒ 完成更早；
    //     · 合批：三工序各 50 件，占用被拉长 ⇒ 完成更晚。
    //   ⇒ 冻结四层目标 ③（FORWARD：更早可行完成优先）指向独立排程。
    //   复审要求：「不可强制为了减少 Setup 而延误高优先需求」；本用例进一步证明
    //   **合法即采用**的贪心合批同样不被允许 —— 必须有可度量的改善。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_02_不合批应胜_合批更晚且不多省Setup_须取独立排程两批各合法Path()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });
        // 无 SetupTransitionRules ⇒ 合批省不下换型 ⇒ 合批只剩「更晚完成」这一个差别。

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 未合批：两条需求各自成批 ⇒ 6 条 Task、两个批键
        Assert.Equal(6, result.FinalTasks.Count);
        Assert.Equal(2, result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());

        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        var bTasks = result.FinalTasks.Where(t => t.SourceDraftId == "B").ToList();
        Assert.Equal(3, aTasks.Count);
        Assert.Equal(3, bTasks.Count);
        Assert.All(aTasks, t => Assert.Equal(30m, t.Quantity));
        Assert.All(bTasks, t => Assert.Equal(20m, t.Quantity));

        // 两批各自合法 Path
        Assert.All(result.FinalTasks, t => Assert.Equal("RT", t.RouteCode));
        Assert.All(result.FinalTasks, t => Assert.Equal(1, t.PathId));

        // B 的完成时间 = 独立排程的真实完成（紧接 A 之后），**早于**合批会给出的完成
        var mergedAlternativeEnd = Day.AddDays(120).AddMinutes(150);   // 50 件 × 3 工序
        var bEnd = bTasks.Max(t => t.PlannedEndTime);
        Assert.True(bEnd < mergedAlternativeEnd,
            $"独立排程的 B 完成（{bEnd:O}）应早于合批方案（{mergedAlternativeEnd:O}）");
    }

    // ════════════════════════════════════════════════════════════════════
    // M-03 多Routing
    //   复审要求：合法公共 Path1/Path2 存在；合批候选必须在**完整 Path 维度**真实选路，
    //   不能「先 A 去 Path1、B 去 Path2，然后因身份冲突放弃合批」。
    //
    //   夹具：Path1 = 快路径（1 min/件 + 三条 M→M 换型规则 60min）；Path2 = 慢路径（10 min/件、无规则）。
    //     · A 先排：Path1 完成更早 ⇒ A 落 Path1（并成批，带批键）；
    //     · B 排：Path1 候选 ⇒ **命中 A 的 Stage 执行批** ⇒ 合批（同批键、完整 Path1）；
    //              Path2 候选 ⇒ 独立排程但工序长 10 倍 ⇒ 完成远晚。
    //   ⇒ 择优必须真的在 Path 维度比较，且合批**不得**因「目标已带批键」被拒。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_03_多Routing_合批候选须在完整Path维度真实选路_不得因身份冲突放弃合批()
    {
        var request = Build(
            paths: new[]
            {
                ThreeOpPath("RT", 1, 1, 2, 3, 1m),
                ThreeOpPath("RT", 2, 11, 12, 13, 10m)
            },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(200)),
                (2, Day.AddDays(120), Day.AddDays(200)),
                (3, Day.AddDays(120), Day.AddDays(200)),
                (11, Day.AddDays(120), Day.AddDays(200)),
                (12, Day.AddDays(120), Day.AddDays(200)),
                (13, Day.AddDays(120), Day.AddDays(200))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批成立 ⇒ 一个 Stage 执行批、完整 Path1（**没有** A 在 Path1、B 在 Path2 的分裂结果）
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKey = Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(result.FinalTasks, t => Assert.Equal(1, t.PathId));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));

        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-04 A/B 边界
    //   复审要求：相同产品但其中一方已下发/锁定/**连续份额**，普通 C 合批不得改写历史 MES 工单/批身份。
    //   夹具：A 是 **A/B 连续份额**（`IsContinuation = true`，契约同源 `NoSplitMerge = true`）。
    //     ⇒ `FindMergeableStageBatches` 的 ⑤ 条必须把「连续份额侧的批」整批排除 ⇒ B 独立排程。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_04_AB边界_连续份额侧不得被普通C合批改写批身份()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200), IsContinuation: true),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // A 的连续份额批**原样保留**（30 件、自己一条批键），B 另起一批（20 件）⇒ 未被合批改写
        Assert.Equal(6, result.FinalTasks.Count);
        Assert.Equal(2, result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());

        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        Assert.Equal(3, aTasks.Count);
        Assert.All(aTasks, t => Assert.Equal(30m, t.Quantity));

        var bTasks = result.FinalTasks.Where(t => t.SourceDraftId == "B").ToList();
        Assert.Equal(3, bTasks.Count);
        Assert.All(bTasks, t => Assert.Equal(20m, t.Quantity));

        // 份额账本不得出现跨需求合并记录：A/B 各自闭合
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-05 量 / Stage / 路径
    //   复审要求：三 Operation 共享**同一 Stage 批身份**、两份 Demand 份额闭合，
    //   不能重复生成工单、混 Path、提前删除工序依赖。
    //   断言侧重 M-01 未覆盖的两项：**工序依赖边未丢**、**无重复 Task**。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_05_量Stage路径_三工序共享同一Stage批身份_份额闭合且不重复生成工单()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        // 无重复 Task：逐 (StageCode, OperationCode) 唯一
        Assert.Equal(
            result.FinalTasks.Count,
            result.FinalTasks.Select(t => (t.StageCode, t.OperationCode)).Distinct().Count());

        // 工序依赖边**未被提前删除**：OP10→OP20、OP20→OP30 两条边都在
        //   （`PhysicalPeggingDrafts` 按 FinalDraftId 连边 ⇒ 先映回工序码再断言）
        var opByDraft = result.FinalTasks.ToDictionary(t => t.FinalDraftId, t => t.OperationCode);
        var edges = result.PhysicalPeggingDrafts
            .Where(p => opByDraft.ContainsKey(p.UpstreamFinalDraftId)
                        && opByDraft.ContainsKey(p.DownstreamFinalDraftId))
            .Select(p => (From: opByDraft[p.UpstreamFinalDraftId], To: opByDraft[p.DownstreamFinalDraftId]))
            .ToList();
        Assert.Contains(("OP10", "OP20"), edges);
        Assert.Contains(("OP20", "OP30"), edges);

        // 同一 Stage 批身份 + 份额闭合
        Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.Equal(50m, result.AllocationShares.Sum(s => s.ComponentQty));
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));

        // 不得把 50 件经 3 道工序错误加成 150 件：逐工序净产出均为 50
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-06 Calendar 跨 90 天
    //   复审要求：合并方案**合法完成日第 120 天**，不能由 `PlanningEnd` 直接否决；
    //   不得替代第 91/180 天时间反证（那三组在 PlanningEndNotHardBoundTests）。
    //
    //   夹具刻意让日历窗**横跨第 90 天**：res1 从第 89 天起（90 天以内），res2/res3 从第 120 天起
    //   （远超 90 天）⇒ 合并方案的承载必须同时用到**两侧**日历，完成日落在第 120 天之后。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_06_Calendar跨90天_合法完成日第120天不得由PlanningEnd否决()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(89), Day.AddDays(200)),
                (2, Day.AddDays(120), Day.AddDays(121)),
                (3, Day.AddDays(120), Day.AddDays(121))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批成立（一个 Stage 执行批、50 件）
        Assert.Equal(3, result.FinalTasks.Count);
        Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));

        // 承载跨过第 90 天：末道工序的完成**晚于** `PlanningEnd`，即合并方案没有被 90 天口径否决
        var endTask = result.FinalTasks.Single(t => t.OperationCode == "OP30");
        Assert.True(endTask.PlannedEndTime > PlanningEnd,
            $"末道工序完成（{endTask.PlannedEndTime:O}）须晚于 PlanningEnd（{PlanningEnd:O}）");
        Assert.True(endTask.PlannedStartTime >= Day.AddDays(120),
            $"末道工序须落在第 120 天的真实日历窗内，实际 {endTask.PlannedStartTime:O}");

        // 逐工序净产出 50（按 Stage 正确计一次）
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
    }

    // ════════════════════════════════════════════════════════════════════
    // OWN-P1-03 专项：Unscheduled 归因不得是「90 天末期」
    //   复审要求：「在**可用合法日历确实覆盖未来**的场景，不得仅因超出 90 天、客户交期或既有排程拥挤
    //   而产出『部分批未排 / Domain 失败』」；「缺 Eligibility、资源 Calendar 未维护…仍是需要明确输出
    //   原因的**真实异常**」。
    //   本用例同一几何两组对照：
    //     A. 日历覆盖未来（第 200 天）⇒ **必须排下**，不得 Unscheduled；
    //     B. 该需求**根本没有可用的资源日历**（资源未维护）⇒ Unscheduled，但原因必须指向
    //        **真实缺失**（日历/Eligibility 类），**不得**出现任何「90 天/PlanningEnd/计划窗口」字样。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task OWN_P1_03_未来有合法日历须排下_无日历侧须报真实缺失原因而非90天末期()
    {
        // ── A：日历覆盖第 200 天（远超 90 天）⇒ 必须排下 ──
        var requestA = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[] { new DemandSpec("D1", 1, 30m, Day.AddDays(20)) },
            calendar: new[]
            {
                (1, Day.AddDays(200), Day.AddDays(201)),
                (2, Day.AddDays(200), Day.AddDays(201)),
                (3, Day.AddDays(200), Day.AddDays(201))
            },
            allowMerge: false);

        var resultA = await _solver.SolveAsync(requestA);

        Assert.True(resultA.Success, resultA.ErrorMessage);
        Assert.Empty(resultA.UnscheduledTasks);
        Assert.Equal(3, resultA.FinalTasks.Count);
        Assert.All(resultA.FinalTasks, t => Assert.True(t.PlannedStartTime >= Day.AddDays(200)));

        // ── B：同一几何但**资源未维护日历** ⇒ 真实缺失，原因不得是 90 天口径 ──
        var requestB = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[] { new DemandSpec("D1", 1, 30m, Day.AddDays(200)) },
            calendar: Array.Empty<(int, DateTime, DateTime)>(),   // 无任何日历事实
            allowMerge: false);

        var resultB = await _solver.SolveAsync(requestB);

        Assert.True(resultB.Success, resultB.ErrorMessage);
        Assert.Empty(resultB.FinalTasks);
        var unsched = Assert.Single(resultB.UnscheduledTasks);
        Assert.Equal("D1", unsched.DraftId);
        Assert.False(string.IsNullOrWhiteSpace(unsched.Reason));
        Assert.DoesNotContain("90", unsched.Reason);
        Assert.DoesNotContain("PlanningEnd", unsched.Reason);
        Assert.DoesNotContain("计划窗口", unsched.Reason);
    }

    // ════════════════════════════════════════════════════════════════════
    // M-07（AUD-1-R01）多个合法锚点：必须**逐锚点**择优，不得只试 `stageBatches[0]`
    //   复审判词（本号位核对：**成立**）：旧实现只试第一个合法合批目标（`scheduledTasks` 中出现最早者）
    //     ⇒ 存在多个合法锚点时可能漏掉业务更优方案（复审明确**不要求**穷举组合，逐锚点即可）。
    //
    //   夹具（三条需求 A30 / B20 / C10，同一 Path、同三道工序，各资源 M→M 显式换型 60min，单一宽窗）：
    //     · **A 交期收紧到 D+120 01:40（+100min）** —— A 独立排程 90min 完成（[0,30]/[30,60]/[60,90]，各资源首件 ⇒ Setup 0）
    //       恰在交期内；**任何把 A 批延长的合批**都会被 P0-04 交期保护拒掉。
    //     · A 交期紧 ⇒ **B 无法并入 A**（合批 A+B 完成 150min > A 交期 100min ⇒ 拒）⇒ B 独立成批（完成 270min）。
    //       于是 C 到来时**存在两个合法锚点**：`stageBatches[0] = A 批`、`stageBatches[1] = B 批`。
    //     · C 侧（交期宽松）：
    //         - 并入 **A 批**（A+C=40）：重排后完成 370min > A 交期 100min ⇒ **交期保护拒绝**；
    //         - 并入 **B 批**（B+C=30）：完成 180min，**远早于独立排程的 340min** ⇒ 冻结目标 ③ 胜出。
    //   ⇒ **旧实现只试 `stageBatches[0]`（A 批）**：合并被拒 ⇒ 回落独立排程 ⇒ 9 条 Task / 3 个批键（本条**红**）；
    //     整改后逐锚点试排 ⇒ 选中 B 批 ⇒ 6 条 Task / 2 个批键（A 批 + B∪C 批）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_07_多锚点_须逐锚点择优_不得只试第一个合法合批目标()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                // A 交期紧（+100min）：独立排 90min 可满足；被合批延长即违约 ⇒ 使 B 无法并入 A。
                new DemandSpec("A", 1, 30m, Day.AddDays(120).AddMinutes(100)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200)),
                new DemandSpec("C", 3, 10m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批胜出且**并入第二个锚点（B 批）**：6 条 Task = A 批 3 条 + 合成批 3 条；2 个执行批键。
        //   旧实现只试 A 批 ⇒ 并入 A 批被交期保护拒绝且不再回落试 B 批 ⇒ 退化成 9 条 / 3 个批键 ⇒ 下列断言**红**。
        Assert.Equal(6, result.FinalTasks.Count);
        Assert.Equal(2, result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());

        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        Assert.Equal(3, aTasks.Count);
        Assert.All(aTasks, t => Assert.Equal(30m, t.Quantity));

        var merged = result.FinalTasks.Where(t => t.SourceDraftId == "B").ToList();
        Assert.Equal(3, merged.Count);
        Assert.All(merged, t => Assert.Equal(30m, t.Quantity));   // B20 + C10

        // 份额闭合：A30 / B20 / C10 各**恰好一次**
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(10m, result.AllocationShares.Where(s => s.AllocationSequence == 3).Sum(s => s.ComponentQty));
        Assert.Equal(60m, result.AllocationShares.Sum(s => s.ComponentQty));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-08（AUD-1-001）连续两次 Merge：份额**不得按 Operation 重复累加**
    //   复审 §7 第 1 项：A30+B20+C10 **连续两次 Merge**、三 Operation、净产出 60、
    //     A/B/C 分别 30/20/10，Phase5 完整闭合。
    //   缺陷形态（旧实现）：A+B 先合成 50 件批（3 条 Operation Task 各持同一份 [A30,B20]）；
    //     再合 C10 时把被移出批的 N 条副本**逐条累加** ⇒ B20×3 + C10 = 70，而新批 Task 数量只有 60
    //     ⇒ Phase5 逐 (需求 × 工序) 闭合失真（B 膨胀、A 锚点份额被淹没）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_08_连续两次合批_三需求三工序_份额按需求各计一次_Phase5完整闭合()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200)),
                new DemandSpec("C", 3, 10m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        // Phase5 硬校验 `ValidateHardResult` 逐 (需求 × 工序) 闭合 ⇒ 份额失真会直接使 Success=false
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 一个 Stage 执行批、完整三工序链、每条 Task 净产出 = 60（**不是** 180）
        Assert.Equal(3, result.FinalTasks.Count);
        Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.All(result.FinalTasks, t => Assert.Equal(60m, t.Quantity));
        Assert.All(result.FinalTasks, t => Assert.Equal(60m, t.PlannedProcessQty));

        // 份额：A30 / B20 / C10，各**恰好一次**，合计 60（判别器）
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(10m, result.AllocationShares.Where(s => s.AllocationSequence == 3).Sum(s => s.ComponentQty));
        Assert.Equal(60m, result.AllocationShares.Sum(s => s.ComponentQty));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-09（AUD-1-003）失败试排必须**全状态回滚**（单工序成功、下游工序失败）
    //   复审 §7 第 2 项要求：断言占用、时间线、Shares、Tasks 深度一致；测试单工序成功但下游 Operation 失败。
    //   夹具：两道工序 —— OP10@STAGE1 上 R1（10h 大窗）、OP20@STAGE2 上 R2（两段窄窗 30min / 40min）。
    //     A30 先排：OP10 00:00-00:30；OP20 落 R2 第一段 02:00-02:30。
    //     B20 到来：合批试排 = 合成 50 件 ⇒ OP10 可排（R1 宽），**OP20 需 50min、两段窄窗都装不下** ⇒ 失败。
    //       探针实测（2026-10-10）：该失败分支**确实被走到**（在 `combinedTasks.Count == 0` 处打 throw 探针，
    //       M-09 即因该异常失败 ⇒ 分支可达，非空跑）。
    //   判别器：合批**被尝试且失败**的结果，必须与**完全不尝试合批**（`allowMerge=false`）逐字段一致；
    //     若失败试排遗留前序工序（OP10）在 R1 上的**幽灵占用** ⇒ B 的 OP10 被推后 ⇒ 本条红。
    //
    //   ⚠ **本号位如实登记的实测结论（重要，不粉饰）**：
    //     把 AUD-1-003 的两处水位回滚**全部临时移除**后，本条**仍为绿**。原因：OWN-P0-02 的合批试排
    //     （`EnumerateLegalBatchPlanCandidates` 内）跑在 `CloneOccupancy(resourceOccupancy, …)` 的
    //     **COW 克隆**上（`EnsureOwned` 写前分叉，r13599 引入），失败写入落在**被丢弃的克隆**里，
    //     真实表从不被试排污染；落定调用仅在**试排成功**（`bestAnchorIndex >= 0`）时发生，
    //     且与试排同初始上下文（确定性）⇒ 真实表永不出现该幽灵。
    //   ⇒ 本号位**不宣称** M-09 是 AUD-1-003 的「整改前红」判别性反证（违反「写『整改前红』必先实测」）。
    //     本条的价值 = **零回归守卫**：钉死「失败试排不得污染后续计算」这一冻结不变式
    //     （RC-001～RC-004 / B-003），防止日后有路径把试排改回真实表时静默回归。
    //     水位回滚本身按 A 类自决保留为**纵深防御**（覆盖「落定调用返回 null」与「`mergeStructurallyPossible`
    //     隔离判据回归」两类风险），**不降目标**：冻结要求「失败试排不可污染」已由 COW 隔离 + 水位回滚
    //     双重达成。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_09_合批失败_单工序成功下游失败_须全状态回滚不留幽灵占用()
    {
        var twoOp = new PathSpec("RT", 1,
            new[] { new OpSpec("STAGE1", "OP10", 1, 1m), new OpSpec("STAGE2", "OP20", 2, 1m) },
            new[] { ("OP10", "OP20") });

        DomainSolveRequest BuildTwo(bool allowMerge) => Build(
            paths: new[] { twoOp },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(120).AddHours(10)),
                (2, Day.AddDays(120).AddHours(2), Day.AddDays(120).AddHours(2).AddMinutes(30)),
                (2, Day.AddDays(120).AddHours(4), Day.AddDays(120).AddHours(4).AddMinutes(40))
            },
            allowMerge: allowMerge);

        var withMergeAttempt = await _solver.SolveAsync(BuildTwo(allowMerge: true));
        var pureSeparate = await _solver.SolveAsync(BuildTwo(allowMerge: false));

        Assert.True(withMergeAttempt.Success, withMergeAttempt.ErrorMessage);
        Assert.True(pureSeparate.Success, pureSeparate.ErrorMessage);
        Assert.Empty(withMergeAttempt.UnscheduledTasks);
        Assert.Empty(pureSeparate.UnscheduledTasks);

        // **深度一致**：Task 数、逐 Task 的 (需求, Stage, 工序, 起, 止, 数量) 全部相同
        static List<(string, string, string, DateTime, DateTime, decimal)> Fingerprint(DomainSolveResult r)
            => r.FinalTasks
                .OrderBy(t => t.SourceDraftId, StringComparer.Ordinal)
                .ThenBy(t => t.OperationCode, StringComparer.Ordinal)
                .Select(t => (t.SourceDraftId, t.StageCode, t.OperationCode,
                              t.PlannedStartTime, t.PlannedEndTime, t.Quantity))
                .ToList();

        Assert.Equal(Fingerprint(pureSeparate), Fingerprint(withMergeAttempt));

        // 份额账本亦不得被污染
        static List<(long, decimal)> Shares(DomainSolveResult r)
            => r.AllocationShares
                .OrderBy(s => s.AllocationSequence)
                .Select(s => (s.AllocationSequence, s.ComponentQty))
                .ToList();

        Assert.Equal(Shares(pureSeparate), Shares(withMergeAttempt));

        // 幽灵占用的**直接**判别器：B 的 OP10 必须**紧接** A 的 OP10（中间不得出现空洞）
        var aOp10 = withMergeAttempt.FinalTasks.Single(t => t.SourceDraftId == "A" && t.OperationCode == "OP10");
        var bOp10 = withMergeAttempt.FinalTasks.Single(t => t.SourceDraftId == "B" && t.OperationCode == "OP10");
        Assert.Equal(aOp10.PlannedEndTime, bOp10.PlannedStartTime);

        // 未丢需求、未错批：两条需求各自一批
        Assert.Equal(2, withMergeAttempt.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());
    }

    // ════════════════════════════════════════════════════════════════════
    // M-10（AUD-1-002 正例）**真正同一 StageCode** 的 N Operation：合批后共用一个 Stage 执行批身份
    //   复审 §7 第 3 项：新增真正同一 `StageCode` 的 N Operation 场景（与 M-01 跨 Stage 反例成对）。
    //   夹具：三道工序**全部落 STAGE1**（OP10/OP20/OP30 @ R1/R2/R3），A30 + B20 合批。
    //   ⇒ `ExecutionBatchDraftKey` 1 个（跨 Stage 工序链身份）；
    //     `StageExecutionBatchDraftKey` **1 个**（同 Stage 的 N 条 Operation Task 共享同一 MES 执行批身份，
    //     T-002/T-005）—— 与 M-01 的「三个不同 Stage ⇒ 3 个 Stage 键」互为正反证。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_10_同一Stage内N工序_合批须共用一个Stage执行批身份()
    {
        var sameStage = new PathSpec("RT", 1,
            new[]
            {
                new OpSpec("STAGE1", "OP10", 1, 1m),
                new OpSpec("STAGE1", "OP20", 2, 1m),
                new OpSpec("STAGE1", "OP30", 3, 1m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

        var request = Build(
            paths: new[] { sameStage },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        Assert.Equal(3, result.FinalTasks.Count);
        Assert.All(result.FinalTasks, t => Assert.Equal("STAGE1", t.StageCode));

        // 跨 Stage 工序链的执行批身份：1 个
        Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());

        // **Stage 内** MES 执行批身份：同一 StageCode ⇒ **1 个**（同批 N 条 Operation Task 共享一个 TaskNo）
        var stageKey = Assert.Single(result.FinalTasks.Select(t => t.StageExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(stageKey));

        // 净产出按 Stage 计一次（不得 50 × 3）
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
    }

    // ─────────────────────────── 构造辅助 ───────────────────────────

    /// <summary>
    /// 工序 + 其**合法替代设备**。`Alternatives` 非空时，`Build` 会为**同一** `OperationCode` 追加
    /// 若干 `OperationResourceEligibility`（首选 `ResourceId` 的 `Priority = 1 / CapacityFactor = 1`，
    /// 替代项依次 `Priority = 2,3,…`）。
    /// 用途（R2-E / R2-B）：让「独立排程」与「合批」落到**不同 CapacityFactor** 的设备上 ⇒
    /// 除 Setup 外占用增量 `Δ = O(锚+本) − O(锚) − O(本) ≠ 0` ⇒ ④.6 具备**真实**判别对象
    /// （打破「同资源 ⇒ Δ ≡ 0」的结构性惰性）。
    /// </summary>
    private readonly record struct OpSpec(
        string StageCode, string OpCode, int ResourceId, decimal Minutes,
        IReadOnlyList<(int ResourceId, decimal CapacityFactor)>? Alternatives = null);

    private readonly record struct PathSpec(
        string RouteCode, int PathId, IReadOnlyList<OpSpec> Ops, IReadOnlyList<(string From, string To)> Deps);

    private readonly record struct DemandSpec(
        string Key, long Seq, decimal Qty, DateTime? Due,
        string? RouteCode = null, int? PathId = null, bool IsContinuation = false);

    /// <summary>三工序串行路径（OP10@STAGE1 → OP20@STAGE2 → OP30@STAGE3），资源与工时按调用方给定。</summary>
    private static PathSpec ThreeOpPath(
        string routeCode, int pathId, int r1, int r2, int r3, decimal minutesPerUnit)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", r1, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", r2, minutesPerUnit),
                new OpSpec("STAGE3", "OP30", r3, minutesPerUnit)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 三工序串行路径，但**仅首工序工时与数量相关**（OP20/OP30 = 0 min）——
    /// 用途：令「独立排程」与「合批」在冻结目标 ①②③④ 四层**完全判平**
    /// （两者末工序完成时间同为 `A + B`；无换型 ⇒ Setup 同为 0；交期远 ⇒ 延期同为 0），
    /// 从而把择优**唯一地**推到第 ④.5 层（经济软偏离）⇒ 经济评分的**鉴别性**反证夹具。
    /// </summary>
    private static PathSpec ZeroTailPath(
        string routeCode, int pathId, int r1, int r2, int r3, decimal minutesPerUnit)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", r1, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", r2, 0m),
                new OpSpec("STAGE3", "OP30", r3, 0m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 三工序串行路径，**首工序两台合法设备**（`firstResourceId` 优先、`firstAltResourceId` 备选），
    /// 且 **仅首工序工时与数量相关**（`OP20`/`OP30` = 0 min）。
    /// 用途：**S1-01 正工时闭合**夹具 —— 正工时（`OP10` 占用 = 数量 × `minutesPerUnit`）
    /// 与「真实多锚」（`OP10` 的两台设备使先到的两个需求各自独立成批）**同时成立**，
    /// 而末工序完成 = `OP10` 完成 ⇒ ③ 层可比且可判平。
    /// </summary>
    private static PathSpec DivergentZeroTailPath(
        string routeCode, int pathId, int firstResourceId, int firstAltResourceId,
        decimal minutesPerUnit, int r2, int r3)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", firstResourceId, minutesPerUnit,
                    new[] { (firstAltResourceId, 1m) }),
                new OpSpec("STAGE2", "OP20", r2, 0m),
                new OpSpec("STAGE3", "OP30", r3, 0m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 同产品连续（M→M）显式换型规则（`SetupOptimizer` §6：同产品 A→A **有显式规则时可覆盖默认 0**）。
    /// 用途：让「独立排程」真实付出换型成本，而「合批」因为目标批被移出、资源上无前产品（InitialState）而不付
    /// ⇒ 合批在冻结目标第 ③（完成更早）与第 ④（Setup 更小）两层同时更优。
    /// </summary>
    /// <summary>
    /// 两工序串行路径（OP10@STAGE1 → OP20@STAGE2），**第二工序有两台合法替代设备**：
    /// 首选 `primaryResourceId`（CF = 1），备选 `altResourceId`（CF = `altCapacityFactor`）。
    /// 用途：R2-E / R2-B 的 **Solver 级正工时**反例 —— 让「合批」因首选设备容不下而落到**另一台不同 CF** 的
    /// 合法替代设备上，从而 `Δ = O(锚+本) − O(锚) − O(本) ≠ 0`，④.6 层**真实**参与择优。
    /// </summary>
    private static PathSpec DivergentPath(
        string routeCode, int pathId, int firstResourceId, decimal minutesPerUnit,
        int primaryResourceId, int altResourceId, decimal altCapacityFactor)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", firstResourceId, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", primaryResourceId, minutesPerUnit,
                    new[] { (altResourceId, altCapacityFactor) })
            },
            new[] { ("OP10", "OP20") });

    /// <summary>
    /// 两工序串行路径（OP10@STAGE1 → OP20@STAGE2），**第二工序有多台合法替代设备**
    /// （首选 `primaryResourceId` 的 `Priority = 1 / CapacityFactor = 1`，其后按 `alternatives` 依次
    /// `Priority = 2,3,…`）。
    /// 用途（**R2-C 多锚整改**反例，0号位 第六轮 §3.2/§3.3）：让「合批到 A」与「合批到 B」分别落到
    /// **不同 CapacityFactor** 的替代设备上 ⇒ 两侧 ④.6 增量**互不相同**，且各自「相对统一基线的增量」
    /// **不等于**「B 相对 A 的胜负差值」⇒ 可判别「以相对基线增量冒充胜负原因」。
    /// </summary>
    private static PathSpec MultiAltPath(
        string routeCode, int pathId, int firstResourceId, decimal minutesPerUnit,
        int primaryResourceId, params (int ResourceId, decimal CapacityFactor)[] alternatives)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", firstResourceId, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", primaryResourceId, minutesPerUnit, alternatives)
            },
            new[] { ("OP10", "OP20") });

    private static IReadOnlyList<SetupTransitionRuleSnapshot> SameProductSetup(
        decimal minutes, params int[] resourceIds)
        => resourceIds
            .Select(rid => new SetupTransitionRuleSnapshot
            {
                ProductionDepartmentId = DeptId,
                StageCode = "",
                OperationCode = $"OP{rid}0",     // 与 ThreeOpPath 的工序码一一对应（资源 1/2/3 → OP10/OP20/OP30）
                ResourceId = rid,
                FromMaterialId = MaterialId,
                ToMaterialId = MaterialId,
                RuleType = "EXACT",
                SetupMinutes = minutes
            })
            .ToArray();

    private static DomainSolveRequest Build(
        IReadOnlyList<PathSpec> paths,
        IReadOnlyList<DemandSpec> demands,
        IReadOnlyList<(int ResourceId, DateTime Start, DateTime End)> calendar,
        bool allowMerge = true,
        bool allowSplit = false,
        string direction = "FORWARD",
        IReadOnlyList<SetupTransitionRuleSnapshot>? setupRules = null,
        IReadOnlyList<ExecutionConstraint>? locked = null,
        IReadOnlyList<BatchPolicyRuleSnapshot>? batchPolicies = null,
        IReadOnlyList<int>? deviceResourceIds = null)
    {
        var routingOps = new List<RoutingOperation>();
        var elig = new List<OperationResourceEligibility>();
        var deps = new List<RoutingDependency>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var p in paths)
        {
            foreach (var op in p.Ops)
            {
                routingOps.Add(new RoutingOperation
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    OperationCode = op.OpCode, StageCode = op.StageCode,
                    StandardDuration = op.Minutes, OperationPlanningMode = "FINITE_RESOURCE"
                });
                elig.Add(new OperationResourceEligibility
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    OperationCode = op.OpCode, ResourceId = op.ResourceId,
                    Priority = 1, CapacityFactor = 1m
                });
                if (op.Alternatives is not null)
                {
                    var altPriority = 2;
                    foreach (var (altResourceId, altCapacityFactor) in op.Alternatives)
                    {
                        elig.Add(new OperationResourceEligibility
                        {
                            MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                            RouteCode = p.RouteCode, PathId = p.PathId,
                            OperationCode = op.OpCode, ResourceId = altResourceId,
                            Priority = altPriority++, CapacityFactor = altCapacityFactor
                        });
                    }
                }
                if (stageDepts.All(s => !string.Equals(s.StageCode, op.StageCode, StringComparison.Ordinal)))
                {
                    stageDepts.Add(new MaterialStageDepartmentContextDto
                    {
                        MaterialId = MaterialId, StageCode = op.StageCode, ProductionDepartmentId = DeptId
                    });
                }
            }

            foreach (var (from, to) in p.Deps)
            {
                deps.Add(new RoutingDependency
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    FromOperationCode = from, ToOperationCode = to,
                    DependencyType = "ES", LagTime = 0m, IsActive = true
                });
            }
        }

        var resources = paths
            .SelectMany(p => p.Ops.SelectMany(o => o.Alternatives is null
                ? new[] { o.ResourceId }
                : new[] { o.ResourceId }.Concat(o.Alternatives.Select(a => a.ResourceId))))
            .Distinct()
            .Select(rid => new ResourceDefinition
            {
                ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m
            })
            .ToList();

        var slots = calendar
            .Select(c => new ResourceCalendarSlot
            {
                ResourceId = c.ResourceId, Start = c.Start, End = c.End, IsAvailable = true
            })
            .ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key, PlanVersionId = 1L, DomainKey = "DOMAIN",
                AllocationSequence = d.Seq, DemandKey = d.Key, MaterialId = MaterialId, FactoryId = 1,
                StartStageCode = "STAGE1",
                NetOutputQty = d.Qty, PlannedProcessQty = d.Qty,
                RequiredAvailableTime = d.Due ?? PlanningStart.AddDays(20),
                DemandSequence = (int)d.Seq,
                RouteCode = d.RouteCode, PathId = d.PathId,
                IsContinuation = d.IsContinuation, NoSplitMerge = d.IsContinuation
            }).ToList(),
            RoutingOperations = routingOps,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = locked ?? Array.Empty<ExecutionConstraint>(),
            Resources = resources,
            CalendarSlots = slots,
            // R-2 设备口径（0号位 J13-05）：`null` = 未投影（兼容边界，与本文件既有用例逐字一致）；
            //   非 null = 纯设备白名单；**空集 ≠ null**。
            DeviceResourceIds = deviceResourceIds,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = direction,
                    AllowMerge = allowMerge,
                    AllowSplit = allowSplit
                },
                // P0-01（0号位 2026-10-08 §四）：C 桶必须显式给出有效 Batch Policy，否则 Fail Closed。
                BatchPolicies = batchPolicies ?? TestBatchPolicy.Permissive(MaterialId, DeptId, allowMerge, allowSplit),
                SetupTransitionRules = setupRules ?? Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // M-07（2026-10-14 S1-01）：**经济 Max 不得作为合批硬拒绝**
    //   依据：0号位《APS V1｜1～5号位联合代码审核报告 v1.0》§4 **S1-01（P0 CONFIRMED）** 逐字：
    //     「……**合批资格亦用经济Max硬拒绝**……**最小整改**：保留PI Original/Remaining/StageFree硬门禁；
    //      在硬PI闸和**合批合法性闸排除经济Max**；软Max仅进入有界候选评分。反例：PI剩1000、
    //      Stage新增合法400、经济Max30/600，若允许拆合且资源合法，**不能只因经济Max得出PI超限**。」
    //
    //   夹具 = **M-01 逐字**（同三工序路径 / 同两需求 A30+B20 / 同省换型规则），**唯一差别** = 经济 Max = 20
    //     （&lt; 合批后 50）⇒ 与 M-01 构成**同夹具判别对**：
    //     · 整改前：`FindMergeableStageBatches` ⑦ 以 `anchor 30 + demand 20 = 50 &gt; 20` 硬拒
    //       ⇒ **无合批候选** ⇒ 两需求各自独立排程 ⇒ **6 条 Task**（本用例红）；
    //     · 整改后：⑦ 已删 ⇒ 合批候选成立 ⇒ `PreferStageMerge` 按冻结四层目标择优（省换型 ⇒ 合批更优）
    //       ⇒ **3 条 Task、1 个批键、净产出 50**。
    //   ✅ **「整改前红」已实测（2026-10-14，非推断）**：临时把 ⑦ 硬拒绝还原（`combinedQty > 经济 Max ⇒ continue`）
    //     并重建 ⇒ 本用例 **红**，断言实测 `Expected: 3 / Actual: 6`（无合批 ⇒ 两需求各 3 条 = 6 条 Task）；
    //     还原后 **绿**（3 条 Task、1 个批键）。M-01（同夹具、`Max=null`）在两次运行中**均绿** ⇒ 构成对照。
    //   ✅ **2026-10-14 残留已闭环**（0号位《正式回复 v2.0》§二 S1-01 ④ 明确「不批准……移出本轮」）：
    //     `PreferStageMerge` 已新增第 ④.5 层消费经济软 Min/Max/Preferred（同一助手 `EconomicDeviationForQty`，
    //     与拆批侧 `CompareBatchPlans` ④.5 同口径同层序）⇒ 合批侧经济 Max **不再是**「既非硬、亦未入软」的真空档。
    //     ⚠ 本条（M-07）的择优在第 ③ 层（合批更早完成）就已决出 ⇒ ④.5 层**不影响本条结果**（本条仍绿，零回归）；
    //     ④.5 层的**鉴别性**证据见 `M_11`（四层判平、唯一由经济层裁决）与层序守卫 `M_12`。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_07_经济Max小于合批量_不得作为合批硬拒绝_仍须合批()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3),
            // **唯一改动**：经济 Max 压到 20（< 合批后 50）。`AllowSplit=false` ⇒ 每需求仍恒 1 批（与 M-01 同）。
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = 20m,
                    PreferredBatchQty = null,
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 与 M-01 **同结论**：合批胜出 ⇒ 3 条 Operation 级 Task、同一批键、净产出 50（按 Stage 计一次）
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKey = Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-11（2026-10-14，0号位《正式回复 v2.0》§二 S1-01 ④⑤）：**合批侧经济软偏离的鉴别性反证**
    //   判词逐字：「**合批经济评分缺口必须补齐。** `PreferStageMerge` 现行固定快照只有合法性/延期/完成时间/
    //     Setup的比较……冻结R-2要求**硬合法候选**的比较实际消费经济软区间、Preferred、Setup、资源效率与交期，
    //     且服从冻结目标层级；不得把"已允许合批"或"仅拆批侧有经济评分"当成合批侧已完成。**不批准以"会影响
    //     现有M-01/M-02选择结果"为由移出本轮既有V1整改**。」＋「经济偏离必须**确实影响同层可比较候选的择优**，
    //     而不是代码里保留但不起作用。」
    //
    //   夹具设计（把 ①②③④ 四层**全部判平**，让择优唯一地落到 ④.5 层）：
    //     · `ZeroTailPath`：OP10 = 1 min/件，OP20/OP30 = **0 min**（工时与数量无关）
    //         ⇒ 独立排程 B 的末工序完成 = A(30) + B(20) = **50 min**；合批(A+B=50) 末工序完成 = **50 min**
    //         ⇒ 第 ③ 层（更早可行完成优先）**判平**；
    //     · 无 `SetupTransitionRules` ⇒ 第 ④ 层 Setup 两侧均 0 ⇒ **判平**；
    //     · 交期远（`Day + 200`）⇒ 第 ② 层延期两侧均 0 ⇒ **判平**；两侧均可行 ⇒ 第 ① 层**判平**；
    //     · 经济软目标 `PreferredBatchQty = 50`（Min/Max 均 null ⇒ 只比 Preferred 偏离）：
    //         独立排程侧偏离 = |20 − 50| = **30**；合批侧偏离 = |50 − 50| = **0**。
    //   ⇒ ④.5 层：合批**严格更优** ⇒ **3 条 Task、1 个批键、净产出 50**。
    //   ⚠ 若 ④.5 层缺失（合批侧不消费经济软目标）：④ 判平后直接落 ⑤「判平取独立排程」⇒ **6 条 Task**。
    //   ✅ **「整改前红」已实测（2026-10-14，非推断）**：临时注释掉 `PreferStageMerge` 的 ④.5 层并重建 ⇒
    //     本用例**红**，实测 `Expected: 3 / Actual: 6`；还原后**绿**。
    //   ⚠ 本条**只**主张「同层可比较候选的择优被经济偏离改变」；**不**主张经济软参数可压过履约 —— 层序守卫见 `M_12`。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_11_经济软偏离_四层判平_须由经济Preferred裁决合批()
    {
        var request = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            // 唯一变量：经济 Preferred = 50（= 合批后数量）⇒ 合批侧偏离 0 < 独立侧偏离 30。
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 50m,
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ④.5 层裁决 ⇒ 合批胜出：3 条 Operation 级 Task、1 个批键、净产出 50（按 Stage 计一次）
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKey = Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));

        // ── 对照组（唯一变量 = 经济 Preferred **置空**，其余夹具逐字相同）──
        //   四层仍判平 ⇒ 落 ⑤「判平取独立排程」⇒ **6 条 Task、2 个批键**。
        //   本条即「经济偏离**确实**改变了同层可比较候选的择优」的**同夹具判别对**（非仅结果断言）。
        var controlRequest = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });
        // 未传 batchPolicies ⇒ `TestBatchPolicy.Permissive`（Min/Max/Preferred 全 null ⇒ 经济层恒平）。

        var control = await _solver.SolveAsync(controlRequest);

        Assert.True(control.Success, control.ErrorMessage);
        Assert.Empty(control.UnscheduledTasks);
        Assert.Equal(6, control.FinalTasks.Count);
        Assert.Equal(2, control.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());
    }

    // ════════════════════════════════════════════════════════════════════
    // M-12（2026-10-14，0号位《正式回复 v2.0》§二 S1-01 ⑤）：**BP-R2b —— 候选排序不得压过履约**
    //   判词要求新增「**候选排序不压过履约/硬约束**」的可观察红绿用例。
    //
    //   夹具（= M-01 逐字，**只把有效交期收窄到计划起点 + 200 min**，令「独立排程」延期）：
    //     · 三道工序各有一条 `EXACT(M→M) = 60 min` 规则 ⇒
    //         独立排程 B 完成 = **290 min**（末工序 200 + 60 换型）；合批(A+B=50) 完成 = **150 min**（无换型）；
    //     · 有效交期 = 计划起点 + **200 min** ⇒ 独立排程**延期 90 min**、合批**按期**（150 ≤ 200）
    //       ⇒ 第 ② 层（履约：延期短者优先）**唯一指向合批**；
    //     · 经济软目标 `PreferredBatchQty = 20`（= 独立排程的 B 批量）**偏向独立排程**：
    //         独立侧偏离 = |20 − 20| = **0**；合批侧偏离 = |50 − 20| = **30**。
    //   ⇒ 第 ② 层（履约）**必须先于** ④.5 层生效 ⇒ **合批胜出** ⇒ **3 条 Task、1 个批键、净产出 50**。
    //   ⚠ 若把经济层错误地提到履约之前，本用例**红**（经济偏向独立 ⇒ 返回 6 条 Task）⇒ 本条是**层序**守卫。
    //   ⚠ 另注：合批候选**本身**必须可行 —— 合批的完成（150）不得推过任一参与需求的有效交期（200），
    //     否则会被 ① 层的**交期保护**（`TryMergeDemandIntoStageBatch`）判为不可行而**不进入 ②③④④.5**。
    //   ✅ **层序鉴别已实测（2026-10-14，非推断）**：临时把 ④.5 层移到第 ② 层之前并重建 ⇒ 本用例**红**
    //     （实测 6 条 Task）；还原后**绿**。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_12_经济软目标偏向独立排程_但独立排程延期_候选排序不得压过履约()
    {
        // 计划起点 + 200 min：独立排程 B（290）延期 90 min、合批（150）按期。
        var due = Day.AddDays(120).AddMinutes(200);

        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, due),
                new DemandSpec("B", 2, 20m, due)
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3),
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 20m,   // 经济上偏向「独立排程」的 20 件批
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 履约层胜出 ⇒ 合批：3 条 Operation 级 Task、1 个批键、净产出 50（按 Stage 计一次）
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKey = Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));

        // 合批完成 150 min，**不晚于**有效交期（200）；而独立排程的 B 会到 290（延期 90）
        var separateAlternativeEnd = Day.AddDays(120).AddMinutes(290);
        var mergedEnd = result.FinalTasks.Max(t => t.PlannedEndTime);
        Assert.True(mergedEnd <= due, $"合批完成（{mergedEnd:O}）应不晚于有效交期（{due:O}）");
        Assert.True(mergedEnd < separateAlternativeEnd,
            $"合批完成（{mergedEnd:O}）应早于独立排程方案（{separateAlternativeEnd:O}）");
    }

    // ════════════════════════════════════════════════════════════════════
    // M-13（2026-10-14，0号位《再次整改回执正式审核回复》§二 **S1-01 第 4 条**）：
    //   **合批侧经济偏离必须比较「两套完整合法候选方案的对应执行批集合」**
    //
    //   判词逐字：「验收时应检查 `EconomicDeviationForQty` 所比较的是**两套完整合法候选方案的对应执行批
    //   集合**，而非只计算新增需求数量、**遗漏锚批原数量**或其它受影响批」。
    //
    //   夹具（= M-11 同形，`ZeroTailPath` 令 ①②③④ 四层判平）：
    //     · 需求 A（seq 1）= **10**（先排 ⇒ 成锚点批，量 = 10）；
    //     · 需求 B（seq 2）= **30**（后到，尝试并入 A 的 Stage 批）；
    //     · 经济 `PreferredBatchQty = 30`（`Min`/`Max` 均 null）。
    //   ⇒ 两侧「受影响执行批集合」：
    //     · 独立排程候选 = { 锚批保持 10, B 独立成批 30 } ⇒ 偏离 = |10−30| + |30−30| = **20**
    //     · 合批候选     = { 锚批合并为 40 }            ⇒ 偏离 = |40−30|            = **10**
    //     ⇒ **合批严格更优** ⇒ 3 条 Operation 级 Task、1 个批键、净产出 40。
    //
    //   ⚠ **整改前（旧实现）**：独立侧只算 `dev(B 的量 30) = |30−30| = 0`、合批侧 `dev(40) = 10`
    //     ⇒ **独立排程胜出** ⇒ 6 条 Task、2 个批键。⇒ 本用例**翻转**，是「遗漏锚批原数量」的**判别性反证**。
    //   ✅ **「整改前红」已实测（2026-10-14，非推断）**：把独立侧偏离临时还原为
    //     `EconomicDeviationForQty(demand.NetOutputQty, economicPolicy)`（不含锚量）并重建 ⇒ 本用例**红**，
    //     实测 `Expected: 3 / Actual: 6`；还原后**绿**。
    //   ⚠ 本条只主张「同层可比较候选的择优被**完整批集合**的经济偏离改变」；**不**主张经济软参数可压过履约。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_13_合批经济偏离_须比较完整批集合_不得遗漏锚批原数量()
    {
        var request = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            // 唯一变量：经济 Preferred = 30。完整批集合口径下合批偏离（10）< 独立偏离（20）⇒ 合批胜出。
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 30m,
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批胜出：3 条 Operation 级 Task、1 个批键、净产出 40
        Assert.Equal(3, result.FinalTasks.Count);
        var batchKey = Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(result.FinalTasks, t => Assert.Equal(40m, t.Quantity));

        // ── 对照组（唯一变量 = 经济 Preferred **置空**）──
        //   四层判平且经济层恒平 ⇒ 落 ⑤「判平取独立排程」⇒ 6 条 Task、2 个批键
        //   ⇒ 证明本条的翻转**确实**由经济层（完整批集合口径）决定，而非夹具副作用。
        var controlRequest = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });

        var control = await _solver.SolveAsync(controlRequest);

        Assert.True(control.Success, control.ErrorMessage);
        Assert.Empty(control.UnscheduledTasks);
        Assert.Equal(6, control.FinalTasks.Count);
        Assert.Equal(2, control.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());
    }

    // ════════════════════════════════════════════════════════════════════
    // M-14（2026-10-14，0号位《APS V1｜致1号位综合正式审核回复 v1.1》§二.2）：**多锚点全方案口径**
    //   判词逐字：「`r13685`的锚点循环在逐个比较时，当前擂主`bestMergeEconDev`只保存被合并锚点的新批偏离；
    //     当新锚点挑战成功时，新挑战者也只保存自身新批偏离，**其余没有合并的原有合法锚批没有被放到双方
    //     同一完整方案基准内**。因此M_13证明了'单锚完整'，不能证明'多锚全方案完整'。」
    //
    //   夹具 = 两个合法锚点（A、B **各自独立**成批）+ 一个待合需求 C，冻结四层**全部判平**，
    //   使择优**唯一地**落到第 ④.5 层（经济软偏离）—— 从而直接检验「多锚择优的标尺」。
    //
    //   为什么三条 Stage 工时**全为 0**（`ZeroTailPath(..., 0m)`）：
    //     多锚夹具的**必要条件**是两个锚点都在第 ③ 层（更早可行完成）与独立排程**判平**。
    //     若锚批在受约束资源上有非零时长，资源**串行**会使其成立范围收窄到**唯一一个锚点**：
    //       设锚批区间 I_1<…<I_k、本批时长 c。独立排程落在「自 I_i.end 起、长度 ≥ c 的第一个空隙」
    //       ⇒ 独立完工 = I_i.end + c；而合批到 I_j 落在「移除 I_j 后、长度 ≥ d_j+c 的第一个空隙」，
    //       其完工 = I_j.end + c **当且仅当** I_j.start 起的空隙 ≥ d_j + c（等价于 I_j 后空隙 ≥ c）。
    //       两个**不同**区间 I_i≠I_j 不可能同时满足（先者已占掉「首个足够空隙」的位置）
    //       ⇒ 只有**末位锚点**能进入 ④.5 ⇒ 多锚标尺缺口**观察不到**。
    //     工时全 0 ⇒ A、B 两批是**同一时刻的零长区间** ⇒ 第 ③ 层对两个锚点**同时判平**
    //       ⇒ ④.5 首次真正面对**两个合批候选**（这正是本用例要检验的形态）。
    //
    //   其余各层：
    //     · ① 可行：三需求各自可排（下方对照组 `Assert.Equal(9, ...)` 反证独立排程**确实可行**）；
    //     · ② 履约：交期 `Day + 200` 远 ⇒ 两侧延期同为 0；
    //     · ④ Setup：无 `SetupTransitionRules` ⇒ 两侧同为 0；
    //     · ④.5：`PreferredBatchQty = 30`（Min/Max 均 null ⇒ 只比 Preferred 偏离）。
    //
    //   数量：锚点 A=16、锚点 B=34、待合需求 C=4。
    //     · **统一基线增量**口径（正确）：基线 = 「A 16、B 34、C 独立 4」。
    //         C 并入 A ⇒ 仅 A 批被改写 ⇒ Δ = dev(20) − dev(16) − dev(4) = 10 − 14 − 26 = **−30**
    //         C 并入 B ⇒ 仅 B 批被改写 ⇒ Δ = dev(38) − dev(34) − dev(4) =  8 −  4 − 26 = **−22**
    //         ⇒ −30 < −22 ⇒ **并入 A 更好**；
    //         （等价的「完整受影响批集合」口径：并入 A ⇒ {20,34} ⇒ 14；并入 B ⇒ {16,38} ⇒ 22。）
    //     · **只比较被合并锚点的新批**口径（旧实现，跨锚点标尺不一致）：A 的新批 20 ⇒ 10；B 的新批 38 ⇒ 8
    //         ⇒ **错误选中 B**。
    //   ⇒ 旧实现落 **并入 B**（A 批 16 / B∪C 批 38）；整改后落 **并入 A**（A∪C 批 20 / B 批 34）。
    //
    //   ✅ **「整改前红」已实测（2026-10-14，非推断）**：临时把 `anchorEconDev` 还原为**绝对**偏离
    //     `dev(锚量+本批)`、`incumbentEconDev` 还原为 `separateEconDev` 并重建 ⇒ 本用例**红**，
    //     实测 `Expected: [20, 34] / Actual: [16, 38]`（与上文预测逐字一致）；还原后**绿**。
    //   ⚠ 本条只主张「多锚点候选的择优标尺必须统一」；**不**主张经济软参数可压过履约或完成时间。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_14_多锚点经济偏离_须统一基线比较_不得只比被合并锚点的新批()
    {
        var request = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 0m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 16m, Day.AddDays(200)),
                new DemandSpec("B", 2, 34m, Day.AddDays(200)),
                new DemandSpec("C", 3, 4m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            // 唯一变量：经济 Preferred = 30。统一基线口径下并入 A 的增量（−30）优于并入 B（−22）。
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 30m,
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 夹具前提：A、B **各自独立**成批（B 并入 A 的经济增量为 +2 > 0 ⇒ 判平取独立）
        //   ⇒ 处理 C 时**确有两个合法锚批**。若 B 已被并入 A，则本用例的多锚前提不成立。
        var batches = result.FinalTasks
            .GroupBy(t => t.ExecutionBatchDraftKey)
            .Select(g => g.Select(t => t.Quantity).Distinct().ToList())
            .ToList();

        Assert.Equal(2, batches.Count);
        Assert.All(batches, q => Assert.Single(q));            // 批内数量一致（执行批语义）
        Assert.Equal(6, result.FinalTasks.Count);

        // 多锚择优：**并入 A** ⇒ 批集合 {20, 34}
        //   （旧实现错选并入 B ⇒ {16, 38} ⇒ 本断言**红**）
        Assert.Equal(
            new List<decimal> { 20m, 34m },
            batches.Select(q => q[0]).OrderBy(x => x).ToList());

        // 份额闭合：A16 / B34 / C4 各**恰好一次**，合计 54
        Assert.Equal(16m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(34m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(4m, result.AllocationShares.Where(s => s.AllocationSequence == 3).Sum(s => s.ComponentQty));
        Assert.Equal(54m, result.AllocationShares.Sum(s => s.ComponentQty));

        // ── 对照组（唯一变量 = 经济 Preferred **置空**）──
        //   四层判平且经济层恒平 ⇒ 落 ⑤「判平取独立排程」⇒ **不产生任何合批** ⇒ 9 条 Task、3 个批键。
        //   该断言同时**反证夹具前提**：若独立排程不可行，第 ① 层会强制合批 ⇒ 此处不会出现 3 个批键。
        var controlRequest = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 0m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 16m, Day.AddDays(200)),
                new DemandSpec("B", 2, 34m, Day.AddDays(200)),
                new DemandSpec("C", 3, 4m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });

        var control = await _solver.SolveAsync(controlRequest);

        Assert.True(control.Success, control.ErrorMessage);
        Assert.Empty(control.UnscheduledTasks);
        Assert.Equal(9, control.FinalTasks.Count);
        Assert.Equal(3, control.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct().Count());
    }

    // ════════════════════════════════════════════════════════════════════
    // M-15（2026-10-14，0号位《APS V1｜致1号位综合正式审核回复 v1.1》§一.3.2 / §一.3.4）：**R-2 ④.6 资源效率层**
    //
    //   本用例锁的是**留痕契约**（Solver 级，非纯函数级）：
    //     · 合批被采纳 ⇒ 必须留下 `RESOURCE_EFFICIENCY_*` 追溯（§一.3.4：指标相同也**不得**沉默略过）；
    //     · 未采纳合批 ⇒ **不得**写 ④.6 留痕（锁死「不是无条件乱写 trace」）。
    //
    //   ⚠ 本用例**不**声称「trace 的出现证明 ④.6 被咨询过」—— 留痕在**合批被采纳**后无条件写，
    //     与 ④.6 是否最终定胜负无关（本夹具实际由 ④.5 决定）。**可达性**另有实测证据：
    //
    //   ✅ **可达性反证（已实测，2026-10-14，非推断）**：把锚点循环传给 ④.6 的两个值临时改为
    //     `(0, −1000)`（强制偏合批）⇒ **M_11 / M_13 / M_14 三条同时变红**
    //     （`失败: 3，通过: 886，总计: 889`）⇒ 证明本层在这些夹具里**真的被咨询**、且**能**改变最终结果；
    //     还原为真实增量后三条复绿 ⇒ 真实增量在此形态下**确实判平**（零回归的正面证据）。
    //
    //   ⚠ **R2-E 更正（2026-10-14，第五轮复核 §三 R2-E；旧注已作废）**：本文件旧注
    //     「现有夹具族下合批与独立排程的增量**恒为 0**、构造不出 ④.6 单独改变 Solver 级择优的夹具」
    //     **过于宽泛**。正确口径：`Δ ≡ 0` 的**前提**是两侧候选落在**同一台设备（同 CapacityFactor）**；
    //     一旦存在**合法替代设备且 CapacityFactor 不同**，`Δ ≠ 0` 且 ④.6 **能**在 Solver 级自然定胜负
    //     ⇒ 见 `M_17`（正例：④.6 自然决定采纳合批）与 `M_18`（反例：Δ≠0 但被 ③ 层短路 ⇒ 不得记 ④.6 功）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_15_第46层留痕语义_45先分胜负者不得写_到达且判平者必写()
    {
        // ── 情形①（R2-C 反证「**④.5 先分胜负但效率也有差异**」）──
        //   经济 `PreferredBatchQty = 30` ⇒ 并入后恰 30（偏离 0）、独立排程偏离 20 ⇒ ④.5 **先决出**合批。
        //   ⇒ 比较器**落点 = ④.5** ⇒ ④.6 **根本没被执行** ⇒ **不得**写 ④.6 痕。
        //   （旧实现只要「合批被采纳」就写 ⇒ 把 ④.5 的功劳记成「资源效率改变择优」⇒ 本条**红**。）
        var econRequest = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 30m,
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var econ = await _solver.SolveAsync(econRequest);

        Assert.True(econ.Success, econ.ErrorMessage);
        Assert.Empty(econ.UnscheduledTasks);

        // 前提：本夹具**确实**走了合批（否则下面的「不得写」断言会因「没合批」而平凡通过）。
        Assert.Equal(3, econ.FinalTasks.Count);
        Assert.Single(econ.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());

        Assert.DoesNotContain(econ.SolveTraceNotes,
            n => n.ReasonCode.StartsWith("RESOURCE_EFFICIENCY_", StringComparison.Ordinal));

        // ── 情形②（R2-C 反证「**两侧指标相同**」）──
        //   唯一变量 = 经济策略置空（`TestBatchPolicy.Permissive`）⇒ ①②③④④.5 **全判平** ⇒
        //   比较器**落点 = ④.6**（**到达**且**判平**）⇒ **必写**一条 `NO_INCREMENT`，且 `Key` 可追溯。
        //   注意：本情形合批**未被采纳**（⑤ 判平取独立排程），但 ④.6 **确实被评价过** ——
        //   这正是 R2-C 要求区分的「到达④.6」与「④.6决定采纳」两个事实。
        var tieRequest = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });

        var tie = await _solver.SolveAsync(tieRequest);

        Assert.True(tie.Success, tie.ErrorMessage);
        Assert.Empty(tie.UnscheduledTasks);
        Assert.Equal(6, tie.FinalTasks.Count);      // ⑤ 判平取独立排程

        var r2 = tie.SolveTraceNotes
            .Where(n => n.ReasonCode.StartsWith("RESOURCE_EFFICIENCY_", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(r2, n => n.ReasonCode == "RESOURCE_EFFICIENCY_NO_INCREMENT");
        Assert.Contains(r2, n => n.Message.Contains("指标相同／无增量差异"));
        // 可定位：追溯键非空（不得是匿名声）。
        Assert.All(r2, n => Assert.False(string.IsNullOrEmpty(n.Key)));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-16（0号位 2026-10-14《致1号位第四轮整改回执正式复核意见 v1.0》§3.6）：
    //   **正工时、无注入极值的 Solver 级候选对照** —— 用来**实证** ④.6 增量在现行工时模型下恒为 0。
    //
    //   夹具与 M_15 同形，但**唯一变量**是「正工时 + 非整十数量」：
    //     · 单位工时 **3 min/件**（`ZeroTailPath(..., 3m)`）⇒ OP10 工时 = 3 × 数量（**正**）；
    //     · 数量 **A = 7 / B = 53**（差异大、非整十，避开极值注入）；
    //     · 交期 `Day + 200` 远 ⇒ ② 判平；无 `SetupTransitionRules` ⇒ ④ 判平；
    //     · 经济策略**置空**（`TestBatchPolicy.Permissive`）⇒ ④.5 判平 ⇒ 比较器**落点 = ④.6**。
    //
    //   ⇒ 代数结论：`O = Setup + Σ(StandardDuration × PlannedProcessQty ÷ CapacityFactor)` 对数量**线性**，
    //     且**两侧候选落在同一台设备（同 CapacityFactor）** ⇒
    //     `Δ = O(锚+本) − O(锚) − O(本) = (180) − (21) − (159) = **0**`。
    //   ⇒ ④.6 **到达且判平** ⇒ 留痕记 `NO_INCREMENT`；若增量非 0 会记 `INCREMENT` ⇒ **实测 `NO_INCREMENT`**。
    //
    //   ⚠ **R2-E 更正**：本用例**不**主张「④.6 在现行工时模型下结构性惰性」。正确口径是
    //     「**同资源 ⇒ Δ ≡ 0**」这一**充分**条件；一旦存在**合法替代设备且 CapacityFactor 不同**，
    //     `Δ ≠ 0` 且 ④.6 **能**自然定胜负 —— 反例见 `M_17`。**不降低 R-2 目标**：本层保留且有效。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_16_正工时下_第46层增量恒为0_须记无增量差异()
    {
        var request = Build(
            paths: new[] { ZeroTailPath("RT", 1, 1, 2, 3, 3m) },     // 3 min/件（**正工时**）
            demands: new[]
            {
                new DemandSpec("A", 1, 7m, Day.AddDays(200)),
                new DemandSpec("B", 2, 53m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ④.6 判平 ⇒ ⑤ 取独立排程（合批**未被采纳**，但 ④.6 **确实被评价过**）。
        Assert.Equal(6, result.FinalTasks.Count);

        var r2 = result.SolveTraceNotes
            .Where(n => n.ReasonCode.StartsWith("RESOURCE_EFFICIENCY_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(r2);

        // **实证**：同资源正工时下增量仍为 0 ⇒ 记「无增量差异」，**不得**出现 `INCREMENT`。
        Assert.Contains(r2, n => n.ReasonCode == "RESOURCE_EFFICIENCY_NO_INCREMENT");
        Assert.DoesNotContain(r2, n => n.ReasonCode == "RESOURCE_EFFICIENCY_INCREMENT");
        Assert.Contains(r2, n => n.Message.Contains("指标相同／无增量差异"));

        // §3.7：留痕**身份可追溯**（`Key` 非空，不得是匿名声）。
        Assert.All(r2, n => Assert.False(string.IsNullOrEmpty(n.Key)));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-17（2026-10-14，第五轮复核 §三 **R2-B / R2-E**）：**Solver 级正工时反例 —— ④.6 自然决定采纳合批**
    //
    //   R2-E 判词逐字：「不能推出跨资源/不同CapacityFactor时总占用增量恒零」；
    //   R2-B 判词逐字：「为Solver落痕调用点补"合批省60、独立省60"双向判别，**验证实际胜者和文案一致**，
    //     不以另一个独立调用`Describe`的RE测试替代。不得为使断言变绿而倒换业务胜负含义。」
    //
    //   夹具（**正工时、无注入极值、全部硬合法**）：
    //     · 路径 `RT/1`：`OP10@STAGE1` 在 **R1**（SD = 1 min/件）；`OP20@STAGE2` 有**两台合法设备** ——
    //       首选 **R2**（`Priority=1, CF=1`）、备选 **R3**（`Priority=2, CF=1.5`）；
    //     · 日历：R1 `[Day, +60min]`、R2 `[Day, +50min]`、R3 `[Day, +60min]`；
    //     · 需求 A = 20 件（seq 1）、B = 10 件（seq 2），交期 `Day+200` 远；无 Setup 规则；经济策略 Permissive。
    //
    //   实测时间线：
    //     · 锚批 A：`OP10 [0,20]@R1`；`OP20 [20,40]@R2` ⇒ `O(锚) = 20 + 20 = 40`；
    //     · 独立排程 B：`OP10 [20,30]@R1`；`OP20 [40,50]@R2`（R2 被锚批占去 `[20,40]`，余段恰 10min）
    //       ⇒ `O(本) = 10 + 10 = 20`，`separateEnd = Day+50`；
    //     · 合批 30 件：`OP10 [0,30]@R1`；`OP20` 需 30min，R2 从 30 起放不下（窗止于 +50）
    //       ⇒ **落到备选设备 R3**，`30 ÷ 1.5 = 20min` ⇒ `[30,50]@R3` ⇒ `O(锚+本) = 30 + 20 = 50`，
    //       `mergeEnd = Day+50`。
    //
    //   ⇒ ③ 判平（`separateEnd == mergeEnd`）、④ 判平（无 Setup）、④.5 判平（Permissive）
    //     ⇒ 比较器**落点 = ④.6**，`Δ = 50 − 40 − 20 = **−10 < 0**` ⇒ **④.6 单独决定采纳合批**。
    //   ⇒ 本条即 R2-B 要求的「**实际胜者与文案一致**」Solver 级判别：胜者 = 合批 ⇒ 文案**必须**写「合批」；
    //     若方向反了（旧 `bestMergeNonSetupIncrement.CompareTo(0m)` 传入 `−1`），文案会写成「独立排程」⇒ 本条**红**。
    //   ⇒ 同时回答 R2-E 的「**报告被哪层短路、是否确实可达到 ④.6**」：本夹具**未被短路**，④.6 真实到达并定胜负。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_17_不同CapacityFactor合法替代设备_第46层自然决定采纳合批_文案须与胜者一致()
    {
        var request = Build(
            paths: new[] { DivergentPath("RT", 1, 1, 1m, primaryResourceId: 2, altResourceId: 3, altCapacityFactor: 1.5m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 20m, Day.AddDays(200)),
                new DemandSpec("B", 2, 10m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(60)),
                (2, Day, Day.AddMinutes(50)),
                (3, Day, Day.AddMinutes(60))
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批被采纳 ⇒ 一个执行批、完整 2 道工序链、30 件（净产出按 Stage 计一次）。
        Assert.Equal(2, result.FinalTasks.Count);
        Assert.Single(result.FinalTasks.Select(t => t.ExecutionBatchDraftKey).Distinct());
        Assert.All(result.FinalTasks, t => Assert.Equal(30m, t.Quantity));

        // 反证夹具前提：合批后 `OP20` **确实**落到备选设备 R3（不同 CapacityFactor）⇒ Δ 才可能 ≠ 0。
        Assert.Equal(1, result.FinalTasks.Single(t => t.OperationCode == "OP10").ResourceId);
        Assert.Equal(3, result.FinalTasks.Single(t => t.OperationCode == "OP20").ResourceId);

        // 份额闭合：A 20 / B 10，合计 30。
        Assert.Equal(20m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(10m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(30m, result.AllocationShares.Sum(s => s.ComponentQty));

        // ④.6 **到达且定胜负** ⇒ 必写 `INCREMENT`，且**文案与胜者一致**（写「合批」而非「独立排程」）。
        var increments = result.SolveTraceNotes
            .Where(n => n.ReasonCode == "RESOURCE_EFFICIENCY_INCREMENT")
            .ToList();
        var note = Assert.Single(increments);
        Assert.False(string.IsNullOrEmpty(note.Key));
        Assert.Contains("合批", note.Message);
        Assert.Contains("少占 10 分钟设备时间", note.Message);
        Assert.DoesNotContain("独立排程", note.Message);
        // 未评价/判平的码**不得**同时出现（同一需求只落一条 ④.6 痕）。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "RESOURCE_EFFICIENCY_NO_INCREMENT"
              || n.ReasonCode == "RESOURCE_EFFICIENCY_DATA_MISSING");
    }

    // ════════════════════════════════════════════════════════════════════
    // M-18（2026-10-14，第五轮复核 §三 **R2-C**）：**Δ ≠ 0 但被 ③ 层短路 ⇒ 不得记 ④.6 的功**
    //
    //   R2-C 判词逐字：「区分`该指标存在差异`、`比较器到达④.6层`和`④.6实际决定采纳方案`三个事实；
    //     只有最后一种才能使用"increment/因效率择优"含义」。
    //
    //   夹具 = **M-17 逐字**，**唯一变量** = 备选设备 R3 的 `CapacityFactor` 由 `1.5` 改为 `2.0`：
    //     · 合批后 `OP20` 在 R3 = `30 ÷ 2 = 15min` ⇒ `[30,45]` ⇒ `mergeEnd = Day+45`；
    //     · 独立排程 `separateEnd = Day+50` ⇒ **③ 更早可行完成**先决出合批 ⇒ **④.6 根本没被执行**。
    //   ⇒ 尽管 `Δ = 45 − 40 − 20 = −15 ≠ 0`（效率**确有**差异），**不得**写 `INCREMENT`
    //     （旧实现只要「合批被采纳」就写 ⇒ 把 ③ 的功劳记成「资源效率改变择优」⇒ 本条**红**）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_18_效率确有差异但被第3层短路_不得记第46层的功()
    {
        var request = Build(
            paths: new[] { DivergentPath("RT", 1, 1, 1m, primaryResourceId: 2, altResourceId: 3, altCapacityFactor: 2.0m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 20m, Day.AddDays(200)),
                new DemandSpec("B", 2, 10m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(60)),
                (2, Day, Day.AddMinutes(50)),
                (3, Day, Day.AddMinutes(60))
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 合批仍被采纳（但由 ③ 决定）⇒ 2 道工序链、`OP20` 在备选设备 R3。
        Assert.Equal(2, result.FinalTasks.Count);
        Assert.Equal(3, result.FinalTasks.Single(t => t.OperationCode == "OP20").ResourceId);

        // **核心断言**：比较器落点 = ③ ⇒ ④.6 **未被评价** ⇒ 不得出现**任何** ④.6 追溯码。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode.StartsWith("RESOURCE_EFFICIENCY_", StringComparison.Ordinal));
    }

    // ════════════════════════════════════════════════════════════════════
    // M-19（2026-10-14，第五轮复核 §6.2 事项3 **S1-01 正工时闭合**）：
    //   判词逐字：「在保留M_14多锚反向数学反证之外，补一个**正工时、合法Calendar、多锚**且检查
    //     **BatchKey/各需求份额/净产出不重不漏**的完整Solver回归；`M_16`只记录"正工时线性零增量"，
    //     不代替批身份验收。」
    //
    //   与 `M_14`（`ZeroTailPath(..., 0m)`，**零工时**）的**唯一差别** = 首工序单位工时 **1 min/件**（**正工时**），
    //   并把 `OP10` 的第二台合法设备引入以形成**真实多锚**：
    //     · `OP10@STAGE1` 两台合法设备 —— **R1**（`Priority=1`，日历**恰 16min**）/ **R2**（`Priority=2`，长窗）；
    //     · `OP20@STAGE2` 在 R3（0 min）、`OP30@STAGE3` 在 R4（0 min）⇒ 末工序完成 = `OP10` 完成（③ 可比）；
    //     · 需求 A=16（seq1）/ B=34（seq2）/ C=4（seq3），合法日历窗，交期 `Day+200` 远。
    //
    //   实测时间线（**正工时**）：
    //     · A：`OP10 [0,16]@R1`（R1 窗恰 16min ⇒ **占满**）；
    //     · B：R1 无余量 ⇒ 落 R2 ⇒ `OP10 [0,34]@R2` ⇒ **A、B 各自独立成批（多锚成立）**；
    //     · C：R1 仍满 ⇒ R2 续排 ⇒ `OP10 [34,38]@R2`，`separateEnd = Day+38`；
    //     · C 并入 **A**：合批 20min 在 R1 的 16min 窗内**放不下** ⇒ ① 层不可行；
    //     · C 并入 **B**：合批 38min 从 Day 起 ⇒ `[0,38]@R2` ⇒ `mergeEnd = Day+38` = 独立侧 ⇒ ③ 判平；
    //       ④ 判平（无换型规则）；④.5 由 `PreferredBatchQty = 38` 判合批（增量 `(−38, 0)` < 基线 `(0,0)`）。
    //   ⇒ 终态 = **2 个执行批**（A=16 / B+C=38），6 条 Task。
    //
    //   本用例锁的是 **批身份 / 份额 / 净产出「不重不漏」**（0号位 §2.2 逐字保留项）：
    //     ① 每批一个 `ExecutionBatchDraftKey`，批内数量一致（执行批语义）；
    //     ② `Σ 各批净产出 = 54`（**按 Stage 计一次**，不得 108/162）；
    //     ③ 各需求份额 A=16 / B=34 / C=4 **各恰好一次**，`Σ = 54`；
    //     ④ `StageExecutionBatchDraftKey` 按 **(执行批 × Stage)** 唯一 ⇒ 2 批 × 3 Stage = **6 个互不相同**
    //        （同一执行批内 3 个 Stage 不同；不同执行批的**同 Stage** 亦不同 ⇒ MES 工单不跨 Stage、不跨批）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_19_正工时多锚_批身份份额与净产出须不重不漏()
    {
        var request = Build(
            paths: new[] { DivergentZeroTailPath("RT", 1, firstResourceId: 1, firstAltResourceId: 2, 1m, 3, 4) },
            demands: new[]
            {
                new DemandSpec("A", 1, 16m, Day.AddDays(200)),
                new DemandSpec("B", 2, 34m, Day.AddDays(200)),
                new DemandSpec("C", 3, 4m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(16)),      // R1：恰容 A=16（正工时，占满）
                (2, Day, Day.AddMinutes(200)),     // R2：OP10 的第二台合法设备
                (3, Day, Day.AddMinutes(200)),     // R3：OP20（0 min）
                (4, Day, Day.AddMinutes(200))      // R4：OP30（0 min）
            },
            batchPolicies: new[]
            {
                new BatchPolicyRuleSnapshot
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    PreferredBatchQty = 38m,       // B+C ⇒ ④.5 判合批（并入 B）
                    AllowMerge = true,
                    AllowSplit = false
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ── ① 批身份：2 个执行批（A / B+C），每批 3 道工序、批内数量一致 ──
        var batches = result.FinalTasks
            .GroupBy(t => t.ExecutionBatchDraftKey)
            .ToList();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, g => Assert.Equal(3, g.Count()));
        Assert.All(batches, g => Assert.Single(g.Select(t => t.Quantity).Distinct()));
        Assert.All(batches, g => Assert.False(string.IsNullOrEmpty(g.Key)));

        // ── ② 净产出按 Stage **计一次**：各批净产出之和 = 54（不得 108 / 162）──
        var netOutput = batches.Sum(g => g.First().Quantity);
        Assert.Equal(54m, netOutput);
        Assert.Equal(
            new List<decimal> { 16m, 38m },
            batches.Select(g => g.First().Quantity).OrderBy(x => x).ToList());

        // ── ③ 各需求份额 **各恰好一次**，合计 54（不重不漏）──
        Assert.Equal(16m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(34m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(4m, result.AllocationShares.Where(s => s.AllocationSequence == 3).Sum(s => s.ComponentQty));
        Assert.Equal(54m, result.AllocationShares.Sum(s => s.ComponentQty));

        // ── ③′ 三方互证（**不重不漏**的实质）：`BatchKey ↔ 份额 ↔ 净产出` 必须闭合 ──
        //   `AllocationTaskShare.FinalDraftId` → Task → `ExecutionBatchDraftKey` 把每条份额挂到执行批上：
        //     · 每条份额**必落在一个且仅一个**执行批内（不悬空、不跨批）；
        //     · 批内份额之和 == 该批净产出（份额账本与批身份互证，任一错位本条即红）；
        //     · 每个需求恰好一条份额行（`AllowSplit=false`）。
        var taskBatch = result.FinalTasks.ToDictionary(t => t.FinalDraftId, t => t.ExecutionBatchDraftKey!);
        Assert.All(result.AllocationShares,
            s => Assert.True(taskBatch.ContainsKey(s.FinalDraftId),
                $"份额 FinalDraftId={s.FinalDraftId} 未落到任何执行批 Task 上（悬空份额）"));

        var shareByBatch = result.AllocationShares
            .GroupBy(s => taskBatch[s.FinalDraftId])
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ComponentQty));
        Assert.Equal(2, shareByBatch.Count);
        Assert.All(batches, g => Assert.Equal(g.First().Quantity, shareByBatch[g.Key]));
        Assert.Equal(54m, shareByBatch.Values.Sum());

        Assert.Single(result.AllocationShares.Where(s => s.AllocationSequence == 1));
        Assert.Single(result.AllocationShares.Where(s => s.AllocationSequence == 2));
        Assert.Single(result.AllocationShares.Where(s => s.AllocationSequence == 3));

        // ── ③″ **多锚**证据（自输出读出，非声明）：A 独占一批（份额 16）、B 与 C 同批（34 + 4 = 38）──
        //   ⇒ 处理 C 时资源上**同时存在** A、B 两个候选锚批（`TryMergeDemandIntoStageBatch` 逐锚择优）；
        //     本夹具选 B 而非 A 的原因 = A 所在 R1 的 16min 日历窗容不下合批后的 20min（① 层不可行）。
        var aBatchKey = shareByBatch.Single(kv => kv.Value == 16m).Key;
        Assert.Equal(1, result.AllocationShares.Count(s => s.AllocationSequence == 1 && taskBatch[s.FinalDraftId] == aBatchKey));
        Assert.DoesNotContain(result.AllocationShares,
            s => s.AllocationSequence != 1 && taskBatch[s.FinalDraftId] == aBatchKey);

        // ── ④ 跨 Stage 不得共用一个 MES 工单：`StageExecutionBatchDraftKey` 按 **(执行批 × Stage)** 唯一 ──
        //   2 个执行批 × 3 个 Stage = 6 个互不相同的 Stage 批身份（与 M_01 的 1×3=3 同口径）：
        //     · 同一执行批内 STAGE1/2/3 互不相同（MES 工单不跨 Stage）；
        //     · 不同执行批的**同 Stage** 亦不相同（不得把 A 与 B+C 的 STAGE1 并成一个 MES 工单）。
        var stageKeys = result.FinalTasks.Select(t => t.StageExecutionBatchDraftKey).ToList();
        Assert.All(stageKeys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Equal(6, stageKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(batches, g => Assert.Equal(
            3, g.Select(t => t.StageExecutionBatchDraftKey).Distinct(StringComparer.Ordinal).Count()));

        // ── 反证夹具前提：**正工时**真实存在（`OP10` 占用 = 数量 × 1min），且合批**确已发生** ──
        var op10Batches = result.FinalTasks
            .Where(t => t.OperationCode == "OP10")
            .Select(t => (t.Quantity, Occupied: (decimal)(t.PlannedEndTime - t.PlannedStartTime).TotalMinutes))
            .ToList();
        Assert.All(op10Batches, b => Assert.Equal(b.Quantity, b.Occupied));
        Assert.Contains(op10Batches, b => b.Quantity == 38m && b.Occupied == 38m);
    }

    // ════════════════════════════════════════════════════════════════════
    // M-20（2026-10-14，0号位 第六轮审核 §3.2 / §七.2 **R2-C 多锚追溯**）
    //
    //   判词逐字（§3.2）：「**最小整改**：保存决定最终采纳的那次比较的**两个真实对手（含锚点身份和比较时
    //     的资源占用键）**、层身份与胜出方向；若另需展示"相对独立基线节时"，可在同一说明中作为另一指标
    //     明确标注，**不得把相对基线的描述冒充胜负原因**。」
    //   判词逐字（§七.2）：「补"**独立方案→合批A→合批B**"的合法竞争反例；保存**最终实际比较对**，
    //     不要用相对独立基线的符号替代 B 相对 A 的胜负原因，不得用循环最后的锚点充当全局决策依据。」
    //   判词逐字（§3.3）：「最低限度区分『最终被选方案的**主决定层**』和『本轮**实际进行过的 ④.6 比较**』
    //     两个事实。」
    //
    //   夹具（**两个 Anchor 相互竞争，且擂主本身已是合批方案** —— 这正是判词所指的「B 相对在位擂主 A
    //     的那次比较」）：
    //     · 路径 `MultiAltPath(RT,1)`：`OP10@STAGE1` 在 R1（CF 1，1 min/件）；
    //       `OP20@STAGE2` 首选 R2（CF 1），替代 R3（**CF 2**）、R4（CF 2，本轮**未被使用**）；
    //     · 日历：R1 400 / R2 **100** / R3 400 / R4 400；
    //     · 需求 A=10(seq1)、B=30(seq2)、C=30(seq3)，交期远（延期恒 0）。
    //     ⇒ **关键结构**：`R2` 的窗恰为 100 分钟，容得下**各自独立**的三条链，却容不下**任一合批链**
    //       ⇒ 两个合批候选都被迫落到 **R3（CF 2）**：既保证 ③ 判平，又使合批**释放产能**（④.6 < 0）。
    //
    //   实测时间线（**由本夹具实跑读出并逐条核算，非推断**）：
    //     ① A：`OP10 [0,10]@R1`、`OP20 [10,20]@R2` ⇒ `O(A)=10+10=20`；
    //     ② B：`OP10 [10,40]@R1`、`OP20 [40,70]@R2` ⇒ `O(B)=30+30=60`
    //        （B 未并入 A：合批 B 需 `OP20` 40 分钟，R2 余段 `[70,100]` 只有 30 ⇒ 落 R3 ⇒ 完成更晚 ⇒ ③ 否决）；
    //     ③ C 到达，候选锚 = **A、B（多锚）**：
    //        · 独立 C ⇒ `OP10 [40,70]@R1`、`OP20 [70,100]@R2` ⇒ 完成 **100**，`O(C)=30+30=60`；
    //        · **并入 A**（A+C=40 件）⇒ `OP10 [40,80]@R1`；R2 余段 30 < 40 ⇒ 落 **R3(CF 2)**
    //          ⇒ `OP20 [80,100]` ⇒ 完成 **100** ⇒ ③ **判平**；⇒ ④.6：增量 `60 − 20 − 60` = **−20** < 0（基线 0）
    //          ⇒ **A 合批胜出，成为擂主**（第 1 次比较，到达 ④.6）；
    //        · **并入 B**（B+C=60 件）⇒ `OP10 [10,70]@R1`；R2 余段 `[20,100]` 不够 >100 ⇒ 落 **R3(CF 2)**
    //          ⇒ `OP20 [70,100]` ⇒ 完成 **100** ⇒ ③ **判平**；⇒ ④.6：增量 `90 − 60 − 60` = **−30**
    //          ⇒ 与**擂主 A**（−20）相比差值 = `−30 − (−20)` = **−10** < 0 ⇒ **合批到 B 胜**（第 2 次比较，到达 ④.6）。
    //     ⇒ 终态 = 2 个执行批（**A=10** / **B+C=60**），4 条 Task；`A.OP20` 留 R2，`B+C.OP20` 落 **R3**。
    //
    //   本条锁的**核心事实**（改前实现按「挑战者相对独立基线」写原因 ⇒ 本条对旧实现**红**）：
    //     ⓐ 留痕必须写出**两个真实对手**：`擂主锚#0[...]` 与 `合批到锚#1[...]`（而非「独立排程／合批」）；
    //     ⓑ 胜负依据必须是**两侧 ④.6 增量之差** `−30 − (−20) = −10`，
    //        **不是**挑战者相对统一基线的 **−30**（旧实现正是把这个数当成了胜负原因）；
    //     ⓒ 排序键差必须按**两份完整候选方案**给：`120 − 110 = 10` 分钟（>0 表示擂主侧少占，与判词同向）；
    //     ⓓ 相对统一基线的增量只能作为**另标签**出现，且须明示「非本条胜负依据」；
    //     ⓔ §3.3 两事实分离：**本轮共 2 次比较到达 ④.6**（采纳 A 的那次 + 采纳 B 的那次），
    //        而本条只代表**决定最终采纳（合批）方案的那一次**。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_20_R2C多锚_合批B胜合批A_留痕须记A与B的真实胜负而非相对基线增量()
    {
        var request = Build(
            paths: new[] { MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m)) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200)),
                new DemandSpec("C", 3, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(400)),    // R1：OP10（CF 1）
                (2, Day, Day.AddMinutes(100)),    // R2：OP20 首选（CF 1）—— **恰容三条独立链**，不容任一合批链
                (3, Day, Day.AddMinutes(400)),    // R3：OP20 替代（**CF 2**）—— 两个合批候选都落在这里
                (4, Day, Day.AddMinutes(400))     // R4：OP20 另一备选（CF 2）—— 本轮未被使用
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ── 终态：C **并入 B**（而非并入 A）⇒ 2 个执行批（A=10 / B+C=60）──
        var batches = result.FinalTasks.GroupBy(t => t.ExecutionBatchDraftKey).ToList();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, g => Assert.Equal(2, g.Count()));                 // 每批 2 道工序
        Assert.Equal(
            new List<decimal> { 10m, 60m },
            batches.Select(g => g.First().Quantity).OrderBy(x => x).ToList());

        var mergedBatchKey = batches.Single(g => g.First().Quantity == 60m).Key;
        var soloBatchKey = batches.Single(g => g.First().Quantity == 10m).Key;
        var taskBatch = result.FinalTasks.ToDictionary(t => t.FinalDraftId, t => t.ExecutionBatchDraftKey!);

        // 份额账本：A=10 / B=30 / C=30，合计 70（不重不漏）
        Assert.Equal(10m, result.AllocationShares.Where(s => s.AllocationSequence == 1).Sum(s => s.ComponentQty));
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 2).Sum(s => s.ComponentQty));
        Assert.Equal(30m, result.AllocationShares.Where(s => s.AllocationSequence == 3).Sum(s => s.ComponentQty));
        Assert.Equal(70m, result.AllocationShares.Sum(s => s.ComponentQty));

        // 关键归属：A（seq1）独占 10 批；**B(seq2) 与 C(seq3) 同落** 60 批（= 30 + 30）⇒ 采纳的是「并入 B」。
        Assert.All(result.AllocationShares.Where(s => s.AllocationSequence == 1),
            s => Assert.Equal(soloBatchKey, taskBatch[s.FinalDraftId]));
        Assert.All(result.AllocationShares.Where(s => s.AllocationSequence == 2 || s.AllocationSequence == 3),
            s => Assert.Equal(mergedBatchKey, taskBatch[s.FinalDraftId]));

        // ── 反证夹具前提（`−delta` 的来源）：**独立链走 R2（CF 1）／合批链被迫走 R3（CF 2）** ──
        Assert.Equal(2, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 10m).ResourceId);
        Assert.Equal(3, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 60m).ResourceId);

        // ── 核心断言：④.6 **决定最终采纳方案** ⇒ 单条 `INCREMENT`，且写明两个**真实对手** ──
        var increments = result.SolveTraceNotes
            .Where(n => n.ReasonCode == "RESOURCE_EFFICIENCY_INCREMENT")
            .ToList();
        var note = Assert.Single(increments);
        Assert.False(string.IsNullOrEmpty(note.Key));

        // ⓐ 两个对手的**锚点身份**（含比较时的批键）——不得只见「合批／独立排程」这种无身份措辞。
        Assert.Contains("擂主=擂主锚#0[", note.Message);
        Assert.Contains("挑战者=合批到锚#1[", note.Message);
        Assert.DoesNotContain("独立排程", note.Message);
        Assert.DoesNotContain("擂主=统一基线", note.Message);   // 擂主**必须**是上一轮的合批方案，不是基线

        // ⓑ 胜负依据 = **两侧 ④.6 增量之差**（−30 − (−20) = −10），**不是**挑战者相对统一基线的 −30。
        Assert.Contains("（④.6 增量 -20 分钟）", note.Message);   // 擂主 A 侧
        Assert.Contains("（④.6 增量 -30 分钟）", note.Message);   // 挑战者 B 侧
        Assert.Contains("差值 -10 分钟", note.Message);
        Assert.Contains("挑战者（合批到锚#1[", note.Message);
        Assert.Contains("胜出 ⇒ **合批释放产能**", note.Message);
        Assert.Contains("决定层=④.6资源效率", note.Message);
        Assert.Contains("占用键（两侧并集）=[", note.Message);     // §3.2：须含比较时的资源占用键

        // ⓒ 排序键差按**两份完整候选方案**：`120 − 110 = 10` 分钟（>0 = 擂主侧少占，与判词同向）。
        Assert.Contains("（110 < 120 分钟）", note.Message);
        Assert.Contains("少占 10 分钟设备时间", note.Message);
        Assert.DoesNotContain("少占 30 分钟设备时间", note.Message);  // 旧实现会把这个「相对基线」的数当胜负原因

        // ⓓ §3.3 两事实分离：本轮 **2** 次比较到达 ④.6（采纳 A 的那次 + 采纳 B 的那次），
        //    本条只代表**决定最终采纳方案的那一次**。
        Assert.Contains("本轮共 2 次比较到达 ④.6", note.Message);
        Assert.Contains("本条代表**决定最终采纳（合批）方案**的那一次比较", note.Message);

        // ⓔ 相对统一基线的增量只作为**另标签**出现，且明示「非本条胜负依据」。
        Assert.Contains("另计（**非本条胜负依据**）", note.Message);
        Assert.Contains("擂主侧 − 挑战者侧排序键 = 10 分钟", note.Message);

        // 同一需求不得同时落判平/缺项码。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "RESOURCE_EFFICIENCY_NO_INCREMENT"
              || n.ReasonCode == "RESOURCE_EFFICIENCY_DATA_MISSING"
              || n.ReasonCode == "RESOURCE_EFFICIENCY_EVALUATED_NOT_DECISIVE");
    }

    // ════════════════════════════════════════════════════════════════════
    // M-21（2026-10-14，0号位 第六轮审核 §3.3 **R2-C 第二事实**）
    //
    //   判词逐字（§3.3）：「最低限度区分『最终被选方案的**主决定层**』和『本轮**实际进行过的 ④.6 比较**』
    //     两个事实」—— 即：④.6 **被执行过**但**没有**决定采纳方案时，不得沉默略过，也不得反过来
    //     由「最终记的是 ③」被读成「④.6 没生效」。
    //
    //   夹具 = **M-20 同族**，**唯一变量** = 需求 B 的量 `30 → 20`（⇒ A=10 / B=20 / C=30）：
    //     · 「并入 A」（A+C=40 件）：`OP10 [30,70]@R1`；R2 余段 50 ≥ 40 但 `[70,110]` 超窗
    //       ⇒ 落 **R3(CF 2)** ⇒ `OP20 [70,90]` ⇒ 完成 **90** = 独立 C 链完成 **90** ⇒ ③ **判平**
    //       ⇒ ④.6 增量 `60 − 20 − 60` = **−20** ⇒ **A 合批胜出**（**第 1 次比较到达 ④.6**）；
    //     · 「并入 B」（B+C=50 件）：`OP10 [10,60]@R1`；R2 `[60,110]` 超窗 ⇒ 落 **R3(CF 2)**
    //       ⇒ `OP20 [60,85]` ⇒ 完成 **85** < 90 ⇒ **③ 层直接决出**（严格更早，根本没到 ④.6）。
    //     ⇒ 终态 = 2 个执行批（**A=10** / **B+C=50**）—— **最终采纳方案由第 ③ 层决出**，
    //       而**本轮确有 1 次比较到达过 ④.6** ⇒ 两事实必须**分别**留痕，**不得**由「记的是 ③」沉默略过 ④.6。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task M_21_R2C第46层被执行但未决定采纳方案_须单列第二事实()
    {
        var request = Build(
            paths: new[] { MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m)) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 20m, Day.AddDays(200)),   // **唯一变量**：30 → 20
                new DemandSpec("C", 3, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(400)),
                (2, Day, Day.AddMinutes(100)),
                (3, Day, Day.AddMinutes(400)),
                (4, Day, Day.AddMinutes(400))
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 终态：合批被采纳，但**由 ③ 决出** ⇒ 2 个执行批（A=10 / B+C=50）。
        var batches = result.FinalTasks.GroupBy(t => t.ExecutionBatchDraftKey).ToList();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, g => Assert.Equal(2, g.Count()));
        Assert.Equal(
            new List<decimal> { 10m, 50m },
            batches.Select(g => g.First().Quantity).OrderBy(x => x).ToList());

        // **核心断言**：④.6 **被执行过但未决定采纳方案** ⇒ 单列 `EVALUATED_NOT_DECISIVE`，
        //   且须同时交代「到达次数」与「真正的主决定层」。
        var notDecisive = result.SolveTraceNotes
            .Where(n => n.ReasonCode == "RESOURCE_EFFICIENCY_EVALUATED_NOT_DECISIVE")
            .ToList();
        var note = Assert.Single(notDecisive);
        Assert.False(string.IsNullOrEmpty(note.Key));
        Assert.Contains("**被执行但未决定最终采纳方案**", note.Message);
        Assert.Contains("本轮共 1 次比较到达 ④.6", note.Message);
        Assert.Contains("擂主=统一基线（未合批）", note.Message);
        Assert.Contains("挑战者=锚#0[", note.Message);
        Assert.Contains("由 ③交期/更早可行完成 层决出", note.Message);
        Assert.Contains("**不得**记为「因资源效率择优」", note.Message);

        // 主决定层 ≠ ④.6 ⇒ **不得**出现任何「因效率择优／判平／缺项」的 ④.6 结论码。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "RESOURCE_EFFICIENCY_INCREMENT"
              || n.ReasonCode == "RESOURCE_EFFICIENCY_NO_INCREMENT"
              || n.ReasonCode == "RESOURCE_EFFICIENCY_DATA_MISSING");

        // 反证夹具前提：**采纳的合批链**确实落在 R3（CF 2）——即第 ③ 层赢在「更早完成」而非「占用更少」。
        Assert.Equal(3, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 50m).ResourceId);
        Assert.Equal(2, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 10m).ResourceId);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // §八.4（2026-10-15）：**调换两个等价锚点枚举次序**的固定反例 + 稳定 Tie-breaker
    //   0号位 第七轮《致1号位第七轮代码审核正式复核回复 v1.0》§八.4 逐字：
    //   「增加一种**调换两个等价锚点枚举次序**的固定反例：对既定同层目标，最终合法方案和关键事实
    //     应不因普通枚举顺序漂移；如果完全判平按既定稳定规则选一方，须明确说明Tie-breaker，
    //     不能误报『效率决定』」。
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **去标签物理方案**：按 (工序, 资源, 起止, 批量) 规范化排序后拼接 ⇒ **不含任何批键/需求键**。
    ///   用于检出「最终合法方案是否因枚举次序漂移」：批键标签可以随业务字段变化，
    ///   **资源×时间×批量的多重集**不可以。
    /// </summary>
    private static string DeLabeledShape(DomainSolveResult result)
        => string.Join(" / ", result.FinalTasks
            .OrderBy(t => t.OperationCode, StringComparer.Ordinal)
            .ThenBy(t => t.ResourceId)
            .ThenBy(t => t.PlannedStartTime)
            .ThenBy(t => t.Quantity)
            .Select(t => $"{t.OperationCode}@R{t.ResourceId}q{t.Quantity}"
                         + $"[{(int)(t.PlannedStartTime - Day).TotalMinutes}-{(int)(t.PlannedEndTime - Day).TotalMinutes}]"));

    /// <summary>R2-C 留痕过滤（只取 ④.6 四条码）。</summary>
    private static List<SolveTraceNote> R2Notes(DomainSolveResult result)
        => result.SolveTraceNotes.Where(n => n.ReasonCode.StartsWith("RESOURCE_EFFICIENCY")).ToList();

    /// <summary>
    /// **§八.4 固定反例①（Solver 级）：调换两个合批锚点的枚举次序 ⇒ 最终合法方案与关键事实不得漂移。**
    ///
    ///   夹具 = `MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m))`（OP10 单资格 R1 CF1；
    ///   OP20 主资格 R2 CF1 / 备选 R3 CF2、R4 CF2）；日历 R1=400 / R2=80 / R3=400 / R4=400；
    ///   三条 C 需求 A / B / C，交期同（第 200 天）。
    ///   **锚点枚举次序由 `AllocationSequence` 决定**（求解器按 seq 升序处理 ⇒ 先处理者先成为锚点）。
    ///
    ///   本用例跑**同一物理问题**的两种枚举次序（**A/B 整体对调**，数量**跟随 seq 位置**保持物理问题不变）：
    ///     · 次序①：`A(seq1, 10)`、`B(seq2, 30)`、`C(seq3, 10)` ⇒ 锚#0 = A(q10)、锚#1 = B(q30)；
    ///     · 次序②：`B(seq1, 10)`、`A(seq2, 30)`、`C(seq3, 10)` ⇒ 锚#0 = B(q10)、锚#1 = A(q30)。
    ///   ⚠ 若只对调 `AllocationSequence` 而**不**同步对调数量，物理问题本身被改变
    ///     （第一个需求从 10 件变 30 件）⇒ 那不是「同一物理问题的枚举次序交换」，不构成本项反例。
    ///
    ///   须成立（①③④ 为 §八.4 的直接判据；实测两条逐字相同）：
    ///     ① **合批胜出批的数量恒为 40**（= 数量较大的锚批 30 + 挑战者 10），且**胜出批身份恒为
    ///        「数量 30 的那个锚批」** ⇒ 胜者由**锚批自身数量**这一**内容派生量**决定，
    ///        **不**由枚举位置决定（枚举位置变了，胜者跟着**内容**走，物理方案不动）；
    ///     ② 两级追溯的**胜负差值恒为 −10 分钟**（`擂主=擂主锚#0` vs `挑战者=合批到锚#1`），
    ///        且**决定层恒为 ④.6**（真实由效率定胜负）；
    ///     ③ **去标签物理方案逐字相同** ⇒ 不漂移；
    ///     ④ `Tie-breaker=未启用（本次胜负由冻结层目标给出）` ⇒ **未**误报 ⑤、**未**误报 Tie-breaker。
    /// </summary>
    [Fact]
    public async Task M_22_R2C调换两个锚点枚举次序_胜者与去标签物理方案须不漂移()
    {
        var path = MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m));

        async Task<DomainSolveResult> Run(bool swapAnchors)
        {
            var demands = swapAnchors
                ? new[]
                {
                    new DemandSpec("A", 2, 30m, Day.AddDays(200)),
                    new DemandSpec("B", 1, 10m, Day.AddDays(200)),
                    new DemandSpec("C", 3, 10m, Day.AddDays(200))
                }
                : new[]
                {
                    new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                    new DemandSpec("B", 2, 30m, Day.AddDays(200)),
                    new DemandSpec("C", 3, 10m, Day.AddDays(200))
                };

            return await _solver.SolveAsync(Build(
                paths: new[] { path },
                demands: demands,
                calendar: new[]
                {
                    (1, Day, Day.AddMinutes(400)),
                    (2, Day, Day.AddMinutes(80)),
                    (3, Day, Day.AddMinutes(400)),
                    (4, Day, Day.AddMinutes(400))
                }));
        }

        var order1 = await Run(swapAnchors: false);
        var order2 = await Run(swapAnchors: true);

        // ① 胜出批 = 数量 30 的那个锚批 + 挑战者 10 ⇒ 数量恒 40（**内容派生量**决定，不由枚举位置决定）。
        var merged1 = order1.FinalTasks
            .Where(t => t.OperationCode == "OP20")
            .OrderByDescending(t => t.Quantity).First();
        var merged2 = order2.FinalTasks
            .Where(t => t.OperationCode == "OP20")
            .OrderByDescending(t => t.Quantity).First();
        Assert.Equal(40m, merged1.Quantity);
        Assert.Equal(40m, merged2.Quantity);
        Assert.Equal(10m, order1.FinalTasks
            .Where(t => t.OperationCode == "OP20").Min(t => t.Quantity));
        Assert.Equal(10m, order2.FinalTasks
            .Where(t => t.OperationCode == "OP20").Min(t => t.Quantity));

        // ③ 去标签物理方案逐字相同 ⇒ 不漂移。
        Assert.Equal(DeLabeledShape(order1), DeLabeledShape(order2));

        // ② + ④ 两级追溯：真实胜负对 + 差值恒 −10 + 决定层恒 ④.6 + 未启用 Tie-breaker。
        foreach (var result in new[] { order1, order2 })
        {
            var note = Assert.Single(R2Notes(result));
            Assert.Equal("RESOURCE_EFFICIENCY_INCREMENT", note.ReasonCode);
            Assert.Contains("擂主=擂主锚#0[", note.Message);
            Assert.Contains("挑战者=合批到锚#1[", note.Message);
            Assert.Contains("（④.6 增量 -10 分钟）", note.Message);
            Assert.Contains("（④.6 增量 -20 分钟）", note.Message);
            Assert.Contains("差值 -10 分钟", note.Message);
            Assert.Contains("决定层=④.6资源效率", note.Message);
            Assert.Contains("Tie-breaker=未启用（本次胜负由冻结层目标给出）", note.Message);
            Assert.Contains("另计（**非本条胜负依据**）", note.Message);
            // 判平语义**不得**出现（本次确有严格胜负）。
            Assert.DoesNotContain("两侧判平（第 ⑤ 层）", note.Message);
        }

        // 标签随业务字段（seq）走 ⇒ 两条的**胜出批键**互为镜像（这是业务字段变化的结果，不是漂移）。
        Assert.Equal("EB|B|001", merged1.ExecutionBatchDraftKey);
        Assert.Equal("EB|A|001", merged2.ExecutionBatchDraftKey);
    }

    /// <summary>
    /// **§八.4 固定反例②（Solver 级）：两条**数量相等**的等价锚点 ⇒ 物理方案与决定层不得漂移。**
    ///
    ///   同夹具，`A(10)` / `B(10)`（**数量相等 ⇒ 两锚点在 ④.6 上增量恒等 ⇒ 真·等价锚点**），
    ///   挑战者 `C(30)`。两种枚举次序（A/B 对调）下：
    ///     ① **去标签物理方案逐字相同**；
    ///     ② ④.6 **未**决定最终采纳方案 ⇒ 必须出现 `RESOURCE_EFFICIENCY_EVALUATED_NOT_DECISIVE`
    ///        （第二事实），且其文案**明确**「本层**未**决定取舍，**不得**记为『因资源效率择优』」；
    ///     ③ 任何 ④.6 留痕**都不得**出现 `Tie-breaker=**已启用**` 之外的「效率择优」冒充 ——
    ///        本夹具的采纳方案由 ③ 交期层给出（`决定层=③交期/更早可行完成`）。
    /// </summary>
    [Fact]
    public async Task M_23_R2C数量相等的等价锚点_物理方案与决定层须不漂移_且不得误报效率决定()
    {
        var path = MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m));

        async Task<DomainSolveResult> Run(bool swapAnchors)
        {
            var demands = swapAnchors
                ? new[]
                {
                    new DemandSpec("A", 2, 10m, Day.AddDays(200)),
                    new DemandSpec("B", 1, 10m, Day.AddDays(200)),
                    new DemandSpec("C", 3, 30m, Day.AddDays(200))
                }
                : new[]
                {
                    new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                    new DemandSpec("B", 2, 10m, Day.AddDays(200)),
                    new DemandSpec("C", 3, 30m, Day.AddDays(200))
                };

            return await _solver.SolveAsync(Build(
                paths: new[] { path },
                demands: demands,
                calendar: new[]
                {
                    (1, Day, Day.AddMinutes(400)),
                    (2, Day, Day.AddMinutes(80)),
                    (3, Day, Day.AddMinutes(400)),
                    (4, Day, Day.AddMinutes(400))
                }));
        }

        var order1 = await Run(swapAnchors: false);
        var order2 = await Run(swapAnchors: true);

        // ① 去标签物理方案逐字相同 ⇒ 等价锚点的枚举次序**不**漂移最终合法方案。
        Assert.Equal(DeLabeledShape(order1), DeLabeledShape(order2));

        // ②③ 决定层不是 ④.6 ⇒ 第二事实单列，且不得冒充「因资源效率择优」。
        foreach (var result in new[] { order1, order2 })
        {
            var notes = R2Notes(result);
            Assert.Single(notes);
            Assert.Equal("RESOURCE_EFFICIENCY_EVALUATED_NOT_DECISIVE", notes[0].ReasonCode);
            Assert.Contains("本轮共 1 次比较到达 ④.6", notes[0].Message);
            Assert.Contains("**未**决定取舍", notes[0].Message);
            Assert.Contains("不得**记为「因资源效率择优」", notes[0].Message);
            Assert.Contains("最终采纳方案由 ③交期/更早可行完成 层决出", notes[0].Message);
        }
    }

    /// <summary>
    /// **§八.4 固定反例③（纯函数级）：⑤「完全判平」的 Tie-breaker 判据直接反证。**
    ///
    ///   （a）`ResolveStageMerge` 在**两侧逐层全等**时返回 `(④.6, false)` ⇒
    ///       「**到达** ④.6」≠「④.6 **决定**了胜负」⇒ 调用点由此得 `TraceNoIncrement`
    ///       （「指标相同／无增量差异」），**不得**记「因资源效率择优」。
    ///   （b）`PreferChallengerOnTie` 的**稳定 Tie-breaker**（⑤ 判平时由谁接管擂主）逐条反证：
    ///       只有在「未胜 + 落点 ④.6 + 挑战者合法 + 擂主已是合批锚 + 挑战者锚批键字典序**严格更小**」
    ///       五条**同时**成立时才接管 ⇒ **内容派生、与枚举次序无关**。
    /// </summary>
    [Fact]
    public void M_24_R2C完全判平的TieBreaker判据_须内容派生且逐条可反证()
    {
        // ── (a) 全等 ⇒ 到达 ④.6 但**不是**效率决定 ──
        var tie = PhaseTwoInitialScheduler.ResolveStageMerge(
            separateFeasible: true, mergeFeasible: true,
            delaySeparate: 0, delayMerge: 0, direction: "FORWARD",
            separateEnd: Day.AddHours(8), mergeEnd: Day.AddHours(8),
            separateSetup: 30m, mergeSetup: 30m,
            separateEconDev: (0m, 0m), mergeEconDev: (0m, 0m),
            separateNonSetupOccupiedMinutes: 90m, mergeNonSetupOccupiedMinutes: 90m);
        Assert.Equal(FrozenLayer.ResourceEfficiency, tie.Layer);   // 确实**到达**了 ④.6
        Assert.False(tie.MergeWins);                               // 但**未**判出胜负 ⇒ 非效率决定
        Assert.Equal(
            ResourceEfficiencyVerdict.Tie,
            ResourceEfficiencyEvaluator.VerdictFromMergeIncrement(90m - 90m));

        // ── (b) Tie-breaker 判据：五条同时成立才接管 ──
        const string small = "SEB|EB|A|001|STAGE1";
        const string large = "SEB|EB|B|001|STAGE1";

        // ① 基准：判平 + 擂主已是合批锚 + 挑战者键更小 ⇒ **接管**（内容派生取舍）。
        Assert.True(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: true,
            incumbentAnchorIndex: 0, challengerAnchorKey: small, incumbentAnchorKey: large));

        // ② 挑战者键更大 ⇒ **不**接管（严格小于才是判据；相等亦不接管 ⇒ 见 ③）。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: true,
            incumbentAnchorIndex: 0, challengerAnchorKey: large, incumbentAnchorKey: small));

        // ③ 键相等 ⇒ **不**接管（`CompareOrdinal < 0` 严格）。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: true,
            incumbentAnchorIndex: 0, challengerAnchorKey: small, incumbentAnchorKey: small));

        // ④ 擂主仍是**统一基线**（`-1`）⇒ **不**接管（判平取独立排程，零回归原则逐字不变）。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: true,
            incumbentAnchorIndex: -1, challengerAnchorKey: small, incumbentAnchorKey: large));

        // ⑤ 冻结层**已判出胜负** ⇒ 本函数**不得**介入（不干扰冻结层目标）。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: true, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: true,
            incumbentAnchorIndex: 0, challengerAnchorKey: small, incumbentAnchorKey: large));

        // ⑥ 落点**不是** ④.6（如 ③ 交期层判平后已 return）⇒ **不**接管。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.DueDate, challengerFeasible: true,
            incumbentAnchorIndex: 0, challengerAnchorKey: small, incumbentAnchorKey: large));

        // ⑦ 挑战者**不是**合法合批候选 ⇒ **不**接管（不能接管一个不存在的候选）。
        Assert.False(PhaseTwoInitialScheduler.PreferChallengerOnTie(
            mergeWins: false, layer: FrozenLayer.ResourceEfficiency, challengerFeasible: false,
            incumbentAnchorIndex: 0, challengerAnchorKey: small, incumbentAnchorKey: large));
    }

    // ════════════════════════════════════════════════════════════════════
    // R-2 设备口径**进入择优**（0号位 J13-05）—— Solver 级「正例 ＋ 反例」
    //   依据：0号位《APS V1｜致2号位第十三轮联合技术契约 v1.2 与 E1/E2/E3 正式复核意见 v1.0》
    //     §5.1 ／处置矩阵 **J13-05**（判定 P1／合同内部冲突）逐字：
    //       「仅提供一个可空 `DeviceResourceIds` 载体，**不能证明 1号位 评分器在正式 Run 接收非 NULL
    //         纯设备集合并排除了人工槽**。……**应改**：Solver `Resources` 仍完整保留设备及人工两类合法
    //         容量；2号位将真实类型明确投影为非 NULL 设备子集（即使为空也与"未投影"区分），**由 1号位
    //         在评分分支使用该子集而非全量 `AffectedResources`**；……**不得默认让新正式 Run 一直走含
    //         人工路径**。**给一个有设备＋人工的正例和纯人工槽反例。**」
    //
    //   ⚠ 本文件既有的**全部** Solver 级用例都**不传** `deviceResourceIds` ⇒ 恒为 `null`（未投影）
    //     ⇒ 走的都是**兼容边界**（④.6 排序键 = 含人工槽键）⇒ 对本次改造**零覆盖**。
    //     本节补上「已投影」的两侧：正例（设备＋人工槽并存）与反例（纯人工槽）。
    //   ⚠ 白名单里的资源 id 在 1号位 侧只是**不透明 int**：本文件用 `{1, 2}` 当纯设备、
    //     用 `{3, 4}` 当**人工槽形态**（真实形态 = 2号位 装载侧的合成键 `ManualSlotResourceOffset + id`，
    //     该常量属装载层，1号位 侧**不抄、不判别**）。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **正例（设备 ＋ 人工槽并存）**：同一 Run 里既有纯设备（R1／R2）又有**人工能力槽**（R3／R4），
    ///   人工槽**有真实占用**（合批链的 OP20 落在 R3 上），但 ④.6 的排序键**不得**把人工槽占用当设备稼动打分。
    ///
    ///   夹具 = `M_20` **逐字**（同路径／同日历／同需求），**唯一差别** = `DeviceResourceIds = {1, 2}`
    ///   （R1／R2 = 纯设备；R3／R4 = 白名单外的人工槽形态）。
    ///
    ///   逐锚点 ④.6 增量（`占用(锚批+本批) − 占用(锚批) − 占用(本批)`，**已扣 Setup**）：
    ///     · 合批到锚#0（A+C 落 R1 ＋ **R3**）：纯设备 = `40 − 20 − 60` = **−40**；含人工槽 = `60 − 20 − 60` = −20
    ///     · 合批到锚#1（B+C 落 R1 ＋ **R3**）：纯设备 = `60 − 60 − 60` = **−60**；含人工槽 = `90 − 60 − 60` = −30
    ///   ⇒ 胜负方向不变（合批到 B 仍胜、仍 2 批），但**决定依据的数字换成了纯设备口径** —— 这正是 J13-05 要的。
    ///
    ///   ★ **判别器**：留痕里**不得**再出现未投影口径的 `−20`／`−30`（旧行为＝人工槽占用进了排序键 ⇒ 本用例红）。
    /// </summary>
    [Fact]
    public async Task M_25_R2设备口径正例_设备与人工槽并存_人工槽占用不得计入406排序键()
    {
        var request = Build(
            paths: new[] { MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m)) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200)),
                new DemandSpec("C", 3, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(400)),    // R1：OP10（CF 1）—— **纯设备**
                (2, Day, Day.AddMinutes(100)),    // R2：OP20 首选（CF 1）—— **纯设备**
                (3, Day, Day.AddMinutes(400)),    // R3：OP20 替代（**CF 2**）—— **人工槽形态**（白名单外）
                (4, Day, Day.AddMinutes(400))     // R4：OP20 另一备选（CF 2）—— 本轮未被使用
            },
            deviceResourceIds: new[] { 1, 2 });

        // 载体验证（**若 `Build` 没把白名单透传下去，本用例会退化成 M_20 而静默变绿** ⇒ 先钉死）。
        Assert.Equal(new[] { 1, 2 }, request.DeviceResourceIds!.ToArray());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ── 物理终态与 M_20 **逐字相同**（J13-05 只换**评分子集**，不动候选构造／可行性／履约）──
        var batches = result.FinalTasks.GroupBy(t => t.ExecutionBatchDraftKey).ToList();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, g => Assert.Equal(2, g.Count()));
        Assert.Equal(
            new List<decimal> { 10m, 60m },
            batches.Select(g => g.First().Quantity).OrderBy(x => x).ToList());

        // 人工槽**确实有真实占用**（合批链 OP20 落在 R3）—— 这正是本正例要证明「有占用但不计分」的前提。
        Assert.Equal(2, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 10m).ResourceId);
        Assert.Equal(3, result.FinalTasks.Single(t => t.OperationCode == "OP20" && t.Quantity == 60m).ResourceId);
        Assert.DoesNotContain(request.DeviceResourceIds!, id => id == 3);   // R3 确在白名单**外**

        // ── 核心断言：决定依据换成了**纯设备**增量 ──
        var increments = result.SolveTraceNotes
            .Where(n => n.ReasonCode == "RESOURCE_EFFICIENCY_INCREMENT")
            .ToList();
        var note = Assert.Single(increments);

        Assert.Contains("（④.6 增量 -40 分钟）", note.Message);   // 擂主锚#0（A+C）
        Assert.Contains("（④.6 增量 -60 分钟）", note.Message);   // 挑战者合批到锚#1（B+C）
        Assert.Contains("差值 -20 分钟", note.Message);

        // ★★ **判别器**：未投影口径的两个数字**必须消失** —— 它们正是「人工槽被当设备打分」的指纹。
        Assert.DoesNotContain("（④.6 增量 -20 分钟）", note.Message);
        Assert.DoesNotContain("（④.6 增量 -30 分钟）", note.Message);

        // 口径声明 + 两个**绝对**键并列（纯设备 100／80；含人工槽 120／110 作对照）。
        Assert.Contains("R-2 排序键口径=**纯设备**", note.Message);
        Assert.Contains("纯设备键 100", note.Message);          // 擂主侧完整候选方案（A+C 合批 + B 独立）
        Assert.Contains("纯设备键 80", note.Message);           // 挑战者侧完整候选方案（B+C 合批 + A 独立）
        Assert.Contains("含人工槽 120", note.Message);          // 同两份方案的「含人工槽」对照值
        Assert.Contains("含人工槽 110", note.Message);
        Assert.Contains("④.6 择优取纯设备键", note.Message);

        // 胜负方向与层身份不变。
        Assert.Contains("决定层=④.6资源效率", note.Message);
        Assert.Contains("胜出 ⇒ **合批释放产能**", note.Message);
        Assert.Contains("本轮共 2 次比较到达 ④.6", note.Message);
    }

    /// <summary>
    /// **反例（纯人工槽）**：`DeviceResourceIds` **已投影但为空集**（`≠ null`）⇒ 本 Run 无任何纯设备
    ///   ⇒ 纯设备键**恒 0** ⇒ ④.6 两侧判平（走第 ⑤ 层）⇒ **不得**凭人工槽占用宣称「合批释放产能」。
    ///
    ///   夹具与 `M_25` **逐字相同**（唯一差别 = 白名单由 `{1, 2}` 换成**空集**）⇒ 构成**同夹具判别对**：
    ///     · `M_25`（`{1, 2}`）：合批释放的是**设备**产能（R3 是人工槽，被排除）⇒ 合批胜 ⇒ **2 批 / 4 Task**；
    ///     · 本条（空集）：全部候选的设备键都是 `0`（合批省的产能全在人工槽上）⇒ 判平 ⇒ ⑤ 取独立排程
    ///       ⇒ **3 批 / 6 Task**（**结果翻转**）。
    ///   ⇒ 这就是「**不得**因兼容旧行为将人工能力槽打分为设备稼动」的可执行形式。
    /// </summary>
    [Fact]
    public async Task M_26_R2设备口径反例_纯人工槽候选_设备键恒零须判平取独立排程()
    {
        var request = Build(
            paths: new[] { MultiAltPath("RT", 1, 1, 1m, 2, (3, 2m), (4, 2m)) },
            demands: new[]
            {
                new DemandSpec("A", 1, 10m, Day.AddDays(200)),
                new DemandSpec("B", 2, 30m, Day.AddDays(200)),
                new DemandSpec("C", 3, 30m, Day.AddDays(200))
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(400)),    // R1：OP10（CF 1）—— 白名单外
                (2, Day, Day.AddMinutes(100)),    // R2：OP20 首选（CF 1）—— 白名单外
                (3, Day, Day.AddMinutes(400)),    // R3：OP20 替代（**CF 2**）—— 白名单外
                (4, Day, Day.AddMinutes(400))     // R4：OP20 另一备选（CF 2）—— 本轮未被使用
            },
            deviceResourceIds: Array.Empty<int>());

        // ★ **空集 ≠ `null`**：载体**必须**是非 null 的空列表（否则本条会退化成 M_20 的兼容边界）。
        Assert.NotNull(request.DeviceResourceIds);
        Assert.Empty(request.DeviceResourceIds!);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // ── 终态翻转：三个需求**各自独立**成批（3 批 / 6 条 Task / 数量 10・30・30）──
        var batches = result.FinalTasks.GroupBy(t => t.ExecutionBatchDraftKey).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Equal(6, result.FinalTasks.Count);
        Assert.All(batches, g => Assert.Equal(2, g.Count()));
        Assert.Equal(
            new List<decimal> { 10m, 30m, 30m },
            batches.Select(g => g.First().Quantity).OrderBy(x => x).ToList());

        // 三条 OP20 全走**首选设备 R2**（CF 1）—— 没有任何一条落到人工槽 R3 ⇒ 合批确未被采纳。
        Assert.All(
            result.FinalTasks.Where(t => t.OperationCode == "OP20"),
            t => Assert.Equal(2, t.ResourceId));

        // ── ④.6 **被执行但未决定**：两侧设备键同为 `0` ⇒ 判平（Tie）⇒ 取独立排程；**不得**记「合批释放产能」──
        var notes = R2Notes(result);
        var note = Assert.Single(notes);
        Assert.Equal("RESOURCE_EFFICIENCY_NO_INCREMENT", note.ReasonCode);
        Assert.Contains("两侧判平（第 ⑤ 层）", note.Message);
        Assert.Contains("取**统一基线／未合批**", note.Message);
        Assert.Contains("**非**因资源效率择优", note.Message);
        Assert.Contains("本轮共 2 次比较到达 ④.6", note.Message);

        // ★ 判别器：**不得**出现「合批释放产能」这种凭人工槽占用得出的结论。
        Assert.DoesNotContain("合批释放产能", note.Message);
        Assert.Contains("决定层=④.6资源效率", note.Message);

        // 口径声明 + 空集留痕（**已投影但无纯设备**，与「未投影」必须可区分）。
        Assert.Contains("R-2 排序键口径=**纯设备**", note.Message);
        Assert.Contains("无纯设备", note.Message);

        // 两个**绝对**键：纯设备恒 0；含人工槽侧仍是真实占用（140／110）⇒ 证明「有占用、但不计分」。
        //   ⚠ 全部候选被否决 ⇒ 擂主恒为**统一基线**（A/B 原量 ＋ C 独立）⇒ 含人工槽键 = 140（非 M_25 的 120）。
        Assert.Contains("纯设备键 0", note.Message);
        Assert.Contains("含人工槽 140", note.Message);
        Assert.Contains("含人工槽 110", note.Message);
    }
}
