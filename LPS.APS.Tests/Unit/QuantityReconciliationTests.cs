using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 「生产需求净量 = 已排净量 + 未排净量」恒等式的钉死测试（P3「未排净量闭合验证」）。
///
/// 【为什么必须有这组测试】
///   该恒等式的原实现内联在 `PeggingOrchestrator` 的事务体里 —— **无法被测试**，于是键域取错
///   一周都没人发现（见下方「键域回归」）。这次把它抽成纯函数，恒等式与**四种失效模式**
///   逐条钉住；任何一条断言失守，就说明「交给 1号位 的数量口径」或「落库对账行」又不可信了。
///
/// 【真实数据形状（实库 2026-10-13 复测）】
///   `AllocationTaskShare.DemandKey` = `ORDER_363139_SYN-FG-001_2`
///   `ScheduleExplanationFact.EvidenceJson.LogicalDemandKey` = `2_265307` / `142_1`
///   ⇒ 两者**不同命名空间**，本组测试的第 1 条即锁死这一点。
/// </summary>
public class QuantityReconciliationTests
{
    // ── 构造器 ──────────────────────────────────────────────────────────

    private static LogicalProductionDemand Demand(string key, decimal netQty, long allocSeq = 1)
        => new() { LogicalDemandKey = key, NetOutputQty = netQty, AllocationSequence = allocSeq };

    /// <summary>1号位 回传的任务：`FinalDraftId` 是 GUID、`SourceDraftId` 才是 LogicalDemandKey。</summary>
    private static FinalTaskDraft Task_(string finalDraftId, string sourceDraftId)
        => new() { FinalDraftId = finalDraftId, SourceDraftId = sourceDraftId };

    private static AllocationTaskShare Share(string finalDraftId, long allocSeq, decimal qty)
        => new() { FinalDraftId = finalDraftId, AllocationSequence = allocSeq, ComponentQty = qty };

    private static UnscheduledTaskResult Unsched(string draftId, string reason = "NO_RESOURCE")
        => new() { DraftId = draftId, Reason = reason };

    private static QuantityReconciliation.RunLevelReconciliation Reconcile(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyDictionary<string, decimal> scheduled,
        params UnscheduledTaskResult[] unscheduled)
        => QuantityReconciliation.Reconcile(demands, scheduled, unscheduled);

    // ═══════════════════════════════════════════════════════════════════
    // 一、键域回归：份额必须按 LogicalDemandKey 归集（走 FinalDraftId→SourceDraftId）
    //     这条锁死「原来的 bug」：按 SupplyAllocation.DemandKey 归集会恒不命中。
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 份额按_SourceDraftId_归集_而不是按业务DemandKey()
    {
        var finalTasks = new[] { Task_("guid-1", "2_265307") };
        var shares = new[] { Share("guid-1", allocSeq: 265307, qty: 100m) };

        var agg = QuantityReconciliation.AggregateScheduledByDemandKey(finalTasks, shares);

        agg.UnmappedShareCount.Should().Be(0);
        agg.ScheduledQtyByDemandKey.Should().ContainKey("2_265307");
        agg.ScheduledQtyByDemandKey["2_265307"].Should().Be(100m);
        // 业务键 `ORDER_…` **绝不**应是聚合键（它与 UnscheduledTaskResult.DraftId 不可比）
        agg.ScheduledQtyByDemandKey.Keys.Should().NotContain(k => k.StartsWith("ORDER_"));
    }

    [Fact]
    public void 同需求多份额累加()
    {
        var finalTasks = new[] { Task_("g1", "2_1"), Task_("g2", "2_1") };
        var shares = new[] { Share("g1", 1, 30m), Share("g2", 1, 70m) };

        var agg = QuantityReconciliation.AggregateScheduledByDemandKey(finalTasks, shares);

        agg.ScheduledQtyByDemandKey["2_1"].Should().Be(100m);
    }

