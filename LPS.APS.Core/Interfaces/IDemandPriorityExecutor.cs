using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 需求优先级执行器（2号位职责 — 消费3号位的 DemandPriorityConfig）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出 FrozenStrategySnapshot.DemandPriority（DemandPriorityConfig）
/// - 2号位负责执行器实现：按策略对 UpstreamDemand 排序，并回填 DemandSequence
///
/// 执行算法（PM 冻结口径）：
/// 1. 按 CalculationLayer 分层
/// 2. 每层内按 SegmentOrder 升序遍历 Segment
/// 3. 每个 Demand 从第一个 Segment 开始匹配，命中第一条后停止（First Match）
/// 4. 每个 Segment 内部按 SortFields 依次排序
/// 5. StableTieBreak 确保确定性（最终兜底：DemandKey ASC）
/// </summary>
public interface IDemandPriorityExecutor
{
    /// <summary>
    /// 执行 Demand 排序，返回有序列表并已赋值 DemandSequence = 1, 2, 3...
    /// </summary>
    List<UpstreamDemand> ExecutePrioritySort(
        IEnumerable<UpstreamDemand> demands,
        DemandPriorityConfig config);
}
