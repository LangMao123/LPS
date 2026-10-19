using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **逐 Task 份额闭合的容差漂移**（1号位 2026-10-16 修复）。
///
/// 问题（**实测，非理论**）：`PhaseFiveCompression.GenerateAllocationShares` 分摊时逐条做
///   `Math.Round(closureTarget * c / totalContribution, 3)`。份额落库列是 `DECIMAL(18,4)`
///   （4 位小数），而分摊比恰为 1 时 `closureTarget * c / total` **恒等于 `c` 本身**
///   ⇒ 这次 3 位量化**不带来任何好处，只制造偏差** `δ = Math.Round(c,3) − c`，`|δ| ≤ 0.0005`。
///   逐 Allocation 的 Σ 已由**末位补差**保证精确 ⇒ 偏差**不落在 Allocation 上，只落在 Task 上**：
///     `ΣShare(Task) − Task.Quantity = Σ_{非末位} δ − Σ_{末位} Σ_{i<末位} δ`
///   （依据 `GetTaskDemandComposition`：一个 Task 的构成之和恒 = `task.Quantity`）。
///   ⇒ 一个 Task 同时挂在 **≥2 个 Allocation** 上时 δ 累加，可越过硬校验
///     `PhaseFiveCompression.cs:2056` 的 `+0.001m` ⇒ **合法全排定被误判为 Domain 失败**。
///
/// 实测佐证（2号位《跨号位技术契约联合件 v1.1》§5.4，APS 真库只读）：
///   135 个有份额 Task 中 **3 个** `ΣShare` 超 `Task.Quantity` **+0.0007**
///   （`13.3340` vs `13.3333`；`9.3340` vs `9.3333`），距 `0.001` 阈值**仅 1.4 倍余量**。
///
/// 修复：分摊比**精确相等**（`totalContribution == closureTarget`，decimal 精确比较）时不量化，
///   直接取 `c.ContributionQty` ⇒ 逐 Allocation 与逐 Task 两条不变量**同时归零**。
///   ⚠ 比率非 1 的一切情形**行为逐字不变**（零回归）；且**不放宽**任何硬校验（收紧，不是放松）。
///
/// ⚠ 本类**只调纯函数**：不起 Solver、不读日历、**不碰数据库**。
/// </summary>
public class AllocationShareToleranceTests
{
    // ════════════════════════════════════════════════════════════════════
    //  夹具：4 个 Allocation，T1 是**跨 4 个 Allocation 的共享 Task**
    // ════════════════════════════════════════════════════════════════════
    //
    //  Allocation 1（声明 10）：T1 贡献 3.3336 ＋ T2 贡献 6.6664   ⇒ 合计 10.0000 = 声明量（比恰为 1）
    //  Allocation 2（声明  5）：T1 贡献 2.2226 ＋ T3 贡献 2.7774   ⇒ 合计  5.0000 = 声明量
    //  Allocation 3（声明  5）：T1 贡献 1.1116 ＋ T4 贡献 3.8884   ⇒ 合计  5.0000 = 声明量
    //  Allocation 4（声明  2）：T1 贡献 0.5556 ＋ T5 贡献 1.4444   ⇒ 合计  2.0000 = 声明量
    //
    //  ⇒ T1 的构成之和 = 3.3336+2.2226+1.1116+0.5556 = **7.2234** = `T1.Quantity`
    //     （4 条贡献全部 4 位小数、末位 6 ⇒ `Math.Round(c,3) − c = +0.0004`，四条同号累加）
    //
    //  修复前：T1 在 4 个 Allocation 里都是**非末位** ⇒ 逐条量化 +0.0004
    //          ⇒ `ΣShare(T1) = 3.334+2.223+1.112+0.556 = 7.225`
    //          ⇒ 漂移 `+0.0016 > 0.001` ⇒ 硬校验第 3 项**判失败**（本类 Test 3 实测红）
    //  修复后：T1 逐条取原值 ⇒ `ΣShare(T1) = 7.2234` ⇒ 漂移 **0**（Test 1／2 绿）
    //
    //  排序键 = `SourceDraftId|StageCode|OperationCode|RouteCode|PathId|BatchKey|Start|End`
    //  （`PhaseFiveCompression.cs:1537`）。本夹具全部同 `(S1, OP1)`（硬校验第 8 项要求
    //   「逐需求 × 工序」数量闭合），故**靠 `SourceDraftId` 与 `PlannedStartTime` 定序**：
    //   各 Allocation 内 T1 均排首位（非末位）⇒ T1 吸收 4 次量化偏差。
    //
    //  全部数量为 **4 位小数**，对齐真库列 `AllocationTaskShare.ShareQty = DECIMAL(18,4)`。

