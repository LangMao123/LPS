using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 「一物料一条链」的物料级收敛器（0号位 2026-10-17 第十四轮 J14-03／J14-04）。
///
/// 断言目的＝**它的声明用途**（`self-check-must-assert-its-stated-purpose`）：
///   本载体的硬不变量是「每物料恰好一条链或零条」。当同一物料在一次运行内被多个需求以**不同角色/请求**
///   收敛出不同链时，载体表达不了 —— 必须**隔离该物料并留诊断**，不得任取一条（1号位 按 MaterialId
///   last-wins，会把静默丢弃搬到下游）。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class DemandStageChainAggregationTests
{
    private static IReadOnlyList<StageSequenceStep> Chain(params (int Seq, string Code)[] steps)
        => steps.Select(s => new StageSequenceStep { StageCode = s.Code, StageSeq = s.Seq }).ToList();

    private static PeggingOrchestrator.DemandChainPick Pick(
        int materialId, string chainKey, string role = "EDGE", params (int, string)[] steps)
        => new(materialId, chainKey, Chain(steps), role);

    [Fact]
    public void 单需求单链_正常出链()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(100, "10:MACH|20:SURF", steps: new[] { (10, "MACH"), (20, "SURF") })
        });

        r.Chains.Should().ContainSingle();
        r.Chains[0].MaterialId.Should().Be(100);
        r.Chains[0].Stages.Select(s => s.StageCode).Should().Equal("MACH", "SURF");
        r.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void 同物料多需求收敛出同一链_去重为一条而非冲突()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(100, "10:MACH|20:SURF", "EDGE", new[] { (10, "MACH"), (20, "SURF") }),
            Pick(100, "10:MACH|20:SURF", "EDGE", new[] { (10, "MACH"), (20, "SURF") }),   // 另一订单/另一片
        });

        r.Chains.Should().ContainSingle("链签名相同 ⇒ 是同一物理链，不应制造伪冲突");
        r.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void 同物料既作子件又作顶层根需求且链不同_必须隔离并报出全部候选()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(100, "10:MACH|20:SURF", "EDGE", new[] { (10, "MACH"), (20, "SURF") }),
            Pick(100, "10:MACH|20:SURF|30:ASM", "ROOT", new[] { (10, "MACH"), (20, "SURF"), (30, "ASM") }),
        });

        r.Chains.Should().BeEmpty("载体每物料仅容一条，任取一条都会在 1号位 last-wins 下静默覆盖另一条");
        r.Conflicts.Should().ContainKey(100);
        r.Conflicts[100].Should().Equal("10:MACH|20:SURF", "10:MACH|20:SURF|30:ASM");
    }

    [Fact]
    public void 冲突签名按ordinal升序_与输入顺序无关()
    {
        var a = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(7, "30:Z", "ROOT", new[] { (30, "Z") }),
            Pick(7, "10:A|20:B", "EDGE", new[] { (10, "A"), (20, "B") }),
        });
        var b = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(7, "10:A|20:B", "EDGE", new[] { (10, "A"), (20, "B") }),
            Pick(7, "30:Z", "ROOT", new[] { (30, "Z") }),
        });

        a.Conflicts[7].Should().Equal(b.Conflicts[7]);
        a.Conflicts[7].Should().Equal("10:A|20:B", "30:Z");
    }

    [Fact]
    public void 多个物料的出链按MaterialId升序_确定性()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(300, "10:C", steps: new[] { (10, "C") }),
            Pick(100, "10:A", steps: new[] { (10, "A") }),
            Pick(200, "10:B", steps: new[] { (10, "B") }),
        });

        r.Chains.Select(c => c.MaterialId).Should().Equal(100, 200, 300);
    }

    [Fact]
    public void 一个物料冲突不影响其它物料出链()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(new[]
        {
            Pick(100, "10:A", "EDGE", new[] { (10, "A") }),
            Pick(100, "10:A|20:X", "ROOT", new[] { (10, "A"), (20, "X") }),
            Pick(200, "10:B", steps: new[] { (10, "B") }),
        });

        r.Chains.Select(c => c.MaterialId).Should().Equal(200);
        r.Conflicts.Keys.Should().Equal(100);
    }

    [Fact]
    public void 无需求选链_空结果()
    {
        var r = PeggingOrchestrator.AggregateDemandChains(Array.Empty<PeggingOrchestrator.DemandChainPick>());

        r.Chains.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty();
    }
}
