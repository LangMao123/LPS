using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 3: 可行性与延期诊断
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 Phase 3
///
/// 职责：
/// - 识别哪些 Demand 未满足
/// - 识别哪些 Task 晚于 RequiredAvailableTime
/// - 识别真实瓶颈
/// - 诊断物料/资源/前序/锁约束
/// - 生成 ScheduleExplanationFact（根因诊断）
/// </summary>
internal class PhaseThreeDiagnostics
{
    /// <summary>
    /// 执行可行性诊断
    /// </summary>
    public DiagnosticsResult Diagnose(
        DomainSolveRequest request,
        InitialScheduleResult scheduleResult,
        ConstraintContext constraints)
    {
        var result = new DiagnosticsResult();

        // P0-12修复：构建已排程任务的索引，使用ToLookup支持多工序（一个Demand生成多个Task）
        var scheduledTasksLookup = scheduleResult.ScheduledTasks
            .ToLookup(t => t.SourceDraftId);

        // ═══════════════════════════════════════════════
        // 1. 延期识别：PlannedEndTime > RequiredAvailableTime
        // ═══════════════════════════════════════════════
        foreach (var demand in request.LogicalProductionDemands)
        {
            // P0-12修复：使用Lookup支持多工序
            if (!scheduledTasksLookup.Contains(demand.LogicalDemandKey))
            {
                // 未排程需求
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            // 找到该需求的最后一道工序
            var demandTasks = scheduledTasksLookup[demand.LogicalDemandKey]
                .OrderBy(t => t.PlannedEndTime)
                .ToList();

            if (demandTasks.Count == 0) continue;

            var lastTask = demandTasks.Last();
            var delay = lastTask.PlannedEndTime - demand.RequiredAvailableTime;

            if (delay > TimeSpan.Zero)
            {
                // 延期
                result.DelayedTaskIds.Add(lastTask.FinalDraftId);

                // 诊断延期原因
                var reasonCode = DiagnoseDelayReason(
                    demand,
                    demandTasks,
                    constraints,
                    request);

                result.ExplanationFacts.Add(new ScheduleExplanationFact
                {
                    FinalDraftId = lastTask.FinalDraftId,
                    ObjectType = "DEMAND",
                    OrderId = demand.OrderId,
                    StageCode = lastTask.StageCode,
                    ReasonCode = reasonCode,
                    Severity = "HIGH",
                    ImpactHours = (decimal)delay.TotalHours,
                    EvidenceJson = $"{{\"RequiredTime\":\"{demand.RequiredAvailableTime:O}\",\"ActualTime\":\"{lastTask.PlannedEndTime:O}\"}}"
                });
            }
        }

        // ═══════════════════════════════════════════════
        // 2. 识别瓶颈资源（Load / AvailableCapacity > 阈值）
        // ═══════════════════════════════════════════════
        // P1-02（BottleneckMode 四模式）：Auto 按利用率阈值自动识别；ForceAnchor 强制锚点必入；
        // PreferAnchor 锚点有负荷时优先入（无负荷/编码无效回退 Auto）；NotAnchor 锚点即使超阈值也排除。
        var solverStrategy = request.StrategySnapshot.SolverStrategy;
        var resourceUtilization = CalculateResourceUtilization(
            scheduleResult.ScheduledTasks,
            constraints,
            request.PlanningStart,
            request.PlanningEnd);

        // Auto 基线：利用率超阈值者入瓶颈集。
        var bottleneckIds = resourceUtilization
            .Where(kv => kv.Value > solverStrategy.BottleneckUtilizationThreshold)
            .Select(kv => kv.Key)
            .ToHashSet();

        // 锚点资源编码 → ResourceId（Code→Id 反向映射，Phase1 BuildResourceCodes 已构建）。
        int? anchorResourceId = null;
        if (!string.IsNullOrEmpty(solverStrategy.AnchorResourceCode) &&
            constraints.ResourceIdsByCode.TryGetValue(solverStrategy.AnchorResourceCode, out var anchorId))
        {
            anchorResourceId = anchorId;
        }

        switch (solverStrategy.BottleneckMode)
        {
            case DynamicBottleneckMode.ForceAnchor:
                // 强制锚点：锚点资源无条件入瓶颈集（展示锚点语义，不突破 Capacity/Calendar 等硬约束）。
                if (anchorResourceId.HasValue)
                    bottleneckIds.Add(anchorResourceId.Value);
                break;

            case DynamicBottleneckMode.PreferAnchor:
                // 优先锚点：锚点资源有负荷（利用率>0）时优先入；无负荷/编码无效回退 Auto 动态识别。
                if (anchorResourceId.HasValue &&
                    resourceUtilization.TryGetValue(anchorResourceId.Value, out var anchorUtil) &&
                    anchorUtil > 0m)
                {
                    bottleneckIds.Add(anchorResourceId.Value);
                }
                break;

            case DynamicBottleneckMode.NotAnchor:
                // 排除锚点：即使利用率超阈值也不判瓶颈（其容量约束仍参与求解，只是不作锚点展示）。
                if (anchorResourceId.HasValue)
                    bottleneckIds.Remove(anchorResourceId.Value);
                break;

            case DynamicBottleneckMode.Auto:
            default:
                // Auto：维持基线。
                break;
        }

        foreach (var resourceId in bottleneckIds)
        {
            var utilization = resourceUtilization.TryGetValue(resourceId, out var u) ? u : 0m;
            result.BottleneckResourceIds.Add(resourceId);

            result.ExplanationFacts.Add(new ScheduleExplanationFact
            {
                FinalDraftId = string.Empty,
                ObjectType = "RESOURCE",
                ResourceId = resourceId,
                ReasonCode = "RESOURCE_CAPACITY_SHORTAGE",
                Severity = "HIGH",
                ImpactHours = null,
                EvidenceJson = $"{{\"Utilization\":{utilization:F2}}}"
            });
        }

        return result;
    }

    /// <summary>
    /// 诊断延期原因
    /// P1-03修复：补齐实施包 §5.4 冻结的 9 类根因码（锁 → 共享/跨域阻挡 → Setup → 物料 → 产能 → 工艺资格 → 前序兜底），
    /// 不再让所有延期都塌缩成 MATERIAL_NOT_AVAILABLE / RESOURCE_CAPACITY_SHORTAGE / PREDECESSOR_DELAY。
    /// </summary>
    private string DiagnoseDelayReason(
        LogicalProductionDemand demand,
        List<FinalTaskDraft> demandTasks,
        ConstraintContext constraints,
        DomainSolveRequest request)
    {
        var lastTask = demandTasks.OrderBy(t => t.PlannedEndTime).Last();

        // 1. 锁定约束：冻结区（FIRM/FROZEN）锁定 vs 其它执行锁
        //    P1-07：复合键 (DraftId, OperationCode)，按 DraftId 匹配该需求任一锁定锚点。
        var locked = constraints.LockedTasks.Values
            .FirstOrDefault(t => t.DraftId == demand.LogicalDemandKey);
        if (locked != null)
        {
            var constraintType = (locked.ConstraintType ?? string.Empty).ToUpperInvariant();
            return constraintType == "FIRM" || constraintType == "FROZEN"
                ? "FIRM_FROZEN_CONSTRAINT"
                : "LOCK_CONSTRAINT";
        }

        // 2. 共享资源/跨域可用性阻挡：按来源域区分（SourceDomainKey 非空 = 跨域）
        if (HasResourceBlockOverlap(demandTasks, request, out var crossDomain))
        {
            return crossDomain ? "CROSS_DOMAIN_AVAILABILITY" : "SHARED_RESOURCE_BLOCK";
        }

        // 3. Setup 边际延期：去掉 Setup 即不延期 → 延期由 Setup 时间决定
        if (lastTask.SetupTime > 0m &&
            lastTask.PlannedEndTime.AddMinutes(-(double)lastTask.SetupTime) <= demand.RequiredAvailableTime)
        {
            return "SETUP_CONSTRAINT";
        }

        // 4. 物料可用时间
        if (constraints.MaterialAvailability.TryGetValue(demand.AllocationSequence, out var segments))
        {
            var earliestMaterialTime = segments.Min(s => s.AvailableTime);
            var firstTaskStart = demandTasks.Min(t => t.PlannedStartTime);

            if (firstTaskStart < earliestMaterialTime)
            {
                return "MATERIAL_NOT_AVAILABLE";
            }
        }

        // 5. 资源容量不足
        var resourceIds = demandTasks.Select(t => t.ResourceId).Distinct().ToList();
        var resourceUtilization = CalculateResourceUtilization(
            demandTasks,
            constraints,
            request.PlanningStart,
            request.PlanningEnd);

        if (resourceIds.Any(rid => resourceUtilization.ContainsKey(rid) && resourceUtilization[rid] > request.StrategySnapshot.SolverStrategy.CapacityShortageUtilizationThreshold))
        {
            return "RESOURCE_CAPACITY_SHORTAGE";
        }

        // 6. 工艺路线资格降级：任务落到的资源不在该工序资格集内（Routing Fallback）
        foreach (var task in demandTasks)
        {
            var eligibilityKey = $"{task.MaterialId}::{task.RouteCode ?? "DEFAULT"}::{task.OperationCode}";
            if (constraints.OperationResourceEligibility.TryGetValue(eligibilityKey, out var eligibleResources) &&
                !eligibleResources.Contains(task.ResourceId))
            {
                return "ROUTING_ELIGIBILITY";
            }
        }

        // 7. 默认原因：前序延期或其他约束
        return "PREDECESSOR_DELAY";
    }

    /// <summary>
    /// P1-03修复：判断需求任务是否与「共享资源/跨域」不可移动阻挡块重叠。
    /// 直接读 request 的跨域块来源（保留 SourceDomainKey 语义，避免 ConstraintContext.ResourceBlocks 丢域信息）：
    /// - SourceDomainKey 非空 → 跨域可用性（CROSS_DOMAIN_AVAILABILITY）；
    /// - SourceDomainKey 为空 → 同域共享资源阻挡（SHARED_RESOURCE_BLOCK）。
    /// </summary>
    private static bool HasResourceBlockOverlap(
        List<FinalTaskDraft> demandTasks,
        DomainSolveRequest request,
        out bool crossDomain)
    {
        crossDomain = false;

        var blocks = new List<(int ResourceId, DateTime Start, DateTime End, bool Cross)>();

        if (request.CandidateContext?.ExternalDomainResourceBlocks != null)
        {
            foreach (var b in request.CandidateContext.ExternalDomainResourceBlocks)
            {
                blocks.Add((b.ResourceId, b.StartTime, b.EndTime, !string.IsNullOrEmpty(b.SourceDomainKey)));
            }
        }

        if (request.UpstreamDomainResourceBlocks != null)
        {
            foreach (var b in request.UpstreamDomainResourceBlocks)
            {
                blocks.Add((b.ResourceId, b.StartTime, b.EndTime, !string.IsNullOrEmpty(b.SourceDomainKey)));
            }
        }

        foreach (var task in demandTasks)
        {
            foreach (var block in blocks)
            {
                if (block.ResourceId == task.ResourceId &&
                    Overlaps(task.PlannedStartTime, task.PlannedEndTime, block.Start, block.End))
                {
                    crossDomain = block.Cross;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// P1-03修复：时间区间重叠判定（左闭右开）。
    /// </summary>
    private static bool Overlaps(DateTime s1, DateTime e1, DateTime s2, DateTime e2)
        => s1 < e2 && s2 < e1;

    /// <summary>
    /// 计算资源利用率
    /// </summary>
    private Dictionary<int, decimal> CalculateResourceUtilization(
        List<FinalTaskDraft> tasks,
        ConstraintContext constraints,
        DateTime planningStart,
        DateTime planningEnd)
    {
        var utilization = new Dictionary<int, decimal>();

        var tasksByResource = tasks.GroupBy(t => t.ResourceId);

        foreach (var group in tasksByResource)
        {
            var resourceId = group.Key;

            // 计算总占用时间
            var totalOccupiedMinutes = group
                .Sum(t => (t.PlannedEndTime - t.PlannedStartTime).TotalMinutes);

            // 计算资源可用时间
            var availableMinutes = 0.0;
            if (constraints.ResourceCalendars.TryGetValue(resourceId, out var calendar))
            {
                availableMinutes = calendar
                    .Where(c => c.Start >= planningStart && c.End <= planningEnd)
                    .Sum(c => (c.End - c.Start).TotalMinutes);
            }

            if (availableMinutes > 0)
            {
                utilization[resourceId] = (decimal)(totalOccupiedMinutes / availableMinutes);
            }
        }

        return utilization;
    }
}

/// <summary>
/// 诊断结果（Phase 3 输出）
/// </summary>
internal class DiagnosticsResult
{
    public List<ScheduleExplanationFact> ExplanationFacts { get; set; } = new();
    public List<string> DelayedTaskIds { get; set; } = new();
    public List<string> UnscheduledDemandKeys { get; set; } = new();
    public List<int> BottleneckResourceIds { get; set; } = new();
}
