using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// PI Stage 顺序的 **scope 选择**（0号位 第十四轮 J14-12）。
///
/// 断言目的＝**它的声明用途**：
///   ① 「按 scope 分开取」必须真的分开 —— `ROOT`（自身完工的完整链）与 `EDGE`（父件阈值截断的链段）
///      **不得**被并成一个阶段集。旧实现 `MIN(StageSeq)` 跨 scope 分组，结果等于「取两边更小的那个」，
///      这个形态没有任何业务口径，且无法从结果差异回溯 —— 本组测试的第一条就是钉死它。
///   ② 选择口径 ROOT → EDGE 兜底：PI 是物料**自身**的生产指示，完整链才能定位在制位置。
///   ③ 两 scope 对同一 Stage 给出不同 Seq ⇒ 必须**计数并可留痕**，不得静默择一。
///   ④ 批次图覆盖兜底图、兜底图补齐批次缺的阶段；两个计数必须与声明一致。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class StageScopeSelectionTests
{
    private static Dictionary<string, int> Map(params (string Stage, int Seq)[] rows)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (stage, seq) in rows) d[stage] = seq;
        return d;
    }

    [Fact]
    public void 两种scope存在时只取ROOT_不得并成两边合集()
    {
        // 声明用途的核心断言：ROOT 有 A，EDGE 有 B ⇒ 结果只能有 A。
        // 若这里出现 {A, B}，说明「按 scope 分开取」退化成并集，与本函数存在的理由相悖。
        var sel = PeggingOrchestrator.SelectStageScope(
            rootBatch: Map(("A", 10)),
            rootFallback: null,
            edgeBatch: Map(("B", 10)),
            edgeFallback: null);

        sel.Scope.Should().Be("ROOT");
        sel.Stages.Keys.Should().BeEquivalentTo(new[] { "A" }, "取 ROOT 就不能把 EDGE 的阶段并进来");
        sel.Stages.Should().NotContainKey("B");
    }

    [Fact]
    public void 同Stage两scope不同Seq_必须取ROOT的值而不是较小值()
    {
        // 旧实现 MIN(StageSeq) 取 10；按 scope 分开后必须取 ROOT 的 20。
        var sel = PeggingOrchestrator.SelectStageScope(
            rootBatch: Map(("CN6_MACH", 20)),
            rootFallback: null,
            edgeBatch: Map(("CN6_MACH", 10)),
            edgeFallback: null);

        sel.Scope.Should().Be("ROOT");
        sel.Stages["CN6_MACH"].Should().Be(20, "MIN 跨 scope 取的是「谁更小」，不代表任何业务口径");
        sel.ConflictPairs.Should().Be(1);
        sel.ConflictSamples.Should().ContainSingle()
            .Which.Should().Contain("CN6_MACH:ROOT=20,EDGE=10");
    }

    [Fact]
    public void 只有EDGE时按EDGE兜底_且如实报告分歧为零()
    {
        var sel = PeggingOrchestrator.SelectStageScope(
            rootBatch: null, rootFallback: null,
            edgeBatch: Map(("CNC", 10), ("GRIND", 20)),
            edgeFallback: null);

        sel.Scope.Should().Be("EDGE");
        sel.Stages.Should().HaveCount(2);
        sel.ConflictPairs.Should().Be(0, "没有 ROOT 就没有可比对的另一侧，不能虚报分歧");
    }

    [Fact]
    public void 两scope都为空时返回空结果_而不是抛异常或造空链()
    {
        var sel = PeggingOrchestrator.SelectStageScope(null, null, null, null);

        sel.Scope.Should().BeNull("「没有链」必须可与「有链但是空」区分");
        sel.Stages.Should().BeEmpty();
    }

    [Fact]
    public void 同Stage两scopeSeq相同不算分歧()
    {
        var sel = PeggingOrchestrator.SelectStageScope(
            rootBatch: Map(("A", 10), ("B", 20)),
            rootFallback: null,
            edgeBatch: Map(("A", 10), ("B", 20)),
            edgeFallback: null);

        sel.ConflictPairs.Should().Be(0);
        sel.ConflictSamples.Should().BeEmpty();
    }

    [Fact]
    public void 批次图覆盖兜底图_兜底图补齐批次缺的阶段()
    {
        // 批次：A=10；兜底：A=99（应被覆盖）、B=20（应被补齐）
        var sel = PeggingOrchestrator.SelectStageScope(
            rootBatch: Map(("A", 10)),
            rootFallback: Map(("A", 99), ("B", 20)),
            edgeBatch: null, edgeFallback: null);

        sel.Scope.Should().Be("ROOT");
        sel.Stages["A"].Should().Be(10, "批次内是精确口径，必须压过跨批兜底");
        sel.Stages["B"].Should().Be(20);
        sel.BatchPairs.Should().Be(1);
        sel.FallbackPairs.Should().Be(1);
        (sel.BatchPairs + sel.FallbackPairs).Should().Be(sel.Stages.Count, "两个计数必须能还原链的长度");
    }

    [Fact]
    public void 分歧样本至多十个但计数按全量()
    {
        var root = new Dictionary<string, int>(StringComparer.Ordinal);
        var edge = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 25; i++)
        {
            root[$"S{i:00}"] = 100 + i;
            edge[$"S{i:00}"] = i;
        }

        var sel = PeggingOrchestrator.SelectStageScope(root, null, edge, null);

        sel.ConflictPairs.Should().Be(25, "计数是判据，不能被样本上限截断");
        sel.ConflictSamples.Should().HaveCount(10, "样本只为留痕，十个足够定位");
    }

    [Fact]
    public void 返回的阶段集不得被调用方意外改写()
    {
        // 调用方会把结果塞进按物料共享的字典；若这里返回内部可变引用，
        // 一处改写会污染其它物料 —— 故断言两次调用的结果互不影响。
        var a = PeggingOrchestrator.SelectStageScope(Map(("A", 10)), null, null, null);
        var b = PeggingOrchestrator.SelectStageScope(Map(("A", 10)), null, null, null);

        ReferenceEquals(a.Stages, b.Stages).Should().BeFalse();
        a.Stages.Should().BeEquivalentTo(b.Stages);
    }
}
