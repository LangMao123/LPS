using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// Demand 优先级策略配置提供器（2号位消费侧 — 3号位产出侧）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出完整的 FrozenStrategySnapshot（其中包含 DemandPriorityConfig）
/// - 2号位通过本接口获取 DemandPriorityConfig 后交给 IDemandPriorityExecutor 执行
///
/// 当前实现：DemandPriorityFixtureProvider（临时 Fixture，仅用于2号位排序执行器联调）
/// 后续替换：3号位真实 FrozenStrategySnapshot 客户端（禁止 Fixture 成为生产 Fallback）
/// </summary>
public interface IDemandPriorityConfigProvider
{
    Task<DemandPriorityConfig> GetPriorityConfigAsync(
        long strategyProfileVersionId,
        CancellationToken cancellationToken = default);
}
