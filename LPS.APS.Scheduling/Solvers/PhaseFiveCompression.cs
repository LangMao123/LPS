using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 5: 压缩空隙与最终评价
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 Phase 5
///
/// 职责：
/// - 在不破坏高优先级交期的情况下：
///   * 减少不必要等待
///   * 减少 WIP
///   * 减少 Setup
///   * 提升利用率
///   * 避免过早生产
///   * 尽量保持计划稳定
/// </summary>
internal class PhaseFiveCompression
{
    /// <summary>
    /// 执行空隙压缩与最终评价
    /// 文档：§六 Phase 5
    /// 职责：在不破坏高优先级交期的情况下压缩空隙
    /// </summary>
    public DomainSolveResult Compress(
        DomainSolveRequest request,
        InitialScheduleResult scheduleResult,
        RepairResult repairResult,
        DiagnosticsResult diagnostics,
        ConstraintContext constraints)
    {
        // 合并所有已排程任务
        var allScheduledTasks = new List<FinalTaskDraft>();
        allScheduledTasks.AddRange(scheduleResult.ScheduledTasks);
        allScheduledTasks.AddRange(repairResult.RepairedTasks);

        // P1-05：Gap Compaction（§六 Phase 5 最小次级优化，Level 3——不破坏 Level 0 硬约束/Level 1 履约/Level 2 交期）。
        // 保序前向压实：把 Task 拉进更早的日历可用空档（减少不必要等待/减少 WIP/提升利用率）；
        // 仅严格增益才移动、绝不重排序（尽量保持计划稳定）；floor 含物料/Routing前序/跨物料子件/同PI连续份额/
        // 同资源前序占用（避免过早生产）；Setup 顺序不因保序压实恶化（FromMaterial 链不变），
        // 真正的 Setup 序列优化 = item1 夜间 FULL 有界搜索（待 2号位 规则通道）。
        // BACKWARD/MIXED 倒排 JIT 锚点即「避免过早生产」语义本身，V1 不做前向拉早。
        // 必须在 Shares/Dependency/硬校验/诊断之前执行，让下游看到压实后的最终时间。
        CompactGaps(allScheduledTasks, request, constraints);

        // 生成 AllocationTaskShare（追溯 Allocation → Task 的份额）
        // P0-04修复：传入 Phase2 的 Merge 份额血缘，保证合并批次的 Allocation→Task 份额闭合
        var allocationShares = GenerateAllocationShares(allScheduledTasks, request, constraints, scheduleResult.AllocationTaskShare);

        // 第5轮修复：TaskDependency必须在硬校验之前生成，以便校验Dependency约束
        // P0-13修复：生成 TaskDependency（基于 Routing 工序依赖关系）
        var taskDependencies = GenerateTaskDependencies(allScheduledTasks, request, constraints);

        // 第4轮Item 10：Phase5最终硬约束校验（§十一）
        var validationResult = ValidateHardResult(allScheduledTasks, allocationShares, taskDependencies, request, constraints);
        if (!validationResult.IsValid)
        {
            return new DomainSolveResult
            {
                Success = false,
                ErrorMessage = $"Phase5硬约束校验失败: {validationResult.ErrorMessage}",
                IsRoughCut = false,
                FinalTasks = Array.Empty<FinalTaskDraft>(),
                AllocationShares = Array.Empty<AllocationTaskShare>(),
                UnscheduledTasks = Array.Empty<UnscheduledTaskResult>(),
                PhysicalPeggingDrafts = Array.Empty<FinalTaskPeggingDraft>(),
                ExplanationFacts = Array.Empty<ScheduleExplanationFact>(),
                Summary = new SolveSummary
                {
                    TotalDrafts = 0,
                    ScheduledCount = 0,
                    UnscheduledCount = 0,
                    ElapsedMs = 0,
                    IssueCount = 0,
                    UsedRoughCut = false
                }
            };
        }

        // 收集未排程需求
        var unscheduledTasks = new List<UnscheduledTaskResult>();

        // Phase 2 未排程的需求
        foreach (var demandKey in scheduleResult.UnscheduledDemandKeys)
        {
            if (!repairResult.RepairedTasks.Any(t => t.SourceDraftId == demandKey))
            {
                // 最小B：缺失生产部门 Context 的需求，Reason 单独标识
                var missingDept = IsMissingDepartmentContext(demandKey, request, constraints);
                unscheduledTasks.Add(new UnscheduledTaskResult
                {
                    DraftId = demandKey,
                    Reason = missingDept
                        ? "MISSING_PRODUCTION_DEPARTMENT_CONTEXT"
                        : "Phase 2 初始排程失败，Phase 4 修复未成功"
                });
            }
        }

        // Phase 4 仍未排程的需求
        foreach (var demandKey in repairResult.StillUnscheduledKeys)
        {
            unscheduledTasks.Add(new UnscheduledTaskResult
            {
                DraftId = demandKey,
                Reason = "Phase 4 局部修复后仍无法排程"
            });
        }

        // P0-03+P0-15修复：区分技术失败与业务Unscheduled
        // 技术失败：Routing非法、数量闭合错误、硬资源约束破坏 → Success=false
        // 业务结果：产能不足、物料太晚、DueDate无法满足 → Success=true + Unscheduled
        bool technicalFailure = scheduleResult.TechnicalFailure;
        string? errorMessage = technicalFailure ? scheduleResult.TechnicalFailureReason : null;

        // P1-03修复：Phase4 可能改变 Resource/Start/End，Phase3 的旧诊断结果已过时。
        // 在最终任务集合上重跑 Phase3 诊断，保证 ExplanationFacts 与最终修复结果一致。
        var finalDiagnostics = new PhaseThreeDiagnostics().Diagnose(
            request,
            new InitialScheduleResult { ScheduledTasks = allScheduledTasks },
            constraints);

        return new DomainSolveResult
        {
            Success = !technicalFailure,
            ErrorMessage = errorMessage,
            IsRoughCut = false,
            FinalTasks = allScheduledTasks,
            AllocationShares = allocationShares,
            UnscheduledTasks = unscheduledTasks,
            PhysicalPeggingDrafts = taskDependencies,
            ExplanationFacts = finalDiagnostics.ExplanationFacts,
            Summary = new SolveSummary
            {
                TotalDrafts = allScheduledTasks.Count + unscheduledTasks.Count,
                ScheduledCount = allScheduledTasks.Count,
                UnscheduledCount = unscheduledTasks.Count,
                ElapsedMs = 0, // 由 FiniteCapacitySolver 填充
                IssueCount = finalDiagnostics.ExplanationFacts.Count,
                UsedRoughCut = false
            }
        };
    }

