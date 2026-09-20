using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 1: 硬约束构建器
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 Phase 1
///
/// 职责：
/// - 建立 Routing（工艺路线）
/// - Resource Eligibility（资源资格约束）
/// - Calendar（日历可用时间）
/// - Material AvailableTime（物料多段可用时间）
/// - Execution/Firm/Frozen（不可逆约束）
/// - Shared-resource blocks（共享资源阻挡）
/// - Quantity-Time（数量-时间约束）
/// - 工序先后关系
/// </summary>
internal class PhaseOneConstraintBuilder
{
    /// <summary>
    /// 构建硬约束上下文
    /// </summary>
    public ConstraintContext BuildConstraints(DomainSolveRequest request)
    {
        var context = new ConstraintContext();

        // ═══════════════════════════════════════════════
        // 0. 部门锁定（最小 B）：按 (MaterialId, StageCode) → ProductionDepartmentId
        //    过滤 Routing 三件套；缺失 Context 的 Material 记入 context.MissingDepartmentContextMaterialIds
        // ═══════════════════════════════════════════════
        ApplyDepartmentLock(request, context,
            out var lockedOperations,
            out var lockedDependencies,
            out var lockedEligibilities);

        // ═══════════════════════════════════════════════
        // 1. 解析工序依赖图（使用部门锁定后的 Routing）
        // ═══════════════════════════════════════════════
        BuildRoutingGraphs(lockedOperations, lockedDependencies, context);

        // ═══════════════════════════════════════════════
        // 1.5 解析跨物料依赖 DAG（任务喂任务，方案A）：子先父后拓扑分层
        // ═══════════════════════════════════════════════
        BuildCrossMaterialDag(request, context);

        // ═══════════════════════════════════════════════
        // 2. 解析工序资源资格（使用部门锁定后的 Eligibility）
        // ═══════════════════════════════════════════════
        BuildOperationResourceEligibility(lockedEligibilities, context);

        // ═══════════════════════════════════════════════
        // 2.5 解析资源编码（ResourceId → ResourceCode）
        // ═══════════════════════════════════════════════
        BuildResourceCodes(request, context);

        // ═══════════════════════════════════════════════
        // 3. 解析资源日历（Resource → 可用时间窗列表）
        // ═══════════════════════════════════════════════
        BuildResourceCalendars(request, context);

        // ═══════════════════════════════════════════════
        // 4. 解析物料多段可用性（AllocationSequence → Quantity-Time 分段）
        // ═══════════════════════════════════════════════
        BuildMaterialAvailability(request, context);

        // ═══════════════════════════════════════════════
        // 5. 解析锁定任务约束（DraftId → 锁定信息）
        // ═══════════════════════════════════════════════
        BuildLockedTasks(request, context);

        // ═══════════════════════════════════════════════
        // 6. 解析共享资源占用块（Resource → 占用时间块列表）
        // ═══════════════════════════════════════════════
        BuildResourceBlocks(request, context);

        return context;
    }

