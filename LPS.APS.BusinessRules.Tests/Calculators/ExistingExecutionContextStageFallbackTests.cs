using LPS.APS.BusinessRules.Calculators;
using LPS.APS.Core.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace LPS.APS.BusinessRules.Tests.Calculators;

/// <summary>
/// ExistingExecutionContext Stage 兜底测试（2号位 v2.1 §四修复）
///
/// 场景：工序层全部完成（RemainingQty=0）但 Stage 层仍有剩余（RemainingQty>0）
/// 修复前：「全部完成」分支静默归零，405 个工单 E=0
/// 修复后：回退取 Stage RemainingQty，按工单 PlannedQty 比例拆分
/// </summary>
public class ExistingExecutionContextStageFallbackTests
{
    private readonly ProductionInstructionPositionCalculator _calculator;

    public ExistingExecutionContextStageFallbackTests()
    {
        _calculator = new ProductionInstructionPositionCalculator(NullLogger<ProductionInstructionPositionCalculator>.Instance);
    }

    /// <summary>
    /// 单工单：工序全部完成（GoodQty=PlannedQty），Stage 剩余 500
    /// 期望：DerivedRemainingQty = 500（唯一工单，比例=100%）
    /// </summary>
    [Test]
    public async Task StageFallback_SingleWorkOrder_TakesFullStageRemaining()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001",
                    MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001",
                    PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS",
                    DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A",
                    OperationName = "装配",
                    StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001",
                    PlannedQty = 1000m,
                    GoodQty = 1000m,       // 已完工
                    RemainingQty = 0m       // 工序层无剩余
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY",
                    GoodCompletedQty = 1000m,
                    PlannedQty = 1500m,
                    RemainingQty = 500m,    // Stage 层仍有 500 剩余
                    StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.That(ctx.StartOperationCode, Is.Null);          // 工序全完成，无前沿工序
        Assert.That(ctx.DerivedRemainingQty, Is.EqualTo(500m)); // Stage 兜底生效
        Assert.That(ctx.StartStageCode, Is.EqualTo("CN_ASSY"));
    }

    /// <summary>
    /// 双工单：工序都全完成，Stage 剩余 600，PlannedQty 比例 1:2
    /// 期望：WO-A = 200，WO-B = 400，Σ E = 600（比例拆分闭合）
    /// </summary>
    [Test]
    public async Task StageFallback_TwoWorkOrders_SplitsByPlannedQtyRatio()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-A",
                    MaterialCode = "MAT-001", PlannedQty = 200m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                },
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-B",
                    MaterialCode = "MAT-001", PlannedQty = 400m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-A", PlannedQty = 200m, GoodQty = 200m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-B", PlannedQty = 400m, GoodQty = 400m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 600m,
                    PlannedQty = 1200m, RemainingQty = 600m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var contexts = results.First().ExistingExecutionContexts;
        Assert.That(contexts.Count, Is.EqualTo(2));

        var woA = contexts.Single(c => c.MESWorkOrderNo == "WO-A");
        var woB = contexts.Single(c => c.MESWorkOrderNo == "WO-B");

        // 600 × (200/600) = 200；600 × (400/600) = 400
        Assert.That(woA.DerivedRemainingQty, Is.EqualTo(200m));
        Assert.That(woB.DerivedRemainingQty, Is.EqualTo(400m));
        Assert.That(woA.DerivedRemainingQty + woB.DerivedRemainingQty, Is.EqualTo(600m));
    }

    /// <summary>
    /// 混现场景（2号位 v2.1 残留口径）：同 PI 内「全部完成工单」+「无工序数据工单」并存
    /// 修复前：无工序数据工单取 Stage 全量 1000（Math.Min 截不住，因 WO-B PlannedQty=2000 > 1000）
    ///         → ΣE = 130.43 + 1000 = 1130.43 > Stage 剩余 1000，超分
    /// 修复后：两路都按 PlannedQty 比例拆分 → ΣE = Stage RemainingQty 闭合
    /// </summary>
    [Test]
    public async Task StageFallback_MixedCompletedAndNoOps_SigmaDoesNotExceedStageRemaining()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                // WO-A：有工序数据，全部完成
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-A",
                    MaterialCode = "MAT-001", PlannedQty = 300m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                },
                // WO-B：无任何工序进度数据，且 PlannedQty > Stage 剩余（暴露旧分支全量超分）
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-B",
                    MaterialCode = "MAT-001", PlannedQty = 2000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                // 只有 WO-A 的工序（已完工）；WO-B 无工序行
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-A", PlannedQty = 300m, GoodQty = 300m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 300m,
                    PlannedQty = 2300m, RemainingQty = 1000m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var contexts = results.First().ExistingExecutionContexts;
        Assert.That(contexts.Count, Is.EqualTo(2));

        var woA = contexts.Single(c => c.MESWorkOrderNo == "WO-A");
        var woB = contexts.Single(c => c.MESWorkOrderNo == "WO-B");

        // 1000 × 300/2300 ≈ 130.43；1000 × 2000/2300 ≈ 869.57
        Assert.That(woA.DerivedRemainingQty, Is.EqualTo(1000m * 300m / 2300m).Within(0.0001m));
        Assert.That(woB.DerivedRemainingQty, Is.EqualTo(1000m * 2000m / 2300m).Within(0.0001m));

        // 核心断言1：WO-B 不再取 Stage 全量（旧分支会给 1000）
        Assert.That(woB.DerivedRemainingQty, Is.LessThan(1000m));

        // 核心断言2：ΣE 闭合到 Stage 剩余，不超分
        Assert.That(woA.DerivedRemainingQty + woB.DerivedRemainingQty, Is.EqualTo(1000m).Within(0.0001m));
    }

    /// <summary>
    /// 工序全部完成且 Stage 也无剩余 → DerivedRemainingQty = 0（真完成，不兜底）
    /// </summary>
    [Test]
    public async Task StageFallback_StageAlsoComplete_ReturnsZero()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 1000m, GoodQty = 1000m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 1000m,
                    PlannedQty = 1000m, RemainingQty = 0m,   // Stage 也无剩余
                    StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.That(ctx.DerivedRemainingQty, Is.EqualTo(0m));
        Assert.That(ctx.StartOperationCode, Is.Null);
    }

    /// <summary>
    /// 构造最小输入（ErpRemainingQty 取 Stage GoodCompleted+Remaining 使 Position 闭合）
    /// </summary>
    private static ProductionInstructionPositionInput BuildInput(
        IReadOnlyList<WorkOrderSnapshotFact> workOrders,
        IReadOnlyList<OperationProgressFact> operationProgress,
        IReadOnlyList<StageProgressFact> stageProgress)
    {
        var stage = stageProgress.First();
        return new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = stage.GoodCompletedQty,   // Position 闭合用（Stage 差分口径）
            StageProgress = stageProgress,
            OperationProgress = operationProgress,
            WorkOrders = workOrders,
            StagePath = new[]
            {
                new StagePathFact { StageCode = stage.StageCode, StageSequence = 1, IsStartStage = true }
            }
        };
    }
}
