using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A-6：`INTERPLANT_IN_TRANSIT` **供给身份硬闸**单测（审核位 2026-10-16 J12-06）。
///
/// 断言目的是**闸门的声明用途本身**，不是「函数返回了 true」：
///   ① **只拦冻结禁的那一个类型** —— 宽一格会把合法采购供给（`PURCHASE_IN_TRANSIT` /
///      `OPEN_PO_REMAINING` / `ARRIVED_NOT_RECEIVED` / `VMI_ONSITE`）整批挡在池外 ⇒ 需求全塌成新生产，
///      比不设闸更危险；
///   ② **大小写不构成绕过路径** —— 身份判据只看字面语义，不因拼写漂移放行；
///   ③ **不得写成「包含」匹配** —— `INTERPLANT_IN_TRANSIT_*` 之类的近似串、以及跨厂的另一个类型
///      `INTER_FACTORY_ORDER`（SH 供给，**合法**、须入池）都**不得**被误拦。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class ForbiddenStandaloneSupplyTypeTests
{
    [Fact]
    public void 冻结禁令类型_必须拦截()
    {
        PeggingOrchestrator
            .IsForbiddenStandaloneSupplyType(PeggingOrchestrator.InterplantInTransitSupplyType)
            .Should().BeTrue("厂间在途不是独立 Supply，它只是 PI Position 的一种当前位置");
    }

    [Theory]
    [InlineData("interplant_in_transit")]
    [InlineData("Interplant_In_Transit")]
    [InlineData("INTERPLANT_in_TRANSIT")]
    public void 大小写漂移_不得绕过闸门(string drifted)
    {
        PeggingOrchestrator.IsForbiddenStandaloneSupplyType(drifted)
            .Should().BeTrue("身份判据不因拼写大小写放行");
    }

    [Theory]
    [InlineData("PURCHASE_IN_TRANSIT")]
    [InlineData("OPEN_PO_REMAINING")]
    [InlineData("ARRIVED_NOT_RECEIVED")]
    [InlineData("VMI_ONSITE")]
    [InlineData("INVENTORY")]
    [InlineData("WIP")]
    public void 合法供给类型_一律放行(string legal)
    {
        PeggingOrchestrator.IsForbiddenStandaloneSupplyType(legal)
            .Should().BeFalse("宽一格即把合法采购供给整批挡在池外");
    }

    [Fact]
    public void 跨厂SH类型_是合法供给_不得误拦()
    {
        // `INTER_FACTORY_ORDER`（SH 单一供给身份）与禁令类型前缀相近，最易被「包含式」实现误伤。
        PeggingOrchestrator.IsForbiddenStandaloneSupplyType("INTER_FACTORY_ORDER")
            .Should().BeFalse("SH 订单是合法供给身份，按 PM 2026-09-09 v1.0 须入池");
    }

    [Theory]
    [InlineData("INTERPLANT_IN_TRANSIT_X")]
    [InlineData("X_INTERPLANT_IN_TRANSIT")]
    [InlineData(" INTERPLANT_IN_TRANSIT")]
    [InlineData("INTERPLANT_IN_TRANSIT ")]
    public void 近似的非等值串_不得被包含式匹配误拦(string nearMiss)
    {
        // 「严格等值（忽略大小写）」才拦；写成 Contains/StartsWith 会把变体也挡掉，那是超范围拦截。
        PeggingOrchestrator.IsForbiddenStandaloneSupplyType(nearMiss)
            .Should().BeFalse("只有恰好等于禁令类型字面值才拦，不做 Trim 也不做包含匹配");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空值_不得判为禁令类型(string? empty)
    {
        PeggingOrchestrator.IsForbiddenStandaloneSupplyType(empty)
            .Should().BeFalse();
    }

    [Fact]
    public void 禁令常量_逐字等于冻结类型名()
    {
        // 冻结词表字面值（若日后口径变更，必须先改常量再改判据，不得只改一处）。
        PeggingOrchestrator.InterplantInTransitSupplyType.Should().Be("INTERPLANT_IN_TRANSIT");
    }
}
