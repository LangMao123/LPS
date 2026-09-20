using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 跨版本连续性分桶纯函数测试（逐 MES 工单契约，§8/§9/§16）。
/// 契约：Q=100、WO001 在制 E=30 → 1号位必须同时看到 Continuation 30 + Free 70；Q 不被 WIP 供给扣小。
/// 多工单绝不合并（P0 红线）；E&gt;Q 不砍单、不产新自由 Task、登记 Execution Over-Commit。
/// ContinuationKey = 源 key + "/WO:" + MESWorkOrderNo；Free = 源 key + "/FREE"；共用源 AllocationSequence。
/// PlannedProcessQty 逐片按材料级良率单位投入比（源 PlannedProcessQty/NetOutputQty）反算。
/// </summary>
public class ContinuityBucketingTests
{
    private static LogicalProductionDemand Demand(
        string key,
        long allocSeq,
        string? pi,
        decimal netQty,
        decimal processQty = 0m,
        string startStage = "ST")
        => new()
        {
            LogicalDemandKey       = key,
            PlanVersionId          = 328,
            DomainKey              = "FAMILY_X",
            AllocationSequence     = allocSeq,
            DemandKey              = $"D-{allocSeq}",
            OrderId                = 100L + allocSeq,
            MaterialId             = 5001,
            FactoryId              = 7,
            StartStageCode         = startStage,
            NetOutputQty           = netQty,
            PlannedProcessQty      = processQty > 0m ? processQty : netQty,
            ProductionInstructionNo = pi,
            IsContinuation         = false
        };

    private static ExistingExecutionContextDto Ctx(
        string pi, string wo, decimal remaining, string startStage = "MACH", string? startOp = null)
        => new()
        {
            ProductionInstructionNo = pi,
            MESWorkOrderNo          = wo,
            WorkOrderStatus         = "IN_PROGRESS",
            StartStageCode          = startStage,
            StartOperationCode      = startOp,
            DerivedRemainingQty     = remaining,
            DataCutoffTime          = DateTime.UtcNow
        };