    /// <summary>
    /// 判断某 Demand 是否因缺失生产部门 Context 而被排除（最小B）。
    /// 部门锁定在 Phase 1 完成，缺失 Context 的 Material 记入 constraints.MissingDepartmentContextMaterialIds；
    /// 此处按 LogicalDemandKey → MaterialId 反查，给 Unscheduled 结果补正确的 Reason。
    /// </summary>
    private bool IsMissingDepartmentContext(
        string demandKey,
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        var demand = request.LogicalProductionDemands
            .FirstOrDefault(d => d.LogicalDemandKey == demandKey);
        if (demand == null)
        {
            return false;
        }
        return constraints.MissingDepartmentContextMaterialIds.Contains(demand.MaterialId);
    }

    /// <summary>
    /// P1-05：Gap Compaction —— 保序前向压实（V1 最小实现，Level 3 次级优化）。
    /// 仅 FORWARD 方向执行；把 Task 拉进更早的日历可用空档，全程满足：
    /// - Level 0 硬约束：Calendar/占用含 Setup（复用 Phase4.FindForwardSlot 唯一实现）/不换资源（Eligibility 不变）/
    ///   Routing 依赖含 Lag（Split 多前序取最大）/物料多段 Quantity-Time/跨物料子件完成（块4）/
    ///   Execution-Firm-Frozen 不可移动（复用 Phase4.IdentifyImmovableTasks）/ExternalDomain 阻挡（BuildResourceOccupancy 已含）；
    /// - Level 1/2：只提前、不推迟 → Demand 履约与交期不可能恶化；
    /// - 稳定性：仅严格增益（新加工开始 &lt; 旧加工开始）才移动；同资源前序占用末端/后继占用起点作序界，绝不重排序；
    /// - 避免过早生产：floor 含物料可用时间与全部依赖下界；BACKWARD/MIXED JIT 锚点整体跳过不前拉。
    /// Setup 减少：保序压实不改变资源上的产品先后关系（FromMaterial 链不变），Setup 不恶化；
    /// 真正的 Setup 序列优化属 item1 夜间 FULL 有界搜索（待 2号位 规则通道接线）。
    /// </summary>
    private static void CompactGaps(
        List<FinalTaskDraft> tasks,
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        // 避免过早生产：BACKWARD/MIXED 的 JIT 倒排锚点不前拉（V1 最小口径，只做 FORWARD 压实）
        if (!string.Equals(request.StrategySnapshot.Parameters.SchedulingDirection, "FORWARD", StringComparison.Ordinal))
        {
            return;
        }

        var occupancy = PhaseFourLocalRepair.BuildResourceOccupancy(tasks, constraints);
        var immovable = PhaseFourLocalRepair.IdentifyImmovableTasks(request, tasks);
        var demandByKey = request.LogicalProductionDemands.ToDictionary(d => d.LogicalDemandKey);

        // demand → 当前完成时间（跨物料动态 floor 用），随压实推进刷新
        var completionByDemand = tasks
            .GroupBy(t => t.SourceDraftId)
            .ToDictionary(g => g.Key, g => g.Max(t => t.PlannedEndTime));

        // 全局按加工开始时间升序处理：Routing 前序/跨物料子件/同 PI 连续份额先于当前 Task 处理，
        // 它们的 floor 用压实后的最新位置（只提前 → 后继 floor 只会更低，安全）。
        var order = tasks
            .Where(t => !immovable.Contains(t.FinalDraftId))
            .OrderBy(t => t.PlannedStartTime)
            .ToList();

        foreach (var task in order)
        {
            if (!demandByKey.TryGetValue(task.SourceDraftId, out var demand))
                continue;

            var setup = TimeSpan.FromMinutes((double)task.SetupTime);
            var totalDuration = (task.PlannedEndTime - task.PlannedStartTime) + setup;
            var myOccStart = task.PlannedStartTime - setup;

            // ── floor（占用开始时间口径，与 Phase2/Phase4 的 FindForwardSlot 用法一致）──
            var floor = request.PlanningStart;

            // 1) 物料可用时间（多段 Quantity-Time）；总量不足 → 保持原位不动
            var materialFloor = PhaseFourLocalRepair.GetMaterialEarliestTime(
                demand.AllocationSequence, demand.NetOutputQty, constraints, request.PlanningStart, out var sufficient);
            if (!sufficient) continue;
            if (materialFloor > floor) floor = materialFloor;

            // 2) Routing 前序（同 Demand，含 Lag；Split 多前序 Task 取最大完成）
            if (constraints.RoutingGraphs.TryGetValue(demand.MaterialId, out var graphs) &&
                graphs.TryGetValue("DEFAULT", out var graph) &&
                graph.Dependencies.TryGetValue(task.OperationCode, out var preds))
            {
                foreach (var pred in preds)
                {
                    foreach (var predTask in tasks.Where(t =>
                                 t.SourceDraftId == task.SourceDraftId &&
                                 t.OperationCode == pred.FromOperationCode))
                    {
                        var predEnd = predTask.PlannedEndTime.AddMinutes((double)pred.LagTime);
                        if (predEnd > floor) floor = predEnd;
                    }
                }
            }

            // 3) 跨物料子件完成时间（块4：任务喂任务）；子件未排 → 保持原位不动
            var dynFloor = PhaseFourLocalRepair.GetDynamicMaterialFloor(
                task.SourceDraftId, constraints, completionByDemand, out var childUnavailable);
            if (childUnavailable) continue;
            if (dynFloor > floor) floor = dynFloor;

            // 4) P0-08：同 PI 自由份额不得早于连续份额完成（用压实后的最新完成时间）
            if (!demand.IsContinuation && !string.IsNullOrEmpty(demand.ProductionInstructionNo))
            {
                foreach (var contTask in tasks.Where(t =>
                             t.SourceDraftId != task.SourceDraftId &&
                             demandByKey.TryGetValue(t.SourceDraftId, out var d2) &&
                             d2.IsContinuation &&
                             d2.ProductionInstructionNo == demand.ProductionInstructionNo))
                {
                    if (contTask.PlannedEndTime > floor) floor = contTask.PlannedEndTime;
                }
            }

            // 5) 保序界：同资源不得越过前序 Task 占用末端（floor）/后继 Task 占用起点（ceil）——绝不重排序
            var orderCeil = DateTime.MaxValue;
            foreach (var t in tasks)
            {
                if (t.ResourceId != task.ResourceId || ReferenceEquals(t, task)) continue;
                var tOccStart = t.PlannedStartTime - TimeSpan.FromMinutes((double)t.SetupTime);
                if (tOccStart < myOccStart)
                {
                    // 前序：占用末端（= 加工结束，占用窗 [start-setup, end]）作 floor
                    if (t.PlannedEndTime > floor) floor = t.PlannedEndTime;
                }
                else if (tOccStart < orderCeil)
                {
                    // 后继：占用起点（含其 Setup）作 ceil
                    orderCeil = tOccStart;
                }
            }

            // floor 已不早于当前位置 → 无前拉空间
            if (floor >= task.PlannedStartTime) continue;

            // ── 日历感知槽查找（复用 Phase4 唯一实现，含 Setup 占用）──
            // 先把自身窗口从 occupancy 摘除：自身当前窗会挡住「与自己部分重叠的更早空档」，
            // 导致合法前拉被误判无槽；拒绝移动时原样加回。
            List<TimeWindow>? windows = null;
            if (occupancy.TryGetValue(task.ResourceId, out windows))
            {
                windows.RemoveAll(w => w.Start == myOccStart && w.End == task.PlannedEndTime);
            }

            var slot = PhaseFourLocalRepair.FindForwardSlot(
                floor, totalDuration, task.ResourceId, constraints, occupancy, request.PlanningEnd);

            if (slot == null)
            {
                windows?.Add(new TimeWindow(myOccStart, task.PlannedEndTime));
                continue;
            }

            var newStart = slot.Value.Start + setup;   // slot.Start = 占用开始（含 Setup）
            var newEnd = slot.Value.End;

            // 仅严格增益 + 不越后继序界才移动（稳定性红线）
            if (newStart >= task.PlannedStartTime || newEnd > orderCeil)
            {
                windows?.Add(new TimeWindow(myOccStart, task.PlannedEndTime));
                continue;
            }

            // ── 落地移动：替换 Task（init-only → 重建）、刷新占用与完成时间 ──
            var idx = tasks.IndexOf(task);
            if (idx < 0)
            {
                windows?.Add(new TimeWindow(myOccStart, task.PlannedEndTime));
                continue;
            }
            tasks[idx] = WithTimes(task, newStart, newEnd);

            if (windows != null)
            {
                windows.Add(new TimeWindow(newStart - setup, newEnd));
            }
            else
            {
                occupancy[task.ResourceId] = new List<TimeWindow> { new TimeWindow(newStart - setup, newEnd) };
            }

            completionByDemand[task.SourceDraftId] = tasks
                .Where(t => t.SourceDraftId == task.SourceDraftId)
                .Max(t => t.PlannedEndTime);
        }
    }

