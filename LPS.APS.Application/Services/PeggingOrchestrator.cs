using System.Data;
using System.Diagnostics;
using Dapper;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Rules;
using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Models;
using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using ApsTask = LPS.APS.Core.Entities.APS.Task;

namespace LPS.APS.Application.Services;

/// <summary>
/// Pegging 编排器（2号位职责）
/// </summary>
public class PeggingOrchestrator : IPeggingOrchestrator
{
    private readonly IDemandSupplyHardLockRepository _lockRepo;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PeggingOrchestrator> _logger;
    private readonly IFiniteCapacityScheduler _scheduler;
    private readonly IDemandPriorityExecutor _demandPriorityExecutor;
    private readonly IDemandPriorityConfigProvider _demandPriorityConfigProvider;
    private readonly IFrozenStrategySnapshotProvider _frozenStrategySnapshotProvider;
    private readonly ITimedSupplyFactLoader _timedSupplyFactLoader;
    private readonly IProcurementManualEtaRepository _procurementManualEtaRepo;
    private readonly IProductionInstructionPositionCalculator _piPositionCalculator;
    private readonly IProductionInstructionPositionSnapshotRepository _piPositionSnapshotRepo;

    public PeggingOrchestrator(
        IDemandSupplyHardLockRepository lockRepo,
        DatabaseConnectionManager connectionManager,
        ILogger<PeggingOrchestrator> logger,
        IFiniteCapacityScheduler scheduler,
        IDemandPriorityExecutor demandPriorityExecutor,
        IDemandPriorityConfigProvider demandPriorityConfigProvider,
        IFrozenStrategySnapshotProvider frozenStrategySnapshotProvider,
        ITimedSupplyFactLoader timedSupplyFactLoader,
        IProcurementManualEtaRepository procurementManualEtaRepo,
        IProductionInstructionPositionCalculator piPositionCalculator,
        IProductionInstructionPositionSnapshotRepository piPositionSnapshotRepo)
    {
        _lockRepo           = lockRepo           ?? throw new ArgumentNullException(nameof(lockRepo));
        _connectionManager  = connectionManager  ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger             = logger             ?? throw new ArgumentNullException(nameof(logger));
        _scheduler          = scheduler          ?? throw new ArgumentNullException(nameof(scheduler));
        _demandPriorityExecutor = demandPriorityExecutor ?? throw new ArgumentNullException(nameof(demandPriorityExecutor));
        _demandPriorityConfigProvider = demandPriorityConfigProvider ?? throw new ArgumentNullException(nameof(demandPriorityConfigProvider));
        _frozenStrategySnapshotProvider = frozenStrategySnapshotProvider ?? throw new ArgumentNullException(nameof(frozenStrategySnapshotProvider));
        _timedSupplyFactLoader = timedSupplyFactLoader ?? throw new ArgumentNullException(nameof(timedSupplyFactLoader));
        _procurementManualEtaRepo = procurementManualEtaRepo ?? throw new ArgumentNullException(nameof(procurementManualEtaRepo));
        _piPositionCalculator = piPositionCalculator ?? throw new ArgumentNullException(nameof(piPositionCalculator));
        _piPositionSnapshotRepo = piPositionSnapshotRepo ?? throw new ArgumentNullException(nameof(piPositionSnapshotRepo));
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<PeggingOrchestrationResult> ExecutePeggingWorkflowAsync(
        PeggingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new PeggingOrchestrationResult
        {
            PlanVersionId = request.PlanVersionId,
            OrderId       = request.OrderIds.FirstOrDefault()
        };

        _logger.LogInformation(
            "[Pegging] 开始: PlanVersionId={PlanVersionId}, 订单数={OrderCount}",
            request.PlanVersionId, request.OrderIds.Count);

        try
        {
            var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, cancellationToken);
            _logger.LogInformation(
                "[Pegging] BOM 快照加载完成: PlanVersionId={PlanVersionId}, 边数={EdgeCount}",
                request.PlanVersionId, bomSnapshot.EdgeCount);

            // ── 冻结策略快照（3号位）：Run 启动按已冻结 VersionId 装载一次，供 Supply 多键排序使用 ──
            // 缺失版本号即运行上下文不完整（与 BuildDemandSequenceMapAsync 同口径），禁止静默回退。
            var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
            if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            {
                throw new InvalidOperationException(
                    "Supply 排序策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
            }
            var frozenSnapshot = await _frozenStrategySnapshotProvider
                .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, cancellationToken);

            var (supplyPool, continuityFacts) = await LoadSupplyPoolAsync(request, frozenSnapshot, cancellationToken);
            _logger.LogInformation(
                "[Pegging] 供给池装载完成: PlanVersionId={PlanVersionId}, 条目={EntryCount}",
                request.PlanVersionId, supplyPool.TotalEntries);

            // ── 步骤3：PeggingLoop BOM 遍历 + 供给扣减 ──
            var voucher = await ExecutePeggingLoopAsync(request, bomSnapshot, supplyPool, cancellationToken);
            result.Voucher = voucher;

            // ── 结果红线校验（PM 口径，前 4 项；在 Solver 与落库之前拦截）──
            var redLineErrors = ValidatePeggingResult(supplyPool, voucher);
            if (redLineErrors.Count > 0)
            {
                foreach (var e in redLineErrors)
                    _logger.LogError("[Pegging] 结果红线校验失败: {Error}", e);
                result.IsSuccess   = false;
                result.ErrorMessage = string.Join("; ", redLineErrors);
                return result;
            }

            // ── 跨版本连续性分桶（PM 0914 红线）：先形成的 Q 按 IN_PROGRESS 既存执行切 Continuation/Free ──
            // Q 不因 WIP 供给减小；被覆盖份额以 Continuation Slice（IsContinuation=true）＋自由份额 Free Slice
            // 一起进 1号位（1号位必须看到 Continuation E + Free (Q−E)，不得只看到 Free）。E 来源 = StageProgressSnapshot.RemainingQty。
            var (bucketedDemands, overCommitEvents) = ApplyContinuityBucketing(
                voucher.LogicalProductionDemands, continuityFacts);
            voucher.LogicalProductionDemands = bucketedDemands;

            // v5.1.2架构整改：不再预先生成TaskDrafts，改为传递LogicalProductionDemands给1号位
            // 1号位基于LogicalProductionDemands生成FinalTasks（含拆批/合批决策）
            _logger.LogInformation("[Pegging] 准备传递LogicalProductionDemands给Solver: {Count} 个",
                voucher.LogicalProductionDemands.Count);

            // ── 装载 Routing 三件套 + 部门归属上下文（PM 裁定：最小 B）──
            // 2号位裁剪当前 Domain 所需的 (MaterialId, StageCode) Context 与 Routing 三件套一并传入 1号位；
            // 1号位按 (MaterialId, StageCode) → ProductionDepartmentId 锁定部门后过滤三件套（不得重新推导部门）。
            var demandMaterialIds = voucher.LogicalProductionDemands
                .Select(d => d.MaterialId)
                .Distinct()
                .ToList();

            var (routingOperations, routingDependencies, operationResourceEligibility) =
                await LoadRoutingContextAsync(demandMaterialIds, cancellationToken);

            var materialStageDeptContexts =
                await LoadMaterialStageDeptContextAsync(demandMaterialIds, cancellationToken);

            // ③ StartStageCode 填值（2026-09-11，5号位 O3 回复划归 2号位）：Routing 图「无入边源结点」的
            // 大工艺阶段码 → LogicalProductionDemand.StartStageCode（原 BuildLogicalProductionDemand 写空）。
            FillStartStageCodes(voucher, routingOperations, routingDependencies);

            if (demandMaterialIds.Count > 0 && routingOperations.Count == 0)
            {
                _logger.LogWarning(
                    "[Pegging] Routing 三件套为空（需求物料数={MaterialCount}），1号位将把所有新增生产需求判定为 Unscheduled；请确认 routing-sync（00:25）已灌入数据",
                    demandMaterialIds.Count);
            }

            // ── 求解参数：由冻结 SolverStrategy 块投影，不再硬编码（Q9 修复，2026-09-04）──
            // Mode → Direction 1:1（固定映射，见 SolverStrategyModeMap）；AllowSplit 由 Split.MaxOptimizationSplitCount>1 推；
            // AllowMerge 由 3号位 补源字段直接投影（2026-09-04 回执认可口径，零行为变化）。
            // P1-02 B 组：ImpactedTaskWarningPercent/MaxPropagationRounds/SplitAlternatives/MinBatchQty 直接投影
            // （3号位 已冻结；字段先行，1号位 换读后逐项激活，零行为变化）。
            var solverStrategy = frozenSnapshot.SolverStrategy;
            var schedulingDirection = SolverStrategyModeMap.ToDirection(solverStrategy.Mode);

            // P0-04：CandidateContext（FULL 为 null）。Base 锚点 = request.BasePlanVersionId（3号位冻结），
            // ChangeSeed/ExternalDomainResourceBlocks 由 BuildCandidateContextAsync 计算/透传。
            var candidateContext = await BuildCandidateContextAsync(request, voucher);

            var solveRequest = new DomainSolveRequest
            {
                ScheduleRunId = request.SchedulingContext?.ScheduleRunId,
                PlanVersionId = request.PlanVersionId,
                DomainKey     = request.DomainKey,
                DataCutoffTime = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt,
                // P0-03：PlanningStart/End 是有限产能求解窗口 = PlanVersion 的 90 天边界（PlanHorizonStart/End），
                // 与「FrozenWindowEnd(now+2h 滑动冻结窗)」解耦；冻结窗是约束（P0-05），不再充当 Solver Horizon。
                PlanningStart = request.SchedulingContext != null && request.SchedulingContext.PlanHorizonStart != default
                    ? request.SchedulingContext.PlanHorizonStart
                    : (request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt),
                PlanningEnd = request.SchedulingContext != null && request.SchedulingContext.PlanHorizonEnd != default
                    ? request.SchedulingContext.PlanHorizonEnd
                    : DateTime.Now.AddDays(90),

                LogicalProductionDemands = voucher.LogicalProductionDemands,
                MaterialRequirementLinks = voucher.MaterialRequirementLinks,
                AllocationLineage = BuildAllocationLineage(voucher),

                RoutingOperations = routingOperations,
                RoutingDependencies = routingDependencies,
                OperationResourceEligibility = operationResourceEligibility,
                MaterialStageDepartmentContexts = materialStageDeptContexts,

                MaterialConstraints = BuildMaterialConstraints(voucher),

                Resources     = BuildResourceDefinitions(request.SchedulingContext),
                CalendarSlots = BuildResourceCalendarSlots(request.SchedulingContext),
                ResourceEligibility = Array.Empty<ResourceEligibilityDefinition>(),
                // D8/R17/T18（同TaskNo自阻挡）：本域上一版本 ACTIVE 旧块不得作为「外部 ResourceBlock」挡自己，
                // 应转成 ExecutionConstraint 锚点（TaskKey 识别同一 Task）。当前锚点机制未建（PreferredResourceId
                // 软偏好=可选先不做），故预留为空；本域旧块既不进外部块（见 SchedulingOrchestrator 的 D8 守卫）、
                // 也不硬锁资源——D8 must-not 天然成立。
                ExecutionConstraints = Array.Empty<ExecutionConstraint>(),

                StrategySnapshot = new SolverStrategySnapshot
                {
                    StrategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId,
                    ParameterSetVersionId = frozenSnapshot.ParameterSetVersionId,
                    // P1-02：⑤⑥ 全字段整块透传（强类型零漂移）——1号位从整块读，不再等 2号位 逐批平铺/读投影子集硬编码。
                    SolverStrategy     = frozenSnapshot.SolverStrategy,
                    CandidateGuardrail = frozenSnapshot.CandidateGuardrail,
                    Parameters = new FiniteCapacityParameters
                    {
                        AllowSplit = solverStrategy.Split.MaxOptimizationSplitCount > 1,
                        AllowMerge = solverStrategy.AllowMerge,
                        SchedulingDirection = schedulingDirection,

                        // P1-02 B 组：⑤⑥ 已冻结字段直接投影（零行为变化；1号位 消费点后置）
                        ImpactedTaskWarningPercent = frozenSnapshot.CandidateGuardrail.ImpactedTaskWarningPercent,
                        MaxPropagationRounds = frozenSnapshot.CandidateGuardrail.MaxPropagationRounds,
                        SplitAlternatives = frozenSnapshot.CandidateGuardrail.SplitAlternatives,
                        MinBatchQty = solverStrategy.Split.MinBatchQty
                    }
                },

                CandidateContext = candidateContext,

                // FULL §9：前序 Domain 成功后的共享 Resource 占用块 → 1号位 作为不可用时间窗
                UpstreamDomainResourceBlocks = request.UpstreamResourceBlocks ?? Array.Empty<ResourceBlock>()
            };
            // 阶段3前：Pegging 阶段耗时（供给装载 + BOM 遍历扣减 + Routing 装载 + 请求构建）
            var peggingMs = sw.ElapsedMilliseconds;

            var solverSw = System.Diagnostics.Stopwatch.StartNew();
            var solveResult = await _scheduler.SolveAsync(solveRequest, cancellationToken);
            solverSw.Stop();
            Console.WriteLine($"[PeggingOrchestrator] IFiniteCapacityScheduler.SolveAsync完成: FinalTasks={solveResult.FinalTasks?.Count ?? 0}, Success={solveResult.Success}");

            var persistSw = System.Diagnostics.Stopwatch.StartNew();
            (result.GeneratedTasks, result.PhysicalPeggingCount, result.SupplyAllocationCount) =
                await PersistDomainAndPeggingInTransactionAsync(
                    request.PlanVersionId, voucher, solveResult, overCommitEvents, cancellationToken);
            persistSw.Stop();
            _logger.LogInformation(
                "[Pegging] 统一事务落库: Task={Tasks}, Pegging={Pegging}, SupplyAllocation={Alloc}",
                result.GeneratedTasks.Count, result.PhysicalPeggingCount, result.SupplyAllocationCount);

            sw.Stop();
            result.IsSuccess      = true;
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            result.PeggingMs      = peggingMs;
            result.SolverMs       = solverSw.ElapsedMilliseconds;
            result.PersistMs      = persistSw.ElapsedMilliseconds;

            _logger.LogInformation(
                "[Pegging] 完成: PlanVersionId={PlanVersionId}, Task={Tasks}, 分配={Alloc}, 耗时={Ms}ms",
                request.PlanVersionId, result.GeneratedTasks.Count,
                result.SupplyAllocationCount, result.ExecutionTimeMs);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Pegging] 编排异常: PlanVersionId={PlanVersionId}", request.PlanVersionId);
            Console.WriteLine($"[PeggingOrchestrator] 捕获异常: {ex.Message}");
            Console.WriteLine($"[PeggingOrchestrator] 异常堆栈: {ex.StackTrace}");
            result.IsSuccess      = false;
            result.ErrorMessage   = ex.Message;
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            return result;
        }
    }
    ///
    /// <inheritdoc />
    public async System.Threading.Tasks.Task<IEnumerable<PeggingOrchestrationResult>> ExecuteBatchPeggingWorkflowAsync(
        PeggingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // P0-01：撤销「20单=独立 Pegging/Solver/事务」的业务切批（原 const int batchSize=20 + OrderIds.Chunk）。
        // 冻结口径：一个 Domain = 一个数量真相边界 / 一次 DemandPriority / 一条 AllocationSequence /
        // 一次有限产能 Solver / 单Domain事务。LoadSupplyPoolAsync 本就按 DataCutoffTime 装载整域供给池（不分单），
        // 20单切批反而导致四个直接错误（见 2号位代码审核报告 P0-01）：
        //   A. 供给被多批重复消费（每批重载原始数量、不回扣上一批已落分配）；
        //   B. DemandPriority 只在批次内部成立，全 Domain 高优先级订单无法跨批争抢同一供给；
        //   C. 同一 Domain 被多次独立调用 Solver，FinalTask 无法在同一有限产能问题中竞争/合批/拆批；
        //   D. AllocationSequence 每批从 1 重复起始，撞 UNIQUE(PlanVersionId, AllocationSequence)。
        // 最小整改（PM 方向）：保留内存结构，整域单次调用；方法名保留 Batch 仅为 IPeggingOrchestrator 接口
        // 与 SchedulingOrchestrator 的聚合口径（r => 单结果集）兼容。
        return new[] { await ExecutePeggingWorkflowAsync(request, cancellationToken) };
    }

    /// <summary>
    /// 统一事务：DELETE 占位 Task → INSERT Task → INSERT Pegging 血缘 → INSERT AllocationLedger。
    /// 四步在同一 SqlTransaction 内，任一失败全部回滚。
    /// </summary>
    private async System.Threading.Tasks.Task<(List<ApsTask> tasks, int peggingCount, int supplyAllocationCount)>
        PersistDomainAndPeggingInTransactionAsync(
            int planVersionId,
            PeggingResultVoucher voucher,
            DomainSolveResult solveResult,
            IReadOnlyList<ContinuityOverCommitEvent> overCommits,
            CancellationToken ct)
    {
        return await _connectionManager.ExecuteInTransactionAsync<(List<ApsTask>, int, int)>(
            async (conn, tx) =>
            {
                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 开始事务，FinalTasks数量={solveResult.FinalTasks.Count}");
                var now    = DateTime.Now;
                var tasks  = new List<ApsTask>(solveResult.FinalTasks.Count);

                var finalDraftToTaskId = new Dictionary<string, long>(
                    solveResult.FinalTasks.Count, StringComparer.Ordinal);

                // W3：Task 落库必须携带 MTS_InstructionNo（连续份额尤须，供 MES 绑原工单识别）。
                // FinalTaskDraft.SourceDraftId = 源 LogicalDemandKey（1号位 PhaseTwoInitialScheduler L1135）→ 反查 LPD.ProductionInstructionNo。
                // Continuation 保持源 key、Free 用源 key+"/FREE"——两者都在 voucher.LogicalProductionDemands 里带了同一 PI，均能命中。
                var piByDemandKey = voucher.LogicalProductionDemands
                    .Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
                    .GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().ProductionInstructionNo!);

                // G4：反查「旧 TaskNo ↔ MESWorkOrderNo」映射（TaskNo 继承，冻结 §12.10.1 / T10）。
                // 映射落点 = 下发中间表 [TaskDispatch]，不是 [Task]（MES 侧工单身份不进核心 Task 模型，0号位 2026-09-17 P2/P3/P4 裁决）。
                // 只对连续份额有 MES 工单号；显式剔除当前 SourcePlanVersionId，命中均为历史版本的 TaskDispatch 下发绑定。
                // 键 = MES 工单号（一个 TaskNo ↔ 一个 MESWorkOrderNo，§7.1），取该工单最新下发记录的旧 TaskNo。
                var continuationWorkOrders = solveResult.FinalTasks
                    .Select(f => ExtractMesWorkOrderNo(f.SourceDraftId))
                    .Where(w => w is not null)
                    .Select(w => w!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var oldTaskNoByWorkOrder = new Dictionary<string, string>(StringComparer.Ordinal);
                if (continuationWorkOrders.Count > 0)
                {
                    var bindRows = await conn.QueryAsync<TaskNoBindingRow>(
                        @"SELECT d.MESWorkOrderNo, d.TaskNo
                          FROM [TaskDispatch] d
                          JOIN (
                              SELECT MESWorkOrderNo, MAX(Id) AS MaxId
                              FROM [TaskDispatch]
                              WHERE MESWorkOrderNo IN @Wos AND SourcePlanVersionId <> @CurrentPlanVersionId
                              GROUP BY MESWorkOrderNo
                          ) m ON m.MaxId = d.Id",
                        new { Wos = continuationWorkOrders, CurrentPlanVersionId = planVersionId },
                        transaction: tx);

                    foreach (var r in bindRows)
                        oldTaskNoByWorkOrder[r.MESWorkOrderNo] = r.TaskNo;
                }

                foreach (var final in solveResult.FinalTasks)
                {
                    ct.ThrowIfCancellationRequested();

                    piByDemandKey.TryGetValue(final.SourceDraftId, out var mtsInstructionNo);

                    // G4：连续份额从 ContinuationKey 反解 MES 工单号；TaskNo 三分支（继承旧号 / 新号）
                    var mesWorkOrderNo = ExtractMesWorkOrderNo(final.SourceDraftId);
                    var taskNo = ResolveTaskNo(planVersionId, final.FinalDraftId, mesWorkOrderNo, oldTaskNoByWorkOrder);
                    var ids = await conn.QueryAsync<long>(
                        @"INSERT INTO [Task] (
                              PlanVersionId, TaskNo, OrderId, MaterialId,
                              OperationSeq, OperationCode,
                              Quantity, PlannedProcessQty, UOM, PlannedStartTime, PlannedEndTime, Duration,
                              ResourceId,
                              Status, IsLocked, IsCriticalPath, TaskType,
                              MTS_InstructionNo,
                              CreatedAt, UpdatedAt
                          )
                          OUTPUT INSERTED.Id
                          VALUES (
                              @PlanVersionId, @TaskNo, @OrderId, @MaterialId,
                              @OperationSeq, @OperationCode,
                              @Quantity, @PlannedProcessQty, @UOM, @PlannedStartTime, @PlannedEndTime, @Duration,
                              @ResourceId,
                              @Status, @IsLocked, @IsCriticalPath, @TaskType,
                              @MTS_InstructionNo,
                              @CreatedAt, @UpdatedAt
                          )",
                        new
                        {
                            PlanVersionId    = planVersionId,
                            TaskNo           = taskNo,
                            OrderId          = voucher.OrderId,
                            MaterialId       = final.MaterialId,
                            OperationSeq     = final.OperationSeq,
                            OperationCode    = final.OperationCode,
                            Quantity         = final.Quantity,
                            // P0-06：1号位 FinalTask 的 PlannedProcessQty 原样落库（DB 列 v5.1.2 已加）
                            PlannedProcessQty = final.PlannedProcessQty,
                            UOM              = final.UOM,
                            PlannedStartTime = final.PlannedStartTime,
                            PlannedEndTime   = final.PlannedEndTime,
                            // Q8：Duration 补齐回写（表有列；单位=分钟，PlannedEnd-Start 计划区间跨度）
                            Duration         = (decimal)Math.Round((final.PlannedEndTime - final.PlannedStartTime).TotalMinutes, 4),
                            // P0-06：1号位实际 Resource 原样落库（不得丢掉 1号位 的时间资源真相）
                            ResourceId       = final.ResourceId,
                            Status           = "PLANNED",
                            IsLocked         = false,
                            IsCriticalPath   = false,
                            TaskType         = final.TaskType,
                            // W3：连续份额 / 自由份额 Task 回填 MTS_InstructionNo（无 PI 需求 = null，不得下发 MES）
                            MTS_InstructionNo = mtsInstructionNo,
                            CreatedAt        = now,
                            UpdatedAt        = now
                        },
                        transaction: tx);

                    var taskId = ids.Single();
                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] Task插入成功: TaskId={taskId}, TaskNo={taskNo}");
                    finalDraftToTaskId[final.FinalDraftId] = taskId;

                    tasks.Add(new ApsTask
                    {
                        Id               = taskId,
                        PlanVersionId    = planVersionId,
                        TaskNo           = taskNo,
                        OrderId          = voucher.OrderId,
                        MaterialId       = final.MaterialId,
                        OperationSeq     = final.OperationSeq,
                        OperationCode    = final.OperationCode,
                        ResourceId       = final.ResourceId,   // P0-06：保留 1号位 实际 Resource（供跨域占用块提取）
                        RouteCode        = "DEFAULT",
                        PathId           = 1,
                        Quantity         = final.Quantity,
                        UOM              = final.UOM,
                        PlannedStartTime = final.PlannedStartTime,
                        PlannedEndTime   = final.PlannedEndTime,
                        Duration         = (decimal)Math.Round((final.PlannedEndTime - final.PlannedStartTime).TotalMinutes, 4),
                        Status           = "PLANNED",
                        IsLocked         = false,
                        IsCriticalPath   = false,
                        TaskType         = final.TaskType,
                        MTS_InstructionNo = mtsInstructionNo,
                        CreatedAt        = now,
                        UpdatedAt        = now
                    });
                }

                // 3. INSERT Pegging 血缘（C1修复：使用 solveResult.PhysicalPeggingDrafts，键为 FinalDraftId）
                var peggingRows = solveResult.PhysicalPeggingDrafts
                    .Where(ppd =>
                        finalDraftToTaskId.ContainsKey(ppd.UpstreamFinalDraftId) &&
                        finalDraftToTaskId.ContainsKey(ppd.DownstreamFinalDraftId))
                    .Select(ppd => new
                    {
                        PlanVersionId        = planVersionId,
                        UpstreamTaskId       = finalDraftToTaskId[ppd.UpstreamFinalDraftId],
                        DownstreamTaskId     = finalDraftToTaskId[ppd.DownstreamFinalDraftId],
                        UpstreamMaterialId   = ppd.UpstreamMaterialId,
                        DownstreamMaterialId = ppd.DownstreamMaterialId,
                        Quantity             = ppd.Quantity,
                        UOM                  = ppd.UOM,
                        PeggingType          = "TASK_TO_TASK",
                        AllocatedQuantity    = ppd.Quantity,
                        InheritedPriority    = ppd.InheritedPriority,
                        AllocationReason     = (string?)null
                    })
                    .ToList();

                if (peggingRows.Count > 0)
                {
                    await conn.ExecuteAsync(
                        @"INSERT INTO [Pegging] (
                              PlanVersionId,
                              UpstreamTaskId, DownstreamTaskId,
                              UpstreamMaterialId, DownstreamMaterialId,
                              Quantity, UOM, PeggingType,
                              LeadTimeDays, IsCrossDomain,
                              AllocatedQuantity, InheritedPriority, AllocationReason,
                              CreatedAt
                          )
                          VALUES (
                              @PlanVersionId,
                              @UpstreamTaskId, @DownstreamTaskId,
                              @UpstreamMaterialId, @DownstreamMaterialId,
                              @Quantity, @UOM, @PeggingType,
                              0, 0,
                              @AllocatedQuantity, @InheritedPriority, @AllocationReason,
                              GETDATE()
                          )",
                        peggingRows,
                        transaction: tx);
                }

                // B5. INSERT AllocationTaskShare (v5.1.2冻结设计：轻量中间表，支持批次拆分多对多)
                var seqToShareId = new Dictionary<long, long>();
                if (solveResult.AllocationShares.Count > 0)
                {
                    foreach (var share in solveResult.AllocationShares)
                    {
                        if (!finalDraftToTaskId.TryGetValue(share.FinalDraftId, out var taskId))
                            continue;

                        var allocation = voucher.SupplyAllocations
                            .FirstOrDefault(a => a.AllocationSequence == share.AllocationSequence);

                        if (allocation == null)
                        {
                            _logger.LogWarning(
                                "[PeggingPersist] AllocationSequence={Seq} 未找到对应的SupplyAllocation",
                                share.AllocationSequence);
                            continue;
                        }

                        // P0-06：TaskShare 真实 Demand 归属来自该 AllocationSequence 对应的 Allocation 自身，
                        // 不再用批次第一单 voucher.OrderId；RootOrderId 从 DemandKey 反解真实 OrderId。
                        var rootOrderId = ExtractOrderIdFromDemandKey(allocation.DemandKey);

                        var shareId = await conn.ExecuteScalarAsync<long>(
                            @"INSERT INTO AllocationTaskShare (
                                  PlanVersionId, AllocationSequence, DemandType, DemandKey,
                                  RootOrderId, TaskId, ShareQty, CreatedAt
                              ) OUTPUT INSERTED.Id
                              VALUES (
                                  @PlanVersionId, @AllocationSequence, @DemandType, @DemandKey,
                                  @RootOrderId, @TaskId, @ShareQty, @CreatedAt
                              )",
                            new
                            {
                                PlanVersionId = planVersionId,
                                AllocationSequence = share.AllocationSequence,
                                DemandType = "ORDER",
                                DemandKey = allocation.DemandKey,
                                RootOrderId = rootOrderId,
                                TaskId = taskId,
                                ShareQty = share.ComponentQty,
                                CreatedAt = now
                            },
                            transaction: tx);
                        seqToShareId[share.AllocationSequence] = shareId;
                    }
                }

                // B6. INSERT PeggingSupplyAllocation (仅对非Task供给)
                // v5.1.2: 直接使用allocation.AllocationSequence（在供需扣减时已生成）
                var nonTaskAllocations = voucher.SupplyAllocations
                    .Where(a => a.SourceType != Core.Enum.SupplySourceType.NEW_REQUIREMENT)
                    .ToList();

                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 准备INSERT PeggingSupplyAllocation，nonTaskAllocations={nonTaskAllocations.Count}");

                // 查询ScheduleRunId（B6和B7都需要）
                var scheduleRunId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
                    new { PlanVersionId = planVersionId },
                    transaction: tx) ?? 0;

                if (nonTaskAllocations.Count > 0)
                {
                    var supplyMaterialIds = nonTaskAllocations.Select(a => a.SupplyMaterialId).Distinct().ToList();
                    var materialMap = new Dictionary<int, string>();
                    for (var i = 0; i < supplyMaterialIds.Count; i += SqlServerInParameterLimit)
                    {
                        var chunk = supplyMaterialIds.Skip(i).Take(SqlServerInParameterLimit).ToList();
                        var batch = await conn.QueryAsync(
                            "SELECT Id, MaterialCode FROM Material WHERE Id IN @Ids",
                            new { Ids = chunk },
                            transaction: tx);
                        foreach (var r in batch)
                            materialMap[(int)r.Id] = (string)r.MaterialCode;
                    }

                    var supplyRows = nonTaskAllocations
                        .Select(a =>
                        {
                            materialMap.TryGetValue(a.SupplyMaterialId, out var materialCode);
                            return new
                            {
                                PlanVersionId          = planVersionId,
                                ScheduleRunId          = scheduleRunId,
                                AllocationSequence     = a.AllocationSequence,
                                RootOrderId            = ExtractOrderIdFromDemandKey(a.DemandKey),
                                MaterialId             = a.SupplyMaterialId,
                                MaterialCode           = materialCode ?? string.Empty,
                                DemandFactoryCode      = a.FactoryCode,
                                DemandQty              = a.DemandQuantity,
                                AllocatedQty           = a.AllocatedQuantity,
                                SupplyType             = a.SourceType.ToString(),
                                SupplyFactoryCode      = a.FactoryCode,
                                KnownAvailableTime     = a.AvailableAt,
                                SupplyDocumentNo       = a.SourceReference,
                                CreatedAt              = now
                            };
                        })
                        .ToList();

                    await conn.ExecuteAsync(
                        @"INSERT INTO PeggingSupplyAllocation (
                              PlanVersionId, ScheduleRunId, AllocationSequence,
                              RootOrderId, MaterialId, MaterialCode,
                              DemandFactoryCode, DemandQty, AllocatedQty,
                              SupplyType, SupplyFactoryCode,
                              KnownAvailableTime, SupplyDocumentNo, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @AllocationSequence,
                              @RootOrderId, @MaterialId, @MaterialCode,
                              @DemandFactoryCode, @DemandQty, @AllocatedQty,
                              @SupplyType, @SupplyFactoryCode,
                              @KnownAvailableTime, @SupplyDocumentNo, @CreatedAt
                          )",
                        supplyRows,
                        transaction: tx);

                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] PeggingSupplyAllocation INSERT成功: {supplyRows.Count} 条");
                }

                // B7. INSERT ScheduleExplanationFact (v5.1.2: 排程决策解释事实)
                if (solveResult.ExplanationFacts.Count > 0)
                {
                    var factRows = solveResult.ExplanationFacts
                        .Select(fact =>
                        {
                            finalDraftToTaskId.TryGetValue(fact.FinalDraftId, out var taskId);
                            return new
                            {
                                PlanVersionId = planVersionId,
                                ScheduleRunId = scheduleRunId,
                                ObjectType = fact.ObjectType,
                                OrderId = fact.OrderId,
                                TaskId = taskId,
                                ResourceId = fact.ResourceId,
                                StageCode = fact.StageCode ?? string.Empty,
                                ReasonCode = fact.ReasonCode,
                                Severity = fact.Severity ?? string.Empty,
                                ImpactHours = fact.ImpactHours ?? 0m,
                                EvidenceJson = fact.EvidenceJson,
                                CreatedAt = now
                            };
                        })
                        .Where(f => f.TaskId > 0)
                        .ToList();

                    if (factRows.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                                  PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                                  ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                                  EvidenceJson, CreatedAt
                              ) VALUES (
                                  @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                                  @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                                  @EvidenceJson, @CreatedAt
                              )",
                            factRows,
                            transaction: tx);

                        Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] ScheduleExplanationFact INSERT成功: {factRows.Count} 条");
                    }
                }

                // B8. 真无解 Unscheduled 闭合记账（PM 2026-09-11：产能不足≠排不下，Unscheduled 只兜「真无解」异常）
                //   1号位 UnscheduledTaskResult.DraftId = LogicalDemandKey；2号位 反查 NetOutputQty，
                //   复用 ScheduleExplanationFact（不加表），UnscheduledQty = NetOutputQty − Σ已排TaskShare
                //   （V1 不引入「已排份额+Unscheduled剩余」部分切分模型，真无解=整单记账，ΣTaskShare=0）；
                //   数量/血缘键进 EvidenceJson，TaskId=null（无对应 FinalTask）。
                if (solveResult.UnscheduledTasks.Count > 0)
                {
                    var demandByKey = voucher.LogicalProductionDemands
                        .Where(d => !string.IsNullOrEmpty(d.LogicalDemandKey))
                        .GroupBy(d => d.LogicalDemandKey)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                    // 不 Drop 未反查命中的 DraftId（数量不得静默消失）：demand 为 null 时 NetOutputQty 记 0，
                    // 但 EvidenceJson 仍带 LogicalDemandKey 可追溯，问题显性化。
                    var unscheduledRows = solveResult.UnscheduledTasks
                        .Select(u =>
                        {
                            demandByKey.TryGetValue(u.DraftId, out var demand);
                            var netOutputQty = demand?.NetOutputQty ?? 0m;
                            var evidence = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                LogicalDemandKey   = u.DraftId,
                                MaterialId         = demand?.MaterialId,
                                DomainKey          = voucher.DomainKey,
                                AllocationSequence = demand?.AllocationSequence,
                                NetOutputQty       = netOutputQty,
                                UnscheduledQty     = netOutputQty
                            });
                            return new
                            {
                                PlanVersionId = planVersionId,
                                ScheduleRunId = scheduleRunId,
                                ObjectType    = "DEMAND",
                                OrderId       = demand?.OrderId,
                                TaskId        = (long?)null,
                                ResourceId    = (int?)null,
                                StageCode     = demand?.StartStageCode ?? string.Empty,
                                ReasonCode    = string.IsNullOrWhiteSpace(u.Reason) ? "UNSCHEDULABLE" : u.Reason,
                                Severity      = "ERROR",
                                ImpactHours   = (decimal?)null,
                                EvidenceJson  = evidence,
                                CreatedAt     = now
                            };
                        })
                        .ToList();

                    if (unscheduledRows.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                                  PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                                  ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                                  EvidenceJson, CreatedAt
                              ) VALUES (
                                  @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                                  @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                                  @EvidenceJson, @CreatedAt
                              )",
                            unscheduledRows,
                            transaction: tx);

                        Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] Unscheduled 闭合记账 ScheduleExplanationFact INSERT成功: {unscheduledRows.Count} 条");
                    }
                }

                // B9. 跨版本连续性 Execution Over-Commit / Demand Mismatch 异常登记（G6，§9.1/§9.2）
                //   复用 ScheduleExplanationFact（与 Q7 Unscheduled 同表，不加表）：E>Q 执行超量 / Q=0 仍开工，
                //   均不产 Task、只登记异常事实供后续对账；ObjectType="DEMAND"、TaskId=null、Severity 按 Kind。
                if (overCommits.Count > 0)
                {
                    var overCommitRows = overCommits
                        .Select(e => new
                        {
                            PlanVersionId = planVersionId,
                            ScheduleRunId = scheduleRunId,
                            ObjectType    = "DEMAND",
                            OrderId       = e.OrderId,
                            TaskId        = (long?)null,
                            ResourceId    = (int?)null,
                            StageCode     = e.StartStageCode,
                            ReasonCode    = e.Kind,
                            Severity      = e.Severity,
                            ImpactHours   = (decimal?)null,
                            EvidenceJson  = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                ProductionInstructionNo = e.ProductionInstructionNo,
                                LogicalDemandKey        = e.LogicalDemandKey,
                                DomainKey               = e.DomainKey,
                                AllocationSequence      = e.AllocationSequence,
                                MaterialId              = e.MaterialId,
                                Quantity                = e.Quantity,
                                ContinuationQty         = e.ContinuationQty,
                                ExcessQty               = e.ExcessQty,
                                Kind                    = e.Kind
                            }),
                            CreatedAt = now
                        })
                        .ToList();

                    await conn.ExecuteAsync(
                        @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                              PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                              ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                              EvidenceJson, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                              @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                              @EvidenceJson, @CreatedAt
                          )",
                        overCommitRows,
                        transaction: tx);

                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] Over-Commit 异常登记 ScheduleExplanationFact INSERT成功: {overCommitRows.Count} 条");
                }

                _logger.LogInformation(
                    "[Pegging] 统一事务提交: Task={Tasks}, Pegging={Pegging} (PlanVersionId={PlanVersionId})",
                    tasks.Count, peggingRows.Count, planVersionId);
                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 事务即将返回: Tasks={tasks.Count}, Pegging={peggingRows.Count}");

                return (tasks, peggingRows.Count, nonTaskAllocations.Count);
            },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// 结果红线校验（PM 口径，6 项全）。
    /// 在 Solver 与落库之前调用，返回错误列表；空列表 = 通过。
    /// ① DemandQuantity 由 TraverseBomNode 逐节点累计；③ PhysicalSourceKey 由 SupplyPool.Add 填充；
    /// ⑤⑥ SH 读 ShippingInstructionNo（INTER_FACTORY_ORDER 分配落 SH No）+ 池内 SH 段 OriginalQty（=指令总量 Order.Quantity）。
    /// </summary>
    private static List<string> ValidatePeggingResult(SupplyPool pool, PeggingResultVoucher voucher)
    {
        var errors = new List<string>();

        // 1. Demand 闭合：Σ(已分配) + 短缺 不得超过需求总量（DemandQuantity 为 0 表示无需求节点，跳过）
        if (voucher.DemandQuantity > 0m)
        {
            var totalAllocated = voucher.SupplyAllocations.Sum(a => a.AllocatedQuantity);
            if (totalAllocated + voucher.ShortageQuantity > voucher.DemandQuantity)
                errors.Add($"Demand 闭合失败: 已分配 {totalAllocated} + 短缺 {voucher.ShortageQuantity} > 需求 {voucher.DemandQuantity}");
        }

        // 2. SupplyBalance 非负：任一供给条目 RemainingQty < 0 即超额消费
        var overConsumed = pool.GetAllEntries().Where(e => e.RemainingQty < 0m).ToList();
        if (overConsumed.Count > 0)
            errors.Add($"SupplyBalance 为负: {overConsumed.Count} 条供给被超额消费（RemainingQty < 0）");

        // 3. 同物理 Supply 不重复消费：同一 PhysicalSourceKey 的已分配量之和不得超过原始量。
        //    PhysicalSourceKey 由 SupplyPool.Add 按来源填充（PI→PI号 / PO→PO:Line / 库存→INV:Id）；无键（虚拟供给）时跳过。
        foreach (var group in pool.GetAllEntries()
                     .Where(e => !string.IsNullOrWhiteSpace(e.PhysicalSourceKey))
                     .GroupBy(e => e.PhysicalSourceKey!))
        {
            foreach (var entry in group)
            {
                var consumed = entry.Allocations.Sum(a => a.AllocatedQty);
                if (consumed > entry.OriginalQty)
                    errors.Add($"物理供给 {group.Key} 被重复消费: 已分配 {consumed} > 原始量 {entry.OriginalQty}");
            }
        }

        // 4. Allocation 合法
        var items = voucher.SupplyAllocations;
        if (items.Any(a => a.AllocatedQuantity <= 0m))
            errors.Add("存在非法分配量：AllocatedQuantity <= 0");
        var seqs = items.Select(a => a.AllocationSequence).ToList();
        if (seqs.Any(s => s <= 0))
            errors.Add("存在非法 AllocationSequence（<= 0）");
        if (seqs.Distinct().Count() != seqs.Count)
            errors.Add("AllocationSequence 存在重复");
        for (var i = 1; i < seqs.Count; i++)
        {
            if (seqs[i] <= seqs[i - 1])
            {
                errors.Add("AllocationSequence 未单调递增");
                break;
            }
        }
        if (items.Any(a => !Enum.IsDefined(typeof(SupplySourceType), a.SourceType)))
            errors.Add("存在非法 SourceType");

        // 5/6. SH 同 SH 匹配不串 SH / 份额不重复计量：读 ShippingInstructionNo（SH 分配落 SH No）+ 池内 SH 段总量（=Order.Quantity 指令总量）
        var shipmentTotals = pool.GetAllEntries()
            .Where(e => e.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER
                     && !string.IsNullOrWhiteSpace(e.PhysicalSourceKey))
            .GroupBy(e => e.PhysicalSourceKey!)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.OriginalQty), StringComparer.Ordinal);
        errors.AddRange(ValidateShConsistency(voucher.SupplyAllocations, shipmentTotals));

        return errors;
    }

    /// <summary>
    /// 红线⑤⑥ SH 校验（纯函数，可单测）。
    /// ⑤ 同一需求（DemandKey）的 SH 分配必须指向同一出荷指示号（不串 SH）；
    /// ⑥ 同一 SH 分配合计不得超过其指令总量（Order.Quantity，shipmentTotals）。
    /// </summary>
    internal static List<string> ValidateShConsistency(
        IReadOnlyList<SupplyAllocationItem> allocations,
        IReadOnlyDictionary<string, decimal> shipmentTotals)
    {
        var errors = new List<string>();
        var shAllocations = allocations
            .Where(a => !string.IsNullOrWhiteSpace(a.ShippingInstructionNo))
            .ToList();
        if (shAllocations.Count == 0) return errors;

        // 5. 不串 SH
        foreach (var group in shAllocations.GroupBy(a => a.DemandKey))
        {
            var shNos = group.Select(a => a.ShippingInstructionNo).Distinct(StringComparer.Ordinal).ToList();
            if (shNos.Count > 1)
                errors.Add($"SH 串单: 需求 {group.Key} 跨出荷指示 {string.Join(", ", shNos)}");
        }

        // 6. 份额不重复计量
        foreach (var group in shAllocations.GroupBy(a => a.ShippingInstructionNo!))
        {
            var consumed = group.Sum(a => a.AllocatedQuantity);
            var total = shipmentTotals.TryGetValue(group.Key, out var t) ? t : 0m;
            if (consumed > total)
                errors.Add($"SH 份额重复计量: {group.Key} 已分配 {consumed} > 实际发生量 {total}");
        }

        return errors;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record BomEdge(
        string ParentCode,
        string ChildCode,
        int ChildMaterialId,
        decimal Qty,
        int Level,
        bool IsLeaf,
        bool IsPurchased,
        string? ChildRequiredStageCode);

    private sealed record BomSnapshot(
        ILookup<string, BomEdge> ByParent,
        IReadOnlyDictionary<string, int> LLCByMaterial,
        IReadOnlyDictionary<string, bool> IsPurchasedByMaterial,
        int EdgeCount);

    private sealed class BomRawRow
    {
        public string ParentMaterialCode      { get; set; } = string.Empty;
        public string ChildMaterialCode       { get; set; } = string.Empty;
        public int ChildMaterialId            { get; set; }
        public decimal Quantity               { get; set; }
        public int Level                      { get; set; }
        public int? LLC                       { get; set; }
        public bool IsLeaf                    { get; set; }
        public bool IsPurchased               { get; set; }
        public string? ChildRequiredStageCode { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 供给池内部数据结构
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 内存供给账本。PeggingLoop 遍历时直接在此对象上扣减，不回写数据库。
    /// 最终扣减结果通过 SupplyAllocationItem 列表落库到 PeggingSupplyAllocation。
    /// </summary>
    private sealed class SupplyPool
    {
        // Key: "MaterialCode|FactoryId"
        private readonly Dictionary<string, List<SupplyLedgerEntry>> _ledger
            = new(StringComparer.Ordinal);

        /// <summary>冻结策略快照（Inventory/PI/Procurement 三类排序参数）；缺省时按稳定兜底排序</summary>
        private readonly FrozenStrategySnapshot? _snapshot;

        public int TotalEntries { get; private set; }

        /// <summary>本次运行供给快照的数据截止时点（= ScheduleRun.DataCutoffTime；编排层 Set，缺省回退 Now）。</summary>
        public DateTime DataCutoffTime { get; set; }

        /// <summary>冻结 DefaultPurchaseLt（天）→ 规划采购占位 AvailableTime = DataCutoffTime + 该天数（P0-07）。</summary>
        public int DefaultPurchaseLtDays => _snapshot == null ? 0 : BuildFrozenFactParameters(_snapshot).DefaultPurchaseLt;

        /// <summary>
        /// Planning Yield（Material 级默认，清单 27）→ PlannedProcessQty 反算用（P1-02）。
        /// YieldPercent 语义为百分数（0 &lt; YieldPercent &lt;= 100，见 PlanningYieldRule 注释 + 3号位校验）。
        /// 返回 100 = 无损兜底（未配置快照 / 无匹配规则 / 越界），调用侧据此不放大。
        /// 当前仅匹配空 StageCode 的 Material 级默认；StageCode 级精确规则待 startStageCode 接入工艺路由后补。
        /// </summary>
        public decimal PlanningYieldPercent(string materialCode)
        {
            if (_snapshot?.Procurement.PlanningYields is not { Count: > 0 } yields) return 100m;
            foreach (var rule in yields)
            {
                if (!string.Equals(rule.MaterialId, materialCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(rule.StageCode)) continue;
                if (rule.YieldPercent <= 0m || rule.YieldPercent > 100m) continue;
                return rule.YieldPercent;
            }
            return 100m;
        }

        public SupplyPool(FrozenStrategySnapshot? snapshot = null) => _snapshot = snapshot;

        public SupplyLedgerEntry Add(
            string materialCode, int materialId, int factoryId, decimal qty,
            DateTime? availableAt, Core.Enum.SupplySourceType sourceType,
            string? sourceRef, string factoryCode, long? supplySourceId = null,
            SupplyConfidence confidence = SupplyConfidence.CONFIRMED,
            SupplyCommitment commitment = SupplyCommitment.COMMITTED,
            SupplySortFacts? sort = null,
            string? physicalSourceKey = null)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list))
            {
                list = new List<SupplyLedgerEntry>();
                _ledger[key] = list;
            }
            var entry = new SupplyLedgerEntry
            {
                OriginalQty     = qty,
                RemainingQty    = qty,
                MaterialId      = materialId,
                AvailableAt     = availableAt,
                SourceType      = sourceType,
                SourceReference = sourceRef,
                PhysicalSourceKey = physicalSourceKey,
                FactoryCode     = factoryCode,
                FactoryId       = factoryId,
                SupplySourceId  = supplySourceId,
                Confidence      = confidence,
                Commitment      = commitment,
                WarehouseCode   = sort?.WarehouseCode,
                ReleaseDate     = sort?.ReleaseDate,
                PoNo            = sort?.PoNo,
                LineNo          = sort?.LineNo,
                PiNo            = sort?.PiNo,
                IssueDate       = sort?.IssueDate,
                CreatedAt       = sort?.CreatedAt
            };
            list.Add(entry);
            TotalEntries++;
            return entry;
        }

        /// <summary>
        /// 返回指定物料+工厂在当前 Demand 业务身份下允许进入的供给条目（PM 2026-08-28 最终裁决：
        /// 不存在 Inventory/PI/Procurement 三类全局优先级；先定允许供给集合，再类内各自排序）：
        ///   - 允许库存时（includeInventory，即 BOM 下阶）：Inventory 按 Warehouse Priority（SupplyBlock.Inventory）
        ///   - 自制件（isPurchased=false）：PI 按 Issue/Create Time ASC → Stable PiNo（SupplyBlock.PiSort）
        ///   - 采购件（isPurchased=true）：Procurement 按 Warehouse Priority → AvailableTime → ReleaseDate → PO+Line
        ///                               （固定链，不可重排；参数取 ProcurementBlock）
        /// 跨厂 Transit/Received（绑定消费）与缺口（Placeholder/Planned）不进入本排序。
        /// 未配置/字段缺失时自动降级为稳定兜底（SourceReference），不引入随机顺序。
        /// </summary>
        public IReadOnlyList<SupplyLedgerEntry> GetEntries(string materialCode, int factoryId, bool isPurchased, bool includeInventory)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list)) return Array.Empty<SupplyLedgerEntry>();

            var ordered = new List<SupplyLedgerEntry>(list.Count);
            if (includeInventory)
                ordered.AddRange(SortInventory(list));
            if (isPurchased)
                ordered.AddRange(SortProcurement(list));
            else
            {
                ordered.AddRange(SortPi(list));
                // 跨 Domain：上游域生产输出作为分段虚拟供给（§8/D12），按可用时间升序
                ordered.AddRange(SortUpstreamDomainProduction(list));
            }
            return ordered;
        }

        /// <summary>
        /// INTER_FACTORY_ORDER（厂间出荷指示，SH级）供给：按 AvailableAt 升序。
        /// 不进入 GetEntries 三类通用排序（PM 裁决：跨厂 SH 绑定消费，走独立消费路径）。
        /// 每个 SH 是单一供给身份（PhysicalSourceKey=SH No），量=Order.Quantity（指令总量，整单一次入库）。
        /// </summary>
        public IReadOnlyList<SupplyLedgerEntry> GetInterFactoryEntries(string materialCode, int factoryId)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list)) return Array.Empty<SupplyLedgerEntry>();

            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER)
                .OrderBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortUpstreamDomainProduction(IReadOnlyList<SupplyLedgerEntry> list)
            => list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.UPSTREAM_DOMAIN_PRODUCTION)
                .OrderBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();

        private IReadOnlyList<SupplyLedgerEntry> SortInventory(IReadOnlyList<SupplyLedgerEntry> list)
        {
            var rankMap = BuildRankMap(_snapshot?.Supply.Inventory.WarehousePriority);
            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.INVENTORY)
                .OrderBy(e => WarehouseRank(e.WarehouseCode, rankMap))
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortPi(IReadOnlyList<SupplyLedgerEntry> list)
        {
            var sortBy = _snapshot?.Supply.PiSort?.SortBy ?? PiSortBy.IssueDateAsc;
            var tieBreak = _snapshot?.Supply.PiSort?.UseStablePiNoTieBreak ?? false;

            var eligible = list.Where(e => e.SourceType == Core.Enum.SupplySourceType.WIP
                                        || e.SourceType == Core.Enum.SupplySourceType.PRODUCTION_INSTRUCTION);

            if (sortBy == PiSortBy.StablePiNoAsc)
                return eligible
                    .OrderBy(e => e.PiNo ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                    .ToList();

            var ordered = eligible.OrderBy(e => PiSortTime(e, sortBy));
            if (tieBreak)
                ordered = ordered.ThenBy(e => e.PiNo ?? string.Empty, StringComparer.Ordinal);
            return ordered.ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal).ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortProcurement(IReadOnlyList<SupplyLedgerEntry> list)
        {
            // 固定链（PM 裁决）：Eligibility → Warehouse Priority → AvailableTime → ReleaseDate → PO+Line，
            // 不可重排、无 Enable/Disable 开关；3号位只治理参数值（此处取 WarehousePriority）。
            var rankMap = BuildRankMap(_snapshot?.Procurement.WarehousePriority);
            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.PIPELINE
                         || e.SourceType == Core.Enum.SupplySourceType.PURCHASE_ORDER)
                .OrderBy(e => WarehouseRank(e.WarehouseCode, rankMap))
                .ThenBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.ReleaseDate ?? DateTime.MaxValue)
                .ThenBy(e => e.PoNo ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.LineNo ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyDictionary<string, int> BuildRankMap(IReadOnlyList<string>? priority)
            => (priority ?? [])
                .Select((code, i) => new { code, i })
                .ToDictionary(x => x.code, x => x.i, StringComparer.OrdinalIgnoreCase);

        private static int WarehouseRank(string? code, IReadOnlyDictionary<string, int> rankMap)
            => code != null && rankMap.TryGetValue(code, out var i) ? i : int.MaxValue;

        private static DateTime PiSortTime(SupplyLedgerEntry e, PiSortBy sortBy) => sortBy switch
        {
            PiSortBy.CreatedAtAsc => e.CreatedAt ?? DateTime.MaxValue,
            _                     => e.IssueDate ?? DateTime.MaxValue  // IssueDateAsc（默认）；StablePiNoAsc 已在 SortPi 上游单独处理
        };

        /// <summary>返回所有供给条目（用于加载Lock数据）</summary>
        public IEnumerable<SupplyLedgerEntry> GetAllEntries()
        {
            return _ledger.Values.SelectMany(list => list);
        }

        public static string BuildKey(string materialCode, int factoryId)
            => $"{materialCode}|{factoryId}";

        /// <summary>
        /// 深拷贝供给账本（阶段2 S2.3 双跑隔离）。旧 DFS 与新 BFS 必须各自跑一份独立供给账本，
        /// 否则先跑的一遍会把共享 Inventory/PI/Procurement 扣光，后跑的一遍无可比性。
        /// 调用时机 = LoadSupplyPoolAsync（含 LoadActiveLockDataAsync 硬锁装载）之后、Pegging 扣减之前。
        /// </summary>
        public SupplyPool Clone()
        {
            var copy = new SupplyPool(_snapshot) { DataCutoffTime = DataCutoffTime };
            foreach (var (key, list) in _ledger)
                copy._ledger[key] = list.Select(CloneEntry).ToList();
            copy.TotalEntries = TotalEntries;
            return copy;
        }

        private static SupplyLedgerEntry CloneEntry(SupplyLedgerEntry e) => new()
        {
            OriginalQty        = e.OriginalQty,
            RemainingQty       = e.RemainingQty,
            LockedQty          = e.LockedQty,
            SupplyKey          = e.SupplyKey,
            PhysicalSourceKey  = e.PhysicalSourceKey,
            MaterialId         = e.MaterialId,
            FactoryId          = e.FactoryId,
            FactoryCode        = e.FactoryCode,
            SourceType         = e.SourceType,
            AvailableAt        = e.AvailableAt,
            SourceReference    = e.SourceReference,
            SupplySourceId     = e.SupplySourceId,
            WarehouseCode      = e.WarehouseCode,
            ReleaseDate        = e.ReleaseDate,
            PoNo               = e.PoNo,
            LineNo             = e.LineNo,
            PiNo               = e.PiNo,
            IssueDate          = e.IssueDate,
            CreatedAt          = e.CreatedAt,
            Confidence         = e.Confidence,
            Commitment         = e.Commitment,
            Allocations        = e.Allocations.Select(a => new AllocationRecord
            {
                AllocationSequence = a.AllocationSequence,
                AllocatedQty       = a.AllocatedQty,
                SupplyKey          = a.SupplyKey,
                SupplyType         = a.SupplyType,
                DemandKey          = a.DemandKey,
                MaterialId         = a.MaterialId,
                AllocatedAt        = a.AllocatedAt,
                RequiresProduction = a.RequiresProduction
            }).ToList(),
            Locks              = e.Locks.Select(l => new LockRecord
            {
                LockType          = l.LockType,
                LockedQty         = l.LockedQty,
                LockedToOrderId   = l.LockedToOrderId,
                LockedToDemandKey = l.LockedToDemandKey,
                LockedAt          = l.LockedAt
            }).ToList()
        };
    }

    /// <summary>供给排序事实（从 SupplyLoadRow 抽取，供多键排序使用；字段缺失为 null，排序时降级兜底）</summary>
    private sealed record SupplySortFacts(
        string? WarehouseCode = null,
        DateTime? ReleaseDate = null,
        string? PoNo = null,
        string? LineNo = null,
        string? PiNo = null,
        DateTime? IssueDate = null,
        DateTime? CreatedAt = null);

    /// <summary>
    /// 供给侧内存账本（V1.2增强版，对齐实施包§5.1）
    ///
    /// 职责：
    ///   - 维护供给的剩余可用数量（RemainingQty）
    ///   - 记录供给的业务属性（SupplyType、AvailableTime、Confidence等）
    ///   - 支持Lock份额管理
    ///   - 与DemandBalance配合，共同实现供需原子匹配
    ///
    /// V1.2核心红线：
    ///   同一物理数量在同一PlanVersion中只能有一个Supply身份
    ///   例如同一PI：PI总量、PI的XC、PI的在途、PI的Stage WIP 不能被当成四份Supply重复消费
    /// </summary>
    private sealed class SupplyLedgerEntry
    {
        // ═══════════════════════════════════════════════════════════════════════
        // 数量字段
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>原始总量（初始值，不可变）</summary>
        public decimal OriginalQty                      { get; init; }

        /// <summary>剩余可用数量（遍历时可变，初始值=OriginalQty）</summary>
        public decimal RemainingQty                     { get; set; }

        /// <summary>已锁定份额（STRICT_BINDING/DEMAND_PROTECTION/Execution）</summary>
        public decimal LockedQty                        { get; set; }

        // ═══════════════════════════════════════════════════════════════════════
        // 供给身份字段
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>供给唯一身份键（格式：MaterialId|FactoryId|SourceType|SupplySourceId）</summary>
        public string SupplyKey                         { get; init; } = string.Empty;

        /// <summary>物理来源键（例如：ProductionInstructionNo、InventoryBatchNo、PONo等）</summary>
        public string? PhysicalSourceKey                { get; init; }

        /// <summary>供给物料ID</summary>
        public int MaterialId                           { get; init; }

        /// <summary>供给工厂ID</summary>
        public int FactoryId                            { get; init; }

        /// <summary>供给工厂代码</summary>
        public string FactoryCode                       { get; init; } = string.Empty;

        // ═══════════════════════════════════════════════════════════════════════
        // 供给类型与时间
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>供给类型（INVENTORY/PRODUCTION_INSTRUCTION/PURCHASE_ORDER/VMI/IN_TRANSIT等）</summary>
        public Core.Enum.SupplySourceType SourceType    { get; init; }

        /// <summary>供给可用时间（库存=当前时间，采购=ETA，生产=预计完成时间）</summary>
        public DateTime? AvailableAt                    { get; init; }

        /// <summary>供给来源引用（原有字段，用于追溯）</summary>
        public string? SourceReference                  { get; init; }

        /// <summary>供给来源ID（原有字段，用于关联）</summary>
        public long? SupplySourceId                     { get; init; }

        // ═══════════════════════════════════════════════════════════════════════
        // 排序字段（阶段2：按 3号位 Frozen 规则三类独立排序，不扁平混排）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>仓库/库位编码（Inventory WarehousePriority / Procurement Warehouse Priority）</summary>
        public string? WarehouseCode                    { get; init; }

        /// <summary>PO 发行时间（Procurement Release Time 排序）</summary>
        public DateTime? ReleaseDate                    { get; init; }

        /// <summary>采购订单号（Procurement PO+Line 稳定排序）</summary>
        public string? PoNo                             { get; init; }

        /// <summary>采购订单行号（Procurement PO+Line 稳定排序）</summary>
        public string? LineNo                           { get; init; }

        /// <summary>生产指示号（PI 稳定 Tie-break）</summary>
        public string? PiNo                             { get; init; }

        /// <summary>PI 发行时间（PiSort.IssueDateAsc）</summary>
        public DateTime? IssueDate                      { get; init; }

        /// <summary>PI 创建时间（PiSort.CreatedAtAsc）</summary>
        public DateTime? CreatedAt                      { get; init; }

        // ═══════════════════════════════════════════════════════════════════════
        // 置信度与承诺度（V1.2新增）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>置信度（CONFIRMED=确定供给，ESTIMATED=估计供给/Planning-only）</summary>
        public SupplyConfidence Confidence              { get; init; } = SupplyConfidence.CONFIRMED;

        /// <summary>承诺度（COMMITTED=已承诺，NOT_COMMITTED=未承诺）</summary>
        public SupplyCommitment Commitment              { get; init; } = SupplyCommitment.COMMITTED;

        // ═══════════════════════════════════════════════════════════════════════
        // Lock与Allocation追溯（V1.2新增）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>本次运行的Allocation记录列表（用于追溯和校验）</summary>
        public List<AllocationRecord> Allocations       { get; init; } = new();

        /// <summary>Lock记录列表（STRICT_BINDING/DEMAND_PROTECTION/Execution）</summary>
        public List<LockRecord> Locks                   { get; init; } = new();
    }

    /// <summary>供给置信度枚举</summary>
    private enum SupplyConfidence
    {
        /// <summary>确定供给（库存/已确认PO/VMI/确定PI）</summary>
        CONFIRMED,
        /// <summary>估计供给（Planning-only占位）</summary>
        ESTIMATED
    }

    /// <summary>供给承诺度枚举</summary>
    private enum SupplyCommitment
    {
        /// <summary>已承诺（可作为CTP承诺）</summary>
        COMMITTED,
        /// <summary>未承诺（不可作为CTP承诺）</summary>
        NOT_COMMITTED
    }

    /// <summary>Lock类型（§8）</summary>
    private enum LockType
    {
        /// <summary>严格绑定：1对1强绑定，其他需求完全不可用</summary>
        STRICT_BINDING,
        /// <summary>需求保护：1对N保护，保护组内需求可用，组外不可用</summary>
        DEMAND_PROTECTION,
        /// <summary>执行锁：不可逆事实（已投料、已发货），不得再分配</summary>
        EXECUTION
    }

    /// <summary>Allocation记录（用于Supply侧追溯）</summary>
    /// <summary>
    /// Allocation记录（V1.2）
    /// 记录Pegging阶段的通用逻辑分配，不是PeggingSupplyAllocation本身
    /// PlanVersionId + AllocationSequence唯一标识一笔Allocation
    /// </summary>
    private sealed class AllocationRecord
    {
        public long AllocationSequence  { get; init; }
        public decimal AllocatedQty     { get; init; }
        public string SupplyKey         { get; init; } = string.Empty;
        public string SupplyType        { get; init; } = string.Empty;
        public string DemandKey         { get; init; } = string.Empty;
        public int MaterialId           { get; init; }
        public DateTime AllocatedAt     { get; init; }

        /// <summary>
        /// 是否需要通过生产形成
        /// true: 需要生成LogicalProductionDemand交给Solver
        /// false: 库存/PO/VMI/Received等直接承接，不生成Task
        /// </summary>
        public bool RequiresProduction  { get; init; }
    }

    /// <summary>Lock记录（用于Supply侧锁定管理）</summary>
    private sealed class LockRecord
    {
        public LockType LockType        { get; init; }
        public decimal LockedQty        { get; init; }
        public long? LockedToOrderId    { get; init; }
        public string? LockedToDemandKey { get; init; }
        public DateTime LockedAt        { get; init; }
    }

    private sealed class DemandKeyQtyRow
    {
        public string DemandKey { get; set; } = string.Empty;
        public decimal Qty      { get; set; }
    }

    private sealed class SupplyLoadRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public int MaterialId           { get; set; }
        public int FactoryId            { get; set; }
        public string FactoryCode       { get; set; } = string.Empty;
        public decimal AvailableQty     { get; set; }
        public DateTime? AvailableAt    { get; set; }
        public string? SourceReference  { get; set; }
        public long? SupplySourceId     { get; set; }
        public string? WarehouseCode    { get; set; }
        public DateTime? ReleaseDate    { get; set; }
        public string? PoNo             { get; set; }
        public string? LineNo           { get; set; }
        public string? PiNo             { get; set; }
        public DateTime? IssueDate      { get; set; }
        public DateTime? CreatedAt      { get; set; }
    }

    /// <summary>跨域依赖行（Domain_Dependency，真实 DomainKey 口径）</summary>
    private sealed class UpstreamDomainDependencyRow
    {
        public string UpstreamDomainCode { get; set; } = string.Empty;
        public string ChildMaterialCode  { get; set; } = string.Empty;
        public int DefaultLeadTimeDays   { get; set; }
    }

    /// <summary>上游域 PlanVersion 定位行</summary>
    private sealed class UpstreamPlanVersionRow
    {
        public int Id { get; set; }
    }

    /// <summary>上游域已落盘 Task 供给行（跨域 Quantity-Time 分段虚拟供给源）</summary>
    private sealed class UpstreamTaskSupplyRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public int MaterialId           { get; set; }
        public int FactoryId            { get; set; }
        public string FactoryCode       { get; set; } = string.Empty;
        public decimal Quantity         { get; set; }
        public DateTime? PlannedEndTime { get; set; }
    }

    /// <summary>WIP Stage 明细行（StageProgressSnapshot 逐 Stage 行，供 PI Position 两级装载）</summary>
    private sealed class WipStageLoadRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MaterialCode            { get; set; } = string.Empty;
        public int MaterialId                 { get; set; }
        public int FactoryId                  { get; set; }
        public string FactoryCode             { get; set; } = string.Empty;
        public string StageCode               { get; set; } = string.Empty;
        public decimal GoodCompletedQty       { get; set; }
        public decimal PlannedQty             { get; set; }
        public decimal RemainingQty           { get; set; }
    }

    /// <summary>
    /// 跨版本连续性装载产物（由 LoadSupplyPoolAsync 随供给池一起返回）。
    /// WipRows = IN_PROGRESS StageProgressSnapshot 行（连续份额 E 的来源）；PiPositions = 5号位 PI Position 结果（执行起点）。
    /// </summary>
    private sealed class ContinuityFacts
    {
        public ContinuityFacts(
            IReadOnlyList<WipStageLoadRow> wipRows,
            IReadOnlyDictionary<string, ProductionInstructionPositionResult> piPositions)
        {
            WipRows      = wipRows;
            PiPositions  = piPositions;
        }

        public IReadOnlyList<WipStageLoadRow> WipRows { get; }
        public IReadOnlyDictionary<string, ProductionInstructionPositionResult> PiPositions { get; }
    }

    /// <summary>MESWorkOrderSnapshot 装载行（仅 IN_PROGRESS 工单）</summary>
    private sealed class WorkOrderSnapshotRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MESWorkOrderNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public decimal PlannedQty { get; set; }
        public string WorkOrderStatus { get; set; } = string.Empty;
        public DateTime DataCutoffTime { get; set; }
    }

    /// <summary>OperationProgressSnapshot 装载行（每工单工序进度）</summary>
    private sealed class OperationProgressRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MESWorkOrderNo { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public string StageCode { get; set; } = string.Empty;
        public decimal PlannedQty { get; set; }
        public decimal GoodQty { get; set; }
        public DateTime? LastReportTime { get; set; }
        public DateTime? SourceUpdatedAt { get; set; }
        public string? LastReportResourceCode { get; set; }
    }

    /// <summary>Stage 顺序行（APS_BOM_STAGE_PATH_RAW，ChildMaterialCode+StageCode → StageSeq）</summary>
    private sealed class StagePathLoadRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string StageCode         { get; set; } = string.Empty;
        public int StageSeq             { get; set; }
    }

    /// <summary>
    /// PI 级库存行（ext_ERP_Inventory_View × ext_MES_ProcessCode_View）
    /// WarehouseCode=6位工序码 LEFT JOIN ProcessCode→StageCode，未命中 StageCode 落 UNKNOWN。
    /// </summary>
    private sealed class PiInventoryLoadRow
    {
        public string MaterialCode  { get; set; } = string.Empty;
        public string FactoryCode   { get; set; } = string.Empty;
        public string WarehouseCode { get; set; } = string.Empty;
        public decimal Quantity     { get; set; }
        public string? StageCode    { get; set; }
    }

    /// <summary>
    /// XC（线边仓）行：ext_MES_ProcessCode_View.ERPProperty='XC' 标记的工序码 × ext_ERP_Inventory_View 库存。
    /// </summary>
    private sealed class XcLoadRow
    {
        public string MaterialCode  { get; set; } = string.Empty;
        public string FactoryCode   { get; set; } = string.Empty;
        public string WarehouseCode { get; set; } = string.Empty;
        public decimal Quantity     { get; set; }
        public string? StageCode    { get; set; }
    }

    /// <summary>
    /// 跨厂边行：ext_MES_APS_BOM_Workset_CrossFactoryEdge。
    /// 物理表无 EdgeSequence，装载时按 (ChildMaterialCode, BatchNo, WorksetId, Id) 稳定排序自编号。
    /// 归组键 = ChildMaterialCode（PI 物料作为子件，其产品跨厂转运的边）。
    /// </summary>
    private sealed class CrossFactoryEdgeLoadRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string FromStageCode     { get; set; } = string.Empty;
        public string FromFactoryCode   { get; set; } = string.Empty;
        public string ToStageCode       { get; set; } = string.Empty;
        public string ToFactoryCode     { get; set; } = string.Empty;
    }

    private sealed class TransitLoadRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public string FactoryCode       { get; set; } = string.Empty;  // 目标工厂（到货厂）
        public string SourceFactoryCode { get; set; } = string.Empty;  // 源工厂（发货厂）
        public decimal Quantity         { get; set; }
        public DateTime? Eta            { get; set; }
        public DateTime? ReleaseDate     { get; set; }
        public string SourceDocumentNo  { get; set; } = string.Empty;
        public string? OrderType        { get; set; }
    }

    /// <summary>
    /// MaterialStageDeptContext 裁剪行（IsCurrent=1）：
    /// (MaterialId, StageCode) → DefaultProductionDepartmentId。
    /// </summary>
    private sealed class MaterialStageDeptContextLoadRow
    {
        public int MaterialId { get; set; }
        public string StageCode { get; set; } = string.Empty;
        public int DefaultProductionDepartmentId { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 供给池装载
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 装载供给池（INVENTORY + PIPELINE + WIP）
    /// </summary>
    private async Task<(SupplyPool Pool, ContinuityFacts Continuity)> LoadSupplyPoolAsync(
        PeggingExecutionRequest request,
        FrozenStrategySnapshot frozenSnapshot,
        CancellationToken ct)
    {
        var pool    = new SupplyPool(frozenSnapshot);
        var cutoff  = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt;
        pool.DataCutoffTime = cutoff;   // P0-07：占位采购 AvailableTime 基准 = 本 Run 冻结 DataCutoffTime

        var inventoryRows = await _connectionManager.QueryAsync<SupplyLoadRow>(
            @"SELECT
                  ib.MaterialCode,
                  m.Id         AS MaterialId,
                  ib.FactoryId,
                  f.Code       AS FactoryCode,
                  ib.AvailableQty,
                  NULL         AS AvailableAt,
                  NULL         AS SourceReference,
                  d.Id         AS SupplySourceId
              FROM InventoryBalance ib
              INNER JOIN Material m ON m.MaterialCode = ib.MaterialCode
              INNER JOIN Factory f  ON f.Id = ib.FactoryId
              OUTER APPLY (
                  SELECT TOP 1 Id
                  FROM InventoryAvailableSupplyDetail
                  WHERE MaterialCode   = ib.MaterialCode
                    AND ProductFamilyId = ib.ProductFamilyId
                    AND FactoryId       = ib.FactoryId
                  ORDER BY RulePriority ASC, Id ASC
              ) d
              WHERE ib.AvailableQty > 0",
            db: DatabaseId.APS);

        foreach (var r in inventoryRows)
            pool.Add(r.MaterialCode, r.MaterialId, r.FactoryId, r.AvailableQty,
                     null, Core.Enum.SupplySourceType.INVENTORY,
                     null, r.FactoryCode, r.SupplySourceId,
                     physicalSourceKey: r.SupplySourceId.HasValue ? $"INV:{r.SupplySourceId}" : null);

        // 采购/定时供给：切 5号位 ITimedSupplyFactLoader 读原始事实（Eta/ReleaseDate，AvailableTime 留 2号位算），
        // 再由 2号位用 AvailableTimeCalculator（EtaInvariant 三级链 + ArrivalToUsableOffset）内存计算 AvailableTime（阶段 3，
        // 不再直读 sfp.AvailableTime）。
        // 注意：ITimedSupplyFactLoader 白名单已排除 INTERPLANT_IN_TRANSIT（仅 PURCHASE_IN_TRANSIT /
        // OPEN_PO_REMAINING / ARRIVED_NOT_RECEIVED），厂间在途不从此装载入池。
        // 厂间在途分两类（PM 2026-09-09 v1.0）：PI 级 Transit = PI Position（LoadTransitFactsAsync →
        // ProductionInstructionPositionCalculator）；SH 级供给 = [Order] CustomerSegment='跨厂' 订单
        // （LoadInterFactoryShipmentsAsync 单一供给，不再三段式 Transit/Received 闭合）。均待真实事实就绪后正式启用。
        var rawFacts = await _timedSupplyFactLoader.LoadRawFactsAsync(
            new SupplyFactScope { DataCutoffTime = cutoff }, ct);

        Dictionary<(string, int, int, string), DateTime> manualEtaMap = new();
        if (rawFacts.Count > 0)
        {
            var materialIds = rawFacts.Select(f => f.MaterialId).Distinct().ToList();
            var poNos = rawFacts.Select(f => f.SourceDocumentNo)
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .Distinct()
                                .ToList();
            var manualEtaOverrides = await _procurementManualEtaRepo.QueryAsync(
                materialIds: materialIds,
                poNos: poNos,
                activeOnly: true,
                ct: ct);
            manualEtaMap = AvailableTimeCalculator.BuildManualEtaMap(manualEtaOverrides);
        }

        foreach (var fact in rawFacts)
        {
            var availableAt = AvailableTimeCalculator.Compute(fact, manualEtaMap, frozenSnapshot);
            if (availableAt.HasValue && availableAt > cutoff)
                continue; // 超出本次运行时窗的未来供给（等价旧 SQL 的 AvailableTime <= Cutoff 过滤）

            pool.Add(fact.MaterialCode, fact.MaterialId, fact.FactoryId, fact.RemainingQty,
                     availableAt, Core.Enum.SupplySourceType.PIPELINE,
                     fact.SourceDocumentNo, fact.FactoryCode,
                     sort: new SupplySortFacts(
                         WarehouseCode: fact.StorageCode,
                         ReleaseDate: fact.ReleaseDate,
                         PoNo: fact.SourceDocumentNo,
                         LineNo: fact.SourceDocumentLineNo),
                     physicalSourceKey: string.IsNullOrWhiteSpace(fact.SourceDocumentNo)
                         ? null
                         : $"PO:{fact.SourceDocumentNo}:{fact.SourceDocumentLineNo}");
        }

        // ── WIP（生产指示）供给：PI 级 RemainingQty 全额入池（§8 + P0-02）──
        // PM 2026-08-28（先选 PI → 再消费该 PI 内部 PI Position）+ 2026-09-07 P0-02 正式回复：
        //   1) 读 Stage 明细（StageCode/GoodCompletedQty/RemainingQty），按 PI 聚合取 PI 级 RemainingQty；
        //   2) 调 5号位 IProductionInstructionPositionCalculator 拆 located/unlocated（供执行起点切片，不决定供给数量）；
        //   3) PI Supply = ERP RemainingQty 全额入池；Located + UNLOCATED 合计 = RemainingQty（数量不闭合由快照登记）。
        var wipStageRows = (await _connectionManager.QueryAsync<WipStageLoadRow>(
            @"SELECT sp.ProductionInstructionNo,
                     sp.MaterialCode,
                     m.Id             AS MaterialId,
                     o.FactoryId      AS FactoryId,
                     f.Code           AS FactoryCode,
                     sp.StageCode,
                     sp.GoodCompletedQty,
                     sp.PlannedQty,
                     sp.RemainingQty
              FROM StageProgressSnapshot sp
              INNER JOIN Material m ON m.MaterialCode = sp.MaterialCode
              -- 圈定在制 PI 集合用「需求侧订单」([Order] 携带 MTS_InstructionNo)，不得用 [Task]：
              -- [Task] 是本轮求解的落库输出，首跑为空 → join 恒 0 → wipStageRows 空 → 连续性切不出（鸡生蛋）。
              INNER JOIN (
                  SELECT DISTINCT o.MTS_InstructionNo, o.FactoryId
                  FROM [Order] o
                  WHERE o.PlanVersionId = @PlanVersionId
                    AND o.MTS_InstructionNo IS NOT NULL
              ) o ON o.MTS_InstructionNo = sp.ProductionInstructionNo
              INNER JOIN Factory f ON f.Id = o.FactoryId
              WHERE sp.ScheduleRunId = (
                  SELECT TOP 1 pv.SourceScheduleRunId
                  FROM PlanVersion pv
                  WHERE pv.Id = @PlanVersionId
              )
              AND sp.RemainingQty > 0",
            new { request.PlanVersionId },
            db: DatabaseId.APS)).ToList();

        var piPositions = await LoadPiPositionsAsync(wipStageRows, frozenSnapshot, request.PlanVersionId, ct);

        // ── W1（PM 0914 红线）：IN_PROGRESS 的 RemainingQty 不再作为 WIP Supply 把需求 Q 扣掉。
        // 被覆盖部分必须以 Continuation Slice + Free Slice 一起进 1号位（见 ApplyContinuityBucketing）。
        // wipStageRows / piPositions 仍保留并随返回带出：供「逐 PI 识别连续份额 + 执行起点（StartOperation）」，
        // 以及 LoadPiPositionsAsync 的 PI Position 快照落库 / 数量闭环校验副作用。

        // ── INTER_FACTORY_ORDER（厂间出荷指示，SH级）供给：SH 单一身份（PM 2026-09-09 v1.0）──
        // SH 本身是 Order（OrderType=SALES_ORDER + CustomerSegment='跨厂'，OrderNo=SH号），
        // 单一 Supply 身份，禁拆 Transit/Received/Unproduced 三段；单个 SH 整单一次入库。
        // 每 SH 只入池一条（PhysicalSourceKey=SH号=OrderNo），量=Order.Quantity（指令总量），由红线③⑥ 防重复计量。
        // AvailableAt = SourceReadyTime + CrossFactoryLT；SourceReadyTime = max(源厂 Task PlannedEndTime)，
        // 依赖 Layer-2「源厂需求→求解→回传」（待 PM 接口/源厂方向字段落地）——当前置 null（源厂未排），见台账。
        var shipments = await LoadInterFactoryShipmentsAsync(ct);
        foreach (var s in shipments)
        {
            // TODO(Layer-2)：s.SourceFactoryId 就绪后，读上游源厂域落盘 Task 取 max(PlannedEndTime)=SourceReadyTime，
            // 再 +CrossFactoryLT 得 availableAt；当前 SourceReadyTime 尚未产出，置 null。
            pool.Add(s.MaterialCode, s.MaterialId, s.TargetFactoryId, s.InstructionQty,
                     null, Core.Enum.SupplySourceType.INTER_FACTORY_ORDER,
                     s.ShipmentNo, s.TargetFactoryCode,
                     physicalSourceKey: s.ShipmentNo);
        }

        _logger.LogDebug(
            "[Pegging] 供给池明细: INVENTORY={Inv}, WIP(PI)={Wip}, PIPELINE={Pipe}, PI Position={PiPos}, SH={Sh}",
            inventoryRows.Count(), wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().Count(),
            rawFacts.Count, piPositions.Count, shipments.Count);

        // 跨 Domain Quantity-Time（§8/D12）：注入上游域生产输出为分段虚拟供给
        await LoadUpstreamDomainSupplyAsync(pool, request, ct);

        await LoadActiveLockDataAsync(pool, ct);

        // 诊断（2026-09-18 BOM 无记忆化缺陷期间）：连续事实在 BOM 回路【之前】就已算完，这里直接打 DerivedQty>0 与 ΣE，
        // 不必等随后的 BOM 展开（专项 ① 落地后可删）。
        var ctxAll = piPositions.Values
            .SelectMany(p => p.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
            .ToList();
        _logger.LogInformation(
            "[Pegging][Continuity] 事实摘要(回路前): PI Position={PiPos}, 上下文总数={Ctx}, DerivedQty>0={Active}, ΣE={E}",
            piPositions.Count, ctxAll.Count,
            ctxAll.Count(c => c.DerivedRemainingQty > 0m),
            ctxAll.Sum(c => c.DerivedRemainingQty));

        return (pool, new ContinuityFacts(wipStageRows, piPositions));
    }

    /// <summary>
    /// 跨版本连续性分桶（PM 0914 红线）：对 Pegging 后已形成的 LogicalProductionDemand，按 IN_PROGRESS 既存执行
    /// 切 Continuation/Free 两片一起进 1号位。Q 不因 WIP 供给减小；被覆盖份额以 Continuation Slice（IsContinuation=true）
    /// ＋自由份额 Free Slice（IsContinuation=false）提交，1号位必须同时看到 各工单 Continuation 与 Free。
    /// 连续份额 = 5号位 ExistingExecutionContexts：每 IN_PROGRESS MES 工单一条，绝不合并（§8/§9，P0 红线）。
    /// </summary>
    private (List<LogicalProductionDemand> Demands, List<ContinuityOverCommitEvent> OverCommits) ApplyContinuityBucketing(
        List<LogicalProductionDemand> demands,
        ContinuityFacts facts)
    {
        // PI → 逐工单既存执行上下文（5号位 ExistingExecutionContexts；每 IN_PROGRESS 工单一条）
        var contextsByPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal);
        foreach (var (pi, pos) in facts.PiPositions)
        {
            if (pos.ExistingExecutionContexts is { Count: > 0 } list)
                contextsByPi[pi] = list;
        }

        // 诊断：分桶输入规模（无既存执行上下文 = 无 IN_PROGRESS 工单进入，连续份额切不出）
        _logger.LogInformation(
            "[Pegging][Continuity] 分桶输入: PI Position数={PiPos}, 既存执行上下文总数={Ctx}, 有上下文的PI={PiCtx}",
            facts.PiPositions.Count,
            facts.PiPositions.Values.Sum(p => p.ExistingExecutionContexts?.Count ?? 0),
            contextsByPi.Count);

        if (contextsByPi.Count == 0)
        {
            _logger.LogWarning("[Pegging][Continuity] 无 IN_PROGRESS 既存执行上下文，不切分（Continuation/Free 未产生）");
            return (demands, new List<ContinuityOverCommitEvent>());
        }

        // 诊断：上下文的派生剩余量 + 需求侧命中情况（定位切不出 Continuation 的根因）
        var ctxList = facts.PiPositions.Values
            .SelectMany(p => p.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
            .ToList();
        var demandsWithPi = demands.Count(d => !string.IsNullOrEmpty(d.ProductionInstructionNo));
        var demandsMatching = demands.Count(d => !string.IsNullOrEmpty(d.ProductionInstructionNo) && contextsByPi.ContainsKey(d.ProductionInstructionNo));
        _logger.LogInformation(
            "[Pegging][Continuity] 明细: 上下文总数={Tot}, DerivedQty>0={Active}, ΣE={E}; 需求总数={Dem}, 带PI={WithPi}, 命中上下文PI={Match}",
            ctxList.Count, ctxList.Count(c => c.DerivedRemainingQty > 0m), ctxList.Sum(c => c.DerivedRemainingQty),
            demands.Count, demandsWithPi, demandsMatching);

        var overCommits = new List<ContinuityOverCommitEvent>();
        var result = BucketContinuityShares(demands, contextsByPi, overCommits.Add);
        foreach (var evt in overCommits)
            _logger.LogWarning("[Pegging][Continuity]{Kind} {Msg}", evt.Kind, evt.Message);

        // 红线验收日志：正常 E≤Q 切分路径此前静默，补一条结构化计数，跑完可直接核「同 PI 下 Continuation + Free 同现」。
        var contSlices = result.Where(d => d.IsContinuation).ToList();
        var freeSlices = result.Where(d => !d.IsContinuation && d.LogicalDemandKey.EndsWith("/FREE", StringComparison.Ordinal)).ToList();
        if (contSlices.Count > 0)
            _logger.LogInformation(
                "[Pegging][Continuity] 分桶完成: Continuation={ContCount}片 ΣE={E}, Free={FreeCount}片 ΣQ={FreeQty}, 涉及PI={PiCount}个",
                contSlices.Count, contSlices.Sum(d => d.NetOutputQty),
                freeSlices.Count, freeSlices.Sum(d => d.NetOutputQty),
                contSlices.Select(d => d.ProductionInstructionNo).Distinct().Count());

        return (result, overCommits);
    }

    /// <summary>
    /// 连续份额分桶纯函数（可单测）。逐 MES 工单独立产出 Continuation（§8，P0 红线：绝不合并多工单）。
    /// 规则：
    ///   无 PI / 无 IN_PROGRESS 工单 / 全 E≤0 → 原样透传（自由需求，不切）；
    ///   Q≤0 且仍有开工中剩余 → 不产 Demand/Task + 登记 Demand Mismatch（§9.2）；
    ///   每工单 → Continuation = DerivedRemainingQty（Ci；不受 Q 上限裁剪）；
    ///   Free = max(Q − ΣCi, 0)；E&gt;Q → 不砍单、不产自由 Task，登记 Execution Over-Commit（§9.1）。
    /// ContinuationKey = 源 LogicalDemandKey + "/WO:" + MESWorkOrderNo；Free = 源 key + "/FREE"；
    /// 共用源 AllocationSequence/DemandKey（P3.2 不新建 Allocation）。
    /// PlannedProcessQty 逐片按材料级良率单位投入比（源 PlannedProcessQty/NetOutputQty）反算（§16）。
    /// </summary>
    internal static List<LogicalProductionDemand> BucketContinuityShares(
        List<LogicalProductionDemand> demands,
        IReadOnlyDictionary<string, IReadOnlyList<ExistingExecutionContextDto>> contextsByPi,
        System.Action<ContinuityOverCommitEvent>? onOverCommit = null)
    {
        var result = new List<LogicalProductionDemand>(demands.Count + 8);

        foreach (var demand in demands)
        {
            var pi = demand.ProductionInstructionNo;
            if (string.IsNullOrEmpty(pi))
            {
                result.Add(demand);
                continue;
            }

            if (!contextsByPi.TryGetValue(pi, out var contexts) || contexts.Count == 0)
            {
                result.Add(demand);
                continue;
            }

            // 只取有连续份额的工单（DerivedRemainingQty > 0）
            var active = contexts.Where(c => c.DerivedRemainingQty > 0m).ToList();
            if (active.Count == 0)
            {
                result.Add(demand);
                continue;
            }

            var q = demand.NetOutputQty;

            // §9.2：Q=0 但仍有开工中剩余 → 不产正常 Demand/Task，登记 Demand Mismatch
            if (q <= 0m)
            {
                var leftover = active.Sum(c => c.DerivedRemainingQty);
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, leftover, leftover, "DEMAND_MISMATCH"));
                continue;
            }

            // §16：材料级良率单位投入比（PlannedProcessQty / NetOutputQty），逐片反算 PlannedProcessQty
            var processRatio = demand.PlannedProcessQty > 0m && q > 0m
                ? demand.PlannedProcessQty / q
                : 1m;

            // §8：逐工单各出一条 Continuation，绝不合并
            var sumE = 0m;
            foreach (var ctx in active)
            {
                var ci = ctx.DerivedRemainingQty;
                sumE += ci;

                var key = demand.LogicalDemandKey + "/WO:" + ctx.MESWorkOrderNo;
                var startOp = string.IsNullOrEmpty(ctx.StartOperationCode) ? null : ctx.StartOperationCode;
                var startStage = !string.IsNullOrEmpty(ctx.StartStageCode) ? ctx.StartStageCode : demand.StartStageCode;

                result.Add(CloneDemandSlice(demand, key, ci, Math.Round(ci * processRatio, 4),
                    isContinuation: true, startStage, startOp));
            }

            var freeQty = q - sumE;
            if (freeQty > 0m)
            {
                // Free 是新生产量：起点从路由首工序起（StartOperation=null，StartStage 由 FillStartStageCodes 回填）
                result.Add(CloneDemandSlice(demand, demand.LogicalDemandKey + "/FREE", freeQty,
                    Math.Round(freeQty * processRatio, 4),
                    isContinuation: false, demand.StartStageCode, startOperationCode: null));
            }
            else if (freeQty < 0m)
            {
                // §9.1：E>Q 执行超量 —— 不砍单、不产新自由 Task，登记 Execution Over-Commit
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, sumE, -freeQty, "EXECUTION_OVER_COMMIT"));
            }
        }

        return result;
    }

    private static LogicalProductionDemand CloneDemandSlice(
        LogicalProductionDemand s,
        string logicalDemandKey,
        decimal netQty,
        decimal processQty,
        bool isContinuation,
        string? startStageCode,
        string? startOperationCode) => new()
    {
        LogicalDemandKey       = logicalDemandKey,
        PlanVersionId          = s.PlanVersionId,
        DomainKey              = s.DomainKey,
        AllocationSequence     = s.AllocationSequence,
        DemandKey              = s.DemandKey,
        OrderId                = s.OrderId,
        MaterialId             = s.MaterialId,
        FactoryId              = s.FactoryId,
        StartStageCode         = string.IsNullOrEmpty(startStageCode) ? s.StartStageCode : startStageCode,
        StartOperationCode     = startOperationCode,
        NetOutputQty           = netQty,
        PlannedProcessQty      = processQty,
        UOM                    = s.UOM,
        RequiredAvailableTime  = s.RequiredAvailableTime,
        DemandSequence         = s.DemandSequence,
        ProductionInstructionNo = s.ProductionInstructionNo,
        IsUnlocated            = s.IsUnlocated,
        PreferredResourceId    = s.PreferredResourceId,
        FallbackResourceId     = s.FallbackResourceId,
        IsContinuation         = isContinuation,
    };

    /// <summary>
    /// 连续性分桶异常事件（G6）：E&gt;Q 执行超量 / Q=0 仍开工 两类，登记到 ScheduleExplanationFact（复用不加表）。
    /// </summary>
    internal sealed record ContinuityOverCommitEvent(
        string ProductionInstructionNo,
        long? OrderId,
        int MaterialId,
        string DomainKey,
        long AllocationSequence,
        string StartStageCode,
        string LogicalDemandKey,
        decimal Quantity,
        decimal ContinuationQty,
        decimal ExcessQty,
        string Kind)   // "EXECUTION_OVER_COMMIT" | "DEMAND_MISMATCH"
    {
        /// <summary>DEMAND_MISMATCH（Q=0 仍开工）=错误级；EXECUTION_OVER_COMMIT（E&gt;Q）=警告级（登记不阻断）。</summary>
        public string Severity => Kind == "DEMAND_MISMATCH" ? "ERROR" : "WARNING";

        public string Message => Kind == "EXECUTION_OVER_COMMIT"
            ? $"PI {ProductionInstructionNo}: E={ContinuationQty} > Q={Quantity}，执行超量 {ExcessQty}（现场执行/数量不一致），不产自由 Task"
            : $"PI {ProductionInstructionNo}: Q=0 但存在开工中剩余 ΣE={ContinuationQty}（Demand Mismatch），不产 Demand/Task";
    }

    /// <summary>
    /// Task 落库阶段旧 TaskNo 反查行（G4）：TaskNo ↔ MESWorkOrderNo 1:1 绑定，来源 = 下发中间表 TaskDispatch（冻结 §7.1）。
    /// </summary>
    private sealed record TaskNoBindingRow(string MESWorkOrderNo, string TaskNo);

    /// <summary>
    /// 从 FinalTaskDraft.SourceDraftId（= LogicalDemandKey，1号位 T08 原样带回）解析 ContinuationKey 携带的 MES 工单号。
    /// 连续份额键 = 源 key + "/WO:" + MESWorkOrderNo（BucketContinuityShares 生成）；自由份额源 key + "/FREE" / 普通需求不含 "/WO:" → 返回 null。
    /// 身份复用 LogicalDemandKey 承载、不另设字段（冻结 §7.2 + LogicalProductionDemand 注释），故落库前从这里反解。
    /// </summary>
    internal static string? ExtractMesWorkOrderNo(string? sourceDraftId)
    {
        if (string.IsNullOrEmpty(sourceDraftId))
            return null;

        const string marker = "/WO:";
        var idx = sourceDraftId.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? sourceDraftId[(idx + marker.Length)..] : null;
    }

    /// <summary>
    /// TaskNo 三分支（冻结 §12.10，T09/T10/T22）：连续份额（mesWorkOrderNo 非空）且反查到旧 TaskNo → 继承（分支1，不新建）；
    /// 连续份额但无旧 TaskNo（首次上线/无绑定）→ 新号（分支2）；自由份额（mesWorkOrderNo=null）→ 新号（分支3）。
    /// </summary>
    internal static string ResolveTaskNo(
        long planVersionId,
        string finalDraftId,
        string? mesWorkOrderNo,
        IReadOnlyDictionary<string, string> oldTaskNoByWorkOrder)
        => mesWorkOrderNo != null && oldTaskNoByWorkOrder.TryGetValue(mesWorkOrderNo, out var oldTaskNo)
            ? oldTaskNo
            : $"PEGG-{planVersionId}-{finalDraftId[..8]}";

    /// <summary>
    /// 跨 Domain Quantity-Time（§8/D12）：下游域启动前，从上游域已落盘 Task 读取 ChildMaterialCode
    /// 的完工时间 + DefaultLeadTimeDays，构造成分段虚拟供给注入本域供给池。
    /// 保持多段（40@15日 + 60@17日），禁止压平——每一条上游 Task 独立 Add 一段。
    /// 工厂口径：Domain_Dependency 承载同厂跨族血缘（FAMILY 域），上游 Task 的 Order.FactoryId 即消耗侧工厂；
    /// 跨厂流动走 CrossFactoryEdge 主链，不经此路径。
    /// Domain_Dependency 为空（V1 现状）时直接跳过，零额外开销。
    /// </summary>
    private async System.Threading.Tasks.Task LoadUpstreamDomainSupplyAsync(
        SupplyPool pool,
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DomainKey))
            return;

        var runId = request.SchedulingContext?.ScheduleRunId;
        if (!runId.HasValue || runId.Value <= 0)
            return;

        var dependencies = (await _connectionManager.QueryAsync<UpstreamDomainDependencyRow>(
            @"SELECT UpstreamDomainCode, ChildMaterialCode, DefaultLeadTimeDays
              FROM Domain_Dependency
              WHERE DownstreamDomainCode = @DomainKey",
            new { DomainKey = request.DomainKey },
            db: DatabaseId.APS)).ToList();

        if (dependencies.Count == 0)
            return;

        foreach (var dep in dependencies)
        {
            // 定位同一 ScheduleRun 内上游域的 PlanVersion（分域后每 Domain 一个 PlanVersion）
            var upstreamPv = await _connectionManager.QueryFirstOrDefaultAsync<UpstreamPlanVersionRow>(
                @"SELECT TOP 1 Id
                  FROM PlanVersion
                  WHERE SourceScheduleRunId = @RunId AND DomainKey = @UpstreamDomainCode
                  ORDER BY Id DESC",
                new { RunId = runId.Value, UpstreamDomainCode = dep.UpstreamDomainCode },
                db: DatabaseId.APS);

            if (upstreamPv == null)
            {
                _logger.LogWarning(
                    "[Pegging] 上游域 {UpstreamDomain} 无对应 PlanVersion（跨域 Quantity-Time 跳过）",
                    dep.UpstreamDomainCode);
                continue;
            }

            // 读上游已落盘 Task 的完工时间，逐条 Add 为分段虚拟供给（保留多段，不压平）
            var upstreamTasks = (await _connectionManager.QueryAsync<UpstreamTaskSupplyRow>(
                @"SELECT m.MaterialCode,
                         t.MaterialId,
                         o.FactoryId,
                         f.Code          AS FactoryCode,
                         t.Quantity,
                         t.PlannedEndTime
                  FROM [Task] t
                  INNER JOIN Material m ON m.Id = t.MaterialId
                  INNER JOIN [Order]  o ON o.Id = t.OrderId
                  INNER JOIN Factory  f ON f.Id = o.FactoryId
                  WHERE t.PlanVersionId = @UpstreamPlanVersionId
                    AND m.MaterialCode = @ChildMaterialCode
                    AND t.PlannedEndTime IS NOT NULL",
                new { UpstreamPlanVersionId = upstreamPv.Id, ChildMaterialCode = dep.ChildMaterialCode },
                db: DatabaseId.APS)).ToList();

            if (upstreamTasks.Count == 0)
                continue;

            var leadTimeDays = dep.DefaultLeadTimeDays > 0 ? dep.DefaultLeadTimeDays : 0;
            foreach (var t in upstreamTasks)
            {
                pool.Add(
                    materialCode: t.MaterialCode,
                    materialId: t.MaterialId,
                    factoryId: t.FactoryId,
                    qty: t.Quantity,
                    availableAt: t.PlannedEndTime.Value.AddDays(leadTimeDays),
                    sourceType: Core.Enum.SupplySourceType.UPSTREAM_DOMAIN_PRODUCTION,
                    sourceRef: $"UPSTREAM_{upstreamPv.Id}_{t.MaterialId}_{t.PlannedEndTime:yyyyMMddHHmmss}",
                    factoryCode: t.FactoryCode);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadActiveLockDataAsync(SupplyPool pool, CancellationToken ct)
    {
        var allSupplyKeys = pool.GetAllEntries()
            .Select(e => e.SupplyKey)
            .Distinct()
            .ToList();

        if (allSupplyKeys.Count == 0)
        {
            _logger.LogDebug("[Pegging] 供给池为空，跳过 Lock 数据加载");
            return;
        }

        // 批量查询所有供给上的活跃 Lock
        var lockTasks = allSupplyKeys.Select(key => _lockRepo.GetActiveLocksOnSupplyAsync(key, ct));
        var lockResults = await System.Threading.Tasks.Task.WhenAll(lockTasks);
        var allLocks = lockResults.SelectMany(x => x).ToList();

        if (allLocks.Count == 0)
        {
            _logger.LogDebug("[Pegging] 未发现活跃 Lock 记录");
            return;
        }

        // 按 SupplyKey 分组，附加到对应的 SupplyLedgerEntry
        var locksBySupplyKey = allLocks.GroupBy(l => l.SupplyKey).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in pool.GetAllEntries())
        {
            if (locksBySupplyKey.TryGetValue(entry.SupplyKey, out var locks))
            {
                foreach (var dbLock in locks)
                {
                    var lockType = dbLock.LockType switch
                    {
                        "STRICT_BINDING" => LockType.STRICT_BINDING,
                        "DEMAND_PROTECTION" => LockType.DEMAND_PROTECTION,
                        _ => (LockType?)null
                    };

                    if (!lockType.HasValue)
                    {
                        _logger.LogWarning(
                            "[Pegging] 未识别的 LockType: {LockType}，SupplyKey={SupplyKey}",
                            dbLock.LockType, entry.SupplyKey);
                        continue;
                    }

                    entry.Locks.Add(new LockRecord
                    {
                        LockType = lockType.Value,
                        LockedQty = dbLock.LockedQty,
                        LockedToOrderId = dbLock.SourcePlanVersionId.HasValue
                            ? null
                            : ExtractOrderIdFromDemandKey(dbLock.DemandKey),
                        LockedToDemandKey = dbLock.DemandKey,
                        LockedAt = dbLock.CreatedAt
                    });

                    // 更新 LockedQty 累计
                    entry.LockedQty += dbLock.LockedQty;
                }
            }
        }

        _logger.LogDebug(
            "[Pegging] 已加载 {LockCount} 条活跃 Lock 记录到供给池",
            allLocks.Count);
    }

    /// <summary>
    /// 从 DemandKey 提取 OrderId（如：ORDER_12345_MAT001_F01 → 12345）
    /// </summary>
    private static long? ExtractOrderIdFromDemandKey(string demandKey)
    {
        if (string.IsNullOrEmpty(demandKey)) return null;

        var parts = demandKey.Split('_');
        if (parts.Length >= 2 && parts[0] == "ORDER" && long.TryParse(parts[1], out var orderId))
            return orderId;

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PI Position 两级装载（§8）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 3号位 FrozenStrategySnapshot 投影出 5号位 FrozenFactParameters（C2-6：2号位在集成层投影，不落 DTO）。
    /// V1 最小集仅 Stage 进度走 PI Position；DefaultPurchaseLt / OverdueMargin / ArrivalToUsableOffsets
    /// 供 5号位后续事实（XC/Transit/Received）消费时使用，当前先按已冻结 ProcurementBlock 参数值投影。
    /// </summary>
    internal static FrozenFactParameters BuildFrozenFactParameters(FrozenStrategySnapshot snapshot)
    {
        // Warehouse 级默认 LT（MaterialId 为空）作为 DefaultPurchaseLt；无配置时 0。
        var defaultLtDays = snapshot.Procurement.DefaultPurchaseLt?
            .FirstOrDefault(r => string.IsNullOrWhiteSpace(r.MaterialId))?.DefaultLtDays ?? 0;

        return new FrozenFactParameters
        {
            StrategyProfileVersionId = snapshot.StrategyProfileVersionId,
            DefaultPurchaseLt = (int)Math.Round(defaultLtDays, MidpointRounding.AwayFromZero),
            // OverdueMargin 语义对齐：FrozenFactParameters.OverdueMargin 为「天」，取 ProcurementBlock 的 MinimumExtraDays。
            OverdueMargin = snapshot.Procurement.OverdueMargin?.MinimumExtraDays ?? 0,
            ArrivalToUsableOffsets = (snapshot.Procurement.ArrivalToUsableOffsets ?? [])
                .GroupBy(r => r.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (int)Math.Round(g.First().OffsetHours, MidpointRounding.AwayFromZero),
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    private const int SqlServerInParameterLimit = 2000;

    /// <summary>
    /// 分批执行带 IN 列表的查询，规避 SQL Server TDS 2100 参数上限。
    /// wipStageRows 非空后 distinct MaterialCode/PI 可达数千，单条 `IN @List`（Dapper 展开为逐值参数）会溢出。
    /// </summary>
    private static async Task<List<TRow>> QueryChunkedInAsync<TValue, TRow>(
        IReadOnlyList<TValue> values,
        Func<IReadOnlyList<TValue>, Task<IEnumerable<TRow>>> query)
    {
        var rows = new List<TRow>();
        for (var i = 0; i < values.Count; i += SqlServerInParameterLimit)
        {
            var chunk = values.Skip(i).Take(SqlServerInParameterLimit).ToList();
            rows.AddRange(await query(chunk));
        }

        return rows;
    }

    /// <summary>
    /// 装载 PI Position（消费 5号位 IProductionInstructionPositionCalculator）。返回 ProductionInstructionNo → 结果。
    /// 事实范围（V1 最小集）：Stage 进度（StageProgressSnapshot）→ StageProgressFact；
    /// Stage 顺序取 APS_BOM_STAGE_PATH_RAW（ChildMaterialCode+StageCode → StageSeq，多批次/BOMNO/Scope 取 MIN，近似）；
    /// PiInventory 已绑定（ext_ERP_Inventory_View × ext_MES_ProcessCode_View）。
    /// PiInventory / XC / CrossFactoryEdge / Transit 已绑定（ext_ 同义词 Loader，Transit 实测 0 行）；Received 已退出计算主链（不再装载，见 PM 裁定）；
    /// 计算器按 UNLOCATED 兜底闭合，2号位据此把未定位份额路由到新增生产。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ProductionInstructionPositionResult>> LoadPiPositionsAsync(
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        FrozenStrategySnapshot frozenSnapshot,
        int planVersionId,
        CancellationToken ct)
    {
        if (wipStageRows.Count == 0)
            return new Dictionary<string, ProductionInstructionPositionResult>();

        // 1) Stage 顺序映射：ChildMaterialCode+StageCode → StageSeq（MIN 去重多 BOMNO/Scope）
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var stagePathRows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<StagePathLoadRow>(
                @"SELECT ChildMaterialCode, StageCode, MIN(StageSeq) AS StageSeq
                  FROM APS_BOM_STAGE_PATH_RAW
                  WHERE ChildMaterialCode IN @MaterialCodes
                  GROUP BY ChildMaterialCode, StageCode",
                new { MaterialCodes = chunk },
                db: DatabaseId.APS));

        var stageSeqMap = new Dictionary<(string MaterialCode, string StageCode), int>();
        foreach (var p in stagePathRows)
            stageSeqMap[(p.ChildMaterialCode, p.StageCode)] = p.StageSeq;

        // 每物料 Stage 路径（按 StageSeq 升序，首/末标记 IsStartStage/IsEndStage）。
        // 关键：5号位 DetermineEffectiveStartStage / CalculateStagePositions(isFirstStage) /
        //       CalculateNextOperationContexts 都消费 input.StagePath；缺了会 startStageCode 为空 →
        //       BuildExistingExecutionContexts 的 derivedRemainingQty=0 → 连续份额切不出（实测 406 上下文全 0）。
        var stagePathByMaterial = stagePathRows
            .GroupBy(p => p.ChildMaterialCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var ordered = g
                        .GroupBy(x => x.StageCode, StringComparer.Ordinal)
                        .Select(grp => (StageCode: grp.Key, StageSequence: grp.Min(x => x.StageSeq)))
                        .OrderBy(x => x.StageSequence)
                        .ToList();
                    return (IReadOnlyList<StagePathFact>)ordered.Select((x, i) => new StagePathFact
                    {
                        StageCode = x.StageCode,
                        StageSequence = x.StageSequence,
                        IsStartStage = i == 0,
                        IsEndStage = i == ordered.Count - 1
                    }).ToList();
                },
                StringComparer.Ordinal);

        // 1.5) PI 级库存事实（ext_ 同义词：ERP_Inventory_View.WarehouseCode=6位工序码 → MES_ProcessCode_View.StageCode）
        var piInventoryMap = await LoadPiInventoryFactsAsync(wipStageRows, ct);

        // 1.6) XC（线边仓）事实（ERPProperty='XC' × 库存）
        var xcMap = await LoadXcFactsAsync(wipStageRows, ct);

        // 1.7) 跨厂边事实（ext_MES_APS_BOM_Workset_CrossFactoryEdge，按 ChildMaterialCode 归组）
        var crossFactoryEdgeMap = await LoadCrossFactoryEdgesAsync(wipStageRows, ct);

        // 1.9) 厂间在途事实（Transit，ext_ERP_InterplantInTransit_View；0 行，待 5号位 ODS 数据）
        // PM 0910：Transit 必须按生产指示号（TransitDocumentNo = PI No）精确归属，Material/目标厂仅做一致性校验。
        var transitFacts = await LoadTransitFactsAsync(wipStageRows, ct);
        var piIdentities = wipStageRows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().MaterialCode, g.First().FactoryCode), StringComparer.Ordinal);
        var (transitByPi, transitIssues) = AttributeInterplantTransitToPi(transitFacts, piIdentities);
        foreach (var issue in transitIssues)
            _logger.LogWarning("[Pegging] Transit 归属 Issue: {Issue}", issue);

        // 1.95) 本次快照批次 ScheduleRunId（OperationProgress / MESWorkOrder 与 StageProgress 同批快照）
        var scheduleRunId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        // 1.96) 每工单快照（IN_PROGRESS）+ 每工单工序进度——5号位 BuildExistingExecutionContexts 的两路事实源
        var workOrderFactsByPi = await LoadWorkOrderSnapshotFactsAsync(scheduleRunId, wipStageRows, ct);
        var operationProgressByPi = await LoadOperationProgressFactsAsync(scheduleRunId, wipStageRows, ct);
        var (routingOpsByPi, routingDepsByPi) = await LoadRoutingFactsAsync(wipStageRows, ct);

        // 诊断：事实装载计数（连续份额依赖 WorkOrder IN_PROGRESS + OperationProgress）
        _logger.LogInformation(
            "[Pegging][Diag] PI Position 事实: WIP行={Wip}, IN_PROGRESS工单={Wo}, 工序进度={Op行}, 工单PI={WoPi}",
            wipStageRows.Count,
            workOrderFactsByPi.Values.Sum(v => (long)v.Count),
            operationProgressByPi.Values.Sum(v => (long)v.Count),
            workOrderFactsByPi.Count);

        // 2) 按 PI 分组构建输入
        var inputs = new List<ProductionInstructionPositionInput>();
        foreach (var group in wipStageRows.GroupBy(r => r.ProductionInstructionNo))
        {
            var first = group.First();
            var stageProgress = new List<StageProgressFact>(group.Count());
            foreach (var s in group)
            {
                stageProgress.Add(new StageProgressFact
                {
                    StageCode = s.StageCode,
                    GoodCompletedQty = s.GoodCompletedQty,
                    PlannedQty = s.PlannedQty,
                    RemainingQty = s.RemainingQty,
                    StageSequence = stageSeqMap.TryGetValue((s.MaterialCode, s.StageCode), out var seq) ? seq : 0,
                    SnapshotId = null
                });
            }

            var key = (first.MaterialCode, first.FactoryCode);
            piInventoryMap.TryGetValue(key, out var piInventories);
            xcMap.TryGetValue(key, out var xcFacts);
            crossFactoryEdgeMap.TryGetValue(first.MaterialCode, out var crossFactoryEdges);
            transitByPi.TryGetValue(first.ProductionInstructionNo, out var piTransitFacts);
            workOrderFactsByPi.TryGetValue(first.ProductionInstructionNo, out var workOrderFacts);
            operationProgressByPi.TryGetValue(first.ProductionInstructionNo, out var operationProgress);
            routingOpsByPi.TryGetValue(first.ProductionInstructionNo, out var routingOperations);
            routingDepsByPi.TryGetValue(first.ProductionInstructionNo, out var routingDependencies);

            inputs.Add(new ProductionInstructionPositionInput
            {
                ProductionInstructionNo = first.ProductionInstructionNo,
                MaterialId = first.MaterialId,
                FactoryId = first.FactoryId,
                MaterialCode = first.MaterialCode,
                FactoryCode = first.FactoryCode,
                ErpRemainingQty = group.Max(r => r.RemainingQty),
                StageProgress = stageProgress,
                StagePath = stagePathByMaterial.TryGetValue(first.MaterialCode, out var spList)
                    ? spList
                    : (IReadOnlyList<StagePathFact>)Array.Empty<StagePathFact>(),
                WorkOrders = workOrderFacts ?? (IReadOnlyList<WorkOrderSnapshotFact>)Array.Empty<WorkOrderSnapshotFact>(),
                OperationProgress = operationProgress ?? (IReadOnlyList<OperationProgressFact>)Array.Empty<OperationProgressFact>(),
                PiInventories = piInventories ?? (IReadOnlyList<PiInventoryFact>)Array.Empty<PiInventoryFact>(),
                XcFacts = xcFacts ?? (IReadOnlyList<XcFact>)Array.Empty<XcFact>(),
                CrossFactoryEdges = crossFactoryEdges ?? (IReadOnlyList<CrossFactoryEdgeFact>)Array.Empty<CrossFactoryEdgeFact>(),
                TransitFacts = piTransitFacts ?? (IReadOnlyList<InterplantTransitFact>)Array.Empty<InterplantTransitFact>(),
                RoutingOperations = routingOperations ?? (IReadOnlyList<RoutingOperationFact>)Array.Empty<RoutingOperationFact>(),
                RoutingDependencies = routingDependencies ?? (IReadOnlyList<RoutingDependencyFact>)Array.Empty<RoutingDependencyFact>()
            });
        }

        // 3) 调 5号位计算器（纯计算，无 I/O）
        var parameters = BuildFrozenFactParameters(frozenSnapshot);
        var results = await _piPositionCalculator.CalculateProductionInstructionPositionsAsync(inputs, parameters, ct);

        // 4) 保存 PI Position 快照 + 数量闭环校验（2号位职责，不修正 5号位 事实）
        await SavePiPositionSnapshotsAsync(planVersionId, inputs, results, wipStageRows, ct);

        return results.ToDictionary(r => r.ProductionInstructionNo, StringComparer.Ordinal);
    }

    /// <summary>
    /// 每工单快照事实装载（IN_PROGRESS 工单）——5号位 ExistingExecutionContext 的工单身份源。
    /// 冻结红线（v1.6/v2.6）：只传 IN_PROGRESS；RELEASED/CLOSED/DELETED 不传；工单级不带 RemainingQty。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<WorkOrderSnapshotFact>>> LoadWorkOrderSnapshotFactsAsync(
        int scheduleRunId,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        CancellationToken ct)
    {
        if (scheduleRunId <= 0)
            return new Dictionary<string, IReadOnlyList<WorkOrderSnapshotFact>>();

        var piNos = wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().ToList();
        var rows = await QueryChunkedInAsync(piNos, chunk =>
            _connectionManager.QueryAsync<WorkOrderSnapshotRow>(
                @"SELECT ProductionInstructionNo, MESWorkOrderNo, MaterialCode, PlannedQty, WorkOrderStatus, DataCutoffTime
                  FROM MESWorkOrderSnapshot
                  WHERE ScheduleRunId = @ScheduleRunId
                    AND WorkOrderStatus = 'IN_PROGRESS'
                    AND ProductionInstructionNo IN @PiNos",
                new { ScheduleRunId = scheduleRunId, PiNos = chunk },
                db: DatabaseId.APS));

        return rows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<WorkOrderSnapshotFact>)g.Select(r => new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = r.ProductionInstructionNo,
                    MESWorkOrderNo = r.MESWorkOrderNo,
                    MaterialCode = r.MaterialCode,
                    PlannedQty = r.PlannedQty,
                    WorkOrderStatus = r.WorkOrderStatus,
                    DataCutoffTime = r.DataCutoffTime
                }).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// 每工单工序进度事实装载——5号位按 MESWorkOrderNo 找当前有效工序的输入事实源。
    /// V1 口径：工序身份 = OperationName（MES 无稳定工序码）。PM 2026-09-15 裁决已废弃线性 OperationSeq：
    /// OperationSequence 保留兼容字段（=0），不参与 StartOperation/Routing/连续份额/Solver 判断；
    /// StartOperationCode 由 5号位基于 RoutingOperation + RoutingDependency 拓扑前沿解析输出，2号位只消费、不拼序。
    /// RemainingQty = max(PlannedQty − GoodQty, 0)，与 v1.6 DerivedRemainingQty 同一公式。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<OperationProgressFact>>> LoadOperationProgressFactsAsync(
        int scheduleRunId,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        CancellationToken ct)
    {
        if (scheduleRunId <= 0)
            return new Dictionary<string, IReadOnlyList<OperationProgressFact>>();

        var piNos = wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().ToList();
        var rows = await QueryChunkedInAsync(piNos, chunk =>
            _connectionManager.QueryAsync<OperationProgressRow>(
                @"SELECT ProductionInstructionNo, MESWorkOrderNo, OperationName, StageCode,
                         PlannedQty, GoodQty, LastReportTime, SourceUpdatedAt, LastReportResourceCode
                  FROM OperationProgressSnapshot
                  WHERE ScheduleRunId = @ScheduleRunId
                    AND ProductionInstructionNo IN @PiNos",
                new { ScheduleRunId = scheduleRunId, PiNos = chunk },
                db: DatabaseId.APS));

        return rows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<OperationProgressFact>)g.Select(r => new OperationProgressFact
                {
                    OperationCode = r.OperationName,   // V1：工序身份 = OperationName，无独立工序码列
                    OperationName = r.OperationName,
                    StageCode = r.StageCode,
                    MESWorkOrderNo = r.MESWorkOrderNo,
                    PlannedQty = r.PlannedQty,
                    GoodQty = r.GoodQty,
                    RemainingQty = Math.Max(r.PlannedQty - r.GoodQty, 0m),
                    OperationSequence = 0,             // PM 裁决（2026-09-15）：废弃线性序，保留兼容字段，不参与业务判断
                    LastReportTime = r.LastReportTime,
                    LastReportResourceCode = string.IsNullOrWhiteSpace(r.LastReportResourceCode) ? null : r.LastReportResourceCode,
                    SnapshotId = null,
                    UpdatedAt = r.SourceUpdatedAt
                }).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Routing 节点/边事实装载（按物料+部门过滤后的 PI 子集）——5号位 DAG 拓扑前沿算法的输入事实源。
    /// 部门维度复用 MaterialStageDeptContext((MaterialId, StageCode) → DefaultProductionDepartmentId)，
    /// 与 1号位 部门锁定同源（5号位 落码执行说明 2026-09-16 §4.3）。
    /// RouteCode/PathId 忠实传真实值（RouteCode 实为工序名序列串、非 "DEFAULT"，勿硬编码）。
    /// V1 简化：按 (MaterialId, ProductionDepartmentId) 过滤；跨部门拼接口径待 2↔5 另行对齐，不阻塞。
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, IReadOnlyList<RoutingOperationFact>> OpsByPi,
                        IReadOnlyDictionary<string, IReadOnlyList<RoutingDependencyFact>> DepsByPi)>
        LoadRoutingFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var emptyOps = new Dictionary<string, IReadOnlyList<RoutingOperationFact>>(StringComparer.Ordinal);
        var emptyDeps = new Dictionary<string, IReadOnlyList<RoutingDependencyFact>>(StringComparer.Ordinal);

        if (wipStageRows.Count == 0)
            return (emptyOps, emptyDeps);

        var materialIds = wipStageRows.Select(r => r.MaterialId).Distinct().ToList();

        // a) (MaterialId, StageCode) → 部门集合（与 1号位 部门锁定同源；缺映射时该 Stage 无部门）
        var deptCtx = await LoadMaterialStageDeptContextAsync(materialIds, ct);
        var deptByMaterialStage = deptCtx
            .GroupBy(c => (c.MaterialId, c.StageCode))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ProductionDepartmentId).Distinct().ToHashSet());

        // b) 装载 Routing 节点/边（IsActive=1），按 PI 材料集合一次取回，再逐 PI 按部门过滤
        var operations = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingOperation>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, OperationName, StageCode
                  FROM RoutingOperation
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        var dependencies = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingDependency>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId,
                         FromOperationCode, ToOperationCode, DependencyType
                  FROM RoutingDependency
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        // c) 按 PI 组装：每个 PI 的部门 = 其 Stage 集在 MSC 的去重部门；无映射时回退到物料全路由
        var opsByPi = new Dictionary<string, IReadOnlyList<RoutingOperationFact>>(StringComparer.Ordinal);
        var depsByPi = new Dictionary<string, IReadOnlyList<RoutingDependencyFact>>(StringComparer.Ordinal);

        foreach (var group in wipStageRows.GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal))
        {
            var pi = group.Key;
            var materialId = group.First().MaterialId;

            var deptIds = new HashSet<int>();
            foreach (var s in group)
            {
                if (deptByMaterialStage.TryGetValue((materialId, s.StageCode), out var set))
                    deptIds.UnionWith(set);
            }

            IEnumerable<RoutingOperation> ops = operations.Where(o => o.MaterialId == materialId);
            IEnumerable<RoutingDependency> deps = dependencies.Where(d => d.MaterialId == materialId);

            if (deptIds.Count > 0)
            {
                ops = ops.Where(o => deptIds.Contains(o.ProductionDepartmentId));
                deps = deps.Where(d => deptIds.Contains(d.ProductionDepartmentId));
            }
            else
            {
                // 无 MSC 部门映射时回退物料全路由（避免静默漏装致 NEXT_OPERATION 误报），与 LoadRoutingContextAsync 同口径。
                _logger.LogWarning("[Pegging] PI {Pi} 无 MaterialStageDeptContext 部门映射，Routing 事实回退到物料全路由", pi);
            }

            opsByPi[pi] = ops.Select(o => new RoutingOperationFact
            {
                OperationCode = o.OperationCode,
                OperationName = o.OperationName,
                StageCode = o.StageCode ?? string.Empty,
                RouteCode = o.RouteCode,
                PathId = o.PathId
            }).ToList();

            depsByPi[pi] = deps.Select(d => new RoutingDependencyFact
            {
                FromOperationCode = d.FromOperationCode,
                ToOperationCode = d.ToOperationCode,
                DependencyType = d.DependencyType,
                RouteCode = d.RouteCode,
                PathId = d.PathId
            }).ToList();
        }

        return (opsByPi, depsByPi);
    }

    /// <summary>
    /// 保存 PI Position 快照 + 数量闭环校验（2号位职责，2026-09-02 双专项冻结）。
    /// 冻结约束：Σ Position.Quantity = ERP PI RemainingQty；异常（数量不闭合 / 计算失败 / 位置缺失）
    /// 只登记（IssueCode 落快照 + 日志），不自行修正 5号位 计算出的位置数量。
    /// </summary>
    private async System.Threading.Tasks.Task SavePiPositionSnapshotsAsync(
        int planVersionId,
        IReadOnlyList<ProductionInstructionPositionInput> inputs,
        IReadOnlyList<ProductionInstructionPositionResult> results,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        CancellationToken ct)
    {
        if (inputs.Count == 0)
            return;

        var scheduleRunId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        if (scheduleRunId <= 0)
        {
            _logger.LogWarning("[Pegging] PI Position 快照跳过：PlanVersionId={PlanVersionId} 无 SourceScheduleRunId", planVersionId);
            return;
        }

        var resultsByPi = results.ToDictionary(r => r.ProductionInstructionNo, StringComparer.Ordinal);
        var materialCodeByPi = wipStageRows
            .GroupBy(r => r.ProductionInstructionNo)
            .ToDictionary(g => g.Key, g => g.First().MaterialCode, StringComparer.Ordinal);

        var (rows, issues) = MapPositionSnapshots(scheduleRunId, planVersionId, inputs, resultsByPi, materialCodeByPi);

        foreach (var issue in issues)
            _logger.LogWarning("[Pegging] PI Position 闭环异常: {Issue}", issue);

        if (rows.Count > 0)
            await _piPositionSnapshotRepo.SaveBatchAsync(scheduleRunId, planVersionId, rows, ct);
    }

    /// <summary>
    /// 把 5号位 计算结果映射为快照行，并做数量闭环校验（纯函数，可单测）。
    /// 校验规则（不修正事实，只登记）：
    ///   - 数量不闭合：Σ Position.Quantity != ErpRemainingQty（容差 0.0001）→ IssueCode=QUANTITY_GAP
    ///   - 计算失败：result.IsSuccess == false → IssueCode=POSITION_FAILED
    ///   - 位置缺失：inputs 有 PI 但计算器无结果 → 记 issue，不落行
    /// </summary>
    internal static (List<ProductionInstructionPositionSnapshot> Rows, List<string> Issues) MapPositionSnapshots(
        int scheduleRunId,
        int planVersionId,
        IReadOnlyList<ProductionInstructionPositionInput> inputs,
        IReadOnlyDictionary<string, ProductionInstructionPositionResult> resultsByPi,
        IReadOnlyDictionary<string, string> materialCodeByPi)
    {
        const decimal tolerance = 0.0001m;

        var rows = new List<ProductionInstructionPositionSnapshot>();
        var issues = new List<string>();

        foreach (var input in inputs)
        {
            if (!resultsByPi.TryGetValue(input.ProductionInstructionNo, out var result))
            {
                issues.Add($"PI {input.ProductionInstructionNo}: 位置缺失（计算器无结果）");
                continue;
            }

            materialCodeByPi.TryGetValue(input.ProductionInstructionNo, out var materialCode);

            var sum = result.Positions.Sum(p => p.Quantity);
            var hasClosureGap = Math.Abs(sum - input.ErpRemainingQty) > tolerance;
            var isFailed = !result.IsSuccess;

            if (hasClosureGap)
                issues.Add($"PI {input.ProductionInstructionNo}: 数量不闭合 ΣPosition={sum} vs ERP RemainingQty={input.ErpRemainingQty}");
            else if (isFailed)
                issues.Add($"PI {input.ProductionInstructionNo}: 计算失败 {result.FailureReason}");

            var issueCode = hasClosureGap ? "QUANTITY_GAP"
                          : isFailed      ? "POSITION_FAILED"
                          : null;

            foreach (var slice in result.Positions)
            {
                rows.Add(new ProductionInstructionPositionSnapshot
                {
                    ScheduleRunId = scheduleRunId,
                    PlanVersionId = planVersionId,
                    ProductionInstructionNo = input.ProductionInstructionNo,
                    MaterialId = input.MaterialId,
                    MaterialCode = materialCode ?? string.Empty,
                    PositionType = slice.PositionType.ToString(),
                    Quantity = slice.Quantity,
                    CurrentStageCode = slice.StageCode,
                    NextStageCode = null,
                    AvailableTime = slice.AvailableTime,
                    SourceType = null,
                    SourceKey = slice.SourceKey,
                    IssueCode = issueCode,
                    Confidence = null
                });
            }
        }

        return (rows, issues);
    }

    /// <summary>
    /// 装载 PI 级库存事实（PiInventory）。
    ///
    /// 结构：ext_ERP_Inventory_View（MaterialCode/FactoryCode/WarehouseCode=6位工序码/Quantity）
    ///       LEFT JOIN ext_MES_ProcessCode_View（ProcessCode→StageCode）。
    /// LocationCategory 由 2号位按映射结果判定：
    ///   - 命中 StageCode → STAGE_INVENTORY（RelatedStageCode=StageCode）
    ///   - 未命中 → UNKNOWN（RelatedStageCode=null，由计算器 UNLOCATED 兜底）
    /// 返回 (MaterialCode, FactoryCode) → 事实列表，供按 PI 归属装载。
    /// </summary>
    private async Task<IReadOnlyDictionary<(string MaterialCode, string FactoryCode), List<PiInventoryFact>>>
        LoadPiInventoryFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var factoryCodes  = wipStageRows.Select(r => r.FactoryCode).Distinct().ToList();

        var rows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<PiInventoryLoadRow>(
                @"SELECT d.MaterialCode,
                         fa.Code        AS FactoryCode,
                         d.StorageCode   AS WarehouseCode,
                         d.Quantity,
                         pc.StageCode
                  FROM InventoryAvailableSupplyDetail d
                  INNER JOIN Factory fa
                    ON fa.Id = d.FactoryId
                   AND fa.IsActive = 1
                  LEFT JOIN ext_MES_ProcessCode_View pc
                    ON pc.ProcessCode = d.StorageCode
                  WHERE d.Quantity > 0
                    AND d.MaterialCode IN @MaterialCodes
                    AND fa.Code         IN @FactoryCodes",
                new { MaterialCodes = chunk, FactoryCodes = factoryCodes },
                db: DatabaseId.APS, commandTimeout: 300));

        var map = new Dictionary<(string MaterialCode, string FactoryCode), List<PiInventoryFact>>();
        foreach (var row in rows)
        {
            var key = (row.MaterialCode, row.FactoryCode);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<PiInventoryFact>();
                map[key] = list;
            }

            list.Add(new PiInventoryFact
            {
                WarehouseCode = row.WarehouseCode,
                Quantity      = row.Quantity,
                AvailableTime = null,
                SourceDocument = null,
                RelatedStageCode = row.StageCode,
                LocationCategory = string.IsNullOrWhiteSpace(row.StageCode) ? "UNKNOWN" : "STAGE_INVENTORY"
            });
        }

        return map;
    }

    /// <summary>
    /// 装载 XC（线边仓）事实。
    /// 链：ext_MES_ProcessCode_View.ERPProperty='XC'（标记线边仓工序码）× ext_ERP_Inventory_View 库存。
    /// 返回 (MaterialCode, FactoryCode) → 事实列表，供按 PI 归属装载。
    /// </summary>
    private async Task<IReadOnlyDictionary<(string MaterialCode, string FactoryCode), List<XcFact>>>
        LoadXcFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var factoryCodes  = wipStageRows.Select(r => r.FactoryCode).Distinct().ToList();

        var rows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<XcLoadRow>(
                @"SELECT d.MaterialCode,
                         fa.Code        AS FactoryCode,
                         d.StorageCode   AS WarehouseCode,
                         d.Quantity,
                         pc.StageCode
                  FROM InventoryAvailableSupplyDetail d
                  INNER JOIN Factory fa
                    ON fa.Id = d.FactoryId
                   AND fa.IsActive = 1
                  INNER JOIN ext_MES_ProcessCode_View pc
                    ON pc.ProcessCode = d.StorageCode
                   AND pc.ERPProperty = 'XC'
                  WHERE d.Quantity > 0
                    AND d.MaterialCode IN @MaterialCodes
                    AND fa.Code         IN @FactoryCodes",
                new { MaterialCodes = chunk, FactoryCodes = factoryCodes },
                db: DatabaseId.APS, commandTimeout: 300));

        var map = new Dictionary<(string MaterialCode, string FactoryCode), List<XcFact>>();
        foreach (var row in rows)
        {
            var key = (row.MaterialCode, row.FactoryCode);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<XcFact>();
                map[key] = list;
            }

            list.Add(new XcFact
            {
                XcWarehouseCode = row.WarehouseCode,
                Quantity        = row.Quantity,
                RelatedStageCode = row.StageCode,
                AvailableTime   = null,
                SourceDocument  = null
            });
        }

        return map;
    }

    /// <summary>
    /// 装载跨厂边事实（CrossFactoryEdge）。
    /// 链：ext_MES_APS_BOM_Workset_CrossFactoryEdge（18374 行）。
    /// 归组键 = ChildMaterialCode（PI 物料作为子件、其产品跨厂转运的边）；
    /// 组内按 (From,To) 四元组去重后自编号 EdgeSequence（物理表无此列）。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<CrossFactoryEdgeFact>>>
        LoadCrossFactoryEdgesAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();

        var rows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<CrossFactoryEdgeLoadRow>(
                @"SELECT ChildMaterialCode,
                         FromStageCode,
                         FromFactoryCode,
                         ToStageCode,
                         ToFactoryCode
                  FROM ext_MES_APS_BOM_Workset_CrossFactoryEdge
                  WHERE ChildMaterialCode IN @MaterialCodes
                  ORDER BY ChildMaterialCode, BatchNo, WorksetId, Id",
                new { MaterialCodes = chunk },
                db: DatabaseId.APS));

        var map = new Dictionary<string, List<CrossFactoryEdgeFact>>();
        foreach (var group in rows.GroupBy(r => r.ChildMaterialCode))
        {
            var edges = new List<CrossFactoryEdgeFact>();
            var seen = new HashSet<(string FromStage, string FromFactory, string ToStage, string ToFactory)>();
            foreach (var row in group)
            {
                var tuple = (row.FromStageCode, row.FromFactoryCode, row.ToStageCode, row.ToFactoryCode);
                if (!seen.Add(tuple))
                    continue; // 多 BatchNo/WorksetId 的重复边去重

                edges.Add(new CrossFactoryEdgeFact
                {
                    FromStageCode   = row.FromStageCode,
                    FromFactoryCode = row.FromFactoryCode,
                    ToStageCode     = row.ToStageCode,
                    ToFactoryCode   = row.ToFactoryCode,
                    EdgeSequence    = edges.Count
                });
            }
            map[group.Key] = edges;
        }

        return map;
    }

    /// <summary>
    /// 将厂间在途事实按生产指示号精确归属到 PI（PM 0910 裁决 §十三，§11 Stage Handoff INTERPLANT_TRANSIT Position 强事实）。
    /// 归属键 = TransitDocumentNo（= SourceDocumentNo = PI No）；Material/目标厂仅做一致性校验，不得替代 PI 号归属（禁 Material+Factory 归组/按 PI 排序/按 RemainingQty 比例分摊）。
    /// 校验顺序（PM §十）：PI 号非空 → 命中唯一有效 PI → Material 一致 → 目标厂一致 → Quantity>0 → 归入该 PI 的 INTERPLANT_TRANSIT。
    /// 任一失败则该事实不作为已定位 Position 消费，记 Issue（TRANSIT_PI_*），对应剩余数量由 PI Position 闭合逻辑落入 UNLOCATED（不得删除 Supply / 转新增生产）。
    /// 同一 PI 多行聚合（PM §十一：TransitQty ≤ PI RemainingQty 由 5号位计算器闭合校验，不在此裁剪）。
    /// </summary>
    internal static (Dictionary<string, List<InterplantTransitFact>> ByPi, List<string> Issues)
        AttributeInterplantTransitToPi(
            IReadOnlyList<InterplantTransitFact> transits,
            IReadOnlyDictionary<string, (string MaterialCode, string FactoryCode)> piIdentities)
    {
        var byPi = new Dictionary<string, List<InterplantTransitFact>>(StringComparer.Ordinal);
        var issues = new List<string>();

        foreach (var t in transits)
        {
            var piNo = t.TransitDocumentNo;
            if (string.IsNullOrWhiteSpace(piNo))
            {
                issues.Add("TRANSIT_PI_NO_MISSING: 厂间在途事实缺少生产指示号（TransitDocumentNo 为空），无法归属 PI。");
                continue;
            }

            if (!piIdentities.TryGetValue(piNo, out var pi))
            {
                issues.Add($"TRANSIT_PI_NOT_FOUND: 在途单号 {piNo} 找不到对应 PI。");
                continue;
            }

            if (!string.Equals(t.MaterialCode, pi.MaterialCode, StringComparison.Ordinal))
            {
                issues.Add($"TRANSIT_PI_MATERIAL_MISMATCH: 在途单号 {piNo} 物料 {t.MaterialCode} ≠ PI 物料 {pi.MaterialCode}。");
                continue;
            }

            if (!string.Equals(t.TargetFactoryCode, pi.FactoryCode, StringComparison.Ordinal))
            {
                issues.Add($"TRANSIT_PI_FACTORY_MISMATCH: 在途单号 {piNo} 目标厂 {t.TargetFactoryCode} ≠ PI 工厂 {pi.FactoryCode}。");
                continue;
            }

            if (t.Quantity <= 0m)
            {
                issues.Add($"TRANSIT_PI_INVALID_QUANTITY: 在途单号 {piNo} 数量 {t.Quantity} ≤ 0。");
                continue;
            }

            if (!byPi.TryGetValue(piNo, out var list))
            {
                list = new List<InterplantTransitFact>();
                byPi[piNo] = list;
            }
            list.Add(t);
        }

        return (byPi, issues);
    }

    /// <summary>
    /// 装载厂间在途事实（Transit），仅 PI 级（§11 大工艺接续 Stage Handoff）。链：ext_ERP_InterplantInTransit_View（实测 0 行，待 5号位 ODS 数据）。
    /// 分流依据：SourceDocumentNo = [Order].OrderNo（PM 已确认 OrderNo 是唯一连接键）→ 仅取 OrderType=PRODUCTION_INSTRUCTION；
    /// OrderType=SALES_ORDER（含 CustomerSegment='跨厂' 的 SH）的在途 = 履行状态，走 INTER_FACTORY_ORDER 单一 Supply（量已并入 Order.Quantity），不进 PI Position。
    /// 返回扁平事实列表（不按键归组），由 AttributeInterplantTransitToPi 按 TransitDocumentNo（= PI No）精确归属（PM 0910 裁决，禁 Material+Factory 归组）。
    /// </summary>
    private async Task<IReadOnlyList<InterplantTransitFact>>
        LoadTransitFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var factoryCodes  = wipStageRows.Select(r => r.FactoryCode).Distinct().ToList();

        var rows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<TransitLoadRow>(
                @"SELECT t.MaterialCode,
                         t.FactoryCode,
                         t.SourceFactoryCode,
                         t.Quantity,
                         t.ETA,
                         t.ReleaseDate,
                         t.SourceDocumentNo,
                         o.OrderType
                  FROM ext_ERP_InterplantInTransit_View t
                  INNER JOIN [Order] o ON o.OrderNo = t.SourceDocumentNo
                  WHERE t.Quantity > 0
                    AND o.OrderType = 'PRODUCTION_INSTRUCTION'
                    AND t.MaterialCode IN @MaterialCodes
                    AND t.FactoryCode  IN @FactoryCodes",
                new { MaterialCodes = chunk, FactoryCodes = factoryCodes },
                db: DatabaseId.APS, commandTimeout: 300));

        var facts = new List<InterplantTransitFact>(rows.Count);
        foreach (var row in rows)
        {
            facts.Add(new InterplantTransitFact
            {
                TransitDocumentNo    = row.SourceDocumentNo,
                MaterialCode         = row.MaterialCode,
                SourceFactoryCode    = row.SourceFactoryCode,
                TargetFactoryCode    = row.FactoryCode,
                Quantity             = row.Quantity,
                EstimatedArrivalTime = row.Eta,
                SourceDocument       = row.SourceDocumentNo,
                ShippedAt            = row.ReleaseDate
            });
        }

        return facts;
    }

    /// <summary>厂间出荷指示（SH）单一供给事实：直接来自 [Order]（CustomerSegment='跨厂' 的 SALES_ORDER）。</summary>
    private sealed class InterFactoryShipmentFact
    {
        /// <summary>SH号 = OrderNo</summary>
        public string ShipmentNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public int MaterialId { get; set; }
        /// <summary>目标/到货厂代码（= Order.FactoryCode）</summary>
        public string TargetFactoryCode { get; set; } = string.Empty;
        /// <summary>目标/到货厂 Id（= Order.FactoryId）</summary>
        public int TargetFactoryId { get; set; }
        /// <summary>源/发货厂 Id（= Order.SourceFactoryId；装载链已就绪，重跑夜间编排器重载订单后落库）</summary>
        public int? SourceFactoryId { get; set; }
        /// <summary>指令总量（= Order.Quantity，整单一次入库）</summary>
        public decimal InstructionQty { get; set; }
    }

    /// <summary>
    /// 装载厂间出荷指示（INTER_FACTORY_ORDER，SH级）事实。
    /// v1.0 口径：SH 本身是 Order（OrderType=SALES_ORDER + CustomerSegment='跨厂'，OrderNo=SH号），
    /// 单一 Supply 身份，量=Order.Quantity（指令总量），不再从 Transit/Received 视图归并两段。
    /// </summary>
    private async Task<IReadOnlyList<InterFactoryShipmentFact>> LoadInterFactoryShipmentsAsync(CancellationToken ct)
    {
        var rows = await _connectionManager.QueryAsync<InterFactoryShipmentFact>(
            @"SELECT o.OrderNo         AS ShipmentNo,
                     m.MaterialCode,
                     o.MaterialId,
                     f.Code           AS TargetFactoryCode,
                     o.FactoryId      AS TargetFactoryId,
                     o.SourceFactoryId,
                     o.Quantity       AS InstructionQty
              FROM [Order] o
              INNER JOIN Material m ON m.Id = o.MaterialId
              INNER JOIN Factory  f ON f.Id = o.FactoryId
              WHERE o.CustomerSegment = N'跨厂'
                AND o.OrderType = 'SALES_ORDER'
                AND o.Quantity > 0",
            db: DatabaseId.APS);

        return rows.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routing 三件套 + 部门归属上下文装载（PM 裁定：最小 B）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按当前 Domain 涉及的 MaterialId 完整装载 Routing 三件套（不过滤部门——
    /// 部门锁定由 1号位消费 MaterialStageDepartmentContexts 时执行，见 ProductionDepartment回复.md）。
    /// 三件套缺失时 1号位将把对应新增生产需求判定为 Unscheduled。
    /// </summary>
    private async Task<(List<RoutingOperation> Operations, List<RoutingDependency> Dependencies, List<OperationResourceEligibility> Eligibility)>
        LoadRoutingContextAsync(IReadOnlyList<int> materialIds, CancellationToken ct)
    {
        if (materialIds.Count == 0)
            return (new List<RoutingOperation>(), new List<RoutingDependency>(), new List<OperationResourceEligibility>());

        // TransferBatchSize 冻结口径（DDL v5.1.4）：批量/规划参数从原 Routing 拆出到 RoutingPlanningParam，
        // 不污染工艺事实层 RoutingOperation。故此处 LEFT JOIN RoutingPlanningParam 取 TransferBatchSize，
        // 键 = (MaterialId, RouteCode, PathId, OperationCode)——RoutingPlanningParam 非部门维度（无 ProductionDepartmentId），
        // 同一工序跨部门共享同一 TransferBatchSize。无参数行时 TransferBatchSize=NULL，1号位按无阈值跳过。
        var operations = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingOperation>(
                @"SELECT ro.MaterialId, ro.ProductionDepartmentId, ro.RouteCode, ro.PathId, ro.OperationCode,
                         ro.OperationName, ro.ProcessType, ro.StageCode, ro.StandardDuration, ro.SetupTime,
                         rpp.TransferBatchSize, ro.IsActive
                  FROM RoutingOperation ro
                  LEFT JOIN RoutingPlanningParam rpp
                    ON rpp.MaterialId    = ro.MaterialId
                   AND rpp.RouteCode     = ro.RouteCode
                   AND rpp.PathId        = ro.PathId
                   AND rpp.OperationCode = ro.OperationCode
                  WHERE ro.IsActive = 1 AND ro.MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        var dependencies = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingDependency>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId,
                         FromOperationCode, ToOperationCode, DependencyType, LagTime, IsActive
                  FROM RoutingDependency
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        var eligibility = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<OperationResourceEligibility>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode,
                         ResourceId, Priority, CapacityFactor, IsPrimary, IsActive
                  FROM OperationResourceEligibility
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        return (operations, dependencies, eligibility);
    }

    /// <summary>
    /// ③ StartStageCode 填值（2026-09-11，5号位 O3 回复划归 2号位）：为每个新增生产需求回填
    /// 起点大工艺阶段码 = 该物料 Routing 有向图中「无入边源结点」工序的 StageCode（新生产从第一道工序起）。
    /// 无源结点 / 无 StageCode 时留空（与 1号位 PhaseTwo 未匹配兜底一致）。
    /// </summary>
    private static void FillStartStageCodes(
        PeggingResultVoucher voucher,
        List<RoutingOperation> operations,
        List<RoutingDependency> dependencies)
    {
        if (operations.Count == 0)
            return;

        // 无入边源结点 = OperationCode 不作任何 RoutingDependency.ToOperationCode 出现（该物料）
        var toNodesByMaterial = dependencies
            .GroupBy(d => d.MaterialId)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(d => d.ToOperationCode), StringComparer.Ordinal));

        var firstStageByMaterial = operations
            .Where(o => !string.IsNullOrEmpty(o.StageCode))
            .GroupBy(o => o.MaterialId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    toNodesByMaterial.TryGetValue(g.Key, out var toNodes);
                    return g.Where(o => toNodes == null || !toNodes.Contains(o.OperationCode))
                            .OrderBy(o => o.OperationCode, StringComparer.Ordinal)
                            .Select(o => o.StageCode!)
                            .FirstOrDefault() ?? string.Empty;
                });

        foreach (var demand in voucher.LogicalProductionDemands)
        {
            if (string.IsNullOrEmpty(demand.StartStageCode) &&
                firstStageByMaterial.TryGetValue(demand.MaterialId, out var startStage))
            {
                demand.StartStageCode = startStage;
            }
        }
    }

    /// <summary>
    /// 按当前 Domain 涉及的 MaterialId 裁剪 MaterialStageDeptContext（IsCurrent=1），
    /// 组装为 MaterialStageDepartmentContextDto 传入 1号位。只传 (MaterialId, StageCode, ProductionDepartmentId)，
    /// 不带 SourceType / SourceDetail / ValidFrom 等治理字段（1号位不需要）。
    /// </summary>
    private async Task<List<MaterialStageDepartmentContextDto>>
        LoadMaterialStageDeptContextAsync(IReadOnlyList<int> materialIds, CancellationToken ct)
    {
        if (materialIds.Count == 0)
            return new List<MaterialStageDepartmentContextDto>();

        var rows = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<MaterialStageDeptContextLoadRow>(
                @"SELECT MaterialId, StageCode, DefaultProductionDepartmentId
                  FROM MaterialStageDeptContext
                  WHERE IsCurrent = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        return rows
            .Select(r => new MaterialStageDepartmentContextDto
            {
                MaterialId = r.MaterialId,
                StageCode = r.StageCode,
                ProductionDepartmentId = r.DefaultProductionDepartmentId
            })
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BOM 快照装载
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 APS_BOM_RAW 加载 BOM 快照（按父件编码索引，供 PeggingLoop BOM 树遍历使用）
    ///
    /// 批次策略：优先取当前 PlanVersion 关联的 BatchNo（经由 OrderBomRequestLink）；
    /// 若关联缺失，兜底取最新 SyncedAt 批次（夜批顺序保证此批为最新）。
    /// </summary>
    private async Task<BomSnapshot> LoadBomSnapshotAsync(
        int planVersionId,
        CancellationToken ct)
    {
        var rows = (await _connectionManager.QueryAsync<BomRawRow>(
            @"SELECT b.ParentMaterialCode,
                     b.ChildMaterialCode,
                     ISNULL(mc.Id, 0)          AS ChildMaterialId,
                     b.Quantity,
                     b.Level,
                     b.LLC,
                     b.IsLeaf,
                     ISNULL(mc.IsPurchased, 0) AS IsPurchased,
                     b.ChildRequiredStageCode
              FROM APS_BOM_RAW b
              LEFT JOIN Material mc ON mc.MaterialCode = b.ChildMaterialCode
              WHERE b.BatchNo = ISNULL(
                  (SELECT TOP 1 r.BatchNo
                   FROM OrderBomRequestLink r
                   INNER JOIN [Order] o ON o.Id = r.OrderId
                   WHERE o.PlanVersionId = @PlanVersionId
                   ORDER BY r.SyncedAt DESC),
                  (SELECT TOP 1 BatchNo FROM APS_BOM_RAW ORDER BY SyncedAt DESC)
              )",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS)).ToList();

        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging] APS_BOM_RAW 无数据（PlanVersionId={PlanVersionId}），BOM 快照为空",
                planVersionId);
            return new BomSnapshot(
                Enumerable.Empty<BomEdge>().ToLookup(e => e.ParentCode),
                new Dictionary<string, int>(),
                new Dictionary<string, bool>(),
                0);
        }

        var edges = rows.Select(r => new BomEdge(
            r.ParentMaterialCode,
            r.ChildMaterialCode,
            r.ChildMaterialId,
            r.Quantity,
            r.Level,
            r.IsLeaf,
            r.IsPurchased,
            r.ChildRequiredStageCode)).ToList();

        // LLC 取各物料在所有 BOM 路径中出现的最小值
        var llcByMaterial = rows
            .Where(r => r.LLC.HasValue)
            .GroupBy(r => r.ChildMaterialCode)
            .ToDictionary(g => g.Key, g => g.Min(r => r.LLC!.Value));

        // IsPurchased 按物料编码分组（每个物料只有一个IsPurchased值）
        var isPurchasedByMaterial = rows
            .GroupBy(r => r.ChildMaterialCode)
            .ToDictionary(g => g.Key, g => g.First().IsPurchased);

        return new BomSnapshot(edges.ToLookup(e => e.ParentCode), llcByMaterial, isPurchasedByMaterial, edges.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 订单装载（PeggingLoop 前置步骤）
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class OrderPeggingRow
    {
        public long     OrderId          { get; set; }
        public int      MaterialId       { get; set; }
        public string   MaterialCode     { get; set; } = string.Empty;
        public int      FactoryId        { get; set; }
        public string   FactoryCode      { get; set; } = string.Empty;
        public string?  OrderType        { get; set; }
        public string?  CustomerTier     { get; set; }
        public DateTime? IssueDate       { get; set; }
        public decimal  DemandQty        { get; set; }
        public DateTime DueDate          { get; set; }
        public string   UOM              { get; set; } = string.Empty;
        public int?     ProductFamilyId  { get; set; }
        public string?  MTS_InstructionNo { get; set; }
    }

    private async Task<IReadOnlyList<OrderPeggingRow>> LoadOrdersForPeggingAsync(
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var rows = await QueryChunkedInAsync(request.OrderIds, chunk =>
            _connectionManager.QueryAsync<OrderPeggingRow>(
                @"SELECT o.Id          AS OrderId,
                         o.MaterialId,
                         m.MaterialCode,
                         o.FactoryId,
                         f.Code        AS FactoryCode,
                         o.OrderType,
                         o.CustomerTier,
                         o.IssueDate,
                         o.Quantity    AS DemandQty,
                         o.CustomerDueDate AS DueDate,
                         o.UOM,
                         m.ProductFamilyId,
                         o.MTS_InstructionNo
                  FROM [Order] o
                  INNER JOIN Material m ON m.Id = o.MaterialId
                  INNER JOIN Factory  f ON f.Id = o.FactoryId
                  WHERE o.Id IN @OrderIds",
                new { OrderIds = chunk },
                db: DatabaseId.APS));

        return rows.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PeggingLoop 主逻辑
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BOM 遍历 + 供给扣减主函数。
    /// 每笔订单从根物料出发递归展开 BOM 树，在每个节点对 SupplyPool 执行贪婪扣减。
    /// 扣减结果累积到同一 PeggingResultVoucher。
    /// </summary>
    private async Task<PeggingResultVoucher> ExecutePeggingLoopAsync(
        PeggingExecutionRequest request,
        BomSnapshot bom,
        SupplyPool supplyPool,
        CancellationToken ct)
    {
        var orders = await LoadOrdersForPeggingAsync(request, ct);

        // ── Demand 优先级排序（2号位消费3号位 DemandPriorityConfig）──
        // 位置：LoadOrdersForPeggingAsync 之后、Pegging 循环之前（PM 冻结口径，不进 SQL 排序）
        // 结果：OrderId → DemandSequence，决定订单处理顺序，并透传给 LogicalProductionDemand
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);

        // 主链切新 BFS（2026-09-18 用户裁决：旧 DFS 无记忆化在真实 BOM 上 10 单/20 分钟不收敛，弃旧计算）：
        // 分配层 O(订单×物料×工厂去重数) + 共享子件合一 LPD；旧 RunDfsLoop 仅留 RunDualCompareAsync 对照用。
        var bomStructure = ToBomStructure(bom);
        return RunBfsLoop(orders, demandSequenceByOrder, request, bom, bomStructure, supplyPool, ct, out _);
    }

    /// <summary>
    /// 旧主链 DFS 循环（阶段2 S2.3 从 ExecutePeggingLoopAsync 原封抽出，语义零变化）：
    /// 按 DemandSequence 升序逐订单从根物料递归展开 BOM，在每个节点对 SupplyPool 贪婪扣减。
    /// RunDualCompareAsync 据此跑「旧」侧；待双跑通过（PM 门槛2）后本方法即被 RunBfsLoop 替换主链。
    /// </summary>
    private PeggingResultVoucher RunDfsLoop(
        IReadOnlyList<OrderPeggingRow> orders,
        IReadOnlyDictionary<long, int> demandSequenceByOrder,
        PeggingExecutionRequest request,
        BomSnapshot bom,
        SupplyPool supplyPool,
        CancellationToken ct,
        out BomTraversalStats traversalStats)
    {
        var voucher = BuildResultVoucher(request, orders);

        // 按 DemandSequence 升序遍历：优先级高的订单先抢供给
        var orderedOrders = orders
            .OrderBy(o => demandSequenceByOrder.GetValueOrDefault(o.OrderId, int.MaxValue))
            .ToList();

        var orderIdx = 0;
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        traversalStats = new BomTraversalStats();
        foreach (var order in orderedOrders)
        {
            ct.ThrowIfCancellationRequested();
            orderIdx++;
            if (orderIdx == 1 || orderIdx % 25 == 0)
                _logger.LogInformation("[Pegging][进度] 回路 {Idx}/{Total} OrderId={OrderId} 物料={Material} 累计={Elapsed}ms",
                    orderIdx, orderedOrders.Count, order.OrderId, order.MaterialCode, loopSw.ElapsedMilliseconds);

            var demandSequence = demandSequenceByOrder.GetValueOrDefault(order.OrderId, 0);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            _ = TraverseBomNode(
                order,
                order.MaterialCode,
                order.MaterialId,
                order.FactoryId,
                order.FactoryCode,
                order.DemandQty,
                bomLevel: 0,
                demandSequence: demandSequence,
                bom,
                supplyPool,
                voucher,
                visited,
                traversalStats: traversalStats);
        }

        _logger.LogInformation(
            "[Pegging][BOM埋点] 展开总次数={Total} 唯一节点={Unique} 重复展开={Dup} 最大共享节点={MaxMat}(访问{MaxCnt}) 订单={Orders} 总耗时={Elapsed}ms 平均单节点={Avg:F3}ms",
            traversalStats.TotalVisits,
            traversalStats.UniqueNodes,
            traversalStats.DuplicateExpansions,
            traversalStats.MaxSharedMaterialCode,
            traversalStats.MaximumVisits,
            orderedOrders.Count,
            loopSw.ElapsedMilliseconds,
            traversalStats.TotalVisits == 0 ? 0d : (double)loopSw.ElapsedMilliseconds / traversalStats.TotalVisits);

        voucher.IsFullyAllocated = voucher.ShortageQuantity == 0;
        voucher.ExecutionTimeMs = loopSw.ElapsedMilliseconds;
        return voucher;
    }

    /// <summary>构造结果凭证头（阶段2 S2.3 双跑复用：旧 DFS 与新 BFS 各自独立凭证，其余字段由扣减过程填充）。</summary>
    private static PeggingResultVoucher BuildResultVoucher(
        PeggingExecutionRequest request,
        IReadOnlyList<OrderPeggingRow> orders)
    {
        var firstOrder = orders.FirstOrDefault();
        return new PeggingResultVoucher
        {
            PlanVersionId    = request.PlanVersionId,
            DomainKey        = request.DomainKey,
            OrderId          = firstOrder?.OrderId ?? request.OrderIds.FirstOrDefault(),
            DemandMaterialId = firstOrder?.MaterialId ?? 0,
            UOM              = firstOrder?.UOM ?? string.Empty,
            IsSuccess        = true,
            ExecutedAt       = DateTime.Now
        };
    }

    /// <summary>
    /// BomSnapshot → BomStructure 适配（阶段2 S2.3）：把旧主链的 BOM 快照转成 Explosion 纯函数层的结构。
    /// 同 (parent → child) 多条 BOM 原始行在此合并求和（配比累加），对齐「干净 BOM」语义；旧 DFS 会逐行各递归一次，
    /// 新去重结构父边唯一——两者仅在脏数据（同父同子重复 BOM 行）下分离，正常数据等价。
    /// </summary>
    private static BomStructure ToBomStructure(BomSnapshot bom) => new(
        bom.ByParent.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<BomComponent>)g
                .GroupBy(e => e.ChildCode, StringComparer.Ordinal)
                .Select(grp => new BomComponent(grp.Key, grp.First().ChildMaterialId, grp.Sum(e => e.Qty)))
                .ToList(),
            StringComparer.Ordinal),
        bom.IsPurchasedByMaterial);

    /// <summary>
    /// 新 BFS 逐层主流程（阶段2 S2.3，PM 0918-3 §二 批准的「宽度优先逐层 + 每(order,material)同一计算上下文一次净额」）。
    /// 不切主链，仅作双跑对照。与旧 DFS 的等价性见方案 §三.3；核心差异：
    ///   a) 每 (order, material, factory) 恰一次 NetAndAllocate（共享子件不再指数重复展开）；
    ///   b) 共享子件生产合并为一张 LPD（碎片 300+300 → 一张 600）；
    ///   c) 血缘边由本层按 ParentEdges 逐边建（一个节点多父 → 多条 Consumer→Producer 边，RequiredQty=父净×配比）。
    /// </summary>
    private PeggingResultVoucher RunBfsLoop(
        IReadOnlyList<OrderPeggingRow> orders,
        IReadOnlyDictionary<long, int> demandSequenceByOrder,
        PeggingExecutionRequest request,
        BomSnapshot bom,
        BomStructure bomStructure,
        SupplyPool supplyPool,
        CancellationToken ct,
        out BomTraversalStats traversalStats)
    {
        var voucher = BuildResultVoucher(request, orders);
        var explosion = new BomExplosionService();

        var orderedOrders = orders
            .OrderBy(o => demandSequenceByOrder.GetValueOrDefault(o.OrderId, int.MaxValue))
            .ToList();

        var orderIdx = 0;
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        traversalStats = new BomTraversalStats();
        foreach (var order in orderedOrders)
        {
            ct.ThrowIfCancellationRequested();
            orderIdx++;
            if (orderIdx == 1 || orderIdx % 25 == 0)
                _logger.LogInformation("[Pegging][BFS进度] 回路 {Idx}/{Total} OrderId={OrderId} 物料={Material} 累计={Elapsed}ms",
                    orderIdx, orderedOrders.Count, order.OrderId, order.MaterialCode, loopSw.ElapsedMilliseconds);

            var demandSequence = demandSequenceByOrder.GetValueOrDefault(order.OrderId, 0);
            var root = new BomOrderDemand(order.OrderId, order.MaterialId, order.MaterialCode, order.FactoryId, order.DemandQty);
            var nodes = explosion.ExplodeOrderStructure(bomStructure, root);

            // produced：本订单内 (material|factory) → 净额承接结果（父净驱动的源量）。
            // 合并 Key = 订单(循环外层) + material + factory；生产上下文(Stage/Domain/必要Routing)为扩展点
            // （PM 0918-3 §二：不得只按 MaterialId 合并；当前数据单订单内 factory 唯一，预留扩展位）。
            var produced = new Dictionary<string, NetAllocationOutcome>(StringComparer.Ordinal);
            var maxLevel = nodes.Count == 0 ? 0 : nodes.Max(n => n.Level);
            for (var level = 0; level <= maxLevel; level++)
            {
                foreach (var node in nodes.Where(n => n.Level == level))
                {
                    traversalStats.Record(node.MaterialCode, node.FactoryId);

                    // 下级依赖需求 = Σ 父件净产出 × 配比（等效旧 L3800 AllocatedQty × edge.Qty）
                    decimal gross = level == 0
                        ? order.DemandQty
                        : node.ParentEdges.Sum(e =>
                            produced.TryGetValue(SupplyPool.BuildKey(e.ParentMaterialCode, node.FactoryId), out var p)
                                ? p.ProducedQty * e.QtyPerUnit
                                : 0m);

                    var outcome = NetAndAllocate(
                        order, node.MaterialCode, node.MaterialId, node.FactoryId, order.FactoryCode,
                        gross, level, demandSequence, bom, supplyPool, voucher,
                        consumerLogicalDemandKey: null, consumerMaterialId: 0);

                    if (outcome is null) continue;                    // 无生产（真实供给补足/采购占位/失败）：不下钻、无血缘
                    if (outcome.ProducerLogicalDemandKey is null) continue; // 防御：NEW_REQUIREMENT 生产恒有 LPD 身份
                    produced[SupplyPool.BuildKey(node.MaterialCode, node.FactoryId)] = outcome;

                    // 血缘建边（父 → 本物料生产）：每条父边独立，RequiredQty = 父净产出 × 配比。
                    // 与旧 DFS 差异：旧在 NetAndAllocate 内建「单 consumer」边；BFS 一个节点多父，在此按 ParentEdges 逐边建。
                    foreach (var edge in node.ParentEdges)
                    {
                        if (!produced.TryGetValue(SupplyPool.BuildKey(edge.ParentMaterialCode, node.FactoryId), out var parent))
                            continue;
                        voucher.MaterialRequirementLinks.Add(new Core.Dto.MaterialRequirementLink
                        {
                            ConsumerLogicalDemandKey   = parent.ProducerLogicalDemandKey!,
                            ProducerLogicalDemandKey   = outcome.ProducerLogicalDemandKey!,
                            ConsumerMaterialId         = edge.ParentMaterialId,
                            ProducerMaterialId         = node.MaterialId,
                            RequiredQty                = parent.ProducedQty * edge.QtyPerUnit,
                            ProducerAllocationSequence = outcome.ProducerAllocationSequence
                        });
                    }
                }
            }
        }

        _logger.LogInformation(
            "[Pegging][BFS埋点] 展开总次数={Total} 唯一节点={Unique} 重复展开={Dup} 订单={Orders} 总耗时={Elapsed}ms",
            traversalStats.TotalVisits,
            traversalStats.UniqueNodes,
            traversalStats.DuplicateExpansions,
            orderedOrders.Count,
            loopSw.ElapsedMilliseconds);

        voucher.IsFullyAllocated = voucher.ShortageQuantity == 0;
        voucher.ExecutionTimeMs = loopSw.ElapsedMilliseconds;
        return voucher;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 阶段2 S2.3 双跑对照（不切主链）：旧 DFS vs 新 BFS，产出 PM 0918-3 §八 7 项 + §六 I1-I4 报告
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="IPeggingOrchestrator.RunDualCompareAsync" />
    public async System.Threading.Tasks.Task<DualRunReport> RunDualCompareAsync(
        PeggingExecutionRequest request,
        CancellationToken ct = default)
    {
        // 同输入：订单 / DemandSequence / BOM 快照 / 冻结策略快照 / 供给池（含硬锁）
        var orders = await LoadOrdersForPeggingAsync(request, ct);
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);
        var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, ct);

        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            throw new InvalidOperationException(
                "双跑策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
        var frozenSnapshot = await _frozenStrategySnapshotProvider
            .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, ct);

        var (supplyPool, _) = await LoadSupplyPoolAsync(request, frozenSnapshot, ct);
        var bomStructure = ToBomStructure(bomSnapshot);

        // 旧 DFS / 新 BFS 各自独立供给账本（Clone 隔离），记录耗时与展开统计
        var oldSw = Stopwatch.StartNew();
        var oldVoucher = RunDfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, supplyPool, ct, out var oldStats);
        oldSw.Stop();

        var newSw = Stopwatch.StartNew();
        var newVoucher = RunBfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, bomStructure, supplyPool.Clone(), ct, out var newStats);
        newSw.Stop();

        var report = ComparePeggingVouchers(oldVoucher, newVoucher, request, orders.Count) with
        {
            OldTraversalVisits = oldStats.TotalVisits,
            NewTraversalVisits = newStats.TotalVisits,
            OldMs              = oldSw.ElapsedMilliseconds,
            NewMs              = newSw.ElapsedMilliseconds
        };

        LogDualRunReport(report);
        return report;
    }

    /// <summary>
    /// 主链 BFS 单跑（只读，不落库不 Solver）：仅跑 RunBfsLoop，验证新 BFS 展开耗时 + I1/I2 红线校验（ValidatePeggingResult）。
    /// 用户 2026-09-18 裁决弃旧 DFS（真实 BOM 上 10 单/20 分钟不收敛）、主链切 BFS 后，作为快速验收入口。
    /// </summary>
    public async System.Threading.Tasks.Task<BfsRunReport> RunBfsOnlyAsync(
        PeggingExecutionRequest request,
        CancellationToken ct = default)
    {
        var orders = await LoadOrdersForPeggingAsync(request, ct);
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);
        var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, ct);

        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            throw new InvalidOperationException(
                "BFS 单跑策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
        var frozenSnapshot = await _frozenStrategySnapshotProvider
            .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, ct);

        var (supplyPool, _) = await LoadSupplyPoolAsync(request, frozenSnapshot, ct);
        var bomStructure = ToBomStructure(bomSnapshot);

        var sw = Stopwatch.StartNew();
        var voucher = RunBfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, bomStructure, supplyPool, ct, out var stats);
        sw.Stop();

        var redLineErrors = ValidatePeggingResult(supplyPool, voucher);

        return new BfsRunReport
        {
            PlanVersionId       = request.PlanVersionId,
            OrderCount          = orders.Count,
            RedLineErrors       = redLineErrors,
            LpdCount            = voucher.LogicalProductionDemands.Count,
            AllocationCount     = voucher.SupplyAllocations.Count,
            LineageCount        = voucher.MaterialRequirementLinks.Count,
            DemandQuantity      = voucher.DemandQuantity,
            ShortageQuantity    = voucher.ShortageQuantity,
            TraversalVisits     = stats.TotalVisits,
            UniqueNodes         = stats.UniqueNodes,
            DuplicateExpansions = stats.DuplicateExpansions,
            ElapsedMs           = sw.ElapsedMilliseconds
        };
    }

    /// <summary>双跑逐维比对（纯函数）：I1-I3 + Δ1-Δ3。I4 恒成立；Δ4 与 Task 影响由 DeferredToPosition1 标注待 1号位。</summary>
    private static DualRunReport ComparePeggingVouchers(
        PeggingResultVoucher oldVoucher,
        PeggingResultVoucher newVoucher,
        PeggingExecutionRequest request,
        int orderCount)
    {
        // I1：NEW_REQUIREMENT 按 DemandKey 聚合净产出（旧碎片 N 笔、新 1 笔，聚合后总量应相等）
        var oldNet = AggregateByDemandKey(oldVoucher, SupplySourceType.NEW_REQUIREMENT);
        var newNet = AggregateByDemandKey(newVoucher, SupplySourceType.NEW_REQUIREMENT);
        var netDiffs = DiffMaps(oldNet, newNet);

        // I2：真实供给（非 NEW_REQUIREMENT / 规划占位）按物理供给身份聚合
        var oldSupply = AggregateByPhysicalSupply(oldVoucher);
        var newSupply = AggregateByPhysicalSupply(newVoucher);
        var supplyDiffs = DiffMaps(oldSupply, newSupply);

        // I2-PI：PI 承接（WIP + PRODUCTION_INSTRUCTION）
        var oldPi = AggregateByPhysicalSupply(oldVoucher, SupplySourceType.WIP, SupplySourceType.PRODUCTION_INSTRUCTION);
        var newPi = AggregateByPhysicalSupply(newVoucher, SupplySourceType.WIP, SupplySourceType.PRODUCTION_INSTRUCTION);
        var piDiffs = DiffMaps(oldPi, newPi);

        // I3：血缘边集 (Consumer, Producer, RequiredQty) 多集比对
        var lineageMismatches = CompareLineage(oldVoucher, newVoucher);

        return new DualRunReport
        {
            PlanVersionId      = request.PlanVersionId,
            OrderCount         = orderCount,
            NetOutputClosed    = netDiffs.Count == 0,
            NetOutputDiffs     = netDiffs,
            SupplyClosed       = supplyDiffs.Count == 0,
            SupplyDiffs        = supplyDiffs,
            PiClosed           = piDiffs.Count == 0,
            PiDiffs            = piDiffs,
            LineageClosed      = lineageMismatches.Count == 0,
            LineageMismatches  = lineageMismatches,
            OldLpdCount        = oldVoucher.LogicalProductionDemands.Count,
            NewLpdCount        = newVoucher.LogicalProductionDemands.Count,
            OldAllocationCount = oldVoucher.SupplyAllocations.Count,
            NewAllocationCount = newVoucher.SupplyAllocations.Count,
            OldDemandQuantity  = oldVoucher.DemandQuantity,
            NewDemandQuantity  = newVoucher.DemandQuantity,
            DeferredToPosition1 = new[]
            {
                "AllocationTaskShare 差异：1号位 PhaseFive 派生，2号位 Pegging 段无产物，待排程后对比",
                "Continuation 差异：1号位 排程后份额切片，2号位 Pegging 段无产物，待排程后对比",
                "Task 数量与资源负荷影响分析：PM 0918-3 §风险2 已判属 1号位 阶段",
                "I4 PI Position 最终一致性：2号位未触碰装载，待 1号位 bitmap 级确认"
            }
        };
    }

    private static IReadOnlyDictionary<string, decimal> AggregateByDemandKey(
        PeggingResultVoucher voucher, params SupplySourceType[] sourceTypes)
    {
        var types = sourceTypes.ToHashSet();
        return voucher.SupplyAllocations
            .Where(a => types.Contains(a.SourceType))
            .GroupBy(a => a.DemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, decimal> AggregateByPhysicalSupply(
        PeggingResultVoucher voucher, params SupplySourceType[] sourceTypes)
    {
        var types = sourceTypes.Length == 0 ? null : sourceTypes.ToHashSet();
        return voucher.SupplyAllocations
            .Where(a => a.SourceType != SupplySourceType.NEW_REQUIREMENT
                     && a.SourceType != SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER
                     && (types == null || types.Contains(a.SourceType)))
            .GroupBy(a => PhysicalSupplyKey(a), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);
    }

    private static string PhysicalSupplyKey(Core.Dto.SupplyAllocationItem a)
        => $"{a.SourceType}|{a.SupplyMaterialId}|{a.FactoryCode}|{a.SupplySourceId?.ToString() ?? "-"}";

    private static IReadOnlyList<DualRunDiff> DiffMaps(
        IReadOnlyDictionary<string, decimal> oldMap,
        IReadOnlyDictionary<string, decimal> newMap)
    {
        var diffs = new List<DualRunDiff>();
        foreach (var kv in oldMap)
        {
            var newQty = newMap.TryGetValue(kv.Key, out var n) ? n : 0m;
            if (kv.Value != newQty)
                diffs.Add(new DualRunDiff(kv.Key, kv.Value, newQty));
        }
        foreach (var kv in newMap)
            if (!oldMap.ContainsKey(kv.Key))
                diffs.Add(new DualRunDiff(kv.Key, 0m, kv.Value));
        return diffs;
    }

    private static IReadOnlyList<string> CompareLineage(
        PeggingResultVoucher oldVoucher,
        PeggingResultVoucher newVoucher)
    {
        var oldEdges = LineageMultiset(oldVoucher);
        var newEdges = LineageMultiset(newVoucher);
        var mismatches = new List<string>();
        foreach (var (key, count) in oldEdges)
        {
            var newCount = newEdges.TryGetValue(key, out var n) ? n : 0;
            if (count != newCount)
                mismatches.Add($"边({key})旧={count}新={newCount}");
        }
        foreach (var key in newEdges.Keys)
            if (!oldEdges.ContainsKey(key))
                mismatches.Add($"边({key})仅新侧存在");
        return mismatches;
    }

    /// <summary>血缘边多集：键 = ConsumerMaterialId|ProducerMaterialId|RequiredQty，值 = 出现次数（供同边重复折叠比对）。</summary>
    private static IReadOnlyDictionary<string, int> LineageMultiset(PeggingResultVoucher voucher)
        => voucher.MaterialRequirementLinks
            .GroupBy(l => $"{l.ConsumerMaterialId}|{l.ProducerMaterialId}|{l.RequiredQty}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private void LogDualRunReport(DualRunReport r)
    {
        _logger.LogInformation(
            "[Pegging][双跑] PlanVersionId={Pv} 订单={Orders} I1净产出={I1} I2供给={I2} PI承接={Pi} I3血缘={I3} I4_PIPos={I4} PASS={Pass}",
            r.PlanVersionId, r.OrderCount, r.NetOutputClosed, r.SupplyClosed, r.PiClosed, r.LineageClosed, r.PiPositionClosed, r.Pass);
        _logger.LogInformation(
            "[Pegging][双跑] Δ1 LPD {OldLpd}->{NewLpd} | Δ2 Allocation笔数 {OldAlloc}->{NewAlloc} | Δ3 DemandQuantity {OldDq}->{NewDq} | 重复展开 {OldVisits}->{NewVisits} | 耗时 {OldMs}ms->{NewMs}ms",
            r.OldLpdCount, r.NewLpdCount, r.OldAllocationCount, r.NewAllocationCount,
            r.OldDemandQuantity, r.NewDemandQuantity, r.OldTraversalVisits, r.NewTraversalVisits, r.OldMs, r.NewMs);
        foreach (var d in r.NetOutputDiffs)
            _logger.LogWarning("[Pegging][双跑][I1差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var d in r.SupplyDiffs)
            _logger.LogWarning("[Pegging][双跑][I2差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var d in r.PiDiffs)
            _logger.LogWarning("[Pegging][双跑][PI差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var m in r.LineageMismatches)
            _logger.LogWarning("[Pegging][双跑][I3差异] {Msg}", m);
    }

    /// <summary>
    /// 将订单转换为 UpstreamDemand，消费3号位 DemandPriorityConfig 排序，返回 OrderId → DemandSequence 映射。
    /// </summary>
    private async Task<Dictionary<long, int>> BuildDemandSequenceMapAsync(
        IReadOnlyList<OrderPeggingRow> orders,
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var demands = orders.Select(o => new UpstreamDemand
        {
            DemandKey    = o.OrderId.ToString(),
            OrderType    = o.OrderType,
            CustomerTier = o.CustomerTier,
            DueDate      = o.DueDate,
            IssueDate    = o.IssueDate,
            // DelayStatus / ProtectionStatus：Order 表暂无对应列，保持 null（不造假），待5号位事实标准化后接入
            SourceDemand = o
        }).ToList();

        // 4.1：策略版本必须取自本 Run 冻结上下文，不再固定传 0；缺失即视为运行上下文不完整，禁止静默回退 Fixture
        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
        {
            throw new InvalidOperationException(
                "DemandPriority 策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退 Fixture。");
        }

        var config = await _demandPriorityConfigProvider.GetPriorityConfigAsync(strategyProfileVersionId.Value, ct);

        // 方案A：外部按 CalculationLayer 调用 Executor —— 只对「第一层：顶层独立需求（订单）」取当前层 Segments
        const int currentCalculationLayer = 1;
        var layerConfig = new DemandPriorityConfig
        {
            Segments = config.Segments
                .Where(s => s.CalculationLayer == currentCalculationLayer)
                .ToList()
        };

        var sorted = _demandPriorityExecutor.ExecutePrioritySort(demands, layerConfig);

        var map = new Dictionary<long, int>();
        foreach (var demand in sorted)
        {
            if (long.TryParse(demand.DemandKey, out var orderId))
            {
                map[orderId] = demand.DemandSequence;
            }
        }

        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V1.2 原子 Allocation 机制（§5.3）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 原子分配结果
    /// </summary>
    private sealed class AllocationResult
    {
        public bool Success { get; init; }
        public decimal AllocatedQty { get; init; }
        public long AllocationSequence { get; init; }
        public string? FailureReason { get; init; }
        public AllocationRecord? Record { get; init; }

        public static AllocationResult Succeeded(decimal qty, long seq, AllocationRecord record) =>
            new() { Success = true, AllocatedQty = qty, AllocationSequence = seq, Record = record };

        public static AllocationResult Failed(string reason) =>
            new() { Success = false, FailureReason = reason };
    }

    /// <summary>
    /// V1.2 原子 Allocation 机制（§5.3）：9步原子动作
    ///
    /// 一笔供需Allocation成功时，必须在同一内存动作中完成：
    /// 1. 校验Demand还有余额
    /// 2. 校验Supply还有余额
    /// 3. 校验资格（Eligibility，当前版本暂不实现，待5号位规则引擎接入）
    /// 4. 校验Strict Binding
    /// 5. 校验Demand Protection
    /// 6. 校验Execution不可逆事实
    /// 7. 扣DemandBalance
    /// 8. 扣SupplyBalance
    /// 9. 此时生成AllocationSequence
    /// 10. 生成逻辑Allocation/LedgerEntry
    ///
    /// 任何一步失败：Demand/Supply余额均不得部分修改（通过在所有校验通过后才执行扣减实现原子性）
    /// </summary>
    private static AllocationResult TryAtomicAllocation(
        SupplyLedgerEntry supply,
        DemandBalance demand,
        int bomLevel,
        PeggingResultVoucher voucher,
        decimal requestedQty)
    {
        // ══════════════════════════════════════════════════════════════════════
        // 第一阶段：校验（所有校验必须通过才能进入扣减阶段）
        // ══════════════════════════════════════════════════════════════════════

        // Step 1: 校验Demand还有余额
        if (demand.RemainingQty <= 0m)
            return AllocationResult.Failed("Demand has no remaining balance");

        // Step 2: 校验Supply还有余额
        if (supply.RemainingQty <= 0m)
            return AllocationResult.Failed("Supply has no remaining balance");

        // 计算本次分配数量 = Min(供应余额, 需求余额, 请求数量)
        var allocQty = Math.Min(Math.Min(supply.RemainingQty, demand.RemainingQty), requestedQty);

        if (allocQty <= 0m)
            return AllocationResult.Failed("Calculated allocation quantity is zero");

        // Step 3: 校验资格（Eligibility）
        if (!ValidateEligibility(supply, demand))
            return AllocationResult.Failed($"Eligibility violation: Supply {supply.SupplyKey} (material {supply.MaterialId}) cannot serve demand (material {demand.MaterialId})");

        // Step 4: 校验Strict Binding
        if (!ValidateStrictBinding(supply, demand.CurrentOrderId, demand.DemandKey))
            return AllocationResult.Failed($"Strict Binding violation: Supply {supply.SupplyKey} is locked to another demand");

        // Step 5: 校验Demand Protection
        if (!ValidateDemandProtection(supply, demand.CurrentOrderId, demand.DemandKey))
            return AllocationResult.Failed($"Demand Protection violation: Supply {supply.SupplyKey} cannot be used for this demand");

        // Step 6: 校验Execution不可逆事实（Execution Lock）
        // Execution Lock表示供给已被不可逆地消耗（如已投料、已发货），不得再分配
        var executionLock = supply.Locks.FirstOrDefault(l => l.LockType == LockType.EXECUTION);
        if (executionLock != null)
            return AllocationResult.Failed($"Execution Lock violation: Supply {supply.SupplyKey} has been irreversibly consumed");

        // ══════════════════════════════════════════════════════════════════════
        // 第二阶段：原子扣减（所有校验通过，开始修改状态）
        // ══════════════════════════════════════════════════════════════════════

        // Step 7: 扣DemandBalance（§5.2要求：使用DemandBalance对象维护需求余额）
        demand.RemainingQty -= allocQty;

        // Step 8: 扣SupplyBalance
        supply.RemainingQty -= allocQty;

        // Step 9: 生成AllocationSequence（在扣减成功时生成，符合§5.4要求）
        var allocationSeq = voucher.NextAllocationSequence++;

        // Step 10: 生成逻辑Allocation/LedgerEntry
        var allocationRecord = new AllocationRecord
        {
            AllocationSequence = allocationSeq,
            AllocatedQty = allocQty,
            SupplyKey = supply.SupplyKey,
            SupplyType = supply.SourceType.ToString(),
            DemandKey = demand.DemandKey,
            MaterialId = supply.MaterialId,
            AllocatedAt = DateTime.UtcNow,
            RequiresProduction = supply.SourceType == Core.Enum.SupplySourceType.NEW_REQUIREMENT
        };

        supply.Allocations.Add(allocationRecord);

        // 添加到凭证的SupplyAllocations（用于持久化）
        voucher.SupplyAllocations.Add(new Core.Dto.SupplyAllocationItem
        {
            AllocationSequence = allocationSeq,
            DemandKey = demand.DemandKey,
            DemandQuantity = demand.RequiredQty,
            SupplyMaterialId = supply.MaterialId,
            SupplySourceId = supply.SupplySourceId,
            AllocatedQuantity = allocQty,
            SourceType = supply.SourceType,
            SourceReference = supply.SourceReference,
            // SH 级（INTER_FACTORY_ORDER）分配落出荷指示号，供红线⑤⑥ 校验「同 SH 匹配不串 SH / 份额不重复计量」
            ShippingInstructionNo = supply.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER
                ? supply.PhysicalSourceKey
                : null,
            FactoryCode = supply.FactoryCode,
            BomLevel = bomLevel,
            AvailableAt = supply.AvailableAt,
            Priority = demand.Priority
        });

        // 添加到凭证的LedgerEntries（BOM遍历内存账本，§5.5要求）
        voucher.LedgerEntries.Add(new Core.Dto.PeggingLedgerEntry
        {
            OrderId = demand.CurrentOrderId ?? demand.RootOrderId ?? 0,
            DemandMaterialId = demand.MaterialId,
            DemandQuantity = demand.RequiredQty,
            SupplyMaterialId = supply.MaterialId,
            AllocatedQuantity = allocQty,
            SourceType = supply.SourceType,
            SourceId = supply.SupplySourceId,
            BomLevel = bomLevel,
            FactoryCode = supply.FactoryCode,
            ProductFamilyId = demand.ProductFamilyId,
            IsInFrozenZone = demand.IsInFrozenZone,
            Strategy = Core.Enum.PeggingStrategyType.FIFO,
            AvailableAt = supply.AvailableAt ?? DateTime.UtcNow
        });

        return AllocationResult.Succeeded(allocQty, allocationSeq, allocationRecord);
    }

    /// <summary>
    /// 校验供给资格（§5.3 Step 3，Eligibility）
    ///
    /// 当前实现仅做「数量真相 Owner」身份的防御性红线：供给与需求物料必须一致。
    /// 该不变量在入池筛选已保证，此处是原子分配点的二次保险，防止入池装载逻辑演进后出现物料错配被静默承接。
    ///
    /// 复杂资格规则（客户指定供应商、渠道限制、质量等级、OperationResourceEligibility 事实源）待 5号位规则引擎接入，
    /// 注入点在此方法内扩展，2号位不发明规则语义。
    /// 跨厂 INTER_FACTORY_ORDER 的工厂归属由入池装载层归一（落 ShippingInstructionNo），此处不做工厂相等推导，避免与分域口径冲突。
    /// </summary>
    private static bool ValidateEligibility(SupplyLedgerEntry supply, DemandBalance demand)
    {
        // 红线1：物料身份一致（防御性；正常已在 LoadSupplyPoolAsync/LoadDemandPoolAsync 按 Material 归池）
        if (supply.MaterialId != demand.MaterialId)
            return false;

        // 扩展点（V1验收前）：待 5号位规则引擎接入复杂资格规则，例如：
        //   - 客户指定供应商 / 渠道限制
        //   - 质量等级门槛
        //   - OperationResourceEligibility 事实源（操作-资源/供给-需求资格）
        return true;
    }

    /// <summary>
    /// 校验Strict Binding Lock（§8.1）
    ///
    /// Strict Binding表示供给被严格绑定到特定需求，其他需求不得使用。
    /// 场景：客户指定料、工单专用料、冻结区锁定等。
    /// </summary>
    private static bool ValidateStrictBinding(
        SupplyLedgerEntry supply,
        long? demandOrderId,
        string demandKey)
    {
        var strictLock = supply.Locks.FirstOrDefault(l => l.LockType == LockType.STRICT_BINDING);
        if (strictLock == null)
            return true; // 无Strict Binding锁，校验通过

        // 有Strict Binding锁，必须锁定到当前需求才允许分配
        var isLockedToCurrentDemand =
            (strictLock.LockedToOrderId.HasValue && strictLock.LockedToOrderId == demandOrderId) ||
            (strictLock.LockedToDemandKey == demandKey);

        return isLockedToCurrentDemand;
    }

    /// <summary>
    /// 校验Demand Protection Lock（§8.2）
    ///
    /// Demand Protection表示供给被保护给特定需求组，其他需求不得使用。
    /// 场景：优先级保护、产品族保护、客户保护等。
    ///
    /// 与Strict Binding的区别：
    /// - Strict Binding：1对1强绑定，其他需求完全不可用
    /// - Demand Protection：1对N保护，保护组内的需求可用，组外不可用
    /// </summary>
    private static bool ValidateDemandProtection(
        SupplyLedgerEntry supply,
        long? demandOrderId,
        string demandKey)
    {
        var protectionLock = supply.Locks.FirstOrDefault(l => l.LockType == LockType.DEMAND_PROTECTION);
        if (protectionLock == null)
            return true; // 无Demand Protection锁，校验通过

        // 有Demand Protection锁，检查当前需求是否在保护组内
        // V1.2 当前版本：暂不实现复杂的保护组规则，待后续补充
        // 简化实现：检查是否锁定到当前需求
        var isProtectedForCurrentDemand =
            (protectionLock.LockedToOrderId.HasValue && protectionLock.LockedToOrderId == demandOrderId) ||
            (protectionLock.LockedToDemandKey == demandKey);

        return isProtectedForCurrentDemand;
    }

    /// <summary>
    /// 构建LogicalProductionDemand（V1.2）
    ///
    /// 将需要生产的AllocationRecord转换成Solver输入
    /// 按PM回复Answer 1规范：一个LogicalProductionDemand对应一个AllocationSequence
    /// </summary>
    private static Core.Dto.LogicalProductionDemand BuildLogicalProductionDemand(
        AllocationRecord allocation,
        string demandKey,
        long? orderId,
        string? productionInstructionNo,
        int materialId,
        int factoryId,
        string materialCode,
        decimal planningYieldPercent,
        DateTime requiredTime,
        int demandSequence,
        PeggingResultVoucher voucher)
    {
        // LogicalDemandKey格式：PlanVersion_AllocationSeq
        var logicalDemandKey = $"{voucher.PlanVersionId}_{allocation.AllocationSequence}";

        // StartStageCode 由 Pegging 后 FillStartStageCodes 统一回填（Routing 无入边源结点），此处仍占位空。
        var startStageCode = string.Empty;

        // 数量双口径：
        // - NetOutputQty：净产出数量（扣除损耗后的有效产出）
        // - PlannedProcessQty：计划加工数量（含损耗的实际投入）
        // PM C2-5：PlannedProcessQty = NetOutputQty / yield 反算（PlanningYield，清单27）。
        // 红线：已有 PI Supply 不得按 Yield 再次放大——反算只作用于「需要生产的缺口」，不碰已有供给。
        // 无匹配规则/越界（YieldPercent ∉ (0,100)）按 100 无损回退；四舍五入 4 位对齐 DECIMAL(18,4)。
        var netOutputQty = allocation.AllocatedQty;
        var plannedProcessQty = planningYieldPercent > 0m && planningYieldPercent < 100m
            ? Math.Round(netOutputQty * 100m / planningYieldPercent, 4)
            : netOutputQty;

        return new Core.Dto.LogicalProductionDemand
        {
            LogicalDemandKey = logicalDemandKey,
            PlanVersionId = voucher.PlanVersionId,
            DomainKey = voucher.DomainKey,
            AllocationSequence = allocation.AllocationSequence,
            DemandKey = demandKey,
            OrderId = orderId,
            MaterialId = materialId,
            FactoryId = factoryId,
            StartStageCode = startStageCode,
            NetOutputQty = netOutputQty,
            PlannedProcessQty = plannedProcessQty,
            RequiredAvailableTime = requiredTime,
            DemandSequence = demandSequence,
            ProductionInstructionNo = productionInstructionNo, // W2：Pegging已确定该需求归属哪张PI（无PI=null）
            IsUnlocated = false // INTEGRATION TODO: 联调占位，V1验收前必须接入5号位PI Position实际计算结果
        };
    }

    /// <summary>
    /// BOM 遍历统计（PM 0918-2 埋点）：量化旧主链「共享子件重复展开」的基线。
    /// 阶段1 只记录 Old 侧基线；阶段2 Explosion/Allocation 解耦后同口径对照「重复展开 → 0」验证整改效果。
    /// 仅统计、不改变任何 Allocation 语义。
    /// </summary>
    private sealed class BomTraversalStats
    {
        private readonly HashSet<string> _uniqueNodeKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _visitsByMaterial = new(StringComparer.Ordinal);

        public long TotalVisits { get; private set; }
        public long UniqueNodes => _uniqueNodeKeys.Count;
        public long DuplicateExpansions => TotalVisits - _uniqueNodeKeys.Count;
        public string? MaxSharedMaterialCode { get; private set; }
        public long MaximumVisits { get; private set; }

        public void Record(string materialCode, int factoryId)
        {
            TotalVisits++;
            _uniqueNodeKeys.Add($"{materialCode}|{factoryId}");
            _visitsByMaterial.TryGetValue(materialCode, out var cnt);
            cnt++;
            _visitsByMaterial[materialCode] = cnt;
            if (cnt > MaximumVisits)
            {
                MaximumVisits = cnt;
                MaxSharedMaterialCode = materialCode;
            }
        }
    }

    /// <summary>
    /// 递归 BOM 节点供给扣减。
    /// 贪婪扣减成功 → 记录 SupplyAllocationItem；有短缺（自制件）→ 累加 NEW_REQUIREMENT 虚拟供给并
    /// 生成 LogicalProductionDemand，再递归子节点。子件确有生产缺口时，产出「父需求 → 子需求」
    /// MaterialRequirementLink（consumerLogicalDemandKey 非空即本节点是子件）。
    /// 叶节点短缺计入 voucher.ShortageQuantity（真正无法拆解的缺口）。
    /// visited 集合防止当前遍历路径循环，退出时移除以允许 BOM 中的共用子件。
    /// </summary>
    private static string? TraverseBomNode(
        OrderPeggingRow order,
        string materialCode,
        int materialId,
        int factoryId,
        string factoryCode,
        decimal demandQty,
        int bomLevel,
        int demandSequence,
        BomSnapshot bom,
        SupplyPool supplyPool,
        PeggingResultVoucher voucher,
        HashSet<string> visited,
        string? consumerLogicalDemandKey = null,
        int consumerMaterialId = 0,
        BomTraversalStats? traversalStats = null)
    {
        var nodeKey = SupplyPool.BuildKey(materialCode, factoryId);
        if (!visited.Add(nodeKey)) return null;
        traversalStats?.Record(materialCode, factoryId);

        try
        {
            // 净额 + 承接 + LPD 生成（阶段2 S2.2 抽取）：与旧递归内联逻辑逐字等价，唯「子件递归」留在本方法。
            var outcome = NetAndAllocate(
                order, materialCode, materialId, factoryId, factoryCode,
                demandQty, bomLevel, demandSequence, bom, supplyPool, voucher,
                consumerLogicalDemandKey, consumerMaterialId);

            if (outcome is null) return null; // 无生产（真实供给补足 / 采购件占位 / 生产失败）：不下钻

            // 自制件确有生产 → 子件需求 = 父件净产出 × 配比（等效旧 result.AllocatedQty × edge.Qty）
            var children = bom.ByParent[materialCode].ToList();
            if (children.Count > 0)
            {
                foreach (var edge in children)
                {
                    TraverseBomNode(
                        order,
                        edge.ChildCode,
                        edge.ChildMaterialId,
                        factoryId,
                        factoryCode,
                        outcome.ProducedQty * edge.Qty,
                        bomLevel + 1,
                        demandSequence,
                        bom,
                        supplyPool,
                        voucher,
                        visited,
                        consumerLogicalDemandKey: outcome.ProducerLogicalDemandKey,
                        consumerMaterialId: materialId,
                        traversalStats: traversalStats);
                }
            }

            return null;
        }
        finally
        {
            visited.Remove(nodeKey);
        }
    }

    /// <summary>某节点净额承接的结果：NetOutput（NEW_REQUIREMENT 净产出）与生产 LPD 身份（未生产=null）。</summary>
    private sealed class NetAllocationOutcome
    {
        public required decimal ProducedQty { get; init; }
        public required string? ProducerLogicalDemandKey { get; init; }

        /// <summary>生产 Allocations 的序号（阶段2 S2.3 BFS 建血缘边用，Key=LogicalDemandKey 尾段）。</summary>
        public required long ProducerAllocationSequence { get; init; }
    }

    /// <summary>
    /// 单一净额承接入口（阶段2 S2.2 抽取，PM 0918-3 §六 唯一净额入口）。
    /// 从旧 TraverseBomNode 递归体内原封搬出的：DemandBalance 构建 → 三类供给扣减 → 跨厂 SH → 缺口处理
    /// （采购占位 / PLANNED_PRODUCTION）+ LPD + 任务喂任务血缘。唯独「子件递归」留在调用方（阶段2 由 BFS 逐层驱动）。
    /// 返回 null = 无生产、不下钻；返回 outcome = 生产成功、ProducedQty 供下级按「父净 × 配比」重算。
    /// </summary>
    private static NetAllocationOutcome? NetAndAllocate(
        OrderPeggingRow order,
        string materialCode,
        int materialId,
        int factoryId,
        string factoryCode,
        decimal demandQty,
        int bomLevel,
        int demandSequence,
        BomSnapshot bom,
        SupplyPool supplyPool,
        PeggingResultVoucher voucher,
        string? consumerLogicalDemandKey,
        int consumerMaterialId)
    {
        var demandKey = $"ORDER_{order.OrderId}_{materialCode}_{factoryId}";

        // 红线① Demand 闭合：累加本节点需求量，供 ValidatePeggingResult 校验
        // 「Σ(已分配) + 短缺 ≤ 需求总量」。跨层级 BOM 展开时总量严格大于叶级分配，属有效上界校验。
        voucher.DemandQuantity += demandQty;

        // §5.2 DemandBalance：构建需求侧内存账本
        var demand = new DemandBalance
        {
            RemainingQty = demandQty,
            MaterialId = materialId,
            MaterialCode = materialCode,
            FactoryId = factoryId,
            FactoryCode = factoryCode,
            DemandType = DemandType.ORDER,
            DemandKey = demandKey,
            RootOrderId = order.OrderId,
            CurrentOrderId = order.OrderId,
            BomLevel = bomLevel,
            DueTime = order.DueDate,
            Priority = demandSequence, // §6 Priority Segment：订单级需求优先级（由 DemandPriorityExecutor 排序结果透传）
            ProductFamilyId = 0, // V1.2：暂不使用产品族
            IsInFrozenZone = false,
            WorksetId = null
        };

        // 供给链选择（PM 2026-08-28 最终裁决 + Pegging 专项 v1.1）：不存在 Inventory/PI/Procurement 三类全局
        // 优先级；先按当前 Demand 业务身份确定允许进入的供给集合，再调用对应类内排序规则。
        //  - 顶层 SALES_ORDER（bomLevel=0）：ERP 已扣成品库存，不再搜普通成品库存（§2.1）。
        //  - 自制件（isPurchased=false）：合资格库存 → PI → 生产缺口。
        //  - 采购件（isPurchased=true）：合资格库存 → 正式采购/在途 → 规划采购占位。
        var isPurchased = bom.IsPurchasedByMaterial.TryGetValue(materialCode, out var purchased) && purchased;
        var includeInventory = bomLevel > 0;

        // 贪婪扣减：使用原子Allocation机制，确保供需扣减、Lock校验、AllocationSequence生成的原子性
        foreach (var entry in supplyPool.GetEntries(materialCode, factoryId, isPurchased, includeInventory))
        {
            if (demand.RemainingQty <= 0m) break;

            var result = TryAtomicAllocation(
                supply: entry,
                demand: demand,
                bomLevel: bomLevel,
                voucher: voucher,
                requestedQty: demand.RemainingQty);

            if (!result.Success)
            {
                // 原子分配失败（Lock冲突、余额不足等），跳过此供给，尝试下一个
                // 失败原因：result.FailureReason（可在调试时输出）
                continue;
            }

            // 原子分配成功，demand.RemainingQty已在TryAtomicAllocation中扣减

            // V1.2：判断是否需要生产，生成LogicalProductionDemand
            if (result.Record != null && result.Record.RequiresProduction)
            {
                voucher.LogicalProductionDemands.Add(BuildLogicalProductionDemand(
                    allocation: result.Record,
                    demandKey: demand.DemandKey,
                    orderId: order.OrderId,
                    productionInstructionNo: order.MTS_InstructionNo,
                    materialId: materialId,
                    factoryId: factoryId,
                    materialCode: materialCode,
                    planningYieldPercent: supplyPool.PlanningYieldPercent(materialCode),
                    requiredTime: order.DueDate,
                    demandSequence: demandSequence,
                    voucher: voucher));
            }
        }

        // ── 跨厂出荷指示（SH级）绑定消费：三类通用排序扣减后仍短缺时，按 AvailableAt 升序消费 SH 供给 ──
        // PM 裁决（§七.2）：SH 保持单一 Supply 身份；Transit/Received 是其履行状态两段，PhysicalSourceKey=SH No，
        // 由红线⑤⑥ 校验「同 SH 匹配不串 SH / 份额不重复计量」。TryAtomicAllocation 落 ShippingInstructionNo=SH No。
        if (demand.RemainingQty > 0m)
        {
            foreach (var shEntry in supplyPool.GetInterFactoryEntries(materialCode, factoryId))
            {
                if (demand.RemainingQty <= 0m) break;

                var shResult = TryAtomicAllocation(
                    supply: shEntry,
                    demand: demand,
                    bomLevel: bomLevel,
                    voucher: voucher,
                    requestedQty: demand.RemainingQty);

                if (!shResult.Success)
                {
                    // 原子分配失败（Lock冲突、余额不足等），跳过此 SH 供给，尝试下一个
                    continue;
                }
            }
        }

        if (demand.RemainingQty <= 0m) return null;

        // V1.2：缺口处理 - 根据IsPurchased区分采购件/自制件
        if (isPurchased)
        {
            // 采购件：生成 Planning-only Purchase Placeholder（§9.3）
            // 特征：仅内存、ESTIMATED、NOT_COMMITTED、不生成采购单、不生成Task、不可作为CTP承诺
            // 不生成LogicalProductionDemand，不触发Task生成
            supplyPool.Add(
                materialCode: materialCode,
                materialId: materialId,
                factoryId: factoryId,
                qty: demand.RemainingQty,
                // P0-07：规划采购占位 AvailableTime = DataCutoffTime + 冻结 DefaultPurchaseLT（不再 DueDate-7 天倒推）
                availableAt: (supplyPool.DataCutoffTime == default ? DateTime.Now : supplyPool.DataCutoffTime)
                    .AddDays(supplyPool.DefaultPurchaseLtDays),
                sourceType: Core.Enum.SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER,
                sourceRef: $"PLANNING_PLACEHOLDER_{voucher.PlanVersionId}_{materialCode}_{Guid.NewGuid():N}",
                factoryCode: factoryCode,
                confidence: SupplyConfidence.ESTIMATED,
                commitment: SupplyCommitment.NOT_COMMITTED);

            voucher.ShortageQuantity += demand.RemainingQty;
            return null;
        }

        // 自制件：生成PLANNED_PRODUCTION虚拟供给，通过原子Allocation流程处理
        // 符合§5.3和§5.4要求：必须经过完整的10步原子校验，AllocationSequence在成功时生成

        // 添加虚拟PLANNED_PRODUCTION供给到SupplyPool（返回引用直接分配；NEW_REQUIREMENT 属缺口结果，
        // 不参与 GetEntries 三类排序，故不能依赖 .Last() 取回）
        var virtualSupply = supplyPool.Add(
            materialCode: materialCode,
            materialId: materialId,
            factoryId: factoryId,
            qty: demand.RemainingQty,
            availableAt: order.DueDate,
            sourceType: Core.Enum.SupplySourceType.NEW_REQUIREMENT,
            sourceRef: $"NEW_REQ_{voucher.PlanVersionId}_{materialCode}_{Guid.NewGuid():N}",
            factoryCode: factoryCode);
        var productionResult = TryAtomicAllocation(
            supply: virtualSupply,
            demand: demand,
            bomLevel: bomLevel,
            voucher: voucher,
            requestedQty: demand.RemainingQty);

        if (!productionResult.Success)
        {
            voucher.ShortageQuantity += demand.RemainingQty;
            return null;
        }

        string? producerLogicalDemandKey = null;
        if (productionResult.Record != null && productionResult.Record.RequiresProduction)
        {
            producerLogicalDemandKey = $"{voucher.PlanVersionId}_{productionResult.Record.AllocationSequence}";
            voucher.LogicalProductionDemands.Add(BuildLogicalProductionDemand(
                allocation: productionResult.Record,
                demandKey: demand.DemandKey,
                orderId: order.OrderId,
                productionInstructionNo: order.MTS_InstructionNo,
                materialId: materialId,
                factoryId: factoryId,
                materialCode: materialCode,
                planningYieldPercent: supplyPool.PlanningYieldPercent(materialCode),
                requiredTime: order.DueDate,
                demandSequence: demandSequence,
                voucher: voucher));

            // 任务喂任务血缘（R2 / PM 2026-09-10）：本节点是子件且确有生产缺口时，
            // 产出「父需求 → 子需求」MaterialRequirementLink，1号位据此生成真实 TaskDependency。
            if (!string.IsNullOrEmpty(consumerLogicalDemandKey))
            {
                voucher.MaterialRequirementLinks.Add(new Core.Dto.MaterialRequirementLink
                {
                    ConsumerLogicalDemandKey   = consumerLogicalDemandKey!,
                    ProducerLogicalDemandKey   = producerLogicalDemandKey!,
                    ConsumerMaterialId         = consumerMaterialId,
                    ProducerMaterialId         = materialId,
                    RequiredQty                = demandQty,
                    ProducerAllocationSequence = productionResult.Record.AllocationSequence
                });
            }
        }

        return new NetAllocationOutcome
        {
            ProducedQty = productionResult.AllocatedQty,
            ProducerLogicalDemandKey = producerLogicalDemandKey,
            ProducerAllocationSequence = productionResult.AllocationSequence
        };
    }

    /// <summary>
    /// P0-04：构造 CandidateContext（FULL 返回 null）。
    /// Base 锚点 = request.BasePlanVersionId（3号位创建 Run 时冻结当前 ACTIVE；2号位运行期不得再查「此刻最新 ACTIVE」）。
    /// ChangeSeed = Candidate 重新 Pegging 相对 Base ACTIVE 的真实逻辑生产需求差异集（新增/减少/数量变化），不是新订单 ID。
    /// ExternalDomainResourceBlocks = 编排层已装载的其它 Domain ACTIVE 共享资源占用（§11，Immutable=true）。
    /// </summary>
    private async Task<CandidateContext?> BuildCandidateContextAsync(
        PeggingExecutionRequest request,
        PeggingResultVoucher voucher)
    {
        if (!request.IsCandidate)
            return null;

        var changeSeedKeys = request.BasePlanVersionId.HasValue
            ? await ComputeChangeSeedKeysAsync(request.BasePlanVersionId.Value, voucher)
            : Array.Empty<string>();

        return new CandidateContext
        {
            BasePlanVersionId            = request.BasePlanVersionId,
            ChangeSeedKeys               = changeSeedKeys,
            ExternalDomainResourceBlocks = request.ExternalDomainResourceBlocks ?? Array.Empty<ResourceBlock>()
        };
    }

    /// <summary>
    /// P0-04 §2.2：ChangeSeed = Candidate Pegging 相对 Base ACTIVE 的差异集。
    /// Base 侧事实源 = AllocationTaskShare（全库唯一持久化 DemandKey 的分配分享表），按 DemandKey 聚合 ShareQty；
    /// Candidate 侧 = 本次 voucher.SupplyAllocations（含 NEW_REQUIREMENT 缺口）按 DemandKey 聚合 AllocatedQuantity。
    /// 差异判定：新增（候选有基无）/减少（基有候选无）/数量变化（|Δ| > 1e-4）。键值为 DemandKey，1号位 PhaseFour 按其匹配。
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeChangeSeedKeysAsync(
        int basePlanVersionId,
        PeggingResultVoucher voucher)
    {
        var baseRows = await _connectionManager.QueryAsync<DemandKeyQtyRow>(
            @"SELECT DemandKey, SUM(ShareQty) AS Qty
              FROM AllocationTaskShare
              WHERE PlanVersionId = @PlanVersionId
              GROUP BY DemandKey",
            new { PlanVersionId = basePlanVersionId },
            db: DatabaseId.APS);

        var baseMap = baseRows.ToDictionary(r => r.DemandKey, r => r.Qty, StringComparer.Ordinal);

        var candMap = voucher.SupplyAllocations
            .GroupBy(a => a.DemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);

        var seeds = new HashSet<string>(StringComparer.Ordinal);

        // 新增 / 减少
        foreach (var k in candMap.Keys)
            if (!baseMap.ContainsKey(k)) seeds.Add(k);
        foreach (var k in baseMap.Keys)
            if (!candMap.ContainsKey(k)) seeds.Add(k);

        // 数量变化（两侧都存在，但本次分配量发生显著变化）
        foreach (var (k, candQty) in candMap)
            if (baseMap.TryGetValue(k, out var baseQty) && Math.Abs(candQty - baseQty) > 0.0001m)
                seeds.Add(k);

        return seeds.ToList();
    }

    private IReadOnlyList<AllocationLineage> BuildAllocationLineage(PeggingResultVoucher voucher)
    {
        var lineage = new List<AllocationLineage>();

        foreach (var alloc in voucher.SupplyAllocations)
        {
            lineage.Add(new AllocationLineage
            {
                AllocationSequence = alloc.AllocationSequence,
                DemandKey = alloc.DemandKey,
                MaterialId = alloc.SupplyMaterialId,
                SupplyType = alloc.SourceType.ToString(),
                SupplyKey = alloc.SourceReference ?? alloc.SupplySourceId?.ToString() ?? "",
                Quantity = alloc.AllocatedQuantity,
                AvailableTime = alloc.AvailableAt
            });
        }

        return lineage;
    }

    private IReadOnlyList<MaterialAvailabilitySlice> BuildMaterialConstraints(PeggingResultVoucher voucher)
    {
        var constraints = new List<MaterialAvailabilitySlice>();

        foreach (var alloc in voucher.SupplyAllocations)
        {
            if (alloc.AvailableAt.HasValue)
            {
                var factoryId = 0;
                if (int.TryParse(alloc.FactoryCode, out var fid))
                    factoryId = fid;

                constraints.Add(new MaterialAvailabilitySlice
                {
                    AllocationSequence = alloc.AllocationSequence,
                    MaterialId = alloc.SupplyMaterialId,
                    FactoryId = factoryId,
                    Quantity = alloc.AllocatedQuantity,
                    AvailableTime = alloc.AvailableAt.Value,
                    SourceType = alloc.SourceType.ToString(),
                    SourceKey = alloc.SourceReference ?? alloc.SupplySourceId?.ToString() ?? "",
                    Commitment = null,
                    Confidence = null
                });
            }
        }

        return constraints;
    }

    private IReadOnlyList<ResourceDefinition> BuildResourceDefinitions(Core.Models.Scheduling.SchedulingContext? context)
    {
        if (context == null || context.Resources.Count == 0)
            return Array.Empty<ResourceDefinition>();

        var resources = new List<ResourceDefinition>();

        foreach (var res in context.Resources)
        {
            resources.Add(new ResourceDefinition
            {
                ResourceId = int.TryParse(res.ResourceId, out var rid) ? rid : 0,
                ResourceCode = res.ResourceName,
                FactoryCode = res.FactoryId,
                Capacity = res.CapacityFactor
            });
        }

        return resources;
    }

    private IReadOnlyList<ResourceCalendarSlot> BuildResourceCalendarSlots(Core.Models.Scheduling.SchedulingContext? context)
    {
        if (context == null || context.ResourceCalendars.Count == 0)
            return Array.Empty<ResourceCalendarSlot>();

        var slots = new List<ResourceCalendarSlot>();

        foreach (var (resourceIdStr, timeWindows) in context.ResourceCalendars)
        {
            if (!int.TryParse(resourceIdStr, out var resourceId))
                continue;

            foreach (var window in timeWindows)
            {
                slots.Add(new ResourceCalendarSlot
                {
                    ResourceId = resourceId,
                    Start = window.Start,
                    End = window.End,
                    IsAvailable = true
                });
            }
        }

        return slots;
    }
}