    private static LogicalProductionDemand Demand(string key, long alloc, decimal netQty) => new()
    {
        LogicalDemandKey = key,
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = alloc,
        DemandKey = key,
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = netQty,
        PlannedProcessQty = netQty,
        DemandSequence = (int)alloc,
        StartStageCode = "S1",
        StartOperationCode = "OP1",
    };

    private static FinalTaskDraft Task(string id, string sourceDraftId, decimal qty, int startHour) => new()
    {
        FinalDraftId = id,
        SourceDraftId = sourceDraftId,
        MaterialId = 1,
        FactoryId = 1,
        StageCode = "S1",
        OperationCode = "OP1",
        Quantity = qty,
        PlannedProcessQty = qty,
        PlannedStartTime = new DateTime(2026, 1, 1, startHour, 0, 0),
        PlannedEndTime = new DateTime(2026, 1, 1, startHour, 30, 0),
    };

    private static DomainSolveRequest Request() => new()
    {
        LogicalProductionDemands = new List<LogicalProductionDemand>
        {
            Demand("D1", 1L, 10m),
            Demand("D2", 2L, 5m),
            Demand("D3", 3L, 5m),
            Demand("D4", 4L, 2m),
        },
    };

    private static List<FinalTaskDraft> Tasks() => new()
    {
        Task("T1", "D1", 7.2234m, 8),   // ★ 跨 4 个 Allocation 的共享 Task（各 Allocation 内均非末位）
        Task("T2", "D1", 6.6664m, 9),
        Task("T3", "D2", 2.7774m, 9),
        Task("T4", "D3", 3.8884m, 9),
        Task("T5", "D4", 1.4444m, 9),
    };

    /// <summary>merge 血缘：T1 的锚点需求是 `D1`，另从 `D2/D3/D4` 各合并进一份份额。</summary>
    private static Dictionary<string, List<(string DemandKey, decimal ShareQty)>> Lineage() => new()
    {
        ["T1"] = new List<(string DemandKey, decimal ShareQty)>
        {
            ("D2", 2.2226m),
            ("D3", 1.1116m),
            ("D4", 0.5556m),
        },
    };

    private static AllocationTaskShare Share(string taskId, long alloc, decimal qty) => new()
    {
        FinalDraftId = taskId,
        AllocationSequence = alloc,
        ComponentQty = qty,
    };

    // ════════════════════════════════════════════════════════════════════
    //  1. 修复后：逐 Allocation 与逐 Task **两条**不变量同时精确成立
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分摊比恰为 1 时不做 3 位量化 ⇒ `ΣShare(Task)` **严格等于** `Task.Quantity`（精确相等，非容差比较）。
    /// ⚠ 修复前此断言在 `T1` 上红（`7.225 ≠ 7.2234`）。
    /// </summary>
    [Fact]
    public void 精确分摊比下_逐Task份额与Quantity严格相等()
    {
        var tasks = Tasks();
        var request = Request();
        var constraints = new ConstraintContext();

        var shares = new PhaseFiveCompression()
            .GenerateAllocationSharesForTest(tasks, request, constraints, Lineage());

        // (a) 逐 Allocation 的 Σ 精确等于该 Allocation 的声明量（末位补差保证）
        foreach (var alloc in shares.GroupBy(s => s.AllocationSequence))
        {
            var declared = request.LogicalProductionDemands
                .Where(d => d.AllocationSequence == alloc.Key)
                .Sum(d => d.NetOutputQty);

            Assert.Equal(declared, alloc.Sum(s => s.ComponentQty));
        }

        // (b) ★ 逐 Task 的 Σ **严格等于** `Quantity` —— 精确相等，不是「小于容差」
        var byTask = shares
            .GroupBy(s => s.FinalDraftId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.ComponentQty));

