using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 落库前 Task 隔离的**级联**行为（0号位 2026-10-17 第十六轮 J16-04）。
///
/// 断言目的是「隔离不得只做一半」：
///   ① 前置 Task 因部门/资源事实被隔离时，依赖它的**后序 Task 不得单独发布**（否则失去前驱约束）；
///   ② 多级链要传到不动点，不是只处理一跳；
///   ③ 与隔离无关的 Task 一律保留（不得误伤）；
///   ④ 结果确定性（同输入同输出）。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class TaskIsolationCascadeTests
{
    private static FinalTaskDraft Task(string id) => new() { FinalDraftId = id, SourceDraftId = "D_" + id };

    private static FinalTaskPeggingDraft Edge(string upstream, string downstream) => new()
    {
        UpstreamFinalDraftId = upstream,
        DownstreamFinalDraftId = downstream
    };

    [Fact]
    public void 前置被隔离_后序一并隔离()
    {
        var tasks = new[] { Task("A"), Task("B") };
        var edges = new[] { Edge("A", "B") };

        var (executable, cascaded) = PeggingOrchestrator.CascadeExcludeDependents(
            tasks, edges, new HashSet<string> { "A" });

        executable.Select(t => t.FinalDraftId).Should().BeEmpty("B 的上游 A 不落库，B 不得单独发布");
        cascaded.Select(t => t.FinalDraftId).Should().Equal("B");
    }

    [Fact]
    public void 多级链_级联到不动点()
    {
        var tasks = new[] { Task("A"), Task("B"), Task("C"), Task("D") };
        var edges = new[] { Edge("A", "B"), Edge("B", "C"), Edge("C", "D") };

        var (executable, cascaded) = PeggingOrchestrator.CascadeExcludeDependents(
            tasks, edges, new HashSet<string> { "A" });

        executable.Should().BeEmpty("A→B→C→D 整条链都建立在不落库的上游上");
        cascaded.Select(t => t.FinalDraftId).Should().Equal("B", "C", "D");
    }

    [Fact]
    public void 无隔离输入_原样保留()
    {
        var tasks = new[] { Task("A"), Task("B") };
        var edges = new[] { Edge("A", "B") };

        var (executable, cascaded) = PeggingOrchestrator.CascadeExcludeDependents(
            tasks, edges, new HashSet<string>());

        executable.Select(t => t.FinalDraftId).Should().Equal("A", "B");
        cascaded.Should().BeEmpty();
    }

    [Fact]
    public void 上游存活时_后序保留_不误伤()
    {
        var tasks = new[] { Task("A"), Task("B"), Task("C") };
        var edges = new[] { Edge("A", "B"), Edge("A", "C") };

        var (executable, cascaded) = PeggingOrchestrator.CascadeExcludeDependents(
            tasks, edges, new HashSet<string> { "Z" });

        executable.Select(t => t.FinalDraftId).Should().Equal("A", "B", "C");
        cascaded.Should().BeEmpty();
    }

    [Fact]
    public void 输出顺序与入参一致_与扫描轮次无关()
    {
        // 入参顺序 D,C,B,A；级联结果必须仍按入参顺序给 B,C,D，而不是按发现顺序
        var tasks = new[] { Task("D"), Task("C"), Task("B"), Task("A") };
        var edges = new[] { Edge("A", "B"), Edge("B", "C"), Edge("C", "D") };

        var (executable, cascaded) = PeggingOrchestrator.CascadeExcludeDependents(
            tasks, edges, new HashSet<string> { "A" });

        executable.Should().BeEmpty();
        cascaded.Select(t => t.FinalDraftId).Should().Equal("D", "C", "B");
    }
}
