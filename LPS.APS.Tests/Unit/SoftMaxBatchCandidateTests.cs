using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **N8-02 §4.3（0号位 2026-10-15 第九轮）**：经济软 `Max` 参与 `nMin` 推导的**待核风险**
///   （本轮定为 `RISK_UNVERIFIED`）。
///
/// 0号位 判词逐字：
///   「1号位回执称 `nMin = ceil(qty / MaxExecutionBatchQty) = ceil(10/6) = 2`。**经济Max是软偏好，
///    不是硬批量上限或强制最少批数。**如该`nMin`导致合法“整批10”等候选被排除，属于另一个R-1/R-3
///    业务违反；如它只是有界候选**启发式建议**、硬合法基础解仍可进入比较，则未必违规。必须交出
///    **包含与不包含经济Max的候选集合、是否仍有整批基解、预算退出/回退路径和对照测试**。」
///
/// 本文件就是那份「候选集合对照」的**实测**：
///   · ① 含经济Max（`Max=6`）与不含（`Max=null`）的**候选集合**、**基线批数**；
///   · ② **整批（1 批）基解**在两种情形下**在不在**候选集里；
///   · ③ **预算退出/回退路径**（`SolverBatchBudget` 截断时基线恒保留）；
///   · ④ `n=3` 均分尾差与**末批余量**（不重不漏）。
///
/// ⚠ 纯内存、直调 `PhaseTwoInitialScheduler` 的公开/内部入口（`InternalsVisibleTo`），
///   不碰数据库、不起 Solver、不读文件。
/// ⚠ 与既有 `ExecutionBatchDraftTests` 的 `优化候选_有界且消费Preferred与上限` / `BP_R3` / `BP_R2`
///   的分工：那三条锁**候选枚举机制与预算**；本文件锁**「经济 Max 参与与否」对候选集合与整批基解的影响**
///   —— 这是 §4.3 点名要的那份对照，既有用例未覆盖（既有用例从未把 `Max` 在 `null` 与给定值之间对照）。
/// </summary>
public class SoftMaxBatchCandidateTests
{
    private const int M = 1;
    private const int Dept = 100;

    // ════════════════════════════════════════════════════════════════════
    //  ① / ② 候选集合对照：同一 Q，只改经济软 Max
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **对照测试（0号位 明令「对照测试」）**：同 `Q=10`、同 `AllowSplit=true`、同 `Min=null`，
    ///   **唯一变量 = 经济软 `Max`**。三个档位实测：
    ///
    /// | 经济 Max | `nMin` | 基线批数 | 候选批数集合 | 整批(1) 在候选？ |
    /// |---|---|---|---|---|
    /// | `null`（无经济 Max） | 1 | **1（整批 10）** | `{1, 2, 3}` | ✅ 是（且**就是基线**） |
    /// | `6` | 2 | 2 | `{2, 3}` | ❌ 否 |
    /// | `4` | 3 | 3 | `{3, 4}` | ❌ 否 |
    ///
    /// ⇒ **实测结论**：经济软 `Max` 一旦给定且 `Q &gt; Max`，**整批候选即被排除**。
    ///   两条机制共同作用、必须分开说：
    ///   · `DecideExecutionBatchPlan:3476` ⇒ `nMin = ceil(Q/经济Max)` **决定基线批数**（⇒ 整批不再当基线）；
    ///   · `EnumerateLegalBatchPlanCandidates:4381` 的 `TryAddInRange(1)` 要求 `1 &gt;= nMin`
    ///     ⇒ `nMin &gt; 1` 时**整批连候选都不是**。
    ///   ⚠ 第②条**不是**经济 Max 的硬过滤，而是**独立的一条设计选择**（`:4395` 注释：整批候选会把
    ///     `AllowMerge` 场景引向「单批合并 ⇒ 批身份被吞」，引 §十二 第 9 行禁令）。
    ///     即「整批被排除」与「经济 Max 越权当硬上限」是**两件事**，不得混同。
    /// </summary>
    [Fact]
    public void 对照_同Q同策略只改经济软Max_候选集合与整批基解实测()
    {
        var noMax = Measure(qty: 10m, min: null, max: null);
        var max6 = Measure(qty: 10m, min: null, max: 6m);
        var max4 = Measure(qty: 10m, min: null, max: 4m);

        // ── 不含经济 Max ⇒ 整批就是基线，且整批在候选集里 ──
        Assert.Equal(new[] { 1, 2, 3 }, noMax.CandidateCounts);
        Assert.Equal(1, noMax.BaselineCount);
        Assert.Contains(1, noMax.CandidateCounts);

        // ── 含经济 Max=6 ⇒ nMin=2 ⇒ 基线 2 批、候选 {2,3}、**整批不在** ──
        Assert.Equal(new[] { 2, 3 }, max6.CandidateCounts);
        Assert.Equal(2, max6.BaselineCount);
        Assert.DoesNotContain(1, max6.CandidateCounts);

        // ── 含经济 Max=4 ⇒ nMin=3 ⇒ 基线 3 批、候选 {3,4}、**整批不在** ──
        Assert.Equal(new[] { 3, 4 }, max4.CandidateCounts);
        Assert.Equal(3, max4.BaselineCount);
        Assert.DoesNotContain(1, max4.CandidateCounts);

        // ── 三档共同点：**硬合法基线解恒在候选集首位**（`EnumerateLegalBatchPlanCandidates:4344`）──
        Assert.Equal(noMax.BaselineCount, noMax.CandidateCounts[0]);
        Assert.Equal(max6.BaselineCount, max6.CandidateCounts[0]);
        Assert.Equal(max4.BaselineCount, max4.CandidateCounts[0]);
    }

