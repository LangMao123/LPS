using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace LPS.APS.Application.Extensions;

/// <summary>
/// Application 层 DI 注册扩展（3号位）
/// </summary>
public static class ApplicationServiceExtensions
{
    /// <summary>
    /// 注册应用服务（Scrutor 自动扫描）
    /// 新增服务只需在 Services 命名空间下创建 IXxxService + XxxService，无需手动注册
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.Scan(scan => scan
            .FromAssemblies(assembly)
                .AddClasses(classes => classes.Where(t =>
                    t.Namespace != null &&
                    t.Namespace.StartsWith("LPS.APS.Application.Services") &&
                    !t.Namespace.StartsWith("LPS.APS.Application.Services.Fixtures") &&
                    t.DeclaringType == null &&
                    !typeof(Exception).IsAssignableFrom(t) && // 异常类排除：防 Scrutor 把伴生异常（如 SnapshotRunResolutionException）误当 Scoped 服务注册，ValidateOnBuild 因构造参数无法解析而启动失败
                    !IsRecord(t))) // record 排除：record 天生实现 IEquatable<T>，会被 AsImplementedInterfaces 当服务注册（2026-10-13 FenceParameters/FenceResolution/AnchorDiagnostic 即因此致 ValidateOnBuild 启动失败）
                .AsImplementedInterfaces()
                .WithScopedLifetime()
        );

        return services;
    }

    /// <summary>
    /// record 判定：编译器为 record 生成非公开的 <c>EqualityContract</c> 属性（record class 特有）。
    /// record struct 无需判定 —— Scrutor <c>AddClasses</c> 默认按 <c>IsClass &amp;&amp; !IsAbstract</c> 过滤，值类型本就进不来。
    /// </summary>
    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) != null;
}
