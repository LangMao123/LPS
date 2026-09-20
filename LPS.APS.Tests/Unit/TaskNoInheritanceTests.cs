using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// G4 TaskNo 三分支纯函数测试（冻结 §12.10 / T09/T10/T22 + R8 反证）。
/// 分支1 APS旧Task连续（有旧TaskNo→继承）；分支2 无TaskNo外部MES连续（无旧号→新号绑原工单）；分支3 自由（新号）。
/// MESWorkOrderNo 身份复用 LogicalDemandKey（"{源key}/WO:{wo}"）承载，落库前反解，不另设字段（§7.2）。
/// </summary>
public class TaskNoInheritanceTests
{
    [Theory]
    [InlineData("328_1/WO:WO001", "WO001")]
    [InlineData("328_9/WO:WO202", "WO202")]
    [InlineData("KEY/WO:MES-DUP-001", "MES-DUP-001")]
    public void ExtractMesWorkOrderNo_连续份额键_解析出工单号(string key, string expected)
        => PeggingOrchestrator.ExtractMesWorkOrderNo(key).Should().Be(expected);

    [Theory]
    [InlineData("328_1/FREE")]
    [InlineData("328_1")]
    [InlineData("PLAIN_KEY")]
    [InlineData(null)]
    [InlineData("")]
    public void ExtractMesWorkOrderNo_非连续键_返回null(string? key)
        => PeggingOrchestrator.ExtractMesWorkOrderNo(key).Should().BeNull();

    [Fact]
    public void ResolveTaskNo_连续且反查到旧号_继承旧TaskNo()
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal) { ["WO001"] = "PEGG-100-OLDTASK1" };

        var taskNo = PeggingOrchestrator.ResolveTaskNo(328, "abcdef12-3456", "WO001", old);

        taskNo.Should().Be("PEGG-100-OLDTASK1");   // 分支1：继承旧号，不新建（T10 / R8 反证）
    }

    [Fact]
    public void ResolveTaskNo_连续但无旧号_新TaskNo()
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal);

        var taskNo = PeggingOrchestrator.ResolveTaskNo(328, "abcdef12-3456", "WO999", old);

        taskNo.Should().Be("PEGG-328-abcdef12");   // 分支2：新号绑原工单（T02/T09/T22）
    }

    [Fact]
    public void ResolveTaskNo_自由份额_新TaskNo()
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal) { ["WO001"] = "PEGG-100-OLDTASK1" };

        var taskNo = PeggingOrchestrator.ResolveTaskNo(328, "abcdef12-3456", null, old);

        taskNo.Should().Be("PEGG-328-abcdef12");   // 分支3：自由=新号（不因字典有旧号而误继承）
    }

    [Fact]
    public void 分桶产出连续片_反解工单号_三分支身份闭环()
    {
        // 关键键闭环：分桶产出的 Continuation Key 必含 "/WO:{wo}"，能被 ExtractMesWorkOrderNo 反解，
        // 进而命中旧 TaskNo 映射实现继承——从分桶到落盘身份一致性。
        var demands = new List<LogicalProductionDemand>
        {
            new()
            {
                LogicalDemandKey       = "328_1",
                PlanVersionId          = 328,
                DomainKey              = "FAMILY_X",
                AllocationSequence     = 1,
                DemandKey              = "D-1",
                MaterialId             = 5001,
                FactoryId              = 7,
                StartStageCode         = "ST",
                NetOutputQty           = 100m,
                PlannedProcessQty      = 100m,
                ProductionInstructionNo = "PI-C01",
                IsContinuation         = false
            }
        };
        var ctxs = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI-C01"] = new[]
            {
                new ExistingExecutionContextDto
                {
                    ProductionInstructionNo = "PI-C01",
                    MESWorkOrderNo          = "WO001",
                    WorkOrderStatus         = "IN_PROGRESS",
                    StartStageCode          = "ST",
                    DerivedRemainingQty     = 30m
                }
            }
        };

        var bucketed = PeggingOrchestrator.BucketContinuityShares(demands, ctxs);
        var cont     = bucketed.Single(d => d.IsContinuation);
        var wo       = PeggingOrchestrator.ExtractMesWorkOrderNo(cont.LogicalDemandKey);

        wo.Should().Be("WO001");

        var old = new Dictionary<string, string>(StringComparer.Ordinal) { ["WO001"] = "PEGG-100-OLDTASK1" };
        PeggingOrchestrator.ResolveTaskNo(328, "whatever", wo, old).Should().Be("PEGG-100-OLDTASK1");
    }
}