    [Fact]
    public void 份额的FinalDraftId找不到对应任务_计入未映射且不进字典()
    {
        var finalTasks = new[] { Task_("g1", "2_1") };
        var shares = new[] { Share("g1", 1, 30m), Share("orphan-guid", 1, 70m) };

        var agg = QuantityReconciliation.AggregateScheduledByDemandKey(finalTasks, shares);

        agg.UnmappedShareCount.Should().Be(1);
        agg.ScheduledQtyByDemandKey["2_1"].Should().Be(30m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 二、恒等式闭合的正例：净 1000 / 已排 800 / 未排 200
    //     （部分已排 ⇒ 该需求同时在「已排」与「未排」两侧，OverlapDetected=true 是**正常**形态）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 净1000_已排800_未排200_闭合_Δ为零且三条清单全空()
    {
        var demands = new[] { Demand("P_1", 1000m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 800m };

        var r = Reconcile(demands, scheduled, Unsched("P_1"));

        r.DemandNetQtyTotal.Should().Be(1000m);
        r.ScheduledQtyTotal.Should().Be(800m);
        r.UnscheduledQtyTotal.Should().Be(200m);
        r.DeltaQty.Should().Be(0m);
        r.MissingDemandKeys.Should().BeEmpty();
        r.UnderScheduledDemandKeys.Should().BeEmpty();
        r.UnmatchedScheduledKeys.Should().BeEmpty();
    }

    [Fact]
    public void 部分已排同时又报未排程_OverlapDetected为真且未排量等于剩余()
    {
        var (remaining, overlap) = QuantityReconciliation.PerDemandRemaining(1000m, 800m);

        remaining.Should().Be(200m);
        overlap.Should().BeTrue();
    }

    [Fact]
    public void 未排量不得为负_已排超过净量时截零()
    {
        var (remaining, overlap) = QuantityReconciliation.PerDemandRemaining(100m, 250m);

        remaining.Should().Be(0m);
        overlap.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 三、恒等式闭合的另两个正例：整单已排 / 整单未排
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 整单已排_无未排_闭合()
    {
        var demands = new[] { Demand("P_1", 1000m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 1000m };

        var r = Reconcile(demands, scheduled);

        r.DeltaQty.Should().Be(0m);
        r.UnscheduledQtyTotal.Should().Be(0m);
        r.ScheduledDemandCount.Should().Be(1);
        r.MissingDemandKeys.Should().BeEmpty();
        r.UnderScheduledDemandKeys.Should().BeEmpty();
    }

    [Fact]
    public void 整单未排_无份额_闭合()
    {
        var demands = new[] { Demand("P_1", 1000m) };

        var r = Reconcile(demands, new Dictionary<string, decimal>(), Unsched("P_1"));

        r.DeltaQty.Should().Be(0m);
        r.ScheduledQtyTotal.Should().Be(0m);
        r.UnscheduledQtyTotal.Should().Be(1000m);
        r.MissingDemandKeys.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 四、失效模式 ③之一：凭空蒸发（既无份额、又未报未排程）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 无份额且未报未排程_进缺失清单且Δ为正()
    {
        var demands = new[] { Demand("P_1", 1000m), Demand("P_2", 500m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 1000m };

        var r = Reconcile(demands, scheduled);

        r.DeltaQty.Should().Be(500m);
        r.MissingDemandKeys.Should().ContainSingle().Which.Should().Be("P_2");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 五、失效模式 ③之二（**原口径的哑火点**）：部分已排、却未被报未排程
    //     Δ 会报警，但它**确实产生了份额** ⇒ 旧口径的 Missing 名单抓不到它 ⇒ 报警没有线索。
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 部分已排但未报未排程_进部分已排清单而不是缺失清单()
    {
        var demands = new[] { Demand("P_1", 1000m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 300m };

        var r = Reconcile(demands, scheduled);   // 未报未排程

        r.DeltaQty.Should().Be(700m);
        r.MissingDemandKeys.Should().BeEmpty();              // ← 关键：不能落进 Missing
        r.UnderScheduledDemandKeys.Should().ContainSingle().Which.Should().Be("P_1");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 六、失效模式 ④：份额归到一个不在需求集里的键
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 份额键不在需求集_进越域清单且Δ为负()
    {
        var demands = new[] { Demand("P_1", 1000m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 1000m, ["X_9"] = 250m };

        var r = Reconcile(demands, scheduled);

        r.DeltaQty.Should().Be(-250m);
        r.UnmatchedScheduledKeys.Should().ContainSingle().Which.Should().Be("X_9");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 七、分片：一个 AllocationSequence 派生多个 LogicalDemandKey，各自独立对账
    //     （`CloneDemandSlice` 产出 `{base}/WO:…`、`{base}/FREE`）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 分片需求各自独立对账_按片自己的键归集份额()
    {
        var demands = new[]
        {
            Demand("2_5/WO:W1", 300m, allocSeq: 5),
            Demand("2_5/FREE",  200m, allocSeq: 5),
        };
        var finalTasks = new[] { Task_("g1", "2_5/WO:W1"), Task_("g2", "2_5/FREE") };
        var shares = new[] { Share("g1", 5, 300m), Share("g2", 5, 120m) };

        var agg = QuantityReconciliation.AggregateScheduledByDemandKey(finalTasks, shares);
        var r = Reconcile(demands, agg.ScheduledQtyByDemandKey, Unsched("2_5/FREE"));

        agg.ScheduledQtyByDemandKey["2_5/WO:W1"].Should().Be(300m);
        agg.ScheduledQtyByDemandKey["2_5/FREE"].Should().Be(120m);
        // 工单片整片已排 ⇒ 闭合；自由片排了 120/200、剩 80 已报未排 ⇒ 也闭合
        r.DemandNetQtyTotal.Should().Be(500m);
        r.ScheduledQtyTotal.Should().Be(420m);
        r.UnscheduledQtyTotal.Should().Be(80m);
        r.DeltaQty.Should().Be(0m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 八、计数与同键去重
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 同键需求重复出现_总量不重复计入()
    {
        var demands = new[] { Demand("P_1", 1000m), Demand("P_1", 1000m) };
        var scheduled = new Dictionary<string, decimal> { ["P_1"] = 1000m };

        var r = Reconcile(demands, scheduled);

        r.DemandCount.Should().Be(1);
        r.DemandNetQtyTotal.Should().Be(1000m);
        r.DeltaQty.Should().Be(0m);
    }

    [Fact]
    public void 空需求集_三量全零_Δ为零()
    {
        var r = Reconcile(Array.Empty<LogicalProductionDemand>(), new Dictionary<string, decimal>());

        r.DeltaQty.Should().Be(0m);
        r.DemandCount.Should().Be(0);
        r.MissingDemandKeys.Should().BeEmpty();
    }
}