    /// <summary>
    /// **04 BP-R1 回归**（`Qty=10 / 经济Min=6 / 经济Max=6`）：经济区间**不整除**（`nMin=2 &gt; nMax=1`）
    ///   ⇒ **不再**判 `BATCH_POLICY_CONFLICT`，取基线批数产出**合法批**（0号位 逐字「取基线批数
    ///   （`nMin`：每批 ≤ 经济 Max）产出**合法批**」）⇒ `nMin` 作为基线是**被冻结口径认可的**。
    ///
    /// 实测：基线 2 批、候选 `{2, 3}`，且 2 批切分为 `5 + 5`（`Σ = 10` 不重不漏）。
    /// </summary>
    [Fact]
    public void 经济Min等于Max等于6_Q10_不判冲突_基线2批且切分不重不漏()
    {
        var r = Measure(qty: 10m, min: 6m, max: 6m);

        Assert.True(r.BaselineIsLegal, r.ConflictReason);
        Assert.Equal(2, r.BaselineCount);
        Assert.Equal(new[] { 5m, 5m }, r.BaselineQtys);
        Assert.Equal(new[] { 2, 3 }, r.CandidateCounts);
        Assert.DoesNotContain(1, r.CandidateCounts);
    }

    // ════════════════════════════════════════════════════════════════════
    //  ③ 预算退出 / 回退路径
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **预算退出/回退路径（0号位 明令「预算退出/回退路径」）**：`SolverBatchBudget` 无论怎么收紧，
    ///   **基线恒保留**（`EnumerateLegalBatchPlanCandidates:4344` 首元素恒为 `baseline`；
    ///   `:4430` 的 `allowed = MaxBatchCandidates - 1` 就是为它留的位）。
    ///   ⇒ 预算只**收**优化候选，**永不**动基线 ⇒ 「不因预算限制硬 Max 强制拆批」（B-007）；
    ///     预算耗尽时的回退是**退回基线**，不是退回失败。
    ///
    /// 取 `Q=10 / 经济Max=6 / Preferred=2` ⇒ `nMin=2`、`nPref=round(10/2)=5`
    ///   ⇒ 内部 `counts` 初始 = `[2, 3, 5]`。两条上限分别收紧：
    ///   · `(3,8)` ⇒ `[2,3,5]`（都不咬）；
    ///   · `(2,8)` ⇒ `[2,3]`（**上限①** `MaxOptimizationSplitCount` 咬）；
    ///   · `(3,3)` ⇒ `[2,3]`（**上限②** `MaxBatchCandidates` 咬，`allowed=2`）；
    ///   · `(0,1)` ⇒ `[2]`（`allowed=0`，**只剩基线**）。
    ///
    /// ⚠ **实测观测（提请 0号位，本轮不落码）**：上限① 的实现在 `:4421-4425` 对 `counts` **整体** `Take(k)`，
    ///   而 `counts` 里**必含 `nMin` 本身**（`:4397`），`nMin` 通常**就等于基线批数** ⇒ `:4439` 的
    ///   `c == baselineCount ⇒ continue` 会把它跳过。**后果**：`MaxOptimizationSplitCount = k` 实际只留下
    ///   `k - 1` 个**真正不同**的优化候选。与 `:4324` 注释「只限制优化性拆分（批数 &gt; 基线者）」的**字面**口径
    ///   差一位（上限② `:4430` 用 `cap - 1` 是**对**的）。本处**只登记、不改**——改它是**行为变更**
    ///   （默认预算下被评估的候选数会变），须 0号位 定夺。
    /// </summary>
    [Fact]
    public void 预算只收优化候选_基线恒保留_两条上限各自可截()
    {
        var full = Measure(qty: 10m, min: null, max: 6m, preferred: 2m,
            budget: new PhaseTwoInitialScheduler.SolverBatchBudget(3, 8, "test"));
        var bySplit = Measure(qty: 10m, min: null, max: 6m, preferred: 2m,
            budget: new PhaseTwoInitialScheduler.SolverBatchBudget(2, 8, "test"));
        var byTotal = Measure(qty: 10m, min: null, max: 6m, preferred: 2m,
            budget: new PhaseTwoInitialScheduler.SolverBatchBudget(3, 3, "test"));
        var onlyBase = Measure(qty: 10m, min: null, max: 6m, preferred: 2m,
            budget: new PhaseTwoInitialScheduler.SolverBatchBudget(0, 1, "test"));

        Assert.Equal(new[] { 2, 3, 5 }, full.CandidateCounts);
        Assert.Equal(new[] { 2, 3 }, bySplit.CandidateCounts);   // 上限① 收
        Assert.Equal(new[] { 2, 3 }, byTotal.CandidateCounts);   // 上限② 收
        Assert.Equal(new[] { 2 }, onlyBase.CandidateCounts);     // 只剩基线

        // 四档共同：**基线（2 批）恒在候选集首位** ⇒ 预算永不制造「无解」。
        Assert.All(new[] { full, bySplit, byTotal, onlyBase }, m =>
        {
            Assert.Equal(2, m.BaselineCount);
            Assert.Equal(2, m.CandidateCounts[0]);
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  ④ n=3 均分尾差 / 末批余量（不重不漏）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **§4.3 第二段（0号位 逐字）**：「`n=3` 均分后可能产生 `0.0001` 级四舍五入尾差，应按真实净量
    ///   与计量精度做**末批余量**及**不重不漏**校验，不以输出近似数掩盖差额。」
    ///
    /// 实测 `Q=10 / n=3`：前两批 `Round(10/3, 4, ToZero) = 3.3333`，**末批取余数** `= 3.3334`
    ///   ⇒ `Σ = 10.0000` **精确闭合**（`BuildBatchFormation:4204-4209`）。
    ///   ⇒ 无 `0.0001` 丢失；尾差全部落在**末批**且被显式补回，不是「近似数掩盖差额」。
    ///
    /// 同时钉住 `PlannedProcessQty` 同口径（两列**各自**闭合，不得互相挪用）。
    /// </summary>
    [Fact]
    public void Q10三批切分_末批补回尾差_两列各自精确闭合()
    {
        var r = Measure(qty: 10m, min: null, max: 4m);   // nMin = ceil(10/4) = 3

        Assert.Equal(3, r.BaselineCount);
        Assert.Equal(new[] { 3.3333m, 3.3333m, 3.3334m }, r.BaselineQtys);
        Assert.Equal(10m, r.BaselineQtys.Sum());
        Assert.Equal(10m, r.BaselineProcQtys.Sum());
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    private sealed record Measurement(
        int BaselineCount,
        bool BaselineIsLegal,
        string? ConflictReason,
        decimal[] BaselineQtys,
        decimal[] BaselineProcQtys,
        int[] CandidateCounts);

    /// <summary>
    /// 走**生产同一入口**取基线（`FormExecutionBatches` ⇒ `DecideExecutionBatchPlan` ⇒
    ///   `BuildBatchFormation`），再走**生产同一入口**枚举优化候选
    ///   （`EnumerateLegalBatchPlanCandidates`）。**唯一变量 = 经济软 Min/Max 与预算。**
    /// </summary>
    private static Measurement Measure(
        decimal qty, decimal? min, decimal? max,
        decimal? preferred = null,
        PhaseTwoInitialScheduler.SolverBatchBudget? budget = null)
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            MaterialId = M,
            FactoryId = 1,
            AllocationSequence = 1,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = "STAGE1"
        };

        var policy = new BatchPolicyRuleSnapshot
        {
            MaterialId = M,
            ProductionDepartmentId = Dept,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            PreferredBatchQty = preferred,
            AllowSplit = true,
            AllowMerge = true
        };

        var baseline = PhaseTwoInitialScheduler.FormExecutionBatches(demand, policy);
        var baselineQtys = baseline.Batches.Select(b => b.NetOutputQty).ToArray();
        var baselineProcQtys = baseline.Batches.Select(b => b.PlannedProcessQty).ToArray();

        var candidates = PhaseTwoInitialScheduler.EnumerateLegalBatchPlanCandidates(
            demand, policy, baseline, budget);

        return new Measurement(
            baseline.Batches.Count,
            baseline.IsLegal,
            baseline.ConflictReason,
            baselineQtys,
            baselineProcQtys,
            candidates.Select(c => c.Batches.Count).ToArray());
    }
}
