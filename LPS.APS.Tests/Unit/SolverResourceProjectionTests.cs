using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 人工槽合成键的**边界语义**单测（G-17 上界守卫）。
///
/// 断言目的**不是**「守卫函数返回了 true/false」，而是它的**声明用途**：
///   ① 上界守卫的翻转点必须**恰好**等于「`偏移 + ManualSlotId` 不再溢出 `int`」的边界；
///   ② 越界时合成键会**掉出人工槽数值空间**（落回设备 `Resource.Id` 区间）—— 这才是真正的危害
///      （不是抛异常，而是**静默与真实设备撞号**）；守卫必须在该情形下为 false；
///   ③ 未越界时 `IsManualSlotId` / `ToManualSlotId` 对合成键**互逆**，拆回无损。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class SolverResourceProjectionTests
{
    // 与实现同源的常数（不硬编码字面量，避免实现改动后单测「恰好还绿」）
    private const int Offset = SolverResourceProjection.ManualSlotResourceOffset;
    private const int MaxGuard = SolverResourceProjection.ManualSlotIdMaxGuard;

    [Fact]
    public void 两个常数必须满足合成不溢出的等价关系()
    {
        // 声明用途：「偏移 + MaxGuard」恰为 int.MaxValue —— 守卫上界即溢出上界。
        (Offset + MaxGuard).Should().Be(int.MaxValue);
        MaxGuard.Should().Be(int.MaxValue - Offset);
    }

    [Theory]
    [InlineData(1, true)]                    // 下界内侧
    [InlineData(2, true)]
    [InlineData(MaxGuard - 1, true)]         // 上界内侧
    [InlineData(MaxGuard, true)]             // 上界**闭**区间端点
    [InlineData(MaxGuard + 1, false)]        // 上界外一格 ⇒ 必须翻转
    [InlineData(0, false)]                   // 非正数：拆回后不构成有效槽 Id
    [InlineData(-1, false)]
    public void 守卫在溢出边界两侧翻转(int manualSlotId, bool expected)
    {
        SolverResourceProjection.IsWithinManualSlotRange(manualSlotId).Should().Be(expected);
    }

    [Fact]
    public void 守卫返回false时必须对应合成键已掉出人工槽空间()
    {
        // 越界一格的槽 Id：合成结果溢出 int，回卷为负数 ⇒ 不再满足 IsManualSlotId。
        int outOfRange = MaxGuard + 1;                          // 运行时局部量，避免编译期常量溢出报错
        int synthetic = Offset + outOfRange;

        SolverResourceProjection.IsWithinManualSlotRange(outOfRange).Should().BeFalse();

        // 声明用途的核心断言：守卫报警 ⇔ 合成键**静默落入设备 Resource.Id 数值空间**。
        synthetic.Should().BeLessThan(Offset, "越界合成键回卷后不再是人工槽键");
        SolverResourceProjection.IsManualSlotId(synthetic).Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(18_376)]     // 实测 ManualSlotId 下沿
    [InlineData(33_735)]     // 实测 ManualSlotId 上沿
    [InlineData(MaxGuard)]
    public void 区间内合成与拆回必须互逆(int manualSlotId)
    {
        SolverResourceProjection.IsWithinManualSlotRange(manualSlotId).Should().BeTrue();

        int synthetic = Offset + manualSlotId;
        SolverResourceProjection.IsManualSlotId(synthetic).Should().BeTrue();
        SolverResourceProjection.ToManualSlotId(synthetic).Should().Be(manualSlotId);
    }

    [Fact]
    public void 设备真实ResourceId不得被当作人工槽()
    {
        // 实测设备 Resource.Id 区间 6,444–14,565，全部应判 false（与人工槽空间不相交）。
        foreach (var deviceResourceId in new[] { 1, 6_444, 14_508, 14_565, Offset - 1 })
            SolverResourceProjection.IsManualSlotId(deviceResourceId)
                .Should().BeFalse($"设备 ResourceId={deviceResourceId} 不应落入人工槽空间");
    }

    // ─────────────────────── R-2 留痕增强：纯设备白名单（1号位 2026-10-16 回执 §四）───────────────────────

    [Fact]
    public void 纯设备白名单必须剔除人工槽合成键()
    {
        // 声明用途：`DeviceResourceIds` 存在的**唯一理由**是让 1号位 能区分
        //   「含人工槽」与「纯设备」两个 R-2 口径。⇒ 若白名单里混进人工槽键，该字段就完全失去意义。
        int[] mixed = [6_444, Offset + 18_376, 14_565, Offset + 33_735, 1];

        var devices = SolverResourceProjection.SelectDeviceResourceIds(mixed);

        devices.Should().Equal(new[] { 1, 6_444, 14_565 }, "只留设备、按升序；人工槽合成键一个都不能在");
        devices.Should().OnlyContain(id => !SolverResourceProjection.IsManualSlotId(id));
    }

    [Fact]
    public void 纯设备白名单升序去重且与输入顺序无关()
    {
        int[] forward  = [14_565, 1, 6_444, 6_444];
        int[] backward = [6_444, 6_444, 1, 14_565];

        var a = SolverResourceProjection.SelectDeviceResourceIds(forward);
        var b = SolverResourceProjection.SelectDeviceResourceIds(backward);

        a.Should().Equal(1, 6_444, 14_565);
        a.Should().Equal(b, "结果必须确定性（1号位 据此算排序键，顺序漂移会造出伪差异）");
    }

    [Fact]
    public void 全为人工槽时白名单为空_但不得与未投影混淆()
    {
        // 「已投影但无纯设备」是有信息量的状态（≠ 未投影）。本函数只负责筛，
        //   null（未投影）语义由调用方（装载点）决定 ⇒ 此处断言**空集**、不返回 null。
        var devices = SolverResourceProjection.SelectDeviceResourceIds(
            new[] { Offset + 18_376, Offset + 33_735 });

        devices.Should().BeEmpty();
        devices.Should().NotBeNull();
    }

    [Fact]
    public void 纯设备白名单必须剔除0与负数()
    {
        // 声明用途（J14-07）：0/负数都不是 `Resource` 主数据里的设备（实测最小 Id=6,444）。
        //   原「解析失败回落 0 并照常入集」会给 Solver 造出一台编号 0 的幽灵设备。
        int[] mixed = [0, -1, -6_444, 1, 6_444, Offset + 18_376];

        var devices = SolverResourceProjection.SelectDeviceResourceIds(mixed);

        devices.Should().Equal(1, 6_444);
        devices.Should().OnlyContain(id => id > 0);
    }

    [Fact]
    public void 空输入返回空集而非抛异常()
    {
        SolverResourceProjection.SelectDeviceResourceIds(Array.Empty<int>()).Should().BeEmpty();
    }
}
