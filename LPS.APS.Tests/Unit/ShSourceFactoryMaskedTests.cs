using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A5：SH 源厂「目标厂静默冒充源工厂」判据单测（冻结 DDL `08:83`）。
///
/// 断言目的是**声明用途本身**，不是「函数返回了 false」：
///   ① 判据必须**只在源厂有值且等于目标厂**时为真 —— 这是 `08:83` 禁的那一句的逐字范围；
///   ② **源厂 NULL 不得被判为冒充**（那是「源厂缺失」，另一种性质；若并入 ⇒ 会把实库那 1 行
///      非跨厂 NULL 与 65,824 行等值行混成同一个数字，报出去的计数就没有意义了）；
///   ③ 源厂 ≠ 目标厂 ⇒ 正是口径 C 落地后的**健康态**，不得报警。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class ShSourceFactoryMaskedTests
{
    private const int TargetFactory = 8;      // 目标厂（[Order].FactoryId）
    private const int OtherFactory = 3;       // 任一**不等于**目标厂的厂

    [Fact]
    public void 源厂等于目标厂_判为冒充()
    {
        PeggingOrchestrator.IsSourceFactoryMasked(TargetFactory, TargetFactory)
            .Should().BeTrue();
    }

    [Fact]
    public void 源厂为Null_不得判为冒充()
    {
        PeggingOrchestrator.IsSourceFactoryMasked(null, TargetFactory)
            .Should().BeFalse("NULL 是「源厂缺失」，与「冒充」是两种性质，不得合并计数");
    }

    [Fact]
    public void 源厂不等于目标厂_不得判为冒充()
    {
        PeggingOrchestrator.IsSourceFactoryMasked(OtherFactory, TargetFactory)
            .Should().BeFalse("源厂≠目标厂 是口径 C 落地后的健康态");
    }

    [Fact]
    public void 与目标厂无关的第三厂_不得判为冒充()
    {
        // 防「实现写成任意比较」：只有**恰好等于**目标厂才算冒充。
        PeggingOrchestrator.IsSourceFactoryMasked(OtherFactory, OtherFactory)
            .Should().BeTrue("源厂等于它自己的目标厂时仍属冒充 —— 判据只看两侧是否相等");
        PeggingOrchestrator.IsSourceFactoryMasked(OtherFactory + 1, OtherFactory)
            .Should().BeFalse();
    }
}
