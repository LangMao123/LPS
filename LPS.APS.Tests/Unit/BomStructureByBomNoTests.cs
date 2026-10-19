using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// BOM 展开结构的 **BOMNO 分桶**（2026-10-19）：同一父件在多个 BOMNO 下的子件边
/// **不得跨 BOM 求和**，否则一份订单的单位用量会被多份 BOM 叠加放大。
///
/// 背景：`LoadBomSnapshotAsync` 已按本 PV 的 `ResolvedBOMNO` 收窄，但一个 PV 里可能有多张
/// 不同的 BOM（不同成品/不同客户配置），同一父件在两张 BOM 下各有一条子件边是**两份独立事实**。
/// 一笔订单只走 `OrderBomRequestLink` 指定的一个 BOMNO ⇒ 展开必须按订单的 BOMNO 取边。
/// </summary>
public class BomStructureByBomNoTests
{
    private static readonly IReadOnlyDictionary<string, bool> NoPurchased =
        new Dictionary<string, bool>();

    private static PeggingOrchestrator.BomEdge Edge(string parent, string child, decimal qty, string? bomNo)
        => new(parent, child, ChildMaterialId: 1, qty, Level: 1, IsLeaf: false, IsPurchased: false,
               ChildRequiredStageCode: null, BomNo: bomNo);

    private static decimal Qty(LPS.APS.Application.Models.BomStructure structure, string parent, string child)
        => structure.ByParent[parent].Single(c => c.ChildCode == child).QtyPerUnit;

    [Fact]
    public void 跨BOMNO同父同子不同配比_各桶独立且并集为两者之和()
    {
        var edges = new[] { Edge("P", "C", 2m, "B1"), Edge("P", "C", 3m, "B2") };

        var (union, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        // 各 BOM 的桶只含自己那条边 —— 这是本改动的核心
        Qty(byBom["B1"], "P", "C").Should().Be(2m);
        Qty(byBom["B2"], "P", "C").Should().Be(3m);
        // 并集桶保持改动前口径（不同配比求和）
        Qty(union, "P", "C").Should().Be(5m);
    }

    [Fact]
    public void 跨BOMNO同配比_两边都保留且并集不双计()
    {
        var edges = new[] { Edge("P", "C", 2m, "B1"), Edge("P", "C", 2m, "B2") };

        var (union, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        byBom.Should().ContainKeys("B1", "B2");
        Qty(byBom["B1"], "P", "C").Should().Be(2m);
        Qty(byBom["B2"], "P", "C").Should().Be(2m);
        // 并集桶按 (父,子,配比) 去重后再求和 ⇒ 仍为 2，不是 4
        Qty(union, "P", "C").Should().Be(2m);
    }

    [Fact]
    public void 同BOMNO内同父同子多配比_桶内累加()
    {
        var edges = new[] { Edge("P", "C", 1.5m, "B1"), Edge("P", "C", 0.5m, "B1") };

        var (union, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        Qty(byBom["B1"], "P", "C").Should().Be(2m);   // 干净 BOM 语义：同 BOM 内多行配比累加
        Qty(union, "P", "C").Should().Be(2m);
    }

    [Fact]
    public void 无BOMNO的行只进并集桶()
    {
        var edges = new[] { Edge("P", "C", 2m, "B1"), Edge("P", "D", 1m, null) };

        var (union, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        union.ByParent["P"].Should().HaveCount(2);
        byBom["B1"].ByParent["P"].Should().ContainSingle().Which.ChildCode.Should().Be("C");
    }

    [Fact]
    public void 全部无BOMNO_不产生任何桶_并集等价旧口径()
    {
        var edges = new[] { Edge("P", "C", 2m, null), Edge("P", "C", 3m, null) };

        var (union, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        byBom.Should().BeEmpty();
        Qty(union, "P", "C").Should().Be(5m);
    }

    [Fact]
    public void 不同父件互不影响_桶按父件分组()
    {
        var edges = new[] { Edge("P1", "C", 2m, "B1"), Edge("P2", "C", 4m, "B1") };

        var (_, byBom) = PeggingOrchestrator.ToBomStructures(edges, NoPurchased);

        Qty(byBom["B1"], "P1", "C").Should().Be(2m);
        Qty(byBom["B1"], "P2", "C").Should().Be(4m);
    }
}