    /// <summary>
    /// 部门锁定（最小 B）：按 (MaterialId, StageCode) → ProductionDepartmentId 过滤 Routing 三件套。
    /// 文档：PM 裁定《ProductionDepartment回复.md》最小 B
    /// 1号位只消费 2号位传入的 MaterialStageDepartmentContexts，不得重新推导部门、不得跨部门优化选择。
    /// </summary>
    private void ApplyDepartmentLock(
        DomainSolveRequest request,
        ConstraintContext context,
        out List<RoutingOperation> lockedOperations,
        out List<RoutingDependency> lockedDependencies,
        out List<OperationResourceEligibility> lockedEligibilities)
    {
        // (MaterialId, StageCode) → ProductionDepartmentId（2号位保证 (MaterialId, StageCode) 唯一）
        var deptContext = request.MaterialStageDepartmentContexts
            .GroupBy(c => (c.MaterialId, c.StageCode))
            .ToDictionary(g => g.Key, g => g.First().ProductionDepartmentId);

        // 1. 识别缺失 Context 的 Material：仅校验「本次求解在范围」的工序 Stage。
        //    P1-06修复：StartOperation/StartStage 之前的已完成 Stage 不再参与本次求解，
        //    即使其缺 Department Context 也不应误杀当前剩余 Routing。
        //    无 Start 信息的需求（新单从首工序起）仍校验全量 Stage。
        var reachableStages = BuildReachableStages(request);
        foreach (var op in request.RoutingOperations)
        {
            var stageCode = op.StageCode ?? string.Empty;

            // 防御：该 Material 无任何需求时，不参与缺失判定。
            if (!reachableStages.TryGetValue(op.MaterialId, out var inScopeStages))
            {
                continue;
            }

            // P1-06修复：仅当该工序 Stage 在「本次求解范围」内时，才纳入缺失判定。
            if (!inScopeStages.Contains(stageCode))
            {
                continue;
            }

            if (!deptContext.ContainsKey((op.MaterialId, stageCode)))
            {
                context.MissingDepartmentContextMaterialIds.Add(op.MaterialId);
            }
        }

        // 2. 过滤 RoutingOperation：缺失 Material 整条剔除 + 部门不符剔除
        // v5.0.16 冻结：Routing 三件套唯一键升级为含 ProductionDepartmentId 的三元/四元组；
        // 部门=「物料×阶段」联合属性，同物料同阶段不同部门有不同小工序集合，
        // 因此 validOperationKeys 必须带 ProductionDepartmentId，否则会把别的部门同名 Operation 的
        // Dependency / Eligibility 漏进当前部门的锁定图。
        lockedOperations = new List<RoutingOperation>();
        var validOperationKeys = new HashSet<(int MaterialId, int ProductionDepartmentId, string RouteCode, string OperationCode)>();

        foreach (var op in request.RoutingOperations)
        {
            if (context.MissingDepartmentContextMaterialIds.Contains(op.MaterialId))
            {
                continue; // 缺失 Context 的 Material 整条剔除，Phase2 无路由 → Unscheduled
            }

            var stageCode = op.StageCode ?? string.Empty;
            var expectedDeptId = deptContext[(op.MaterialId, stageCode)];

            if (op.ProductionDepartmentId != expectedDeptId)
            {
                continue; // 部门不符，剔除
            }

            lockedOperations.Add(op);
            validOperationKeys.Add((op.MaterialId, op.ProductionDepartmentId, op.RouteCode, op.OperationCode));
        }

        // 3. 过滤 RoutingDependency：两端 Operation 都必须合法，且部门与锁定 Operation 一致
        lockedDependencies = request.RoutingDependencies
            .Where(dep =>
                validOperationKeys.Contains((dep.MaterialId, dep.ProductionDepartmentId, dep.RouteCode, dep.FromOperationCode)) &&
                validOperationKeys.Contains((dep.MaterialId, dep.ProductionDepartmentId, dep.RouteCode, dep.ToOperationCode)))
            .ToList();

        // 4. 过滤 OperationResourceEligibility：Operation 必须合法，且部门与锁定 Operation 一致
        lockedEligibilities = request.OperationResourceEligibility
            .Where(e => validOperationKeys.Contains((e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.OperationCode)))
            .ToList();
    }

