using System.Data;
using System.Diagnostics;
using Dapper;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using ApsTask = LPS.APS.Core.Entities.APS.Task;

namespace LPS.APS.Application.Services;

/// <summary>
/// Pegging 编排器（2号位职责）
///
/// 职责边界（已按 pegging3.md 修正）：
///   - 2号位是 Pegging 主引擎：读 BOM 快照、维护内存账本、执行供给扣减、落库
///   - 在关键节点调用 5号位 IPeggingRuleService 获取裁决建议（规则插件模式）
///   - 5号位不是主引擎，只做规则判断并返回 PeggingRuleVoucher
///
/// 主流程（全部由2号位驱动）：
///   步骤1: 读 APS_BOM_RAW / APS_BOM_STAGE_PATH_RAW / APS_BOM_CROSS_FACTORY_EDGE_RAW
///   步骤2: 读供给池（INVENTORY / WIP / PIPELINE / PRODUCTION_INSTRUCTION / PURCHASE_ORDER）
///   步骤3: 遇到跨厂边 → 调5号位 EvaluateCrossFactoryModeAsync
///   步骤4: 枚举供给候选 → 调5号位 SelectSupplyCandidatesByRuleAsync 排序
///   步骤5: 遇到 PRODUCTION_INSTRUCTION → 调5号位 ValidateZpBpDocumentMatchAsync
///   步骤6: 维护内存 PeggingLedgerEntry 账本，执行扣减
///   步骤7: NEW_REQUIREMENT 触发 TaskDraft 生成，交1号位排程实例化 Task
///   步骤8: 调5号位 ValidateBusinessRuleResultAsync 红线校验
///   步骤9: 写 PeggingSupplyAllocation（非NEW_REQUIREMENT），写物理 Pegging（Task-to-Task）
/// </summary>
public class PeggingOrchestrator : IPeggingOrchestrator
{
    private readonly IPeggingRuleService _peggingRuleService;
    private readonly IPeggingSupplyAllocationRepository _allocationRepo;
    private readonly IFrozenZoneSnapshotRepository _frozenRepo;
    private readonly IVirtualInventoryBalanceRepository _virtualRepo;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PeggingOrchestrator> _logger;
    /// <summary>
    /// 1号位排程接口。
    /// 当前注入 <see cref="PassThroughSchedulerStub"/>（原样透传，不做真正排程）。
    /// 1号位就绪后替换为正式实现，此处无需改动。
    /// </summary>
    private readonly IFiniteCapacityScheduler _scheduler;

    public PeggingOrchestrator(
        IPeggingRuleService peggingRuleService,
        IPeggingSupplyAllocationRepository allocationRepo,
        IFrozenZoneSnapshotRepository frozenRepo,
        IVirtualInventoryBalanceRepository virtualRepo,
        DatabaseConnectionManager connectionManager,
        ILogger<PeggingOrchestrator> logger,
        IFiniteCapacityScheduler scheduler)
    {
        _peggingRuleService = peggingRuleService ?? throw new ArgumentNullException(nameof(peggingRuleService));
        _allocationRepo     = allocationRepo     ?? throw new ArgumentNullException(nameof(allocationRepo));
        _frozenRepo         = frozenRepo         ?? throw new ArgumentNullException(nameof(frozenRepo));
        _virtualRepo        = virtualRepo        ?? throw new ArgumentNullException(nameof(virtualRepo));
        _connectionManager  = connectionManager  ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger             = logger             ?? throw new ArgumentNullException(nameof(logger));
        _scheduler          = scheduler          ?? throw new ArgumentNullException(nameof(scheduler));
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
            // ── 步骤1：加载 BOM 快照（APS_BOM_RAW → 内存树，按父件编码索引）──
            var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, cancellationToken);
            _logger.LogInformation(
                "[Pegging] BOM 快照加载完成: PlanVersionId={PlanVersionId}, 边数={EdgeCount}",
                request.PlanVersionId, bomSnapshot.EdgeCount);

            // ── 步骤2：装载供给池（INVENTORY + PIPELINE）──
            var supplyPool = await LoadSupplyPoolAsync(request, cancellationToken);
            _logger.LogInformation(
                "[Pegging] 供给池装载完成: PlanVersionId={PlanVersionId}, 条目={EntryCount}",
                request.PlanVersionId, supplyPool.TotalEntries);

            // ── 步骤3：PeggingLoop BOM 遍历 + 供给扣减 ──
            var voucher = await ExecutePeggingLoopAsync(request, bomSnapshot, supplyPool, cancellationToken);
            result.Voucher = voucher;

            // ── 步骤2.7：5号位业务规则红线校验 ──
            var ruleVoucher = await _peggingRuleService.BuildPeggingVoucherAsync(
                request.PlanVersionId,
                result.OrderId,
                new List<SupplyCandidate>(),
                cancellationToken);

            var (ruleValid, ruleErrors) = await _peggingRuleService.ValidateBusinessRuleResultAsync(
                ruleVoucher, cancellationToken);

            voucher.RuleVoucher = ruleVoucher;

