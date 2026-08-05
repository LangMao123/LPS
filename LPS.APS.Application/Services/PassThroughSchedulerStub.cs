using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services;

/// <summary>
/// ⚠️ 1号位临时适配器（仅供2号位集成测试用，不做真正排程）
///
/// 交接说明（给1号位）：
///   - 实现 <see cref="IFiniteCapacityScheduler"/>，替换本类即可，无需改动2号位代码
///   - 本 stub 将每个 TaskDraft 原样映射为 FinalTaskDraft（PlannedStartTime 不变，DueTime → PlannedEndTime）
///   - ResourceId / ResourceCode 留空，Priority 直接透传
///   - 真正实现时需填入资源分配、产能约束、日历排程逻辑
/// </summary>
internal sealed class PassThroughSchedulerStub : IFiniteCapacityScheduler
{
    public Task<DomainSolveResult> SolveAsync(
        DomainSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var finalTasks = request.TaskDrafts
            .Select(d => new FinalTaskDraft
            {
                SourceDraftId    = d.DraftId,
                MaterialId       = d.MaterialId,
                StageCode        = d.StageCode,
                OperationCode    = d.OperationCode,
                TaskType         = d.IsVirtual ? "VIRTUAL" : "NEW_REQUIREMENT",
                Quantity         = d.Quantity,
                UOM              = d.UOM,
                PlannedStartTime = d.EarliestAvailableTime,
                PlannedEndTime   = d.DueTime,
                Priority         = d.Priority,
                IsVirtual        = d.IsVirtual,
                ExistingMESPlanReleaseId = d.ExistingMESPlanReleaseId,
                ExecutionLockId  = d.ExecutionLockId
            })
            .ToList();

        // 建立 SourceDraftId → FinalDraftId 映射，用于构建物理Pegging
        var sourceTofinal = finalTasks.ToDictionary(
            f => f.SourceDraftId, f => f.FinalDraftId, StringComparer.Ordinal);

        var draftById = request.TaskDrafts.ToDictionary(d => d.DraftId, StringComparer.Ordinal);

        var peggingDrafts = request.TaskDrafts
            .SelectMany(d => d.UpstreamDraftIds
                .Where(upId => sourceTofinal.ContainsKey(upId) && sourceTofinal.ContainsKey(d.DraftId))
                .Select(upId => new FinalTaskPeggingDraft
                {
                    UpstreamFinalDraftId   = sourceTofinal[upId],
                    DownstreamFinalDraftId = sourceTofinal[d.DraftId],
                    UpstreamMaterialId     = draftById.TryGetValue(upId, out var up) ? up.MaterialId : 0,
                    DownstreamMaterialId   = d.MaterialId,
                    Quantity               = d.Quantity,
                    UOM                    = d.UOM,
                    InheritedPriority      = d.Priority
                }))
            .ToList();

        var result = new DomainSolveResult
        {
            Success               = true,
            FinalTasks            = finalTasks,
            PhysicalPeggingDrafts = peggingDrafts,
            Summary               = new SolveSummary
            {
                TotalDrafts      = request.TaskDrafts.Count,
                ScheduledCount   = finalTasks.Count,
                UnscheduledCount = 0
            }
        };

        return Task.FromResult(result);
    }
}
