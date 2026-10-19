using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 红线⑤⑥ SH 校验（ValidateShConsistency 纯函数）测试。
/// 契约（PM《pm回复826》§2 逐字：「SH场景下 Transit / Received 必须同SH匹配，不能串SH」＋
///        「同一SH的Transit、Received、未生产份额不得重复计量」；《pm回复828》:95 同；
///        《APS_V1_2号位5号位接口裁决临时说明_v1.0_20260827》§3.2/§4.3「DocumentNo 是否等于当前SH号」「禁止跨SH串用」）：
///   ⑤ SH 同 SH 匹配不串 SH —— 分配携带的 SH 身份必须落在分配上，且与该供给条目**自身的 SH 身份**一致；
///   ⑥ 同一 SH 份额不重复计量 —— 同一 SH 分配合计不得超过其指令总量（Order.Quantity）。
///
/// 【⚠ 不得按 DemandKey 分组】冻结正文逐字「第一层普通Demand**可由一张或多张SH承接**，
///   普通Demand↔SH**允许多对多**」（Pegging v1.6 §6.3 / §12.1；业务基线 v1.13 §40.1）——
///   一个需求跨多张 SH 是**正例**，本文件用 `同一需求跨两个SH_冻结正例_通过` 钉死这一点。
/// </summary>
public class ValidateShConsistencyTests
{
    private static SupplyAllocationItem Sh(string demandKey, string shNo, decimal qty)
        => new()
        {
            AllocationSequence = 1,
            DemandKey = demandKey,
            ShippingInstructionNo = shNo,
            AllocatedQuantity = qty,
            SourceType = SupplySourceType.INTER_FACTORY_ORDER,
            SourceReference = shNo
        };

    [Fact]
    public void 无SH分配_通过()
    {
        var allocations = new List<SupplyAllocationItem>
        {
            new() { DemandKey = "D1", AllocatedQuantity = 10m, SourceType = SupplySourceType.INVENTORY }
        };

        var errors = PeggingOrchestrator.ValidateShConsistency(allocations, new Dictionary<string, decimal>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void 单需求单SH_不超总量_通过()
    {
        var allocations = new[] { Sh("D1", "SH-100", 30m) };
        var totals = new Dictionary<string, decimal> { ["SH-100"] = 50m };

        var errors = PeggingOrchestrator.ValidateShConsistency(allocations, totals);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void 同一需求跨两个SH_冻结正例_通过()
    {
        // 冻结正文逐字（Pegging v1.6 §12.1 / 业务基线 v1.13 §40.1）：
        //   「第一层普通Demand可由一张或多张SH承接，普通Demand↔SH允许多对多」——
        //   需求A 100件 ├─ SH001 40件 └─ SH002 60件 是**正例**。
        var allocations = new[]
        {
            Sh("D1", "SH-100", 20m),
            Sh("D1", "SH-200", 10m)
        };
        var totals = new Dictionary<string, decimal> { ["SH-100"] = 50m, ["SH-200"] = 50m };

        var errors = PeggingOrchestrator.ValidateShConsistency(allocations, totals);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void SH分配携带的SH身份与供给自身不一致_打跨SH串用()
    {
        // ⑤ 原文：同一SH号下，Transit 的 SourceDocumentNo 必须等于 Received 的 DocumentNo，不得跨SH串用。
        // 落成分配 = 分配携带的出荷指示号 与 该供给条目自身的 SH 身份（SourceReference）不一致。
        var allocations = new[]
        {
            new SupplyAllocationItem
            {
                AllocationSequence = 1,
                DemandKey = "D1",
                ShippingInstructionNo = "SH-100",   // 分配上写的 SH
                AllocatedQuantity = 20m,
                SourceType = SupplySourceType.INTER_FACTORY_ORDER,
                SourceReference = "SH-200"          // 实际消费的供给条目是另一张 SH
            }
        };

        var errors = PeggingOrchestrator.ValidateShConsistency(
            allocations, new Dictionary<string, decimal> { ["SH-100"] = 50m, ["SH-200"] = 50m });

        errors.Should().ContainSingle(e => e.Contains("跨SH串用") && e.Contains("SH-100") && e.Contains("SH-200"));
    }

    [Fact]
    public void SH供给分配未落出荷指示号_打身份缺失()
    {
        var allocations = new[]
        {
            new SupplyAllocationItem
            {
                AllocationSequence = 1,
                DemandKey = "D1",
                ShippingInstructionNo = null,
                AllocatedQuantity = 20m,
                SourceType = SupplySourceType.INTER_FACTORY_ORDER,
                SourceReference = "SH-100"
            }
        };

        var errors = PeggingOrchestrator.ValidateShConsistency(
            allocations, new Dictionary<string, decimal> { ["SH-100"] = 50m });

        errors.Should().ContainSingle(e => e.Contains("SH 身份缺失") && e.Contains("D1"));
    }

    [Fact]
    public void 非SH供给不参与SH红线_通过()
    {
        // SourceType 非 INTER_FACTORY_ORDER 的分配（即使误带 ShippingInstructionNo）不进 SH 红线。
        var allocations = new[]
        {
            new SupplyAllocationItem
            {
                AllocationSequence = 1,
                DemandKey = "D1",
                ShippingInstructionNo = "SH-100",
                AllocatedQuantity = 999m,
                SourceType = SupplySourceType.INVENTORY,
                SourceReference = "BATCH-1"
            }
        };

        var errors = PeggingOrchestrator.ValidateShConsistency(
            allocations, new Dictionary<string, decimal> { ["SH-100"] = 1m });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void 不同需求各自SH_不误报()
    {
        var allocations = new[]
        {
            Sh("D1", "SH-100", 20m),
            Sh("D2", "SH-200", 10m)
        };
        var totals = new Dictionary<string, decimal> { ["SH-100"] = 50m, ["SH-200"] = 50m };

        var errors = PeggingOrchestrator.ValidateShConsistency(allocations, totals);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void SH分配合计超总量_打重复计量()
    {
        // 同一 SH（单一供给身份）被多个需求分配合计 120 > 指令总量 100
        var allocations = new[]
        {
            Sh("D1", "SH-100", 60m),
            new SupplyAllocationItem
            {
                AllocationSequence = 2,
                DemandKey = "D2",
                ShippingInstructionNo = "SH-100",
                AllocatedQuantity = 60m,
                SourceType = SupplySourceType.INTER_FACTORY_ORDER,
                SourceReference = "SH-100"
            }
        };
        var totals = new Dictionary<string, decimal> { ["SH-100"] = 100m };

        var errors = PeggingOrchestrator.ValidateShConsistency(allocations, totals);

        errors.Should().ContainSingle(e => e.Contains("重复计量") && e.Contains("SH-100"));
    }
}
