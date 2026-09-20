using LPS.APS.Core.Interfaces;
using LPS.APS.Scheduling.Solvers;
using Microsoft.Extensions.DependencyInjection;

namespace LPS.APS.Scheduling.Extensions;

/// <summary>
/// Scheduling 层 DI 注册扩展
/// </summary>
public static class SchedulingServiceExtensions
{
    /// <summary>
    /// 注册排程算法服务（1号位）
    /// </summary>
    public static IServiceCollection AddSchedulingServices(this IServiceCollection services)
    {
        // 注册1号位核心接口（IFiniteCapacityScheduler）——唯一真实生产入口，内部走 SolveAsync → Phase1-5
        services.AddSingleton<IFiniteCapacityScheduler, FiniteCapacitySolver>();

        // 【遗留注册】以下两个组件均为死代码/未启用（见各自文件头注释）：
        // - TimeSlotFinder：只被 FiniteCapacitySolver 的死代码 Solve()/Reschedule() 使用
        // - SetupOptimizer：FiniteCapacitySolver 构造时实例化但从不调用
        // 生产流程不依赖它们，保留注册仅为避免 DI 缺失报错 / 历史兼容，可后续清理。
        services.AddSingleton<TimeSlotFinder>();
        services.AddSingleton<SetupOptimizer>();

        return services;
    }
}
