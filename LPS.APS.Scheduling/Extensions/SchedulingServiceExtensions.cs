using LPS.APS.Scheduling.Solvers;
using Microsoft.Extensions.DependencyInjection;

namespace LPS.APS.Scheduling.Extensions;

/// <summary>
/// Scheduling 层 DI 注册扩展
/// </summary>
public static class SchedulingServiceExtensions
{
    /// <summary>
    /// 注册排程算法服务
    /// </summary>
    public static IServiceCollection AddSchedulingServices(this IServiceCollection services)
    {
        services.AddSingleton<FiniteCapacitySolver>();
        services.AddSingleton<TimeSlotFinder>();
        services.AddSingleton<SetupOptimizer>();

        return services;
    }
}