        foreach (var task in tasks)
        {
            Assert.Equal(task.Quantity, byTask[task.FinalDraftId]);
        }

        // (c) 钉死判别器本体：T1 的份额必须是 7.2234（修复前为 7.225）
        Assert.Equal(7.2234m, byTask["T1"]);
    }

    // ════════════════════════════════════════════════════════════════════
    //  2. 修复后：真实硬校验 `ValidateHardResult` 放行
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 用**修复后**的份额驱动真实硬校验 ⇒ 通过。
    /// </summary>
    [Fact]
    public void 硬校验_修复后份额通过()
    {
        var tasks = Tasks();
        var request = Request();
        var constraints = new ConstraintContext();
        var lineage = Lineage();

        var shares = new PhaseFiveCompression()
            .GenerateAllocationSharesForTest(tasks, request, constraints, lineage);

        var (isValid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, shares, new List<FinalTaskPeggingDraft>(), request, constraints,
            new HashSet<string>(), lineage);

        Assert.True(isValid, error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  3. ★鉴别性：**修复前**的份额（逐条 3 位量化）必被硬校验拒绝
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **这是本类最重要的一条** —— 证明「逐条 3 位量化」这条真实路径**会被硬校验拦下**，
    /// 从而证明修复是**必要的**（不是「结果一致性」护栏，而是**可构造的反例**）。
    ///
    /// 份额取**修复前**实现的实际输出（逐条 `Math.Round(c,3)`；末位补差）：
    ///   alloc1: T1 3.334 ＋ T2 6.666 = 10.000
    ///   alloc2: T1 2.223 ＋ T3 2.777 =  5.000
    ///   alloc3: T1 1.112 ＋ T4 3.888 =  5.000
    ///   alloc4: T1 0.556 ＋ T5 1.444 =  2.000
    /// ⇒ 逐 Allocation 的 Σ **仍精确**（所以 check #1 不会拦），
    ///   但 `ΣShare(T1) = 7.225 > 7.2234 + 0.001` ⇒ **check #3 拦下**（漂移 +0.0016）。
    /// </summary>
    [Fact]
    public void 鉴别性_修复前逐条三位量化会被硬校验拒绝()
    {
        var tasks = Tasks();
        var request = Request();
        var constraints = new ConstraintContext();
        var lineage = Lineage();

        var legacyShares = new List<AllocationTaskShare>
        {
            Share("T1", 1L, 3.334m), Share("T2", 1L, 6.666m),
            Share("T1", 2L, 2.223m), Share("T3", 2L, 2.777m),
            Share("T1", 3L, 1.112m), Share("T4", 3L, 3.888m),
            Share("T1", 4L, 0.556m), Share("T5", 4L, 1.444m),
        };

        // 前置：修复前的份额**逐 Allocation 仍然精确** ⇒ 失败**不是** check #1 造成的
        foreach (var alloc in legacyShares.GroupBy(s => s.AllocationSequence))
        {
            var declared = request.LogicalProductionDemands
                .Where(d => d.AllocationSequence == alloc.Key)
                .Sum(d => d.NetOutputQty);

            Assert.Equal(declared, alloc.Sum(s => s.ComponentQty));
        }

        var (isValid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, legacyShares, new List<FinalTaskPeggingDraft>(), request, constraints,
            new HashSet<string>(), lineage);

        Assert.False(isValid);
        Assert.Contains("TaskShare数量不闭合", error);   // 冻结词，`03 v2.0 §41:1106`
        Assert.Contains("T1", error);
        Assert.Contains("7.225", error);                 // ΣShare（修复前）
        Assert.Contains("7.2234", error);                // Quantity
    }
}
