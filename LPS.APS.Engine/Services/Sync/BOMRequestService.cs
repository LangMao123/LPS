using System.Data;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// BOM展开请求服务（2号位职责）
///
/// 时序：每天00:00
///   Step 1: 调用 sp_GetActiveRootBOMNOs 统计活跃根集合（日志+统计用）
///   Step 2: 直接从 Order_Canonical 查询活跃订单（不再JOIN ERP_Order_Staging）
///   Step 3: 在 ODS 库插入 MES_API_BOM_Request 批次头（PENDING状态）
///   Step 4: 通过 SqlBulkCopy 推送订单级明细到 MES_API_BOM_Request_Detail
///   Step 5: SQL Agent Job 00:05 自动拾取 PENDING 批次执行 sp_ExpandBOMBatch_vNext
///
/// 【批次幂等红线】：
///   ✅ BatchNo 全局唯一（REQ_yyyyMMdd_xxxxxxxx）
///   ✅ MES_API_BOM_Request_Detail 有 (BatchNo, OrderCanonicalId) 唯一约束（v5.0.31改）
///   ✅ 2号位只推送基础字段，BOM入口解析由5号位负责
/// </summary>
public class BOMRequestService : IBOMRequestService
{
    /// <summary>`APS_ETL_Log.Step` 取值（0号位 2026-10-17 第十五轮 BOM 收口 §三.A）。</summary>
    private const string EtlStep = "BomRequestPush";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<BOMRequestService> _logger;

