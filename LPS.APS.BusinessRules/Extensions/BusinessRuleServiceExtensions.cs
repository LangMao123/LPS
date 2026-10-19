using System.Reflection;
using LPS.APS.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LPS.APS.BusinessRules.Extensions;

/// <summary>
/// BusinessRules 层 DI 注册扩展（5号位）
/// </summary>
public static class BusinessRuleServiceExtensions
{
    /// <summary>
    /// 注册业务规则服务（Scrutor 自动扫描）
    /// V1正式路径：Calculators / Services / Repositories / Loaders / Converters
    /// </summary>
    public static IServiceCollection AddBusinessRuleServices(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.Scan(scan => scan
            .FromAssemblies(assembly)
                .AddClasses(classes => classes.Where(t =>
                    t.Namespace != null &&
                    (t.Namespace.StartsWith("LPS.APS.BusinessRules.Calculators") ||
                     t.Namespace.StartsWith("LPS.APS.BusinessRules.Services") ||
                     t.Namespace.StartsWith("LPS.APS.BusinessRules.Repositories") ||
                     t.Namespace.StartsWith("LPS.APS.BusinessRules.Loaders") ||
                     t.Namespace.StartsWith("LPS.APS.BusinessRules.Converters")) &&
                    // 嵌套／异常／record 排除（谓词与 Application、Engine 侧扫描共用同一份）：
                    // record 天生实现 IEquatable<T>，被 AsSelfWithInterfaces/AsImplementedInterfaces 当服务注册后
                    // ValidateOnBuild 因构造参数无法解析而「启动即崩」——2026-10-13／10-17 两次实例见 ServiceScanFilters 注释。
                    // 2026-10-17 2号位 指出本处缺此守卫；当时五个命名空间 record 数为 0，属潜在风险。
                    ServiceScanFilters.IsServiceCandidate(t)))
                .AsSelfWithInterfaces()
                .WithScopedLifetime()
        );

        return services;
    }
}