    /// <summary>P1-05：FinalTaskDraft 为 init-only，移动时重建实例（除时间外全字段原样保留，含 OperationSeq）。</summary>
    private static FinalTaskDraft WithTimes(FinalTaskDraft task, DateTime newStart, DateTime newEnd)
        => new FinalTaskDraft
        {
            FinalDraftId = task.FinalDraftId,
            SourceDraftId = task.SourceDraftId,
            MaterialId = task.MaterialId,
            FactoryId = task.FactoryId,
            StageCode = task.StageCode,
            OperationCode = task.OperationCode,
            OperationSeq = task.OperationSeq,
            TaskType = task.TaskType,
            ResourceId = task.ResourceId,
            ResourceCode = task.ResourceCode,
            RouteCode = task.RouteCode,
            PathId = task.PathId,
            Quantity = task.Quantity,
            PlannedProcessQty = task.PlannedProcessQty,
            UOM = task.UOM,
            PlannedStartTime = newStart,
            PlannedEndTime = newEnd,
            SetupTime = task.SetupTime,
            Priority = task.Priority,
            IsVirtual = task.IsVirtual,
            StageExecutionBatchDraftKey = task.StageExecutionBatchDraftKey,
            StageExecutionBatchQty = task.StageExecutionBatchQty,
            ExistingMESPlanReleaseId = task.ExistingMESPlanReleaseId,
            ExecutionLockId = task.ExecutionLockId
        };