    /// <summary>
    /// P1-06修复：按需求计算「本次求解在范围」的工序 Stage 集合。
    /// 续排（有 StartOperationCode/StartStageCode）时，起点之前的已完成 Stage 不在本次求解范围，
    /// 即使其缺 Department Context 也不应把整条 Routing 误判为 MISSING_PRODUCTION_DEPARTMENT_CONTEXT。
    /// 无 Start 信息的需求（新单从首工序起）仍校验全量 Stage（BFS 自根工序覆盖整图）。
    ///
    /// 返回：MaterialId → 该物料在范围内（= 所有相关需求可达 Stage 的并集）的 StageCode 集合。
    /// </summary>
    private Dictionary<int, HashSet<string>> BuildReachableStages(DomainSolveRequest request)
    {
        var result = new Dictionary<int, HashSet<string>>();

        // 预处理：物料 → (工序码 → StageCode) 与依赖邻接表（V1 默认 DEFAULT 路径）
        var opsByMaterial = request.RoutingOperations
            .GroupBy(op => op.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var depsByMaterial = request.RoutingDependencies
            .GroupBy(dep => dep.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var demand in request.LogicalProductionDemands)
        {
            if (!opsByMaterial.TryGetValue(demand.MaterialId, out var ops))
            {
                continue; // 该物料无 Routing，无 Stage 可校验
            }

            if (!result.TryGetValue(demand.MaterialId, out var stageSet))
            {
                stageSet = new HashSet<string>();
                result[demand.MaterialId] = stageSet;
            }

            // 工序码 → StageCode（仅 DEFAULT 路径，V1 单路径）
            var opCodeToStage = ops
                .Where(op => op.RouteCode == "DEFAULT")
                .GroupBy(op => op.OperationCode)
                .ToDictionary(g => g.Key, g => g.First().StageCode ?? string.Empty);

            // 依赖邻接表：From → List&lt;To&gt; + 入度（用于识别根工序）
            var adjacency = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();
            foreach (var opCode in opCodeToStage.Keys)
            {
                inDegree[opCode] = 0;
            }

            if (depsByMaterial.TryGetValue(demand.MaterialId, out var deps))
            {
                foreach (var dep in deps.Where(d => d.RouteCode == "DEFAULT"))
                {
                    if (!adjacency.TryGetValue(dep.FromOperationCode, out var tos))
                    {
                        tos = new List<string>();
                        adjacency[dep.FromOperationCode] = tos;
                    }
                    tos.Add(dep.ToOperationCode);

                    if (inDegree.ContainsKey(dep.ToOperationCode))
                    {
                        inDegree[dep.ToOperationCode]++;
                    }
                }
            }

            // 起点工序集合
            var hasStartOperation = !string.IsNullOrWhiteSpace(demand.StartOperationCode);
            var hasStartStage = !string.IsNullOrWhiteSpace(demand.StartStageCode);
            var startOps = new List<string>();

            if (hasStartOperation)
            {
                // 5号位 交付的工序级起点：精确到单个工序
                if (opCodeToStage.ContainsKey(demand.StartOperationCode!))
                {
                    startOps.Add(demand.StartOperationCode!);
                }
            }
            else
            {
                // 无工序级起点：新单从根工序起；若有 StartStageCode 则收窄到该 Stage 的根工序
                foreach (var opCode in opCodeToStage.Keys)
                {
                    if (inDegree[opCode] != 0)
                    {
                        continue; // 非根工序
                    }
                    if (hasStartStage && opCodeToStage[opCode] != demand.StartStageCode)
                    {
                        continue; // 不在起始 Stage
                    }
                    startOps.Add(opCode);
                }
            }

            // BFS 可达工序 → 收集 StageCode
            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            foreach (var s in startOps)
            {
                if (visited.Add(s))
                {
                    queue.Enqueue(s);
                }
            }

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (opCodeToStage.TryGetValue(cur, out var stage))
                {
                    stageSet.Add(stage);
                }

                if (adjacency.TryGetValue(cur, out var tos))
                {
                    foreach (var to in tos)
                    {
                        if (visited.Add(to))
                        {
                            queue.Enqueue(to);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 构建工序依赖图（使用部门锁定后的 Routing）
    /// </summary>
    private void BuildRoutingGraphs(
        List<RoutingOperation> routingOperations,
        List<RoutingDependency> routingDependencies,
        ConstraintContext context)
    {
        // 按 MaterialId 分组
        var operationsByMaterial = routingOperations
            .GroupBy(op => op.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (materialId, operations) in operationsByMaterial)
        {
            // 按 RouteCode 再分组
            var operationsByRoute = operations
                .GroupBy(op => op.RouteCode)
                .ToDictionary(g => g.Key, g => g.ToList());

            var routeGraphs = new Dictionary<string, RoutingGraph>();

            foreach (var (routeCode, routeOps) in operationsByRoute)
            {
                var graph = new RoutingGraph();

                // 构建工序节点
                foreach (var op in routeOps)
                {
                    graph.Operations[op.OperationCode] = new OperationNode
                    {
                        OperationCode = op.OperationCode,
                        OperationName = op.OperationName,
                        ProcessType = op.ProcessType,
                        StageCode = op.StageCode,
                        StandardDuration = op.StandardDuration,
                        SetupTime = op.SetupTime,
                        TransferBatchSize = op.TransferBatchSize,
                        RouteCode = op.RouteCode,
                        PathId = op.PathId
                    };
                }

                // 构建依赖边
                var dependencies = routingDependencies
                    .Where(dep => dep.MaterialId == materialId && dep.RouteCode == routeCode)
                    .ToList();

                foreach (var dep in dependencies)
                {
                    if (!graph.Dependencies.ContainsKey(dep.ToOperationCode))
                    {
                        graph.Dependencies[dep.ToOperationCode] = new List<DependencyEdge>();
                    }

                    graph.Dependencies[dep.ToOperationCode].Add(new DependencyEdge
                    {
                        FromOperationCode = dep.FromOperationCode,
                        ToOperationCode = dep.ToOperationCode,
                        DependencyType = dep.DependencyType,
                        LagTime = dep.LagTime
                    });
                }

                // 识别根工序（无前驱的工序）
                var allToOps = graph.Dependencies.Keys.ToHashSet();
                graph.RootOperations = graph.Operations.Keys
                    .Where(opCode => !allToOps.Contains(opCode))
                    .ToList();

                routeGraphs[routeCode] = graph;
            }

            context.RoutingGraphs[materialId] = routeGraphs;
        }
    }

    /// <summary>
    /// 构建跨物料依赖 DAG（任务喂任务，方案A）：子先父后拓扑分层
    /// 文档：0号位 2026-09-10 裁决 方案A —— 跨物料时序作为硬约束在求解内实现
    ///
    /// 消费 request.MaterialRequirementLinks（父 ConsumerLogicalDemandKey → 子 ProducerLogicalDemandKey）
    /// 产出：
    ///   - context.CrossMaterialOrder：按「子层先、父层后」拍平的有序 DemandKey 列表（层内按 DemandSequence）
    ///   - context.CrossMaterialLayers：分层结构（每层一个 DemandKey 列表），供 Phase2 层内排序
    ///   - context.CrossMaterialHasCycle：是否检测到 BOM 依赖环（技术失败）
    /// 无 link（如子件全库存/全PI，不产 link）时：CrossMaterialOrder 保持原 DemandSequence 平铺顺序，零影响。
    /// </summary>
    private void BuildCrossMaterialDag(DomainSolveRequest request, ConstraintContext context)
    {
        var links = request.MaterialRequirementLinks;
        if (links == null || links.Count == 0)
        {
            // 无跨物料依赖：直接按 DemandSequence 平铺（等价于旧行为）
            context.CrossMaterialOrder = request.LogicalProductionDemands
                .OrderBy(d => d.DemandSequence)
                .Select(d => d.LogicalDemandKey)
                .ToList();
            context.CrossMaterialLayers = new List<List<string>> { context.CrossMaterialOrder };
            return;
        }

        // 参与拓扑的节点 = 所有 demand 的 LogicalDemandKey
        var allDemandKeys = request.LogicalProductionDemands
            .Select(d => d.LogicalDemandKey)
            .ToHashSet();

        // 邻接表：child → list<parent>（子先排，父后排）
        var childrenToParents = new Dictionary<string, List<string>>();
        var parentToChildren = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();
        foreach (var key in allDemandKeys)
        {
            inDegree[key] = 0;
        }

        foreach (var link in links)
        {
            // 只处理两端节点都在当前 Demand 集合内的 link（防御：忽略脏数据/域外引用）
            if (!allDemandKeys.Contains(link.ProducerLogicalDemandKey) ||
                !allDemandKeys.Contains(link.ConsumerLogicalDemandKey))
            {
                continue;
            }

            var child = link.ProducerLogicalDemandKey;
            var parent = link.ConsumerLogicalDemandKey;

            if (!childrenToParents.TryGetValue(child, out var parents))
            {
                parents = new List<string>();
                childrenToParents[child] = parents;
            }
            parents.Add(parent);

            if (!parentToChildren.TryGetValue(parent, out var children))
            {
                children = new List<string>();
                parentToChildren[parent] = children;
            }
            children.Add(child);

            // P1-12：记录 父→子 边界的滞后时间（分钟），供 GetDynamicMaterialFloor 累加。
            context.CrossMaterialLagMinutes[(parent, child)] = link.LagMinutes;

            // 父的入度 = 它依赖的子件数
            inDegree[parent]++;
        }

        context.CrossMaterialParentToChildren = parentToChildren;

        // 层内按 DemandSequence 排序（保持 2号位 业务优先级）——索引只建一次
        var demandByKey = request.LogicalProductionDemands
            .ToDictionary(d => d.LogicalDemandKey);

        // Kahn 拓扑排序：入度为 0 的（不依赖任何子件的）先入队
        var queue = new Queue<string>();
        foreach (var key in allDemandKeys)
        {
            if (inDegree[key] == 0)
            {
                queue.Enqueue(key);
            }
        }

        var layers = new List<List<string>>();
        var ordered = new List<string>();
        var visited = 0;

        while (queue.Count > 0)
        {
            var layer = new List<string>(queue.Count);
            var nextLayer = new List<string>();

            foreach (var node in queue)
            {
                layer.Add(node);
                ordered.Add(node);
                visited++;

                if (childrenToParents.TryGetValue(node, out var parents))
                {
                    foreach (var parent in parents)
                    {
                        inDegree[parent]--;
                        if (inDegree[parent] == 0)
                        {
                            nextLayer.Add(parent);
                        }
                    }
                }
            }

            layer.Sort((a, b) => DemandSeq(a).CompareTo(DemandSeq(b)));
            nextLayer.Sort((a, b) => DemandSeq(a).CompareTo(DemandSeq(b)));

            layers.Add(layer);
            queue = new Queue<string>(nextLayer);
        }

        int DemandSeq(string key)
            => demandByKey.TryGetValue(key, out var d) ? d.DemandSequence : int.MaxValue;

        // 环检测：visited < 总节点数 → 存在环
        if (visited < allDemandKeys.Count)
        {
            context.CrossMaterialHasCycle = true;
            context.CrossMaterialOrder = ordered;
            context.CrossMaterialLayers = layers;
            return;
        }

        context.CrossMaterialOrder = ordered;
        context.CrossMaterialLayers = layers;
    }

    /// <summary>
    /// 构建工序资源资格映射（使用部门锁定后的 Eligibility）
    /// </summary>
    private void BuildOperationResourceEligibility(
        List<OperationResourceEligibility> eligibilities,
        ConstraintContext context)
    {
        // 按 (MaterialId, RouteCode, OperationCode) → ResourceId 列表（按 Priority 排序）
        // P0-01修复：使用冻结接口 OperationResourceEligibility，不再使用旧的 ResourceEligibility
        // 第4轮C1修复：索引加入MaterialId，避免不同物料共享资源资格
        var eligibilityGroups = eligibilities
            .GroupBy(e => $"{e.MaterialId}::{e.RouteCode}::{e.OperationCode}")
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.Priority)
                      .Select(e => e.ResourceId)
                      .ToList()
            );

        context.OperationResourceEligibility = eligibilityGroups;

        // P0-04修复：同时构建 ResourceCapacityFactors 映射
        // 第4轮C1修复：索引加入MaterialId
        // (MaterialId::RouteCode::OperationCode, ResourceId) → CapacityFactor
        var capacityFactors = eligibilities
            .GroupBy(e => $"{e.MaterialId}::{e.RouteCode}::{e.OperationCode}")
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    e => e.ResourceId,
                    e => e.CapacityFactor
                )
            );

        context.ResourceCapacityFactors = capacityFactors;
    }

    /// <summary>
    /// 构建资源编码映射（ResourceId → ResourceCode）
    /// P1-08修复：供 Phase2/Phase4 生成 FinalTaskDraft 时回填 ResourceCode。
    /// </summary>
    private void BuildResourceCodes(DomainSolveRequest request, ConstraintContext context)
    {
        context.ResourceCodes = request.Resources
            .GroupBy(r => r.ResourceId)
            .ToDictionary(g => g.Key, g => g.First().ResourceCode);

        // P1-02（BottleneckMode 锚点）：反向映射 Code → ResourceId，供 Phase3 消费 AnchorResourceCode 反查。
        // 空编码（string.Empty）跳过；编码重复时取首个 ResourceId（业务编码应唯一，防御 GroupBy）。
        context.ResourceIdsByCode = request.Resources
            .Where(r => !string.IsNullOrEmpty(r.ResourceCode))
            .GroupBy(r => r.ResourceCode)
            .ToDictionary(g => g.Key, g => g.First().ResourceId);
    }

    /// <summary>
    /// 构建资源日历（只保留可用时间窗）
    /// </summary>
    private void BuildResourceCalendars(DomainSolveRequest request, ConstraintContext context)
    {
        var calendarsByResource = request.CalendarSlots
            .Where(slot => slot.IsAvailable)  // 只保留可用时间窗
            .GroupBy(slot => slot.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(slot => slot.Start)
                      .Select(slot => new TimeWindow(slot.Start, slot.End))
                      .ToList()
            );

        context.ResourceCalendars = calendarsByResource;
    }

    /// <summary>
    /// 构建物料多段可用性
    /// </summary>
    private void BuildMaterialAvailability(DomainSolveRequest request, ConstraintContext context)
    {
        var availabilityByAllocation = request.MaterialConstraints
            .GroupBy(m => m.AllocationSequence)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(m => m.AvailableTime)
                      .Select(m => new MaterialAvailabilitySegment
                      {
                          Quantity = m.Quantity,
                          AvailableTime = m.AvailableTime,
                          SourceType = m.SourceType,
                          SourceKey = m.SourceKey
                      })
                      .ToList()
            );

        context.MaterialAvailability = availabilityByAllocation;
    }

    /// <summary>
    /// 构建锁定任务约束
    /// 第8轮P0-01修复：完整传递ExecutionConstraint的StageCode/OperationCode/LockedQuantity/TaskKey
    /// </summary>
    private void BuildLockedTasks(DomainSolveRequest request, ConstraintContext context)
    {
        // P1-07：复合键 (DraftId, OperationCode)。同一 LogicalDemand 多操作锚点（多 ExecutionConstraint）
        // 不再因重复 DraftId 抛 ToDictionary 异常；OperationCode 归一化为 string.Empty（2号位 口径：恒非空）。
        var lockedTasks = request.ExecutionConstraints
            .ToDictionary(
                ec => (ec.DraftId, ec.OperationCode ?? string.Empty),
                ec => new LockedTaskConstraint
                {
                    DraftId = ec.DraftId,
                    ResourceId = ec.ResourceId,
                    LockedStart = ec.LockedStart,
                    LockedEnd = ec.LockedEnd,
                    ConstraintType = ec.ConstraintType,
                    StageCode = ec.StageCode,
                    OperationCode = ec.OperationCode,
                    LockedQuantity = ec.LockedQuantity,
                    LockedNetOutputQty = ec.LockedNetOutputQty,
                    LockedPlannedProcessQty = ec.LockedPlannedProcessQty,
                    TaskKey = ec.TaskKey
                }
            );

        context.LockedTasks = lockedTasks;
    }

    /// <summary>
    /// 构建共享资源占用块
    /// </summary>
    private void BuildResourceBlocks(DomainSolveRequest request, ConstraintContext context)
    {
        // Candidate §11：其它 Domain 当前 ACTIVE 共享资源占用（外部不可移动阻挡块）
        var blocks = new List<ResourceBlock>();
        if (request.CandidateContext?.ExternalDomainResourceBlocks != null)
            blocks.AddRange(request.CandidateContext.ExternalDomainResourceBlocks);

        // FULL §9：前序 Domain 成功后的共享 Resource 占用块（与 Candidate 外部块同语义：不可用时间窗）
        if (request.UpstreamDomainResourceBlocks != null)
            blocks.AddRange(request.UpstreamDomainResourceBlocks);

        if (blocks.Count == 0)
        {
            return;
        }

        var blocksByResource = blocks
            .GroupBy(block => block.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(block => new ResourceBlockInfo
                {
                    ResourceId = block.ResourceId,
                    StartTime = block.StartTime,
                    EndTime = block.EndTime,
                    Reason = block.Reason
                })
                .ToList()
            );

        context.ResourceBlocks = blocksByResource;
    }
}

/// <summary>
/// 硬约束上下文（Phase 1 输出）
/// </summary>
internal class ConstraintContext
{
    /// <summary>
    /// 工序依赖图：MaterialId → RouteCode → 工序依赖关系
    /// </summary>
    public Dictionary<int, Dictionary<string, RoutingGraph>> RoutingGraphs { get; set; } = new();

