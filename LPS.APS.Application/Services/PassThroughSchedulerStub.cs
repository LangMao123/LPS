using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services;

/// <summary>
/// ⚠️ 1号位临时适配器（仅供2号位集成测试用，不做真正排程）
///
/// v5.1.2架构整改后：
///   - 输入：LogicalProductionDemands（不再是TaskDrafts）
///   - 输出：FinalTasks + AllocationTaskShare + PhysicalPeggingDrafts + ExplanationFacts
///   - 本stub简单为每个LogicalProductionDemand生成一个FinalTask（1:1映射）
///   - 真正1号位实现时会做拆批/合批、资源分配、时间槽寻址、产能约束等
///
/// ⚠️ 务必保持 internal（严禁改成 public）：
///   - Application 层 Scrutor 会扫描 LPS.APS.Application.Services 命名空间并 .AsImplementedInterfaces()
///   - AddClasses(Action&lt;IImplementationTypeFilter&gt;) 默认 publicOnly:true，internal 类不会被注册
///   - 若改成 public，AddApplicationServices() 在 Program.cs 中晚于 AddSchedulingServices() 执行，
///     会以 scoped 身份 shadow 掉 singleton 的 FiniteCapacitySolver，且不报错（静默变成 1:1 假排程）
///   - 当前本 stub 未被任何生产代码/测试注册（测试用 Moq 的 Mock&lt;IFiniteCapacityScheduler&gt;），
///     属于预留的 1:1 测试适配器，待 1号位单元测试需要时再显式注册
/// </summary>
internal sealed class PassThroughSchedulerStub : IFiniteCapacityScheduler
{
    public Task<DomainSolveResult> SolveAsync(
        DomainSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var finalTasks = new List<FinalTaskDraft>();
        var allocationShares = new List<AllocationTaskShare>();
        var peggingDrafts = new List<FinalTaskPeggingDraft>();

        // 为每个LogicalProductionDemand生成一个FinalTask（简单1:1映射）
        foreach (var demand in request.LogicalProductionDemands)
        {
            var finalTask = new FinalTaskDraft
            {
                FinalDraftId     = Guid.NewGuid().ToString(),
                SourceDraftId    = demand.DemandKey,
                MaterialId       = demand.MaterialId,
                StageCode        = demand.StartStageCode,
                OperationCode    = string.Empty,
                TaskType         = "NEW_REQUIREMENT",
                Quantity         = demand.PlannedProcessQty,
                UOM              = "PCS",
                PlannedStartTime = demand.RequiredAvailableTime.AddHours(-8),
                PlannedEndTime   = demand.RequiredAvailableTime,
                Priority         = demand.DemandSequence,
                ResourceId       = 0,
                ResourceCode     = string.Empty,
                IsVirtual        = false
            };
            finalTasks.Add(finalTask);

            // 生成AllocationTaskShare（Allocation → FinalTask的追溯）
            foreach (var alloc in request.AllocationLineage.Where(a => a.DemandKey == demand.DemandKey))
            {
                allocationShares.Add(new AllocationTaskShare
                {
                    FinalDraftId      = finalTask.FinalDraftId,
                    AllocationSequence = alloc.AllocationSequence,
                    ComponentQty      = alloc.Quantity
                });
            }
        }

        // 基于RoutingDependencies生成物理Task依赖
        // （stub简化处理：假设每个demand独立，不生成复杂依赖）

        var result = new DomainSolveResult
        {
            Success               = true,
            FinalTasks            = finalTasks,
            AllocationShares      = allocationShares,
            PhysicalPeggingDrafts = peggingDrafts,
            UnscheduledTasks      = Array.Empty<UnscheduledTaskResult>(),
            ExplanationFacts      = Array.Empty<ScheduleExplanationFact>(),
            Summary               = new SolveSummary
            {
                TotalDrafts      = request.LogicalProductionDemands.Count,
                ScheduledCount   = finalTasks.Count,
                UnscheduledCount = 0
            }
        };

        return Task.FromResult(result);
    }
}
