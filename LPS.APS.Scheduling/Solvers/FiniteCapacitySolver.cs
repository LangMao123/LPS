using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 有限产能排程求解器（1号位核心实现）
/// 实现文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》
///
/// 职责：
/// - 接收2号位传来的 LogicalProductionDemands
/// - 执行五阶段有限产能排程（Phase 1-5）
/// - 返回 FinalTaskDraft + AllocationTaskShare + ExplanationFacts
///
/// 架构红线：
/// - 纯内存计算，严禁任何I/O操作
/// - 不读库、不写库，只对内存对象排资源和时间
/// - 设备负荷率必须 ≤ 100%（这是算法正确性保证，不是业务校验）
///
/// 【生产入口】<see cref="SolveAsync"/> 是唯一排程入口（Phase 1-5 五阶段流程）。
/// 早期单方法版本 Solve() / Reschedule() 及其依赖（TimeSlotFinder / IntervalTree / PriorityTaskQueue /
/// ScopeConstraint / SetupOptimizer 实例字段）已随死代码清理批次删除；需回溯请查 SVN 历史。
/// </summary>
public class FiniteCapacitySolver : IFiniteCapacityScheduler
{
    /// <summary>
    /// 执行单域有限产能排程（IFiniteCapacityScheduler接口实现）
    /// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 五阶段流程
    /// </summary>
    public async Task<DomainSolveResult> SolveAsync(
        DomainSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        // ═══════════════════════════════════════════════
        // Phase 1: 硬约束构建
        // ═══════════════════════════════════════════════
        var phase1 = new PhaseOneConstraintBuilder();
        var constraints = phase1.BuildConstraints(request);

        // ═══════════════════════════════════════════════
        // Phase 2: 初始有限产能排程
        // ═══════════════════════════════════════════════
        var phase2 = new PhaseTwoInitialScheduler();
        var scheduleResult = phase2.Schedule(request, constraints);

        // ═══════════════════════════════════════════════
        // Phase 3: 可行性与延期诊断
        // ═══════════════════════════════════════════════
        var phase3 = new PhaseThreeDiagnostics();
        var diagnostics = phase3.Diagnose(request, scheduleResult, constraints);

        // ═══════════════════════════════════════════════
        // Phase 4: 有界局部修复
        // ═══════════════════════════════════════════════
        var phase4 = new PhaseFourLocalRepair();
        var repairResult = phase4.Repair(request, scheduleResult, diagnostics, constraints);

        // ═══════════════════════════════════════════════
        // Phase 5: 压缩空隙与最终评价
        // ═══════════════════════════════════════════════
        var phase5 = new PhaseFiveCompression();
        var finalResult = phase5.Compress(request, scheduleResult, repairResult, diagnostics, constraints);

        // 补充耗时统计
        var elapsed = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
        finalResult = new DomainSolveResult
        {
            Success = finalResult.Success,
            ErrorMessage = finalResult.ErrorMessage,
            IsRoughCut = finalResult.IsRoughCut,
            FinalTasks = finalResult.FinalTasks,
            AllocationShares = finalResult.AllocationShares,
            UnscheduledTasks = finalResult.UnscheduledTasks,
            PhysicalPeggingDrafts = finalResult.PhysicalPeggingDrafts,
            ExplanationFacts = finalResult.ExplanationFacts,
            // B.2：透传 Phase5 导出的求解过程追溯 —— 此前重组装漏拷，会静默丢弃 2号位 r13494 落地的 SolveTraceNotes 载体
            SolveTraceNotes = finalResult.SolveTraceNotes,
            Summary = new SolveSummary
            {
                TotalDrafts = finalResult.Summary.TotalDrafts,
                ScheduledCount = finalResult.Summary.ScheduledCount,
                UnscheduledCount = finalResult.Summary.UnscheduledCount,
                ElapsedMs = elapsed,
                IssueCount = finalResult.Summary.IssueCount,
                UsedRoughCut = finalResult.Summary.UsedRoughCut
            }
        };

        return await Task.FromResult(finalResult);
    }
}