    /// <summary>
    /// 工序资源资格：(MaterialId, RouteCode, OperationCode) → 合法 ResourceId 列表（按优先级排序）
    /// </summary>
    public Dictionary<string, List<int>> OperationResourceEligibility { get; set; } = new();

    /// <summary>
    /// P0-04修复：工序资源产能系数映射：(RouteCode::OperationCode, ResourceId) → CapacityFactor
    /// 用于Duration计算：StandardDuration × PlannedProcessQty ÷ CapacityFactor
    /// </summary>
    public Dictionary<string, Dictionary<int, decimal>> ResourceCapacityFactors { get; set; } = new();

    /// <summary>
    /// 资源编码映射：ResourceId → ResourceCode（源 ResourceDefinition.ResourceCode）。
    /// P1-08修复：供 Phase2/Phase4 生成 FinalTaskDraft 时回填 ResourceCode，不再留空。
    /// </summary>
    public Dictionary<int, string> ResourceCodes { get; set; } = new();

    /// <summary>
    /// 资源编码反向映射：ResourceCode → ResourceId。
    /// P1-02（BottleneckMode 锚点）：供 Phase3 消费 AnchorResourceCode（业务编码）反查 ResourceId。
    /// 空编码（string.Empty）不纳入，避免无效键。
    /// </summary>
    public Dictionary<string, int> ResourceIdsByCode { get; set; } = new();

