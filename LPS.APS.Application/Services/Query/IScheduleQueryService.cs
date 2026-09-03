using LPS.APS.Application.Services.Query.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// 排程结果查询服务（3号位接口域职责 — §阶段5.3/第八部分场景1）
///
/// 用途：供 4号位前端甘特图拉取排程结果，不触碰推演期的内存沙盘
/// 数据来源：已落盘的 APS_Production 库（PlanVersion + Task + Resource + Order + Material）
///
/// V1 范围：
///   - 版本列表（供前端切换查看）
///   - 甘特图数据（按 PlanVersionId 返回 Task + Resource 明细）
///   - 排程概要（KPI：已排/未排/延期计数）
///   - 候选对比（candidate vs base 两个版本间的 Task 变化，v1.2 §三 H1）
///
/// V2 扩展点（预留接口但暂不实现）：
///   - 战报查询（ExplainTrace）
///   - 局部重排触发
/// </summary>
public interface IScheduleQueryService
{
    /// <summary>
    /// 获取计划版本列表（最新优先）
    /// </summary>
    Task<IReadOnlyList<PlanVersionSummaryDto>> GetVersionsAsync(
        int take = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定计划版本的甘特图数据
    /// </summary>
    Task<GanttDataDto> GetGanttAsync(
        int planVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取排程概要 KPI（已排 / 未排 / 延期数）
    /// </summary>
    Task<ScheduleSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 候选 vs 基础 计划版本对比复合查询（H1；U10 候选对比页，v1.2 §三）
    /// 校验：两版本不存在抛 KeyNotFoundException（404）；候选状态非 CANDIDATE 抛 InvalidOperationException（400）
    /// 边界：estimatedOnlyCount 归 2号位 返回 null；crossDomain 计数未落盘保守返 0（v1.2 §九-b）；reasons[] 不在 v1.2 返回
    /// </summary>
    Task<CandidateComparisonDto> GetCandidateComparisonAsync(
        int candidatePlanVersionId,
        int basePlanVersionId,
        CancellationToken cancellationToken = default);
}