    public BOMRequestService(
        DatabaseConnectionManager connectionManager,
        ILogger<BOMRequestService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<BOMRequestResult> PushBOMRequestToODSAsync(CancellationToken cancellationToken = default)
    {
        var batchNo = $"REQ_{DateTime.Now:yyyyMMdd}_{Guid.NewGuid():N}"[..28];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var spParams = new DynamicParameters();
            spParams.Add("@RootCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
            spParams.Add("@TotalOrderCount", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await _connectionManager.QueryAsync<ActiveBOMNODto>(
                "sp_GetActiveRootBOMNOs",
                spParams,
                CommandType.StoredProcedure,
                DatabaseId.APS);

            var rootCount = spParams.Get<int>("@RootCount");
            var totalOrderCount = spParams.Get<int>("@TotalOrderCount");

            if (totalOrderCount == 0)
            {
                _logger.LogWarning("活跃订单为空，跳过BOM推送");
                return new BOMRequestResult { BatchNo = batchNo, RootCount = 0, TotalOrderCount = 0 };
            }

            var insertRequestSql = @"
                INSERT INTO MES_API_BOM_Request (BatchNo, Status, RootCount, CreatedAt, RetryCount)
                VALUES (@BatchNo, 'PENDING', @RootCount, GETDATE(), 0)";

            await _connectionManager.ExecuteAsync(
                insertRequestSql,
                new { BatchNo = batchNo, RootCount = totalOrderCount },
                db: DatabaseId.ODS);

            var detailSql = @"
                SELECT
                    oc.Id AS OrderCanonicalId,
                    oc.OrderNo,
                    oc.SourceSystem,
                    oc.SourceOrderId,
                    oc.MaterialCode,
                    oc.FactoryCode,
                    oc.OrderType,
                    oc.BOMNO AS RequestedBOMNO
                FROM Order_Canonical oc
                WHERE oc.Status = N'OPEN'
                  AND (
                      (oc.OrderType = 'SALES_ORDER'
                       AND oc.DueDate BETWEEN CAST(GETDATE() AS DATE) AND DATEADD(DAY, 90, CAST(GETDATE() AS DATE)))
                      OR
                      (oc.OrderType = 'PRODUCTION_INSTRUCTION')
                  )";

            var activeOrders = (await _connectionManager.QueryAsync<BOMRequestDetailDto>(
                detailSql, db: DatabaseId.APS, commandTimeout: 120)).ToList();

            if (activeOrders.Count == 0)
            {
                _logger.LogWarning("活跃订单明细为空");
                // 只建了批头、没有明细 ⇒ 必须留痕，否则 00:05 只能看到「今日无 SUCCESS 推送」而不知原因。
                await TryLogPushAsync(batchNo, "SKIPPED",
                    $"活跃订单明细为空（仅建批头，未推明细） | NightKey={BomNightBatchIdentity.NightKey(DateTime.Now)}");
                return new BOMRequestResult { BatchNo = batchNo, RootCount = rootCount, TotalOrderCount = 0 };
            }

            var dataTable = new DataTable("MES_API_BOM_Request_Detail");
            dataTable.Columns.Add("BatchNo", typeof(string));
            dataTable.Columns.Add("OrderCanonicalId", typeof(long));
            dataTable.Columns.Add("OrderNo", typeof(string));
            dataTable.Columns.Add("SourceSystem", typeof(string));
            dataTable.Columns.Add("SourceOrderId", typeof(string));
            dataTable.Columns.Add("MaterialCode", typeof(string));
            dataTable.Columns.Add("FactoryCode", typeof(string));
            dataTable.Columns.Add("OrderType", typeof(string));
            dataTable.Columns.Add("RequestedBOMNO", typeof(string));

            foreach (var order in activeOrders)
            {
                dataTable.Rows.Add(
                    batchNo,
                    order.OrderCanonicalId,
                    (object?)order.OrderNo ?? DBNull.Value,
                    (object?)order.SourceSystem ?? DBNull.Value,
                    (object?)order.SourceOrderId ?? DBNull.Value,
                    (object?)order.MaterialCode ?? DBNull.Value,
                    (object?)order.FactoryCode ?? DBNull.Value,
                    (object?)order.OrderType ?? DBNull.Value,
                    (object?)order.RequestedBOMNO ?? DBNull.Value);
            }

            await _connectionManager.BulkInsertAsync(dataTable, "MES_API_BOM_Request_Detail", DatabaseId.ODS);

            stopwatch.Stop();
            _logger.LogInformation(
                "BOM推送完成: BatchNo={BatchNo}, 订单数={Count}, 耗时={Elapsed}ms",
                batchNo, activeOrders.Count, stopwatch.ElapsedMilliseconds);

            // ── 0号位 2026-10-17 BOM 收口 §三.A：**真实提交成功之后**才写这条 SUCCESS 日志 ──
            //   00:05 的 bom-batch-expand 靠它拿到「本次推送」的准确批号，不再扫「最新 PENDING」猜。
            //   `NightKey` = 本次夜间推送的本地日历日（yyyyMMdd），是跨 Job 的关联键；
            //   同日多批时由展开侧判「无法唯一识别 ⇒ SKIPPED + Issue」，不猜。
            await TryLogPushAsync(batchNo, "SUCCESS",
                $"推送成功 | NightKey={BomNightBatchIdentity.NightKey(DateTime.Now)};Roots={rootCount};Details={activeOrders.Count}");

            return new BOMRequestResult
            {
                BatchNo = batchNo,
                RootCount = rootCount,
                TotalOrderCount = activeOrders.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BOM推送失败: BatchNo={BatchNo}", batchNo);
            throw;
        }
    }

    /// <summary>
    /// 记录本次推送的身份到 `APS_ETL_Log`（0号位 2026-10-17 §三.A）。
    ///
    /// 写失败**不得**触发「再生成一个批次当原批成功」—— ODS 侧已提交，重推会产生两个批、
    /// 让本已可唯一识别的身份变成多义。此处只显性登记缺口：下次 00:05 找不到本次 SUCCESS 记录 ⇒ `SKIPPED`，
    /// 由人工按既有 BatchNo 与 ODS 对账后处理。
    /// </summary>
    private async Task TryLogPushAsync(string batchNo, string status, string message)
    {
        try
        {
            await _connectionManager.ExecuteAsync(
                @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                  VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())",
                new { BatchNo = batchNo, Step = EtlStep, Message = message, Status = status },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BOM推送已在 ODS 提交成功，但 APS_ETL_Log 写入失败（BatchNo={BatchNo}）⇒ 本次夜间身份缺失，" +
                "00:05 将 SKIPPED；**不得自动重推**，须按既有 BatchNo 与 ODS 对账", batchNo);
        }
    }
}