    /// <summary>
    /// 资源日历：ResourceId → 可用时间窗列表（已排序）
    /// </summary>
    public Dictionary<int, List<TimeWindow>> ResourceCalendars { get; set; } = new();

    /// <summary>
    /// 物料多段可用性：AllocationSequence → Quantity-Time 分段列表（已按时间排序）
    /// </summary>
    public Dictionary<long, List<MaterialAvailabilitySegment>> MaterialAvailability { get; set; } = new();

    /// <summary>
    /// 锁定任务约束：P1-07 复合键 (DraftId, OperationCode) → 锁定信息。
    /// 同一 LogicalDemand 可有多操作锚点，故不能再用 DraftId 单键。
    /// </summary>
    public Dictionary<(string DraftId, string OperationCode), LockedTaskConstraint> LockedTasks { get; set; } = new();

    /// <summary>
    /// 共享资源占用块：ResourceId → 占用时间块列表
    /// </summary>
    public Dictionary<int, List<ResourceBlockInfo>> ResourceBlocks { get; set; } = new();

    /// <summary>
    /// 缺失生产部门 Context 的 MaterialId 集合。
    /// 部门锁定（最小 B）：按 (MaterialId, StageCode) 查 MaterialStageDepartmentContexts，
    /// 若某 Material 的任一工序 Stage 查不到 Context，则该 Material 整条 Routing 无法锁定部门，
    /// 对应的 Demand 应标记 Unscheduled，Reason = MISSING_PRODUCTION_DEPARTMENT_CONTEXT。
    /// </summary>
    public HashSet<int> MissingDepartmentContextMaterialIds { get; set; } = new();