    private static Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>> Map(
        params ExistingExecutionContextDto[] ctxs)
        => ctxs
            .GroupBy(c => c.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ExistingExecutionContextDto>)g.ToList(),
                StringComparer.Ordinal);

    private static List<LogicalProductionDemand> Bucket(
        List<LogicalProductionDemand> demands,
        Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>> contextsByPi,
        System.Action<PeggingOrchestrator.ContinuityOverCommitEvent>? onOverCommit = null)
        => PeggingOrchestrator.BucketContinuityShares(demands, contextsByPi, onOverCommit);

    [Fact]
    public void 单工单E小于Q_切出连续份额与自由份额()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_1", 1, "PI-C01", 100m) };
        var ctxs = Map(Ctx("PI-C01", "WO001", 30m));

        var result = Bucket(demands, ctxs);

        result.Should().HaveCount(2);
        var continuation = result.Single(d => d.IsContinuation);
        continuation.NetOutputQty.Should().Be(30m);
        continuation.LogicalDemandKey.Should().Be("328_1/WO:WO001");   // ContinuationKey 带工单身份
        continuation.AllocationSequence.Should().Be(1);                 // 不新建 Allocation
        continuation.DemandKey.Should().Be("D-1");

        var free = result.Single(d => !d.IsContinuation);
        free.NetOutputQty.Should().Be(70m);                             // Free = Q − E，非 Q 被扣小后的 40
        free.LogicalDemandKey.Should().Be("328_1/FREE");
    }

    [Fact]
    public void 多工单绝不合并_每张独立连续份额()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_2", 2, "PI-C02", 100m) };
        var ctxs = Map(Ctx("PI-C02", "WO101", 20m), Ctx("PI-C02", "WO102", 30m));

        var result = Bucket(demands, ctxs);

        var continuations = result.Where(d => d.IsContinuation).ToList();
        continuations.Should().HaveCount(2);                            // P0 红线：不合并成一条 50
        continuations.Select(c => c.NetOutputQty).Should().BeEquivalentTo(new[] { 20m, 30m });

        result.Single(d => !d.IsContinuation).NetOutputQty.Should().Be(50m);   // Free = Q − ΣE = 50
    }

    [Fact]
    public void 单工单E等于Q_只连续份额_不产生自由份额()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_3", 3, "PI-C03", 50m) };
        var ctxs = Map(Ctx("PI-C03", "WO001", 50m));

        var result = Bucket(demands, ctxs);

        result.Should().ContainSingle();
        result[0].IsContinuation.Should().BeTrue();
        result[0].NetOutputQty.Should().Be(50m);
    }

    [Fact]
    public void E大于Q_不砍单_只连续份额_登记执行超量()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_4", 4, "PI-C04", 50m) };
        var ctxs = Map(Ctx("PI-C04", "WO001", 70m));
        var over = new List<PeggingOrchestrator.ContinuityOverCommitEvent>();

        var result = Bucket(demands, ctxs, over.Add);

        result.Should().ContainSingle();                                // 不产自由 Task
        result[0].IsContinuation.Should().BeTrue();
        result[0].NetOutputQty.Should().Be(70m);                        // §9.1：不砍到 Q=50，C 原样 70
        over.Should().ContainSingle(o => o.Kind == "EXECUTION_OVER_COMMIT" && o.ExcessQty == 20m); // Execution Over-Commit=20
    }

    [Fact]
    public void Q等于0_在制仍IN_PROGRESS_不产需求_登记DemandMismatch()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_5", 5, "PI-C05", 0m) };
        var ctxs = Map(Ctx("PI-C05", "WO001", 20m));
        var over = new List<PeggingOrchestrator.ContinuityOverCommitEvent>();

        var result = Bucket(demands, ctxs, over.Add);

        result.Should().BeEmpty();                                      // §9.2：不生成正常 Demand/Task
        over.Should().ContainSingle(o => o.Kind == "DEMAND_MISMATCH" && o.ContinuationQty == 20m);
    }

    [Fact]
    public void 无PI_原样透传不切分()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_6", 6, null, 80m) };

        var result = Bucket(demands, Map());

        result.Should().ContainSingle();
        result[0].IsContinuation.Should().BeFalse();
        result[0].NetOutputQty.Should().Be(80m);
    }

    [Fact]
    public void 工单剩余为零_原样透传自由需求()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_7", 7, "PI-C07", 60m) };
        var ctxs = Map(Ctx("PI-C07", "WO001", 0m));

        var result = Bucket(demands, ctxs);

        result.Should().ContainSingle();
        result[0].IsContinuation.Should().BeFalse();
        result[0].NetOutputQty.Should().Be(60m);
    }

    [Fact]
    public void 续排起点从五号位带出_自由份额起点归零_PlannedProcessQty按良率反算()
    {
        var demands = new List<LogicalProductionDemand> { Demand("328_8", 8, "PI-C08", 100m, processQty: 120m) };
        var ctxs = Map(Ctx("PI-C08", "WO001", 30m, startStage: "MACH", startOp: "NC"));

        var result = Bucket(demands, ctxs);

        var continuation = result.Single(d => d.IsContinuation);
        continuation.StartOperationCode.Should().Be("NC");
        continuation.StartStageCode.Should().Be("MACH");
        continuation.PlannedProcessQty.Should().Be(36m);                // 30 × 120/100 反算

        var free = result.Single(d => !d.IsContinuation);
        free.StartOperationCode.Should().BeNull();                      // 自由=新生产量，起点归首工序
        free.StartStageCode.Should().Be("ST");                          // 沿用源需求起点
        free.PlannedProcessQty.Should().Be(84m);                        // 70 × 120/100 反算
    }

    [Fact]
    public void 实景修复后_多工单含部分完工_逐工单连续加一份自由_红线同现()
    {
        // 328 实景（5号位修好后）：一个 PI 下多张在制工单，有的有剩余、有的已完工(E=0)。
        // 本用例钉死两件事：① E=0 的工单绝不掺入连续份额；② 只要存在 E>0 的工单，Free 片必然同现。
        var demands = new List<LogicalProductionDemand> { Demand("328_9", 9, "PI-C09", 100m) };
        var ctxs = Map(
            Ctx("PI-C09", "WO201", 30m),
            Ctx("PI-C09", "WO202", 20m),
            Ctx("PI-C09", "WO203", 0m));    // 已完工，不产连续份额

        var result = Bucket(demands, ctxs);

        var continuations = result.Where(d => d.IsContinuation).ToList();
        continuations.Should().HaveCount(2);                              // 只 E>0 的两张，绝不掺入 E=0
        continuations.Sum(c => c.NetOutputQty).Should().Be(50m);
        continuations.Select(c => c.LogicalDemandKey)
            .Should().BeEquivalentTo(new[] { "328_9/WO:WO201", "328_9/WO:WO202" });

        var free = result.Single(d => !d.IsContinuation && d.LogicalDemandKey.EndsWith("/FREE"));
        free.NetOutputQty.Should().Be(50m);                               // Free = 100 − (30+20) 必然同现
    }
}