            if (!ruleValid)
            {
                foreach (var e in ruleErrors)
                    _logger.LogError("[Pegging] 业务规则红线: {Error}", e);
                result.IsSuccess   = false;
                result.ErrorMessage = string.Join("; ", ruleErrors);
                return result;
            }

            foreach (var w in ruleVoucher.Warnings)
            {
                _logger.LogWarning("[Pegging] 规则警告: {Warning}", w);
                result.Warnings.Add(w);
            }

            // ── 步骤2.8：构建 TaskDraft（纯内存，交1号位排程）──
            var taskDrafts = BuildTaskDraftsFromVoucher(voucher);
            _logger.LogInformation("[Pegging] TaskDraft 构建: {Count} 个", taskDrafts.Count);

            // ── 步骤2.8.1：构造 DomainSolveRequest 交1号位排程 ──
            // ⚠️ 当前注入 PassThroughSchedulerStub（原样透传）。
            // 1号位就绪后替换 DI 注册即可，此处无需改动。
            var solveRequest = new DomainSolveRequest
            {
                PlanVersionId = request.PlanVersionId,
                DomainKey     = request.PlanVersionId.ToString(),
                PlanningStart = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt,
                PlanningEnd   = request.FrozenWindowEnd == default ? DateTime.Now.AddDays(90) : request.FrozenWindowEnd,
                TaskDrafts    = taskDrafts
            };
            var solveResult = await _scheduler.SolveAsync(solveRequest, cancellationToken);

            // ── 步骤2.9-2.10：统一事务落库（Task INSERT + Pegging INSERT）──
            (result.GeneratedTasks, result.PhysicalPeggingCount) =
                await PersistDomainAndPeggingInTransactionAsync(
                    request.PlanVersionId, voucher, solveResult, cancellationToken);
            _logger.LogInformation(
                "[Pegging] 统一事务落库: Task={Tasks}, Pegging={Pegging}",
                result.GeneratedTasks.Count, result.PhysicalPeggingCount);

            // ── 步骤2.10：PeggingSupplyAllocation（非NEW_REQUIREMENT的供给分配）──
            result.SupplyAllocationCount = await PersistSupplyAllocationAsync(voucher, cancellationToken);
            _logger.LogInformation(
                "[Pegging] PeggingSupplyAllocation 写入: {Count} 条", result.SupplyAllocationCount);

            // ── 步骤2.3：MES_DISPATCHED 滑动窗口冻结快照 ──
            await UpdateFrozenZoneSnapshotAsync(
                request.PlanVersionId,
                request.FrozenWindowStart,
                request.FrozenWindowEnd,
                cancellationToken);

            sw.Stop();
            result.IsSuccess      = true;
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;

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
        const int batchSize = 20;
        var results = new List<PeggingOrchestrationResult>();