    /// <summary>
    /// 跨物料依赖（任务喂任务，方案A）：子先父后的有序 LogicalDemandKey 列表（已按层拍平）。
    /// 无跨物料 link 时等价于按 DemandSequence 平铺。
    /// </summary>
    public List<string> CrossMaterialOrder { get; set; } = new();

    /// <summary>
    /// 跨物料依赖分层：每层一个 LogicalDemandKey 列表（子件层在前，父件层在后）。
    /// </summary>
    public List<List<string>> CrossMaterialLayers { get; set; } = new();

    /// <summary>
    /// 父 → 子 映射（父 LogicalDemandKey → 其直接子件 LogicalDemandKey 列表）。
    /// 供 Phase2 在排父件时取子件的真实完成时间（动态物料可用时间合并）。
    /// </summary>
    public Dictionary<string, List<string>> CrossMaterialParentToChildren { get; set; } = new();

    /// <summary>
    /// P1-12：父 → (父, 子) 边界的滞后时间（分钟）。键为 (父 LogicalDemandKey, 子 LogicalDemandKey)。
    /// 供 Phase2/Phase4 的 GetDynamicMaterialFloor 在子件完成时间上累加滞后。
    /// </summary>
    public Dictionary<(string, string), decimal> CrossMaterialLagMinutes { get; set; } = new();

