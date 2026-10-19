using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 部门最小安全校验的分区（0号位 2026-10-17 第十四轮 J14-06）。
///
/// 断言目的＝**它的声明用途**：
///   ① 判据必须与**落库点同一个函数**（`ResolvePersistedDepartmentId`，`(MaterialId, StageCode)` 双键）
///      —— 否则「隔离集」与「落库集」之间会出现缝隙，缺口那批带着 NULL 部门照样落库；
///   ② `StageCode` 空 ⇒ **必然**进 Missing（`MaterialStageDeptContext.StageCode` 实测零空，空键不可能命中）；
///   ③ 同一物料不同 Stage 各自判定，不互相串用；
///   ④ 分区不改变顺序、不丢行（Executable + Missing = 全量）。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class DepartmentFailClosedPartitionTests
{
    private static FinalTaskDraft Task(int materialId, string stageCode, string? id = null)
        => new()
        {
            FinalDraftId = id ?? $"{materialId}-{stageCode}-{Guid.NewGuid():N}",
            MaterialId = materialId,
            StageCode = stageCode
        };

    private static Dictionary<(int MaterialId, string StageCode), int> Contexts(
        params (int MaterialId, string StageCode, int DeptId)[] rows)
        => rows.ToDictionary(r => (r.MaterialId, r.StageCode), r => r.DeptId);

    [Fact]
    public void 命中部门的Task可落库_缺失的进隔离集()
    {
        var tasks = new[] { Task(100, "CN_MACH"), Task(200, "CN_SURF") };
        var dept = Contexts((100, "CN_MACH", 7));

        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(tasks, dept);

        executable.Select(t => t.MaterialId).Should().Equal(100);
        missing.Select(t => t.MaterialId).Should().Equal(200);
    }

    [Fact]
    public void StageCode为空必须进隔离集_不得拿空键去撞()
    {
        // MSC 的 StageCode 实测零空 ⇒ 空键不可能命中；若这里被误判为可落库，
        //   Task.ProductionDepartmentId 就会落 NULL —— 正是 J14-06 要禁止的情形。
        var tasks = new[] { Task(100, string.Empty) };
        var dept = Contexts((100, string.Empty, 7));   // 即便上下文里真有空键行也不得命中

        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(tasks, dept);

        executable.Should().BeEmpty();
        missing.Should().ContainSingle();
    }

    [Fact]
    public void 同物料不同Stage各自判定_不互相串用()
    {
        var tasks = new[] { Task(100, "CN_MACH"), Task(100, "CN_SURF") };
        var dept = Contexts((100, "CN_MACH", 7));      // 只有 MACH 有部门

        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(tasks, dept);

        executable.Select(t => t.StageCode).Should().Equal("CN_MACH");
        missing.Select(t => t.StageCode).Should().Equal("CN_SURF");
    }

    [Fact]
    public void 分区不丢行且保持原顺序()
    {
        var tasks = new[]
        {
            Task(1, "A", "d1"), Task(2, "B", "d2"), Task(3, "C", "d3"), Task(4, "D", "d4")
        };
        var dept = Contexts((1, "A", 9), (3, "C", 9));

        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(tasks, dept);

        executable.Should().HaveCount(2);
        missing.Should().HaveCount(2);
        (executable.Count + missing.Count).Should().Be(tasks.Length, "分区必须守恒，不得丢行");
        executable.Select(t => t.FinalDraftId).Should().Equal("d1", "d3");
        missing.Select(t => t.FinalDraftId).Should().Equal("d2", "d4");
    }

    [Fact]
    public void 空上下文时全部隔离()
    {
        var tasks = new[] { Task(1, "A"), Task(2, "B") };

        var (executable, missing) =
            PeggingOrchestrator.PartitionByDepartment(tasks, new Dictionary<(int, string), int>());

        executable.Should().BeEmpty();
        missing.Should().HaveCount(2);
    }

    [Fact]
    public void 无FinalTask时两集皆空()
    {
        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(
            Array.Empty<FinalTaskDraft>(), new Dictionary<(int, string), int>());

        executable.Should().BeEmpty();
        missing.Should().BeEmpty();
    }

    [Fact]
    public void 分区判据与落库判据必须是同一函数()
    {
        // 声明用途断言：把「分区判为可落库」的每一条拿去直接解析，必须都拿得到部门 Id；
        //   反之亦然。这条防的是「分区用一套口径、落库用另一套」的缝隙。
        var tasks = new[] { Task(100, "CN_MACH"), Task(200, string.Empty), Task(300, "CN_SURF") };
        var dept = Contexts((100, "CN_MACH", 7), (300, "CN_SURF", 8));

        var (executable, missing) = PeggingOrchestrator.PartitionByDepartment(tasks, dept);

        executable.Should().OnlyContain(t =>
            PeggingOrchestrator.ResolvePersistedDepartmentId(dept, t.MaterialId, t.StageCode).HasValue);
        missing.Should().OnlyContain(t =>
            !PeggingOrchestrator.ResolvePersistedDepartmentId(dept, t.MaterialId, t.StageCode).HasValue);
    }
}
