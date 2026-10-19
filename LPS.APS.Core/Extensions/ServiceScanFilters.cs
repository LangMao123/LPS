using System.Reflection;

namespace LPS.APS.Core.Extensions;

/// <summary>
/// Scrutor 自动扫描的**共用过滤谓词**。
///
/// 扫描一律以 <c>AsImplementedInterfaces()</c> 注册，而有两类类型「天生带接口」，会被误当服务：
///   - **record** 天生实现 <c>IEquatable&lt;T&gt;</c>，注册后 <c>ValidateOnBuild</c> 构造时
///     因参数（如 <c>string</c>／<c>bool</c>／<c>DateTime</c>）无法解析而**启动即失败**；
///   - **嵌套类型**是对外层类型的实现细节，不应被当独立服务拖进容器。
///
/// 已发生两次：2026-10-13 <c>FenceParameters／FenceResolution／AnchorDiagnostic</c>、
/// 2026-10-17 <c>BomNightBatchResolver.PushRecord／Resolution</c>（均在
/// <c>LPS.APS.Engine.Services.Sync</c> 下）。谓词放在这里，两个扫描点共用一份，避免再各写一套而漏掉其中一处。
/// </summary>
public static class ServiceScanFilters
{
    /// <summary>
    /// record 判定：编译器为 record class 生成非公开的 <c>EqualityContract</c> 属性。
    /// record struct 无需判定 —— Scrutor <c>AddClasses</c> 默认按 <c>IsClass &amp;&amp; !IsAbstract</c> 过滤，值类型本就进不来。
    /// </summary>
    public static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) != null;

    /// <summary>可参与 Scrutor 扫描的类型：非嵌套、非异常、非 record。</summary>
    public static bool IsServiceCandidate(Type type)
        => type.DeclaringType == null
           && !typeof(Exception).IsAssignableFrom(type)
           && !IsRecord(type);
}
