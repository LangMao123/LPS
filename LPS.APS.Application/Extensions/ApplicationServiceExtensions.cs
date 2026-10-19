using System.Reflection;
using LPS.APS.Core.Extensions;
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
                    // 嵌套／异常／record 排除：record 天生实现 IEquatable<T>，会被 AsImplementedInterfaces 当服务注册，
                    // ValidateOnBuild 因构造参数无法解析而启动失败（谓词与 Engine 侧扫描共用，见 ServiceScanFilters）。
                    ServiceScanFilters.IsServiceCandidate(t)))
                .AsImplementedInterfaces()
                .WithScopedLifetime()
        );

        return services;
    }
}