    /// <summary>
    /// 跨物料 BOM 依赖存在环（技术失败标记）。
    /// </summary>
    public bool CrossMaterialHasCycle { get; set; } = false;
}

/// <summary>
/// 工序依赖图（单个物料单条路径）
/// </summary>
internal class RoutingGraph
{
    /// <summary>
    /// 工序节点：OperationCode → 工序信息
    /// </summary>
    public Dictionary<string, OperationNode> Operations { get; set; } = new();

    /// <summary>
    /// 依赖边：ToOperationCode → 前驱列表
    /// </summary>
    public Dictionary<string, List<DependencyEdge>> Dependencies { get; set; } = new();

    /// <summary>
    /// 根工序（无前驱的工序）
    /// </summary>
    public List<string> RootOperations { get; set; } = new();
}

/// <summary>
/// 工序节点
/// </summary>
internal class OperationNode
{
    public string OperationCode { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string ProcessType { get; set; } = string.Empty;
    public string? StageCode { get; set; }
    public decimal StandardDuration { get; set; }
    public decimal SetupTime { get; set; }
    public decimal? TransferBatchSize { get; set; }

    // P1-08修复：工序节点补齐工艺路径编码与路径序号（源 RoutingOperation.RouteCode/PathId）。
    // 供 Phase2/Phase4 生成 FinalTaskDraft 时回填 RouteCode/PathId，不再留 null。
    public string RouteCode { get; set; } = "DEFAULT";
    public int PathId { get; set; } = 1;
}

/// <summary>
/// 依赖边
/// </summary>
internal class DependencyEdge
{
    public string FromOperationCode { get; set; } = string.Empty;
    public string ToOperationCode { get; set; } = string.Empty;
    public string DependencyType { get; set; } = "ES";
    public decimal LagTime { get; set; }
}

/// <summary>
/// 物料可用性分段
/// </summary>
internal class MaterialAvailabilitySegment
{
    public decimal Quantity { get; set; }
    public DateTime AvailableTime { get; set; }
    public string? SourceType { get; set; }
    public string? SourceKey { get; set; }
}

/// <summary>
/// 锁定任务约束
/// </summary>
internal class LockedTaskConstraint
{
    public string DraftId { get; set; } = string.Empty;
    public int ResourceId { get; set; }
    public DateTime LockedStart { get; set; }
    public DateTime LockedEnd { get; set; }
    public string ConstraintType { get; set; } = string.Empty;

    // 第8轮P0-01修复：Anchor部分数量和工序信息闭环
    public string? StageCode { get; set; }
    public string? OperationCode { get; set; }
    public decimal? LockedQuantity { get; set; }

    // P1-01：净合格数量（YIELD 场景，锁定 Task 的净产出，!= 加工量）
    public decimal? LockedNetOutputQty { get; set; }

    // P1-01：产能加工数量（锁定 Task 的计划加工量）
    public decimal? LockedPlannedProcessQty { get; set; }

    public string? TaskKey { get; set; }
}

/// <summary>
/// 资源占用块信息
/// </summary>
internal class ResourceBlockInfo
{
    public int ResourceId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}
