using System.Data;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// BOM 批展开驱动服务（2号位职责）。
/// 链路、调用契约与破坏性说明见 <see cref="IBomBatchExpandService"/> 接口注释。
/// </summary>
public class BomBatchExpandService : IBomBatchExpandService
{
    /// <summary>
    /// 批展开 SP 的执行超时（秒）。
    /// ODS 连接串的 CommandTimeout 是 120 秒，对「清空 + 重建四表」不够
    /// （2026-10-16 实测写入段单段即 223 秒）；此处显式抬到 1800，与
    /// BOMResultPullService.BulkCopyTimeoutSeconds 的同类余量口径一致。
    /// </summary>
    private const int ExpandCommandTimeoutSeconds = 1800;

    /// <summary>APS_ETL_Log.Step 取值</summary>
    private const string EtlStep = "BomBatchExpand";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<BomBatchExpandService> _logger;

    public BomBatchExpandService(
        DatabaseConnectionManager connectionManager,
        ILogger<BomBatchExpandService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<BomBatchExpandResult> ExpandNightBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var pendingBacklog = 0;
        string? batchNo = null;
        var startedAt = DateTime.Now;

        try
        {
            // ── 步骤1：取「**本次**」推送批号 —— 只认推送成功日志，不扫 PENDING 猜选 ──
            // 0号位 2026-10-17 §二.2／§三.B：同日可能有多批（实测 2026-10-17 一天 3 个 REQ_ 批），
            // 「最新 PENDING」没有任何同批次保证。关联键 = `APS_ETL_Log` 的 BomRequestPush SUCCESS 行 + NightKey。
            pendingBacklog = await CountPendingBacklogAsync();
            var nightKey = BomNightBatchIdentity.NightKey(startedAt);
            var nightResolution = await BomNightBatchResolver.ResolveAsync(_connectionManager, startedAt, cancellationToken);

            if (!nightResolution.IsUnique)
            {
                var listed = nightResolution.Records.Count == 0
                    ? "(无)"
                    : string.Join(", ", nightResolution.Records.Take(5).Select(r => $"{r.BatchNo}@{r.CreatedAt:HH:mm:ss}"));

                if (nightResolution.Records.Count == 0)
                {
                    _logger.LogWarning(
                        "BOM 批展开：{Reason}，跳过（不猜最新 PENDING；PENDING 积压 {PendingBacklog}）",
                        nightResolution.Reason, pendingBacklog);
                }
                else
                {
                    _logger.LogError(
                        "BOM 批展开：{Reason} ⇒ 停止猜测（{Listed}）",
                        nightResolution.Reason, listed);
                }

                await LogEtlAsync(null,
                    $"{nightResolution.Reason} ⇒ 跳过（不猜最新 PENDING） | 记录:{listed} | PENDING积压:{pendingBacklog}",
                    "SKIPPED");

                issues.Add($"{nightResolution.Reason} ⇒ 未展开；记录：{listed}；PENDING 积压 {pendingBacklog}");
                return new BomBatchExpandResult
                {
                    PendingBacklogCount = pendingBacklog,
                    Issues = issues,
                };
            }

            batchNo = nightResolution.BatchNo;

            // ── 步骤1.2：ODS 批次状态闸门（0号位 §三.B）──
            var odsStatus = await _connectionManager.QueryFirstOrDefaultAsync<string>(
                "SELECT Status FROM MES_API_BOM_Request WHERE BatchNo = @BatchNo",
                new { BatchNo = batchNo },
                db: DatabaseId.ODS);

            if (string.IsNullOrEmpty(odsStatus))
            {
                await LogEtlAsync(batchNo,
                    $"推送日志指向的批次在 ODS 不存在，跳过 | NightKey:{nightKey}", "SKIPPED");
                issues.Add($"批次 {batchNo} 在 ODS 不存在（推送日志与实际表不一致）");
                return FailFast(batchNo, pendingBacklog, issues);
            }

            switch (odsStatus.Trim().ToUpperInvariant())
            {
                case "PENDING":
                    break;   // 唯一可展开状态

                case "PROCESSING":
                    _logger.LogWarning("BOM 批展开：批次 {BatchNo} 仍为 PROCESSING（上次执行结果待核），不并发重开", batchNo);
                    await LogEtlAsync(batchNo,
                        $"批次处于 PROCESSING（结果待核，不并发重开） | NightKey:{nightKey}", "SKIPPED");
                    issues.Add($"批次 {batchNo} 处于 PROCESSING ⇒ 跳过（须先核实旧 SQL 会话是否已终止）");
                    return FailFast(batchNo, pendingBacklog, issues);

                case "READY":
                    await LogEtlAsync(batchNo, $"批次已 READY（不重算，交由 00:30 接货） | NightKey:{nightKey}", "SKIPPED");
                    issues.Add($"批次 {batchNo} 已 READY ⇒ 不重算");
                    return FailFast(batchNo, pendingBacklog, issues);

                case "CONSUMED":
                    await LogEtlAsync(batchNo, $"批次已 CONSUMED（不重算） | NightKey:{nightKey}", "SKIPPED");
                    issues.Add($"批次 {batchNo} 已 CONSUMED ⇒ 不重算");
                    return FailFast(batchNo, pendingBacklog, issues);

                case "FAILED":
                    await LogEtlAsync(batchNo,
                        $"批次处于 FAILED（须先确认旧 SQL 会话终止并由人工决定是否重试） | NightKey:{nightKey}", "SKIPPED");
                    issues.Add($"批次 {batchNo} 处于 FAILED ⇒ 跳过（不自动重试）");
                    return FailFast(batchNo, pendingBacklog, issues);

                default:
                    await LogEtlAsync(batchNo, $"批次状态未预期：{odsStatus}，跳过 | NightKey:{nightKey}", "SKIPPED");
                    issues.Add($"批次 {batchNo} 状态未预期：{odsStatus} ⇒ 跳过");
                    return FailFast(batchNo, pendingBacklog, issues);
            }

            // ── 步骤1.5：明细为空的批不得交给该 SP ──
            // 该 SP 是「先清空四表、再按 _Detail 重建」：0 条明细 ⇒ 清空后写入 0 行 ⇒ 四表被清空。
            // 而批次头由 BOMRequestService「先建头、后插明细」：其「活跃订单明细为空」分支
            // 与 BulkInsert 失败都会留下只有头、没有明细的 PENDING 批。
            // ⇒ 调用前必须挡一道，宁可当夜不展开（记 SKIPPED 留痕），不可把四表清空。
            var detailCount = await _connectionManager.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM MES_API_BOM_Request_Detail WHERE BatchNo = @BatchNo",
                new { BatchNo = batchNo },
                db: DatabaseId.ODS);

            if (detailCount <= 0)
            {
                _logger.LogError(
                    "BOM 批展开：批次 {BatchNo} 明细为空，拒绝调用（否则会清空 ODS 四表）", batchNo);
                await LogEtlAsync(
                    batchNo, $"批 {batchNo} 明细为空，拒绝展开 | PENDING积压:{pendingBacklog}", "SKIPPED");

                issues.Add($"批次 {batchNo} 明细为空，已拒绝调用展开 SP");

                return new BomBatchExpandResult
                {
                    BatchNo = batchNo,
                    Expanded = false,
                    PendingBacklogCount = pendingBacklog,
                    Issues = issues,
                };
            }

            // ── 步骤2：显式传 @BatchNo 调 5号位 批展开 SP ──
            // SP 会自行把该批次 Status 先置 PROCESSING、成功后置 READY + CompletedAt + ExpandedRowCount
            // （WHERE BatchNo = @BatchNo），从而满足 00:30 接货的 READY 闸门。
            await _connectionManager.ExecuteAsync(
                "sp_ExpandBOMBatch_vNext",
                new { BatchNo = batchNo },
                CommandType.StoredProcedure,
                DatabaseId.ODS,
                commandTimeout: ExpandCommandTimeoutSeconds);

            // ── 步骤3：以 ODS 真实状态作证据（0号位 §三.B：Job 记录的「调用已返回成功」不替代 ODS 真实 READY）──
            var confirmedStatus = await _connectionManager.QueryFirstOrDefaultAsync<string>(
                "SELECT Status FROM MES_API_BOM_Request WHERE BatchNo = @BatchNo",
                new { BatchNo = batchNo },
                db: DatabaseId.ODS);

            var elapsedSeconds = (int)(DateTime.Now - startedAt).TotalSeconds;
            var isReady = string.Equals(confirmedStatus?.Trim(), "READY", StringComparison.OrdinalIgnoreCase);

            if (!isReady)
            {
                _logger.LogError(
                    "BOM 批展开：SP 已返回但批次 {BatchNo} 实际状态为 {Status}（非 READY），不得视为展开成功",
                    batchNo, confirmedStatus ?? "<空>");
                issues.Add($"批次 {batchNo} 展开后实际状态 = {confirmedStatus ?? "<空>"}（非 READY）");
                await LogEtlAsync(batchNo,
                    $"SP 返回但实际状态={confirmedStatus ?? "<空>"}（非 READY） | 耗时:{elapsedSeconds}s | PENDING积压:{pendingBacklog}",
                    "FAILED");

                return new BomBatchExpandResult
                {
                    BatchNo = batchNo,
                    Expanded = false,
                    PendingBacklogCount = pendingBacklog,
                    Issues = issues,
                };
            }

            _logger.LogInformation(
                "BOM 批展开完成：BatchNo={BatchNo}，实际状态=READY，耗时={Elapsed}s，PENDING 积压 {PendingBacklog}",
                batchNo, elapsedSeconds, pendingBacklog);

            if (pendingBacklog > 1)
            {
                _logger.LogWarning(
                    "BOM 批展开：仍有 {PendingBacklog} 个 PENDING 批次未消化（本任务每日只展开本次夜间一批，历史批不自动补展开）",
                    pendingBacklog);
            }

            await LogEtlAsync(batchNo,
                $"展开完成 | 实际状态:READY | 耗时:{elapsedSeconds}s | PENDING积压:{pendingBacklog}", "SUCCESS");

            return new BomBatchExpandResult
            {
                BatchNo = batchNo,
                Expanded = true,
                PendingBacklogCount = pendingBacklog,
                Issues = issues,
            };
        }
        catch (Exception ex)
        {
            // 失败转 Issue、**不硬断**：照 BomRealtimeExpandService「不得作为硬失败打断」的既有口径。
            // 吞异常后 Hangfire 判成功 ⇒ APS_ETL_Log 是唯一留痕，故下面的 FAILED 记录必须发出。
            issues.Add($"BOM 批展开失败（BatchNo={batchNo ?? "<无>"}）：{ex.Message}");
            _logger.LogError(ex, "BOM 批展开失败：BatchNo={BatchNo}", batchNo);

            await LogEtlAsync(batchNo,
                $"展开失败：{ex.Message} | 耗时:{(int)(DateTime.Now - startedAt).TotalSeconds}s | PENDING积压:{pendingBacklog}",
                "FAILED");

            return new BomBatchExpandResult
            {
                BatchNo = batchNo,
                Expanded = false,
                PendingBacklogCount = pendingBacklog,
                Issues = issues,
            };
        }
    }

    /// <summary>不展开时统一构造返回（保留批号与积压数，便于 Job 侧判定）。</summary>
    private static BomBatchExpandResult FailFast(string? batchNo, int pendingBacklog, List<string> issues)
        => new()
        {
            BatchNo = batchNo,
            Expanded = false,
            PendingBacklogCount = pendingBacklog,
            Issues = issues,
        };

    /// <summary>
    /// 统计 PENDING 积压批次数。
    /// 排除白天 <c>RT:</c> 批头（BomRealtimeExpandService 建的 FK 锚定头，Status 按设计恒为 PENDING，
    /// 不走 READY/CONSUMED 流转），否则该计数恒 ≥ 有实时展开的天数、失去告警意义。
    /// </summary>
    private async Task<int> CountPendingBacklogAsync()
        => await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"SELECT COUNT(*) FROM MES_API_BOM_Request
              WHERE Status = 'PENDING' AND BatchNo NOT LIKE 'RT:%'",
            db: DatabaseId.ODS);

    /// <summary>
    /// 记录 ETL 日志到 APS_ETL_Log。
    /// 全仓无共享 ETL 日志抽象（10 个 Engine 服务各自私有实现），故此处自带同型一份；
    /// 自身失败仅告警、非致命（照 NightlyBatchOrchestrator.LogETLAsync）。
    /// </summary>
    private async Task LogEtlAsync(string? batchNo, string message, string status)
    {
        try
        {
            const string sql = @"
                INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())";

            await _connectionManager.ExecuteAsync(
                sql,
                new { BatchNo = batchNo, Step = EtlStep, Message = message, Status = status },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入ETL日志失败（非致命）");
        }
    }
}
