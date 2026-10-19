using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 产出侧 Inv2 三量闭合的**遍历域**（1号位 2026-10-17 §11.4 ②）。
///
/// 判据：`Σ ShareQty ＋ Σ U == Σ NetOutputQty`，逐 `AllocationSequence`，容差 1e-3。
/// 遍历域必须覆盖 `expectedByAlloc ∪ actualByAlloc`：
/// 只遍历「有份额行的 Allocation」时，**有需求、零份额、又没记 U** 的分配会整个跳过循环
/// ⇒ 静默通过 ⇒ 与该形态「显性失败、交 Routing 排查」的既定处理相反。
///
/// 全程不触库：编排器只用 Mock 构造，校验方法是纯内存计算。
/// </summary>
public class AllocationShareInvariantTests
{
    private static PeggingOrchestrator BuildOrchestrator()
    {
        var options = Options.Create(new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=APS" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=ODS" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=Auth" }
        });

        return new PeggingOrchestrator(
            Mock.Of<IDemandSupplyHardLockRepository>(),
            new DatabaseConnectionManager(options),
            NullLogger<PeggingOrchestrator>.Instance,
            Mock.Of<IFiniteCapacityScheduler>(),
            Mock.Of<IDemandPriorityExecutor>(),
            Mock.Of<IDemandPriorityConfigProvider>(),
            Mock.Of<IFrozenStrategySnapshotProvider>(),
            Mock.Of<ITimedSupplyFactLoader>(),
            Mock.Of<IProcurementManualEtaRepository>(),
            Mock.Of<IProductionInstructionPositionCalculator>(),
            Mock.Of<IProductionInstructionPositionSnapshotRepository>());
    }

    private static PeggingResultVoucher Voucher(params LogicalProductionDemand[] demands)
    {
        var v = new PeggingResultVoucher();
        foreach (var d in demands)
            v.LogicalProductionDemands.Add(d);
        return v;
    }

    private static LogicalProductionDemand Demand(string key, long alloc, decimal netQty) => new()
    {
        LogicalDemandKey = key,
        AllocationSequence = alloc,
        NetOutputQty = netQty
    };

    [Fact]
    public void 某Allocation一条份额行都没有且未记未排量_必须判Inv2违例()
    {
        var solve = new DomainSolveResult
        {
            AllocationShares = new[]
            {
                new AllocationTaskShare { FinalDraftId = "D1", AllocationSequence = 1, ComponentQty = 10m }
            }
            // AllocationSequence = 2 一条份额行都没有，且 UnscheduledTasks 里也没有它的 U
        };

        var voucher = Voucher(
            Demand("K1", 1, 10m),
            Demand("K2", 2, 10m));

        var map = new Dictionary<string, long> { ["D1"] = 100 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildOrchestrator().ValidateAllocationShareInvariants(solve, voucher, map));

        ex.Message.Should().Contain("seq=2",
            "零份额的分配同样必须过判：ΣShare=0 ＋ ΣU=0 ≠ ΣNetOutputQty=10");
        ex.Message.Should().Contain("ΣShareQty+ΣU=0");
    }

    [Fact]
    public void 零份额但未排量记满声明量_属合法全未排_不判违例()
    {
        var solve = new DomainSolveResult
        {
            AllocationShares = new[]
            {
                new AllocationTaskShare { FinalDraftId = "D1", AllocationSequence = 1, ComponentQty = 10m }
            },
            UnscheduledTasks = new[]
            {
                new UnscheduledTaskResult { DraftId = "K2", Reason = "MATERIAL_SHORTAGE", UnscheduledQty = 10m }
            }
        };

        var voucher = Voucher(
            Demand("K1", 1, 10m),
            Demand("K2", 2, 10m));

        var map = new Dictionary<string, long> { ["D1"] = 100 };

        BuildOrchestrator()
            .Invoking(o => o.ValidateAllocationShareInvariants(solve, voucher, map))
            .Should().NotThrow();
    }

    [Fact]
    public void 三量不闭合_报文同时给出份额与未排量分量()
    {
        var solve = new DomainSolveResult
        {
            AllocationShares = new[]
            {
                new AllocationTaskShare { FinalDraftId = "D1", AllocationSequence = 1, ComponentQty = 4m }
            },
            UnscheduledTasks = new[]
            {
                new UnscheduledTaskResult { DraftId = "K1", Reason = "MATERIAL_SHORTAGE", UnscheduledQty = 2m }
            }
        };

        var voucher = Voucher(Demand("K1", 1, 10m));
        var map = new Dictionary<string, long> { ["D1"] = 100 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildOrchestrator().ValidateAllocationShareInvariants(solve, voucher, map));

        ex.Message.Should().Contain("ΣShareQty+ΣU=6");
        ex.Message.Should().Contain("ΣShareQty=4");
        ex.Message.Should().Contain("ΣU=2");
        ex.Message.Should().Contain("ΣNetOutputQty=10");
    }

    [Fact]
    public void 未排量归属不到Allocation_必须显性失败而不是静默丢弃()
    {
        var solve = new DomainSolveResult
        {
            AllocationShares = new[]
            {
                new AllocationTaskShare { FinalDraftId = "D1", AllocationSequence = 1, ComponentQty = 10m }
            },
            UnscheduledTasks = new[]
            {
                // FinalDraftId 键域（不是 LogicalDemandKey）⇒ 归属不到
                new UnscheduledTaskResult { DraftId = "FD-9", Reason = "MATERIAL_SHORTAGE", UnscheduledQty = 3m }
            }
        };

        var voucher = Voucher(Demand("K1", 1, 10m));
        var map = new Dictionary<string, long> { ["D1"] = 100 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildOrchestrator().ValidateAllocationShareInvariants(solve, voucher, map));

        ex.Message.Should().Contain("未排量无法归属到Allocation");
    }
}