    /// <summary>
    /// 生成 AllocationTaskShare（追溯机制）
    /// 文档：§五 5.2
    /// P0-14修复（0号位严重Bug反馈）：只有末端Task记录净产出份额，串行前序通过TaskDependency追溯
    /// 闭合检查：Σ ShareQty = 该Allocation需制造的NetOutputQty
    /// P0-04/P0-05修复：接收 merge 血缘，闭合目标改为该 Allocation 下所有 Demand 的 NetOutputQty 之和，
    /// 并按末端Task的真实 Demand 构成归并（修复合批M:N与多切片闭合错误）。
    /// </summary>
    private List<AllocationTaskShare> GenerateAllocationShares(
        List<FinalTaskDraft> tasks,
        DomainSolveRequest request,
        ConstraintContext constraints,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> mergeLineage)
    {
        var shares = new List<AllocationTaskShare>();

        // P0-04/P0-05修复：按 LogicalDemandKey 建索引，供 merge 血缘展开
        var demandByKey = request.LogicalProductionDemands
            .ToDictionary(d => d.LogicalDemandKey);

        // 构建TaskDependency，用于识别末端Task
        var downstreamTasks = new HashSet<string>();
        foreach (var demandGroup in tasks.GroupBy(t => t.SourceDraftId))
        {
            if (!demandByKey.TryGetValue(demandGroup.Key, out var demand))
                continue;

            // 获取工艺路线依赖关系
            if (!constraints.RoutingGraphs.TryGetValue(demand.MaterialId, out var routeGraphs))
                continue;
            if (!routeGraphs.TryGetValue("DEFAULT", out var routingGraph))
                continue;

            // 标记所有有downstream的Task（非末端）
            foreach (var depList in routingGraph.Dependencies.Values)
            {
                foreach (var dep in depList)
                {
                    var upstreamTask = demandGroup
                        .FirstOrDefault(t => t.OperationCode == dep.FromOperationCode);
                    if (upstreamTask != null)
                    {
                        downstreamTasks.Add(upstreamTask.FinalDraftId);
                    }
                }
            }
        }

        // P0-05修复：闭合目标 = 每个 Allocation 下所有 Demand 的 NetOutputQty 之和
        var allocationTotalQty = request.LogicalProductionDemands
            .GroupBy(d => d.AllocationSequence)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty));

        // P0-05修复：展开每个末端Task的真实 (Demand, Qty) 构成（含 merge 血缘），按 Allocation 归并贡献
        var contributions = tasks
            .Where(t => !downstreamTasks.Contains(t.FinalDraftId))
            .SelectMany(t => GetTaskDemandComposition(t, demandByKey, mergeLineage)
                .Select(c => new { TaskId = t.FinalDraftId, AllocationSeq = c.Demand.AllocationSequence, Qty = c.Qty }))
            .GroupBy(c => c.AllocationSeq);

        foreach (var allocGroup in contributions)
        {
            var allocationSeq = allocGroup.Key;
            if (!allocationTotalQty.TryGetValue(allocationSeq, out var expectedQty))
            {
                continue;
            }

            var taskQtys = allocGroup
                .GroupBy(c => c.TaskId)
                .Select(g => new { TaskId = g.Key, ContributionQty = g.Sum(c => c.Qty) })
                .OrderBy(x => x.TaskId)
                .ToList();

            if (taskQtys.Count == 0) continue;

            decimal totalContribution = taskQtys.Sum(x => x.ContributionQty);

            for (int i = 0; i < taskQtys.Count; i++)
            {
                var c = taskQtys[i];
                decimal shareQty;

                if (i == taskQtys.Count - 1)
                {
                    // 最后一个：补差闭合
                    var alreadyAllocated = shares
                        .Where(s => s.AllocationSequence == allocationSeq)
                        .Sum(s => s.ComponentQty);
                    shareQty = expectedQty - alreadyAllocated;
                }
                else if (totalContribution > 0)
                {
                    shareQty = Math.Round(expectedQty * c.ContributionQty / totalContribution, 3);
                }
                else
                {
                    shareQty = expectedQty / taskQtys.Count;
                }

                shares.Add(new AllocationTaskShare
                {
                    FinalDraftId = c.TaskId,
                    AllocationSequence = allocationSeq,
                    ComponentQty = shareQty
                });
            }
        }

        return shares;
    }

    /// <summary>
    /// P0-05修复：展开一个末端Task承载的真实 (Demand, Qty) 构成。
    /// 合并任务（M:N）会承载多个 Demand：merge 血缘里记录的份额 + SourceDraftId 锚点 Demand 的剩余份额。
    /// </summary>
    private List<(LogicalProductionDemand Demand, decimal Qty)> GetTaskDemandComposition(
        FinalTaskDraft task,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> mergeLineage)
    {
        var composition = new List<(LogicalProductionDemand, decimal)>();
        decimal mergedTotal = 0m;

        // 合并进来的 Demand（merge 血缘）
        if (mergeLineage.TryGetValue(task.FinalDraftId, out var merged))
        {
            foreach (var (demandKey, shareQty) in merged)
            {
                if (demandByKey.TryGetValue(demandKey, out var mergedDemand))
                {
                    composition.Add((mergedDemand, shareQty));
                    mergedTotal += shareQty;
                }
            }
        }

        // 锚点 Demand（SourceDraftId）承担剩余份额
        if (demandByKey.TryGetValue(task.SourceDraftId, out var anchorDemand))
        {
            var anchorQty = task.Quantity - mergedTotal;
            if (anchorQty > 0)
            {
                composition.Add((anchorDemand, anchorQty));
            }
        }

        return composition;
    }

    /// <summary>
    /// 生成 TaskDependency（基于 Routing 工序依赖关系）
    /// 文档：§五 5.3
    /// P0-13修复：根据工艺路线生成工序间的物理依赖关系
    /// </summary>
    private List<FinalTaskPeggingDraft> GenerateTaskDependencies(
        List<FinalTaskDraft> tasks,
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        var dependencies = new List<FinalTaskPeggingDraft>();

        // 按 SourceDraftId（需求）分组任务
        var tasksByDemand = tasks
            .GroupBy(t => t.SourceDraftId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.PlannedStartTime).ToList());

        // 为每个需求生成工序依赖
        foreach (var demandGroup in tasksByDemand)
        {
            var demand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == demandGroup.Key);
            if (demand == null) continue;

            // 获取工艺路线
            if (!constraints.RoutingGraphs.TryGetValue(demand.MaterialId, out var routeGraphs))
                continue;
            if (!routeGraphs.TryGetValue("DEFAULT", out var routingGraph))
                continue;

            // 遍历工艺路线中的依赖关系
            // 第5轮修复：Split场景下，一个Operation可能对应多个Task，必须为所有组合建立Dependency
            foreach (var depList in routingGraph.Dependencies.Values)
            {
                foreach (var dep in depList)
                {
                    // 找到对应的所有上游Task和下游Task
                    var upstreamTasks = demandGroup.Value
                        .Where(t => t.OperationCode == dep.FromOperationCode)
                        .ToList();
                    var downstreamTasks = demandGroup.Value
                        .Where(t => t.OperationCode == dep.ToOperationCode)
                        .ToList();

                    // P0-06修复：Split场景不再做全量交叉积（那会把整条需求数量重复算到每条边）。
                    // 等数量时按下标一一配对（每条边取下游Task真实数量）；
                    // 数量不等时，把每个下游Task的数量按上游个数均摊，避免数量重复累计。
                    if (upstreamTasks.Count == 0 || downstreamTasks.Count == 0)
                    {
                        continue;
                    }

                    if (upstreamTasks.Count == downstreamTasks.Count)
                    {
                        for (int i = 0; i < upstreamTasks.Count; i++)
                        {
                            dependencies.Add(new FinalTaskPeggingDraft
                            {
                                UpstreamFinalDraftId = upstreamTasks[i].FinalDraftId,
                                DownstreamFinalDraftId = downstreamTasks[i].FinalDraftId,
                                UpstreamMaterialId = demand.MaterialId,
                                DownstreamMaterialId = demand.MaterialId,
                                Quantity = downstreamTasks[i].Quantity,
                                UOM = string.Empty,
                                InheritedPriority = demand.DemandSequence,
                                DependencyType = dep.DependencyType,
                                LagTime = dep.LagTime
                            });
                        }
                    }
                    else
                    {
                        foreach (var downstreamTask in downstreamTasks)
                        {
                            decimal edgeQty = Math.Round(downstreamTask.Quantity / upstreamTasks.Count, 3);
                            foreach (var upstreamTask in upstreamTasks)
                            {
                                dependencies.Add(new FinalTaskPeggingDraft
                                {
                                    UpstreamFinalDraftId = upstreamTask.FinalDraftId,
                                    DownstreamFinalDraftId = downstreamTask.FinalDraftId,
                                    UpstreamMaterialId = demand.MaterialId,
                                    DownstreamMaterialId = demand.MaterialId,
                                    Quantity = edgeQty,
                                    UOM = string.Empty,
                                    InheritedPriority = demand.DemandSequence,
                                    DependencyType = dep.DependencyType,
                                    LagTime = dep.LagTime
                                });
                            }
                        }
                    }
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// 第4轮Item 10：Phase5最终硬约束校验（§十一）
    /// 验证FinalTask结果是否违反硬约束
    /// 第5轮修复：增加TaskDependency校验
    /// </summary>
    private ValidationResult ValidateHardResult(
        List<FinalTaskDraft> tasks,
        List<AllocationTaskShare> allocationShares,
        List<FinalTaskPeggingDraft> taskDependencies,
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        // 1. P0-05修复：验证每个Allocation的ΣShareQty == 该Allocation下所有Demand的NetOutputQty之和
        var allocationTotalNetOutput = request.LogicalProductionDemands
            .GroupBy(d => d.AllocationSequence)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty));

        var sharesByAllocation = allocationShares
            .GroupBy(s => s.AllocationSequence)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var allocKvp in sharesByAllocation)
        {
            var allocationSeq = allocKvp.Key;
            var shares = allocKvp.Value;

            if (!allocationTotalNetOutput.TryGetValue(allocationSeq, out var expectedQty))
            {
                // 该 Allocation 无 Demand 定义（异常数据），跳过闭合校验
                continue;
            }

            decimal totalShare = shares.Sum(s => s.ComponentQty);
            if (Math.Abs(totalShare - expectedQty) > 0.001m)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Allocation {allocationSeq} 数量闭合失败: ΣShareQty={totalShare}, 期望NetOutputQty={expectedQty}"
                };
            }

            // 2. 验证每个ShareQty > 0
            foreach (var share in shares)
            {
                if (share.ComponentQty <= 0)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Task {share.FinalDraftId} 的 ShareQty <= 0: {share.ComponentQty}"
                    };
                }
            }
        }

        // 3. 验证每个FinalTask的ΣShare不超过Task.Quantity
        var sharesByTask = allocationShares
            .GroupBy(s => s.FinalDraftId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.ComponentQty));

        foreach (var task in tasks)
        {
            if (sharesByTask.TryGetValue(task.FinalDraftId, out var totalTaskShare))
            {
                if (totalTaskShare > task.Quantity + 0.001m)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Task {task.FinalDraftId} 的 ΣShare={totalTaskShare} 超过 Quantity={task.Quantity}"
                    };
                }
            }
        }

        // 4. 验证FinalTask Resource硬互斥（同一资源上的时间段不重叠）
        var tasksByResource = tasks
            .GroupBy(t => t.ResourceId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.PlannedStartTime).ToList());

        foreach (var resourceGroup in tasksByResource.Values)
        {
            for (int i = 0; i < resourceGroup.Count - 1; i++)
            {
                var current = resourceGroup[i];
                var next = resourceGroup[i + 1];

                // 当前Task的结束时间（包括Setup）必须 <= 下一个Task的开始时间（Setup之前）
                var currentOccupancyStart = current.PlannedStartTime.AddMinutes(-(double)current.SetupTime);
                var nextOccupancyStart = next.PlannedStartTime.AddMinutes(-(double)next.SetupTime);

                if (current.PlannedEndTime > nextOccupancyStart)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Resource {current.ResourceId} 时间冲突: Task {current.FinalDraftId} [{currentOccupancyStart:HH:mm:ss}-{current.PlannedEndTime:HH:mm:ss}] 与 Task {next.FinalDraftId} [{nextOccupancyStart:HH:mm:ss}-{next.PlannedEndTime:HH:mm:ss}] 重叠"
                    };
                }
            }
        }

        // 5. 验证Task不早于Material AvailableTime
        // 第5轮修复：必须按累计数量达到Task所需Qty，取真正Material Ready Time
        foreach (var task in tasks)
        {
            var demand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == task.SourceDraftId);
            if (demand == null) continue;

            if (constraints.MaterialAvailability.TryGetValue(demand.AllocationSequence, out var segments) && segments.Count > 0)
            {
                // 按时间排序Segments，累计数量直到满足Task需求
                var sortedSegments = segments.OrderBy(s => s.AvailableTime).ToList();
                decimal cumulativeQty = 0m;
                DateTime? materialReadyTime = null;

                // Task所需的物料数量（取PlannedProcessQty，因为这是实际加工需要的数量）
                var requiredQty = task.PlannedProcessQty;

                foreach (var segment in sortedSegments)
                {
                    cumulativeQty += segment.Quantity;
                    if (cumulativeQty >= requiredQty)
                    {
                        materialReadyTime = segment.AvailableTime;
                        break;
                    }
                }

                // 如果累计数量仍不足，取最后一个Segment的时间（物料始终不足）
                if (materialReadyTime == null && sortedSegments.Count > 0)
                {
                    materialReadyTime = sortedSegments.Last().AvailableTime;
                }

                if (materialReadyTime.HasValue && task.PlannedStartTime < materialReadyTime.Value)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Task {task.FinalDraftId} 开始时间 {task.PlannedStartTime:yyyy-MM-dd HH:mm:ss} 早于物料累计可用时间 {materialReadyTime.Value:yyyy-MM-dd HH:mm:ss}"
                    };
                }
            }
        }

        // 6. 验证Locked Anchor是否保持原地
        foreach (var lockedTask in constraints.LockedTasks.Values)
        {
            var correspondingTask = tasks
                .FirstOrDefault(t => t.SourceDraftId == lockedTask.DraftId &&
                                    t.ResourceId == lockedTask.ResourceId);

            if (correspondingTask != null)
            {
                // 验证时间是否与锁定时间一致
                if (Math.Abs((correspondingTask.PlannedStartTime - lockedTask.LockedStart).TotalSeconds) > 1 ||
                    Math.Abs((correspondingTask.PlannedEndTime - lockedTask.LockedEnd).TotalSeconds) > 1)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Locked Task {lockedTask.DraftId} 时间未保持原地: 期望[{lockedTask.LockedStart:HH:mm:ss}-{lockedTask.LockedEnd:HH:mm:ss}], 实际[{correspondingTask.PlannedStartTime:HH:mm:ss}-{correspondingTask.PlannedEndTime:HH:mm:ss}]"
                    };
                }
            }
        }

        // 7. 第5轮修复：验证TaskDependency硬约束
        var taskDict = tasks.ToDictionary(t => t.FinalDraftId);
        foreach (var dep in taskDependencies)
        {
            // 检查上游Task是否存在
            if (!taskDict.TryGetValue(dep.UpstreamFinalDraftId, out var upstreamTask))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"TaskDependency引用的上游Task {dep.UpstreamFinalDraftId} 不存在"
                };
            }

            // 检查下游Task是否存在
            if (!taskDict.TryGetValue(dep.DownstreamFinalDraftId, out var downstreamTask))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"TaskDependency引用的下游Task {dep.DownstreamFinalDraftId} 不存在"
                };
            }

            // 检查时间约束：下游开始时间必须 >= 上游结束时间 + Lag
            var lagTime = TimeSpan.FromMinutes((double)dep.LagTime);
            var earliestDownstreamStart = upstreamTask.PlannedEndTime + lagTime;
            if (downstreamTask.PlannedStartTime < earliestDownstreamStart)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"TaskDependency违反时间约束: 下游Task {downstreamTask.FinalDraftId} 开始时间 {downstreamTask.PlannedStartTime:yyyy-MM-dd HH:mm:ss} 早于上游Task {upstreamTask.FinalDraftId} 结束时间 {upstreamTask.PlannedEndTime:yyyy-MM-dd HH:mm:ss} + Lag {dep.LagTime}分钟"
                };
            }
        }

        return new ValidationResult { IsValid = true };
    }

    /// <summary>
    /// 验证结果
    /// </summary>
    private class ValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