        foreach (var batch in request.OrderIds.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchRequest = new PeggingExecutionRequest
            {
                PlanVersionId     = request.PlanVersionId,
                OrderIds          = batch.ToList(),
                SnapshotAt        = request.SnapshotAt,
                FrozenWindowStart = request.FrozenWindowStart,
                FrozenWindowEnd   = request.FrozenWindowEnd,
                AllowCrossFactory = request.AllowCrossFactory,
                CrossFactoryMode  = request.CrossFactoryMode,
                DefaultStrategy   = request.DefaultStrategy,
                ProductFamilyIds  = request.ProductFamilyIds,
                TopologicalOrder  = request.TopologicalOrder,
                VirtualInventory  = request.VirtualInventory,
                MaxBomDepth       = request.MaxBomDepth,
                TimeoutSeconds    = request.TimeoutSeconds,
                ExecutionMode     = request.ExecutionMode
            };

            results.Add(await ExecutePeggingWorkflowAsync(batchRequest, cancellationToken));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<Core.Dto.TaskDraft> BuildTaskDraftsFromVoucher(PeggingResultVoucher voucher)
    {
        var drafts = voucher.TaskDrafts;
        if (drafts.Count == 0)
        {
            _logger.LogDebug("[Pegging] BuildTaskDraftsFromVoucher: TaskDrafts 为空");
            return Array.Empty<Core.Dto.TaskDraft>();
        }

        return TopologicalSortDrafts(drafts).ToList();
    }

    /// <summary>
    /// 统一事务：DELETE 占位 Task → INSERT Task → INSERT Pegging 血缘 → INSERT AllocationLedger。
    /// 四步在同一 SqlTransaction 内，任一失败全部回滚。
    /// </summary>
    private async System.Threading.Tasks.Task<(List<ApsTask> tasks, int peggingCount)>
        PersistDomainAndPeggingInTransactionAsync(
            int planVersionId,
            PeggingResultVoucher voucher,
            DomainSolveResult solveResult,
            CancellationToken ct)
    {
        return await _connectionManager.ExecuteInTransactionAsync<(List<ApsTask>, int)>(
            async (conn, tx) =>
            {
                var now    = DateTime.Now;
                var tasks  = new List<ApsTask>(solveResult.FinalTasks.Count);

                // 1. 删除 Phase 1 占位 Task
                await conn.ExecuteAsync(
                    "DELETE FROM [Task] WHERE PlanVersionId = @PlanVersionId AND TaskType = 'PRODUCTION'",
                    new { PlanVersionId = planVersionId },
                    transaction: tx);

                // 2. INSERT Task，建立显式 FinalDraftId → TaskId 映射（C2修复：不依赖Zip顺序）
                var finalDraftToTaskId = new Dictionary<string, long>(
                    solveResult.FinalTasks.Count, StringComparer.Ordinal);

                foreach (var final in solveResult.FinalTasks)
                {
                    ct.ThrowIfCancellationRequested();

                    var taskNo = $"PEGG-{planVersionId}-{final.FinalDraftId[..8]}";
                    var ids = await conn.QueryAsync<long>(
                        @"INSERT INTO [Task] (
                              PlanVersionId, TaskNo, OrderId, MaterialId,
                              OperationSeq, OperationCode,
                              Quantity, UOM, PlannedStartTime, PlannedEndTime,
                              Status, IsLocked, IsCriticalPath, TaskType,
                              CreatedAt, UpdatedAt
                          )
                          OUTPUT INSERTED.Id
                          VALUES (
                              @PlanVersionId, @TaskNo, @OrderId, @MaterialId,
                              @OperationSeq, @OperationCode,
                              @Quantity, @UOM, @PlannedStartTime, @PlannedEndTime,
                              @Status, @IsLocked, @IsCriticalPath, @TaskType,
                              @CreatedAt, @UpdatedAt
                          )",
                        new
                        {
                            PlanVersionId    = planVersionId,
                            TaskNo           = taskNo,
                            OrderId          = voucher.OrderId,
                            MaterialId       = final.MaterialId,
                            OperationSeq     = 0,
                            OperationCode    = final.OperationCode,
                            Quantity         = final.Quantity,
                            UOM              = final.UOM,
                            PlannedStartTime = final.PlannedStartTime,
                            PlannedEndTime   = final.PlannedEndTime,
                            Status           = "PLANNED",
                            IsLocked         = false,
                            IsCriticalPath   = false,
                            TaskType         = final.TaskType,
                            CreatedAt        = now,
                            UpdatedAt        = now
                        },
                        transaction: tx);

                    var taskId = ids.Single();
                    finalDraftToTaskId[final.FinalDraftId] = taskId;

                    tasks.Add(new ApsTask
                    {
                        Id               = taskId,
                        PlanVersionId    = planVersionId,
                        TaskNo           = taskNo,
                        OrderId          = voucher.OrderId,
                        MaterialId       = final.MaterialId,
                        OperationSeq     = 0,
                        OperationCode    = final.OperationCode,
                        RouteCode        = "DEFAULT",
                        PathId           = 1,
                        Quantity         = final.Quantity,
                        UOM              = final.UOM,
                        PlannedStartTime = final.PlannedStartTime,
                        PlannedEndTime   = final.PlannedEndTime,
                        Status           = "PLANNED",
                        IsLocked         = false,
                        IsCriticalPath   = false,
                        TaskType         = final.TaskType,
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

                // B5. INSERT PeggingAllocationLedger
                var seqToLedgerId = new Dictionary<long, long>();
                if (voucher.LedgerEntries.Count > 0)
                {
                    var scheduleRunId = await conn.ExecuteScalarAsync<int?>(
                        "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
                        new { PlanVersionId = planVersionId },
                        transaction: tx) ?? 0;

                    var orderCanonicalMap = (await conn.QueryAsync(
                        "SELECT DISTINCT OrderId, OrderCanonicalId FROM OrderBomRequestLink WHERE PlanVersionId = @PlanVersionId",
                        new { PlanVersionId = planVersionId },
                        transaction: tx))
                        .ToDictionary(r => (long)r.OrderId, r => (long)r.OrderCanonicalId);

                    var shareBySeq = solveResult.AllocationShares
                        .ToDictionary(s => s.AllocationSequence);

                    var ledgerRows = voucher.LedgerEntries
                        .Select((e, i) =>
                        {
                            var seq = (long)(i + 1);
                            shareBySeq.TryGetValue(seq, out var share);
                            long? finalTaskId = share != null && finalDraftToTaskId.TryGetValue(share.FinalDraftId, out var tid) ? tid : null;
                            orderCanonicalMap.TryGetValue(e.OrderId, out var canonicalId);
                            return new
                            {
                                AllocationSequence         = seq,
                                PlanVersionId              = planVersionId,
                                ScheduleRunId              = scheduleRunId,
                                DomainKey                  = planVersionId.ToString(),
                                RootDemandOrderCanonicalId = canonicalId,
                                DemandOrderCanonicalId     = canonicalId,
                                DemandOrderId              = e.OrderId,
                                DemandMaterialId           = e.DemandMaterialId,
                                SupplyType                 = e.SourceType.ToString(),
                                SupplyBusinessKey          = e.SourceId?.ToString() ?? string.Empty,
                                AllocatedQty               = e.AllocatedQuantity,
                                UOM                        = "EA",
                                AvailableTime              = e.AvailableAt,
                                AllocationMode             = "SOFT",
                                FinalTaskId                = finalTaskId,
                                TaskComponentQty           = share?.ComponentQty,
                                CreatedAt                  = now
                            };
                        })
                        .ToList();

                    foreach (var row in ledgerRows)
                    {
                        var ledgerId = await conn.ExecuteScalarAsync<long>(
                            @"INSERT INTO PeggingAllocationLedger (
                                  PlanVersionId, ScheduleRunId, DomainKey, AllocationSequence,
                                  RootDemandOrderCanonicalId, DemandOrderCanonicalId, DemandOrderId,
                                  DemandMaterialId, SupplyType, SupplyBusinessKey,
                                  AllocatedQty, UOM, AvailableTime, AllocationMode,
                                  FinalTaskId, TaskComponentQty, CreatedAt
                              ) OUTPUT INSERTED.Id
                              VALUES (
                                  @PlanVersionId, @ScheduleRunId, @DomainKey, @AllocationSequence,
                                  @RootDemandOrderCanonicalId, @DemandOrderCanonicalId, @DemandOrderId,
                                  @DemandMaterialId, @SupplyType, @SupplyBusinessKey,
                                  @AllocatedQty, @UOM, @AvailableTime, @AllocationMode,
                                  @FinalTaskId, @TaskComponentQty, @CreatedAt
                              )",
                            row,
                            transaction: tx);
                        seqToLedgerId[row.AllocationSequence] = ledgerId;
                    }
                }

                // B6. INSERT PeggingSupplyAllocation (仅对非Task供给)
                var nonTaskEntries = voucher.LedgerEntries
                    .Select((e, i) => new { Entry = e, Seq = (long)(i + 1) })
                    .Where(x => x.Entry.SourceType != Core.Enum.SupplySourceType.NEW_REQUIREMENT)
                    .ToList();

                if (nonTaskEntries.Count > 0)
                {
                    var scheduleRunId = await conn.ExecuteScalarAsync<int?>(
                        "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
                        new { PlanVersionId = planVersionId },
                        transaction: tx) ?? 0;

                    var orderCanonicalMap = (await conn.QueryAsync(
                        "SELECT DISTINCT OrderId, OrderCanonicalId FROM OrderBomRequestLink WHERE PlanVersionId = @PlanVersionId",
                        new { PlanVersionId = planVersionId },
                        transaction: tx))
                        .ToDictionary(r => (long)r.OrderId, r => (long)r.OrderCanonicalId);

                    var materialMap = (await conn.QueryAsync(
                        "SELECT Id, MaterialCode FROM Material WHERE Id IN @Ids",
                        new { Ids = nonTaskEntries.Select(x => x.Entry.DemandMaterialId).Distinct() },
                        transaction: tx))
                        .ToDictionary(r => (int)r.Id, r => (string)r.MaterialCode);

                    var supplyRows = nonTaskEntries
                        .Select(x =>
                        {
                            var e = x.Entry;
                            seqToLedgerId.TryGetValue(x.Seq, out var ledgerId);
                            orderCanonicalMap.TryGetValue(e.OrderId, out var canonicalId);
                            materialMap.TryGetValue(e.DemandMaterialId, out var materialCode);
                            return new
                            {
                                PlanVersionId          = planVersionId,
                                ScheduleRunId          = scheduleRunId,
                                LedgerId               = ledgerId,
                                RootOrderCanonicalId   = canonicalId,
                                DemandOrderCanonicalId = canonicalId,
                                MaterialId             = e.DemandMaterialId,
                                MaterialCode           = materialCode ?? string.Empty,
                                DemandFactoryCode      = e.FactoryCode,
                                DemandQty              = e.DemandQuantity,
                                AllocatedQty           = e.AllocatedQuantity,
                                AllocationMode         = "SOFT",
                                SupplyType             = e.SourceType.ToString(),
                                SupplyBusinessKey      = e.SourceId?.ToString() ?? string.Empty,
                                SupplyFactoryCode      = e.FactoryCode,
                                KnownAvailableTime     = e.AvailableAt,
                                CreatedAt              = now
                            };
                        })
                        .ToList();

                    await conn.ExecuteAsync(
                        @"INSERT INTO PeggingSupplyAllocation (
                              PlanVersionId, ScheduleRunId, LedgerId,
                              RootOrderCanonicalId, DemandOrderCanonicalId,
                              MaterialId, MaterialCode,
                              DemandFactoryCode, DemandQty, AllocatedQty, AllocationMode,
                              SupplyType, SupplyBusinessKey, SupplyFactoryCode,
                              KnownAvailableTime, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @LedgerId,
                              @RootOrderCanonicalId, @DemandOrderCanonicalId,
                              @MaterialId, @MaterialCode,
                              @DemandFactoryCode, @DemandQty, @AllocatedQty, @AllocationMode,
                              @SupplyType, @SupplyBusinessKey, @SupplyFactoryCode,
                              @KnownAvailableTime, @CreatedAt
                          )",
                        supplyRows,
                        transaction: tx);
                }

                _logger.LogInformation(
                    "[Pegging] 统一事务提交: Task={Tasks}, Pegging={Pegging} (PlanVersionId={PlanVersionId})",
                    tasks.Count, peggingRows.Count, planVersionId);

                return (tasks, peggingRows.Count);
            },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// 按 UpstreamDraftIds 做 DFS 后序拓扑排序，确保上游草稿排在下游之前。
    /// </summary>
    private static IEnumerable<Core.Dto.TaskDraft> TopologicalSortDrafts(
        IReadOnlyList<Core.Dto.TaskDraft> drafts)
    {
        var byId    = drafts.ToDictionary(d => d.DraftId, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result  = new List<Core.Dto.TaskDraft>(drafts.Count);

        void Visit(Core.Dto.TaskDraft d)
        {
            if (!visited.Add(d.DraftId)) return;
            foreach (var upId in d.UpstreamDraftIds)
                if (byId.TryGetValue(upId, out var upstream))
                    Visit(upstream);
            result.Add(d);
        }

        foreach (var d in drafts) Visit(d);
        return result;
    }


    /// <inheritdoc />
    public async System.Threading.Tasks.Task<int> PersistSupplyAllocationAsync(
        PeggingResultVoucher voucher,
        CancellationToken cancellationToken = default)
    {
        var allocations = MapVoucherToSupplyAllocations(voucher);
        if (allocations.Count == 0) return 0;

        var count = await _allocationRepo.BulkInsertAsync(allocations, cancellationToken);

        // 更新 InventoryBalance.AllocatedQty
        var groups = voucher.SupplyAllocations
            .Where(a => a.SourceType == Core.Enum.SupplySourceType.INVENTORY)
            .GroupBy(a => new { a.SupplyMaterialId, a.FactoryCode });

        foreach (var grp in groups)
        {
            await _connectionManager.ExecuteAsync(
                @"UPDATE ib SET ib.AllocatedQty = ib.AllocatedQty + @Delta
                  FROM InventoryBalance ib
                  INNER JOIN Material m ON m.MaterialCode = ib.MaterialCode AND m.Id = @MaterialId
                  INNER JOIN Factory  f ON f.Id = ib.FactoryId AND f.Code = @FactoryCode",
                new { Delta = grp.Sum(a => a.AllocatedQuantity), MaterialId = grp.Key.SupplyMaterialId, FactoryCode = grp.Key.FactoryCode },
                db: DatabaseId.APS);
        }

        return count;
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<int> UpdateFrozenZoneSnapshotAsync(
        int planVersionId,
        DateTime frozenWindowStart,
        DateTime frozenWindowEnd,
        CancellationToken cancellationToken = default)
    {
        var existing = await _frozenRepo.CountInFrozenWindowAsync(
            planVersionId, frozenWindowStart, frozenWindowEnd, cancellationToken);

        if (existing > 0)
        {
            _logger.LogDebug("[Pegging] 冻结区快照已存在 {Count} 条，跳过", existing);
            return existing;
        }

        // TODO: 2号位实现
        // 从 MES 已下发快照表读取 MES_DISPATCHED 任务写入 FrozenZoneSnapshot
        // 待3号位确认 MES 快照表来源后补全（是 MESWorkOrderSnapshot 还是其他表）
        _logger.LogDebug("[Pegging] 冻结区窗口: {Start} ~ {End}", frozenWindowStart, frozenWindowEnd);
        return 0;
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<int> PropagateVirtualInventoryAsync(
        int planVersionId,
        int sourceProductFamilyId,
        int targetProductFamilyId,
        CancellationToken cancellationToken = default)
    {
        var unpropagated = await _virtualRepo.GetUnpropagatedOrderedAsync(planVersionId, cancellationToken);

        var ids = unpropagated
            .Where(v => v.SourceProductFamilyId == sourceProductFamilyId
                     && v.TargetProductFamilyId  == targetProductFamilyId)
            .Select(v => v.Id)
            .ToList();

        if (ids.Count == 0) return 0;

        return await _virtualRepo.MarkAsPropagatedAsync(planVersionId, ids, cancellationToken);
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task RollbackPeggingWorkflowAsync(
        int planVersionId,
        long orderId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "[Pegging] 回滚: PlanVersionId={PlanVersionId}, OrderId={OrderId}",
            planVersionId, orderId);

        await System.Threading.Tasks.Task.WhenAll(
            _allocationRepo.DeleteByPlanVersionIdAsync(planVersionId, cancellationToken),
            _frozenRepo.DeleteByPlanVersionIdAsync(planVersionId, cancellationToken),
            _virtualRepo.DeleteByPlanVersionIdAsync(planVersionId, cancellationToken)
        );
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<(bool IsValid, List<string> ValidationErrors)> ValidateWorkflowConsistencyAsync(
        PeggingOrchestrationResult result,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!result.IsSuccess)
            errors.Add($"Pegging 执行失败: {result.ErrorMessage}");

        if (result.Voucher?.ShortageQuantity > 0)
            errors.Add($"供应短缺数量: {result.Voucher.ShortageQuantity}");

        if (result.Voucher?.RuleVoucher is { PassedBusinessRules: false })
            errors.AddRange(result.Voucher.RuleVoucher.BusinessRuleErrors);

        return await System.Threading.Tasks.Task.FromResult((errors.Count == 0, errors));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 将 Voucher 中的供应分配映射为 PeggingSupplyAllocation 实体
    ///
    /// NEW_REQUIREMENT 不写 PeggingSupplyAllocation：
    ///   该类型对应"需新排产"，最终通过 Task 实例化 + 物理 Pegging 表记录 Task-to-Task 血缘
    /// </summary>
    private static List<Core.Entities.APS.PeggingSupplyAllocation> MapVoucherToSupplyAllocations(
        PeggingResultVoucher voucher)
    {
        var now = DateTime.Now;

        return voucher.SupplyAllocations
            .Where(a => a.SourceType != Core.Enum.SupplySourceType.NEW_REQUIREMENT)
            .Select(alloc => new Core.Entities.APS.PeggingSupplyAllocation
            {
                PlanVersionId     = voucher.PlanVersionId,
                OrderId           = voucher.OrderId,
                DemandMaterialId  = voucher.DemandMaterialId,
                SupplyMaterialId  = alloc.SupplyMaterialId,
                AllocatedQuantity = alloc.AllocatedQuantity,
                UOM               = voucher.UOM,
                SupplySourceType  = alloc.SourceType.ToString(),
                SupplySourceId    = alloc.SupplySourceId,
                SourceReference   = alloc.SourceReference,
                FactoryCode       = alloc.FactoryCode,
                Priority          = alloc.Priority,
                AllocatedAt       = alloc.AvailableAt ?? DateTime.Now,
                IsConsumed        = false,
                CreatedAt         = now
            }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BOM 快照内部数据结构
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record BomEdge(
        string ParentCode,
        string ChildCode,
        int ChildMaterialId,
        decimal Qty,
        int Level,
        bool IsLeaf,
        string? ChildRequiredStageCode);

    private sealed record BomSnapshot(
        ILookup<string, BomEdge> ByParent,
        IReadOnlyDictionary<string, int> LLCByMaterial,
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

        public int TotalEntries { get; private set; }

        public void Add(
            string materialCode, int materialId, int factoryId, decimal qty,
            DateTime? availableAt, Core.Enum.SupplySourceType sourceType,
            string? sourceRef, string factoryCode, long? supplySourceId = null)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list))
            {
                list = new List<SupplyLedgerEntry>();
                _ledger[key] = list;
            }
            list.Add(new SupplyLedgerEntry
            {
                RemainingQty    = qty,
                MaterialId      = materialId,
                AvailableAt     = availableAt,
                SourceType      = sourceType,
                SourceReference = sourceRef,
                FactoryCode     = factoryCode,
                FactoryId       = factoryId,
                SupplySourceId  = supplySourceId
            });
            TotalEntries++;
        }

        /// <summary>返回指定物料+工厂的所有供给条目（按 AvailableAt 升序排列，现货在前）</summary>
        public IReadOnlyList<SupplyLedgerEntry> GetEntries(string materialCode, int factoryId)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list)) return Array.Empty<SupplyLedgerEntry>();
            return list.OrderBy(e => e.AvailableAt ?? DateTime.MinValue).ToList();
        }

        public static string BuildKey(string materialCode, int factoryId)
            => $"{materialCode}|{factoryId}";
    }

    private sealed class SupplyLedgerEntry
    {
        public decimal RemainingQty                     { get; set; }   // 遍历时可变
        public int MaterialId                           { get; init; }
        public DateTime? AvailableAt                    { get; init; }
        public Core.Enum.SupplySourceType SourceType    { get; init; }
        public string? SourceReference                  { get; init; }
        public string FactoryCode                       { get; init; } = string.Empty;
        public int FactoryId                            { get; init; }
        public long? SupplySourceId                     { get; init; }
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
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 供给池装载
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 装载供给池（INVENTORY + PIPELINE 两类，2号位独立职责，不依赖5号位）
    ///
    /// INVENTORY  — 来源：InventoryBalance（现货，ERP+MES 六层链路最终输出，立即可用）
    /// PIPELINE   — 来源：SupplyFact_Pipeline（在途/未入库，按 AvailableTime &lt;= SnapshotAt 过滤）
    ///
    /// WIP / PRODUCTION_INSTRUCTION / PURCHASE_ORDER 三类来源在 5号位规则引擎接入后补充。
    /// </summary>
    private async Task<SupplyPool> LoadSupplyPoolAsync(
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var pool    = new SupplyPool();
        var cutoff  = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt;

        // ── INVENTORY：现货库存（立即可用，AvailableAt = null 表示无时间约束）──
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
                     null, r.FactoryCode, r.SupplySourceId);

        // ── PIPELINE：管道供给（在途/未入库，AvailableTime 在切片时间之前才计入）──
        var pipelineRows = await _connectionManager.QueryAsync<SupplyLoadRow>(
            @"SELECT
                  sfp.MaterialCode,
                  sfp.MaterialId,
                  sfp.FactoryId,
                  sfp.FactoryCode,
                  sfp.Quantity                                       AS AvailableQty,
                  sfp.AvailableTime                                  AS AvailableAt,
                  ISNULL(sfp.SourceDocumentNo, sfp.SourceRowKey)     AS SourceReference
              FROM SupplyFact_Pipeline sfp
              WHERE sfp.IsActive = 1
                AND sfp.Quantity > 0
                AND (sfp.AvailableTime IS NULL OR sfp.AvailableTime <= @Cutoff)",
            new { Cutoff = cutoff },
            db: DatabaseId.APS);

        foreach (var r in pipelineRows)
            pool.Add(r.MaterialCode, r.MaterialId, r.FactoryId, r.AvailableQty,
                     r.AvailableAt, Core.Enum.SupplySourceType.PIPELINE,
                     r.SourceReference, r.FactoryCode);

        // ── WIP：在制工单剩余量（来源：StageProgressSnapshot）──
        // 通过 MTS_InstructionNo → [Task] 关联取工厂信息。
        // ⚠️ 首次运行时 [Task] 表为空，WIP 将为零行，属正常情况。
        // ⚠️ SourceScheduleRunId 为 NULL（计划版本尚未关联 ScheduleRun）时同样返回零行。
        var wipRows = await _connectionManager.QueryAsync<SupplyLoadRow>(
            @"SELECT sp.MaterialCode,
                     m.Id             AS MaterialId,
                     f.Id             AS FactoryId,
                     f.Code           AS FactoryCode,
                     sp.RemainingQty  AS AvailableQty,
                     NULL             AS AvailableAt,
                     sp.ProductionInstructionNo AS SourceReference
              FROM StageProgressSnapshot sp
              INNER JOIN Material m ON m.MaterialCode = sp.MaterialCode
              INNER JOIN (
                  SELECT DISTINCT t.MTS_InstructionNo, o.FactoryId
                  FROM [Task] t
                  INNER JOIN [Order] o ON o.Id = t.OrderId
                  WHERE t.PlanVersionId = @PlanVersionId
                    AND t.MTS_InstructionNo IS NOT NULL
              ) t ON t.MTS_InstructionNo = sp.ProductionInstructionNo
              INNER JOIN Factory f ON f.Id = t.FactoryId
              WHERE sp.ScheduleRunId = (
                  SELECT TOP 1 pv.SourceScheduleRunId
                  FROM PlanVersion pv
                  WHERE pv.Id = @PlanVersionId
              )
              AND sp.RemainingQty > 0",
            new { request.PlanVersionId },
            db: DatabaseId.APS);

        foreach (var r in wipRows)
            pool.Add(r.MaterialCode, r.MaterialId, r.FactoryId, r.AvailableQty,
                     null, Core.Enum.SupplySourceType.WIP,
                     r.SourceReference, r.FactoryCode);

        _logger.LogDebug(
            "[Pegging] 供给池明细: INVENTORY={Inv}, WIP={Wip}, PIPELINE={Pipe}",
            inventoryRows.Count(), wipRows.Count(), pipelineRows.Count());

        return pool;
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
                0);
        }

        var edges = rows.Select(r => new BomEdge(
            r.ParentMaterialCode,
            r.ChildMaterialCode,
            r.ChildMaterialId,
            r.Quantity,
            r.Level,
            r.IsLeaf,
            r.ChildRequiredStageCode)).ToList();

        // LLC 取各物料在所有 BOM 路径中出现的最小值
        var llcByMaterial = rows
            .Where(r => r.LLC.HasValue)
            .GroupBy(r => r.ChildMaterialCode)
            .ToDictionary(g => g.Key, g => g.Min(r => r.LLC!.Value));

        return new BomSnapshot(edges.ToLookup(e => e.ParentCode), llcByMaterial, edges.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 订单装载（PeggingLoop 前置步骤）
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class OrderPeggingRow
    {
        public long     OrderId         { get; set; }
        public int      MaterialId      { get; set; }
        public string   MaterialCode    { get; set; } = string.Empty;
        public int      FactoryId       { get; set; }
        public string   FactoryCode     { get; set; } = string.Empty;
        public decimal  DemandQty       { get; set; }
        public DateTime DueDate         { get; set; }
        public string   UOM             { get; set; } = string.Empty;
        public int?     ProductFamilyId { get; set; }
    }

    private async Task<IReadOnlyList<OrderPeggingRow>> LoadOrdersForPeggingAsync(
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var rows = await _connectionManager.QueryAsync<OrderPeggingRow>(
            @"SELECT o.Id          AS OrderId,
                     o.MaterialId,
                     m.MaterialCode,
                     o.FactoryId,
                     f.Code        AS FactoryCode,
                     o.Quantity    AS DemandQty,
                     o.CustomerDueDate AS DueDate,
                     o.UOM,
                     m.ProductFamilyId
              FROM [Order] o
              INNER JOIN Material m ON m.Id = o.MaterialId
              INNER JOIN Factory  f ON f.Id = o.FactoryId
              WHERE o.Id IN @OrderIds",
            new { request.OrderIds },
            db: DatabaseId.APS);

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

        var firstOrder = orders.FirstOrDefault();
        var voucher = new PeggingResultVoucher
        {
            PlanVersionId    = request.PlanVersionId,
            OrderId          = firstOrder?.OrderId ?? request.OrderIds.FirstOrDefault(),
            DemandMaterialId = firstOrder?.MaterialId ?? 0,
            UOM              = firstOrder?.UOM ?? string.Empty,
            IsSuccess        = true,
            ExecutedAt       = DateTime.Now
        };

        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();

            var visited = new HashSet<string>(StringComparer.Ordinal);
            _ = TraverseBomNode(
                order,
                order.MaterialCode,
                order.MaterialId,
                order.FactoryId,
                order.FactoryCode,
                order.DemandQty,
                bomLevel: 0,
                bom,
                supplyPool,
                voucher,
                visited);
        }

        voucher.IsFullyAllocated = voucher.ShortageQuantity == 0;
        return voucher;
    }

    /// <summary>
    /// 递归 BOM 节点供给扣减。
    /// 贪婪扣减成功 → 记录 SupplyAllocationItem。
    /// 有短缺 → 在当前节点创建 TaskDraft，然后：
    ///   有 BOM 子节点 → 递归子节点，子件 DraftId 填入本节点 UpstreamDraftIds。
    ///   叶子节点     → 计入 voucher.ShortageQuantity（真正无法拆解的缺口）。
    /// 返回本节点创建的 DraftId，供父节点写入 UpstreamDraftIds；供给完全满足时返回 null。
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
        BomSnapshot bom,
        SupplyPool supplyPool,
        PeggingResultVoucher voucher,
        HashSet<string> visited)
    {
        var nodeKey = SupplyPool.BuildKey(materialCode, factoryId);
        if (!visited.Add(nodeKey)) return null;

        try
        {
            var remaining = demandQty;

            // 贪婪扣减：AvailableAt 升序（INVENTORY null → MinValue 排最前）
            foreach (var entry in supplyPool.GetEntries(materialCode, factoryId))
            {
                if (remaining <= 0m) break;

                var take = Math.Min(entry.RemainingQty, remaining);
                if (take <= 0m) continue;

                entry.RemainingQty -= take;
                remaining          -= take;

                voucher.SupplyAllocations.Add(new Core.Dto.SupplyAllocationItem
                {
                    SupplyMaterialId  = entry.MaterialId,
                    SupplySourceId    = entry.SupplySourceId,
                    AllocatedQuantity = take,
                    SourceType        = entry.SourceType,
                    SourceReference   = entry.SourceReference,
                    FactoryCode       = entry.FactoryCode,
                    BomLevel          = bomLevel,
                    AvailableAt       = entry.AvailableAt,
                    Priority          = bomLevel
                });
            }

            if (remaining <= 0m) return null; // 完全满足，无需新排产

            var children = bom.ByParent[materialCode].ToList();

            if (children.Count > 0)
            {
                // 先递归子件，只有子件有短缺时才创建本节点 TaskDraft
                var childDraftIds = new List<string>();
                foreach (var edge in children)
                {
                    var childDraftId = TraverseBomNode(
                        order,
                        edge.ChildCode,
                        edge.ChildMaterialId,
                        factoryId,
                        factoryCode,
                        remaining * edge.Qty,
                        bomLevel + 1,
                        bom,
                        supplyPool,
                        voucher,
                        visited);

                    if (childDraftId != null)
                        childDraftIds.Add(childDraftId);
                }

                if (childDraftIds.Count == 0) return null; // 所有子件已满足，无需新排产

                var draft = new Core.Dto.TaskDraft
                {
                    MaterialId        = materialId,
                    Quantity          = remaining,
                    UOM               = order.UOM,
                    FactoryCode       = factoryCode,
                    ProductFamilyId   = order.ProductFamilyId ?? 0,
                    EarliestAvailableTime = DateTime.Now,
                    DueTime           = order.DueDate,
                    UpstreamDraftIds  = childDraftIds,
                    Priority          = bomLevel
                };
                voucher.TaskDrafts.Add(draft);
                return draft.DraftId;
            }
            else
            {
                // 叶子节点（采购件）：真正无法满足的短缺
                voucher.ShortageQuantity += remaining;
                var draft = new Core.Dto.TaskDraft
                {
                    MaterialId        = materialId,
                    Quantity          = remaining,
                    UOM               = order.UOM,
                    FactoryCode       = factoryCode,
                    ProductFamilyId   = order.ProductFamilyId ?? 0,
                    EarliestAvailableTime = DateTime.Now,
                    DueTime           = order.DueDate,
                    UpstreamDraftIds  = new List<string>(),
                    Priority          = bomLevel
                };
                voucher.TaskDrafts.Add(draft);
                return draft.DraftId;
            }
        }
        finally
        {
            visited.Remove(nodeKey);
        }
    }


}
