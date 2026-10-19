using FluentAssertions;
using LPS.APS.Core.Extensions;
using LPS.APS.Engine.Services.Sync;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>命名空间级 record（非嵌套）—— 验证 record 判定不依赖 DeclaringType。</summary>
public sealed record ScanProbeRecord(int Value, string Text);

/// <summary>命名空间级普通服务类。</summary>
public sealed class ScanProbeService
{
}

/// <summary>外层类 —— 其嵌套类用于验证嵌套判定不依赖 record。</summary>
public sealed class ScanProbeOuter
{
    public sealed class NestedService
    {
    }
}

/// <summary>
/// Scrutor 扫描过滤谓词的回归保护。
///
/// 断言对象用的是**真实致故障的类型**（<c>BomNightBatchResolver</c> 的两个嵌套 record），
/// 而不是临时造的同形样本 —— 2026-10-17 启动失败正是这两个类型被
/// <c>AsImplementedInterfaces()</c> 注册成 <c>IEquatable&lt;T&gt;</c>，<c>ValidateOnBuild</c>
/// 构造时解析不到 <c>string</c>／<c>bool</c> 参数。
/// </summary>
public class ServiceScanFiltersTests
{
    [Fact]
    public void 启动失败的两个嵌套record均不得成为扫描候选()
    {
        ServiceScanFilters.IsServiceCandidate(typeof(BomNightBatchResolver.PushRecord))
            .Should().BeFalse("PushRecord(string, DateTime) 被注册后 ValidateOnBuild 解析不到 string");
        ServiceScanFilters.IsServiceCandidate(typeof(BomNightBatchResolver.Resolution))
            .Should().BeFalse("Resolution(bool, string?, ...) 被注册后 ValidateOnBuild 解析不到 bool");
    }

    [Fact]
    public void 非嵌套record也按record排除()
    {
        ServiceScanFilters.IsRecord(typeof(ScanProbeRecord)).Should().BeTrue();
        ServiceScanFilters.IsServiceCandidate(typeof(ScanProbeRecord)).Should().BeFalse();
    }

    [Fact]
    public void 嵌套类型不因record判定而被放行()
    {
        // 嵌套的不是 record：record 判定为 false，但 DeclaringType 判定应拦下
        ServiceScanFilters.IsRecord(typeof(ScanProbeOuter.NestedService)).Should().BeFalse();
        ServiceScanFilters.IsServiceCandidate(typeof(ScanProbeOuter.NestedService)).Should().BeFalse();
    }

    [Fact]
    public void 普通服务类可成为扫描候选()
    {
        ServiceScanFilters.IsServiceCandidate(typeof(ScanProbeService)).Should().BeTrue();
        ServiceScanFilters.IsServiceCandidate(typeof(BomBatchExpandService)).Should().BeTrue();
    }

    [Fact]
    public void 异常类型不得成为扫描候选()
    {
        ServiceScanFilters.IsServiceCandidate(typeof(InvalidOperationException)).Should().BeFalse();
    }
}
