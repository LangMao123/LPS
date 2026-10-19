global using Task = System.Threading.Tasks.Task;
using System.Data;
using System.Text.Json;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// ERP订单同步服务（2号位职责）
/// 遵循 Socket-Plug 模式：通过 ODS v_APS_SalesOrder 视图间接拉取，不直连ERP
/// 数据路径：ODS.v_APS_SalesOrder → APS.ERP_Order_Staging → sp_ValidateAndPromoteOrders → APS.Order_Canonical
/// 
/// 同步策略：
/// - 全量同步：每日凌晨，拉取所有未取消/未完成订单（日期窗口过滤延迟到ETL层）
/// - 增量同步：每小时，基于 UpdatedAt 水位线拉取变更订单；**提升整体成功才推进水位线**，
///   整体失败保持原值以便下次重拉（逐行提升失败属正常业务分支，仍推进）
/// - 新BOMNO嗅探：增量同步时检测 Order_Canonical 中不存在的BOMNO，触发实时BOM展开
/// </summary>
public class ERPOrderSyncService : IERPOrderSyncService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IOrderPromotionService _promotionService;
    private readonly IOrderLoadingService _orderLoadingService;
    private readonly ILogger<ERPOrderSyncService> _logger;

    public ERPOrderSyncService(
        DatabaseConnectionManager connectionManager,
        IOrderPromotionService promotionService,
        IOrderLoadingService orderLoadingService,
        ILogger<ERPOrderSyncService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _promotionService = promotionService ?? throw new ArgumentNullException(nameof(promotionService));
        _orderLoadingService = orderLoadingService ?? throw new ArgumentNullException(nameof(orderLoadingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 【白天候选】订单池同步：把 Order_Canonical 的增量订单压进当前 ACTIVE PlanVersion 的 [Order]。
    /// 用户从该订单池选单做插单试排（PM 0923「白天 2号位 自己写 [Order]」）。
    /// 幂等（底层 SP 有 NOT EXISTS 保护）；**失败不阻断 ERP 订单同步主流程**（仅记日志）。
    /// </summary>
    private async Task SyncOrdersToActivePartitionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inserted = await _orderLoadingService.LoadOrdersToActivePlanVersionsAsync(cancellationToken);
            if (inserted > 0)
            {
                _logger.LogInformation("白天订单池同步：新增 {Inserted} 条订单压入 ACTIVE PlanVersion", inserted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "白天订单池同步失败（不阻断订单同步主流程）");
        }
    }

    /// <inheritdoc />
    public async Task FullSyncAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await CleanupOldStagingRecordsAsync();

            var orders = await QueryOrdersFromODSAsync(
                whereClause: "",
                parameters: null);

            if (orders.Count == 0)
            {
                _logger.LogWarning("全量同步：ODS视图未返回任何订单");
                return;
            }

            await BulkWriteToStagingAsync(orders);
            var (promoted, failed) = await _promotionService.ValidateAndPromoteAsync(cancellationToken);
            var newBOMNOs = await DetectNewBOMNOsAsync();

            stopwatch.Stop();
            _logger.LogInformation(
                "全量同步完成: 拉取={Fetched}, 提升={Promoted}, 失败={Failed}, 新BOMNO={NewBOMNO}, 耗时={Elapsed}ms",
                orders.Count, promoted, failed, newBOMNOs.Count, stopwatch.ElapsedMilliseconds);

            if (newBOMNOs.Count > 0)
            {
                _logger.LogWarning("新BOMNO需实时展开: {BOMNOs}",
                    string.Join(", ", newBOMNOs.Take(20)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量同步失败");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task IncrementalSyncAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var lastSyncTime = await GetLastSyncWatermarkAsync();

            var orders = await QueryOrdersFromODSAsync(
                whereClause: "WHERE UpdatedAt > @LastSyncTime",
                parameters: new { LastSyncTime = lastSyncTime });

            if (orders.Count == 0)
            {
                await UpdateSyncWatermarkAsync(DateTime.Now);
                // 无增量订单也压一次（幂等补齐：首次上线 / 上次压单失败时）
                await SyncOrdersToActivePartitionAsync(cancellationToken);
                return;
            }

            var maxUpdatedAt = orders.Max(o => o.UpdatedAt);
            var promotionCompleted = false;

            try
            {
                await BulkWriteToStagingAsync(orders);
                var (promoted, failed) = await _promotionService.ValidateAndPromoteAsync(cancellationToken);
                var newBOMNOs = await DetectNewBOMNOsAsync();

                // 水位线只在**整条增量链**全部成功后推进（0号位 第二十二轮 J22-02 / P1）。
                // 若把本行放在 DetectNewBOMNOsAsync 之前：提升已成功、探测抛异常时，
                // finally 仍会推进水位线，而 catch 日志宣称「整体失败、水位线保持」——
                // 日志与真实重试边界不一致，下次增量不会再拉取同批订单。
                promotionCompleted = true;

                stopwatch.Stop();
                _logger.LogInformation(
                    "增量同步完成: 拉取={Fetched}, 提升={Promoted}, 失败={Failed}, 耗时={Elapsed}ms",
                    orders.Count, promoted, failed, stopwatch.ElapsedMilliseconds);

                if (newBOMNOs.Count > 0)
                {
                    _logger.LogWarning("新BOMNO需实时展开: {BOMNOs}",
                        string.Join(", ", newBOMNOs.Take(20)));
                }
            }
            catch (Exception ex)
            {
                // 整体失败（批量写 Staging、提升、或新 BOMNO 探测任一抛异常）⇒ 水位线保持原值，本批订单下次仍会被拉取。
                // 仅「逐行提升失败」（ValidateAndPromoteAsync 正常返回 failed > 0）才推进水位线，属正常业务分支。
                _logger.LogError(ex,
                    "增量同步整体失败，水位线保持 {LastSyncTime} 不回退推进，本批 {Count} 条订单下次重试",
                    lastSyncTime, orders.Count);
            }
            finally
            {
                if (promotionCompleted)
                {
                    await UpdateSyncWatermarkAsync(maxUpdatedAt);
                }
            }

            // 【白天候选】订单池同步：增量订单压进 ACTIVE PlanVersion 的 [Order]
            await SyncOrdersToActivePartitionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "增量同步失败");
            throw;
        }
    }

    /// <summary>
    /// 直接查询 ODS 库的 v_APS_SalesOrder 视图
    /// 遵守 Socket-Plug 红线：2号位不直连ERP，通过 ODS 视图拉取
    /// ⚠️ 原方案通过 APS SYNONYM → ODS 跨库查询，22万条耗时3分钟+
    ///    改为直连 ODS 库查询，耗时降至约10秒
    /// ⚠️ 全量同步时数据量大（22万条），设置5分钟超时
    /// </summary>
    // ↓ 以下五个 IO 边沿为 protected virtual：供单测覆写，验证水位线推进边界（J22-02 负例）。
    //   生产路径零覆写者，语义与 private 相同。
    protected virtual async Task<List<ERPOrderDto>> QueryOrdersFromODSAsync(string whereClause, object? parameters, int commandTimeout = 300)
    {
        var sql = $@"
            SELECT
                SourceOrderId,
                OrderNo,
                OrderType,
                MaterialCode,
                BOMNO,
                FactoryCode,
                SourceWarehouseProcessCode,
                Quantity,
                UOM,
                DueDate,
                OriginalDueDate,
                ReceivedQty,
                Priority,
                Status,
                SourceSystem,
                SourceMasterID,
                TransportMode,
                CustomerName,
                MTS_InstructionNo,
                CustomerCode,
                JPOrderNo,
                SalesOrderCategory,
                DemandMaturityStatus,
                CreatedAt,
                UpdatedAt
            FROM v_APS_SalesOrder
            {whereClause}
            ORDER BY UpdatedAt";

        var result = await _connectionManager.QueryAsync<ERPOrderDto>(
            sql,
            parameters,
            db: DatabaseId.ODS,
            commandTimeout: commandTimeout);

        return result.ToList();
    }

    /// <summary>
    /// 批量写入 ERP_Order_Staging 表（SyncStatus = PENDING）
    /// 使用 SqlBulkCopy 高性能写入
    /// ⚠️ 写入前按 OrderNo 去重，避免 ODS 视图返回重复数据导致违反唯一约束
    /// ⚠️ 写入后按「同一 OrderNo 只保留最新一条 PENDING」收敛：增量同步整体失败时不推进水位线
    ///    （见 IncrementalSyncAsync），下一轮会重新拉取同一批订单。若同一 OrderNo 存在两条 PENDING，
    ///    sp_ValidateAndPromoteOrders 的 MERGE 源侧无去重、按 OrderNo 匹配 ⇒ 8672「多行匹配同一目标行」
    ///    ⇒ 事务整体回滚 ⇒ 每次重试都失败，无法自愈。唯一约束为 (OrderNo, SyncedAt)，
    ///    重试写入新 SyncedAt 不会自动拦截，故须收敛。
    ///
    /// 【为什么是「写入后收敛」，不是「写入前全表清空」（0号位 第二十二轮 J22-01 / P0）】
    ///   · 写入前 `DELETE … WHERE SyncStatus='PENDING'` 无批次／来源约束，会把**其它批次、并发调用
    ///     正在处理**的待校验订单一并删掉；且 DELETE 与 BulkInsert 非原子，插入失败即「清零而新行未就绪」，
    ///     待处理事实凭空消失。
    ///   · 改为插入成功后，只删除「同一 OrderNo 已被更新 PENDING 覆盖」的旧行：
    ///     范围由 OrderNo ＋ SyncedAt 确定，其它订单的 PENDING 一条不碰；插入失败时旧行原样保留。
    ///   · 收敛失败时最坏结果是「同单号两条 PENDING」⇒ 下次提升 8672 ⇒ 下轮再收敛，属**可自愈**，
    ///     不再出现「事实丢失且不可恢复」。
    /// </summary>
    protected virtual async Task BulkWriteToStagingAsync(List<ERPOrderDto> orders)
    {
        // 按 OrderNo 去重（OrderNo 是 APS 业务主键），保留 UpdatedAt 最新的记录
        var distinctOrders = orders
            .GroupBy(o => o.OrderNo)
            .Select(g => g.OrderByDescending(o => o.UpdatedAt).First())
            .ToList();

        if (distinctOrders.Count < orders.Count)
        {
            _logger.LogWarning(
                "ODS视图存在重复OrderNo，已去重: 原始={Original}, 去重后={Distinct}",
                orders.Count, distinctOrders.Count);
        }

        var dataTable = new DataTable("ERP_Order_Staging");
        dataTable.Columns.Add("SourceOrderId", typeof(string));
        dataTable.Columns.Add("SourceSystem", typeof(string));
        dataTable.Columns.Add("SourceMasterID", typeof(int));
        dataTable.Columns.Add("OrderNo", typeof(string));
        dataTable.Columns.Add("OrderType", typeof(string));
        dataTable.Columns.Add("MaterialCode", typeof(string));
        dataTable.Columns.Add("FactoryCode", typeof(string));
        dataTable.Columns.Add("SourceWarehouseProcessCode", typeof(string));
        dataTable.Columns.Add("Quantity", typeof(decimal));
        dataTable.Columns.Add("UOM", typeof(string));
        dataTable.Columns.Add("DueDate", typeof(DateTime));
        dataTable.Columns.Add("OriginalDueDate", typeof(DateTime));
        // 视图的 CreatedAt 即 ERP 的 InstrIssueDate（订单/生产指示正式下发日期）。
        // 冻结 07 IssueDate=源事实字段，下游 sp_ValidateAndPromoteOrders /
        // sp_SyncOrdersToPartitionTable 均已透传，此前只断在装载这一处。
        dataTable.Columns.Add("IssueDate", typeof(DateTime));
        dataTable.Columns.Add("ReceivedQty", typeof(decimal));
        dataTable.Columns.Add("Priority", typeof(int));
        dataTable.Columns.Add("BOMNO", typeof(string));
        dataTable.Columns.Add("Status", typeof(string));
        // v5.0.3 源事实字段
        dataTable.Columns.Add("TransportMode", typeof(string));
        dataTable.Columns.Add("CustomerName", typeof(string));
        dataTable.Columns.Add("MTS_InstructionNo", typeof(string));
        // v5.0.24 原始字段（由 sp_ValidateAndPromoteOrders 派生标准化）
        dataTable.Columns.Add("CustomerCode", typeof(string));
        dataTable.Columns.Add("JPOrderNo", typeof(string));
        dataTable.Columns.Add("SalesOrderCategory", typeof(string));
        dataTable.Columns.Add("DemandMaturityStatus", typeof(string));
        dataTable.Columns.Add("RawData", typeof(string));
        dataTable.Columns.Add("SyncStatus", typeof(string));
        dataTable.Columns.Add("SyncedAt", typeof(DateTime));

        var now = DateTime.Now;
        foreach (var order in distinctOrders)  // 使用去重后的数据
        {
            dataTable.Rows.Add(
                order.SourceOrderId,
                order.SourceSystem ?? "ERP",
                order.SourceMasterID.HasValue ? (object)order.SourceMasterID.Value : DBNull.Value,
                order.OrderNo,
                order.OrderType,
                order.MaterialCode,
                order.FactoryCode,
                order.SourceWarehouseProcessCode,
                order.Quantity,
                order.UOM,
                order.DueDate,
                order.OriginalDueDate,
                order.CreatedAt,          // = InstrIssueDate → IssueDate
                order.ReceivedQty,
                order.Priority,
                order.BOMNO,
                order.Status ?? "OPEN",
                // v5.0.3 源事实字段
                (object?)order.TransportMode ?? DBNull.Value,
                (object?)order.CustomerName ?? DBNull.Value,
                (object?)order.MTS_InstructionNo ?? DBNull.Value,
                // v5.0.24 原始字段（由 sp_ValidateAndPromoteOrders 派生标准化）
                (object?)order.CustomerCode ?? DBNull.Value,
                (object?)order.JPOrderNo ?? DBNull.Value,
                (object?)order.SalesOrderCategory ?? DBNull.Value,
                (object?)order.DemandMaturityStatus ?? DBNull.Value,
                JsonSerializer.Serialize(order),
                "PENDING",
                now
            );
        }

        // 先写本次批次：写入失败时上一轮的残留 PENDING 原样保留（不丢事实）。
        await _connectionManager.BulkInsertAsync(dataTable, "ERP_Order_Staging", DatabaseId.APS);

        // 再收敛重复：同一 OrderNo 只保留 SyncedAt 最新的一条 PENDING（本次刚写入的那条）。
        // 影响范围仅限「本 OrderNo 自己」，不触碰其它批次／并发调用的 PENDING；
        // 被收敛的是同单号的旧待处理副本，该订单的最新事实仍在，不会造成订单事实丢失。
        var purged = await _connectionManager.ExecuteAsync(
            @"DELETE stale
              FROM ERP_Order_Staging AS stale
              WHERE stale.SyncStatus = 'PENDING'
                AND EXISTS (
                    SELECT 1
                    FROM ERP_Order_Staging AS newer
                    WHERE newer.OrderNo    = stale.OrderNo
                      AND newer.SyncStatus = 'PENDING'
                      AND (newer.SyncedAt > stale.SyncedAt
                           OR (newer.SyncedAt = stale.SyncedAt AND newer.Id > stale.Id))
                );",
            db: DatabaseId.APS);

        if (purged > 0)
        {
            _logger.LogWarning(
                "写入后收敛同单号旧 PENDING: {Count} 条（上轮整体失败遗留，已被本次最新记录覆盖）", purged);
        }
    }

    /// <summary>
    /// 检测新BOMNO（紧急插单支持）
    /// 对比最新Staging批次中的BOMNO与现有Order_Canonical，找出新增的BOMNO
    /// </summary>
    protected virtual async Task<List<string>> DetectNewBOMNOsAsync()
    {
        var sql = @"
            SELECT DISTINCT stg.BOMNO
            FROM ERP_Order_Staging stg
            WHERE stg.SyncStatus IN ('VALIDATED', 'PROCESSED')
              AND stg.ProcessedAt >= DATEADD(HOUR, -2, GETDATE())
              AND stg.BOMNO IS NOT NULL
              AND stg.BOMNO <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM Order_Canonical oc 
                  WHERE oc.BOMNO = stg.BOMNO 
                    AND oc.CreatedAt < DATEADD(HOUR, -2, GETDATE())
              )";

        var result = await _connectionManager.QueryAsync<string>(sql, db: DatabaseId.APS);
        return result.ToList();
    }

    /// <summary>
    /// 获取上次同步水位线（从APS_SyncWatermark表读取）
    /// ⚠️ 首次同步：返回一个很早的日期（2020-01-01），确保全量拉取所有历史订单
    /// ⚠️ 后续同步：返回上次水位线，拉取所有变更（不限制回溯时间）
    /// </summary>
    protected virtual async Task<DateTime> GetLastSyncWatermarkAsync()
    {
        try
        {
            var sql = @"
                SELECT TOP 1 LastSyncTime
                FROM APS_SyncWatermark
                WHERE SyncType = 'ERP_Order'
                ORDER BY LastSyncTime DESC";

            var result = await _connectionManager.QueryFirstOrDefaultAsync<DateTime?>(sql, db: DatabaseId.APS);

            if (result == null)
            {
                _logger.LogWarning("未找到同步水位线，将全量拉取");
                return new DateTime(2020, 1, 1);
            }

            return result.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "水位线查询失败，回退全量拉取");
            return new DateTime(2020, 1, 1);
        }
    }

    /// <summary>
    /// 更新同步水位线
    /// </summary>
    protected virtual async Task UpdateSyncWatermarkAsync(DateTime syncTime)
    {
        try
        {
            var sql = @"
                IF EXISTS (SELECT 1 FROM APS_SyncWatermark WHERE SyncType = 'ERP_Order')
                    UPDATE APS_SyncWatermark
                    SET LastSyncTime = @SyncTime, UpdatedAt = GETDATE()
                    WHERE SyncType = 'ERP_Order'
                ELSE
                    INSERT INTO APS_SyncWatermark (SyncType, LastSyncTime, UpdatedAt)
                    VALUES ('ERP_Order', @SyncTime, GETDATE())";

            await _connectionManager.ExecuteAsync(sql, new { SyncTime = syncTime }, db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新同步水位线失败（非致命错误，下次同步将使用旧水位线）");
        }
    }

    /// <summary>
    /// 清理Staging表历史数据
    /// 策略：
    /// - 全量同步前清理所有残留 PENDING 记录（马上要重新拉全量，旧的无意义）
    /// - 保留7天内已处理(PROCESSED)的记录用于审计追溯
    /// - 保留所有失败(FAILED)记录用于问题排查
    /// - 删除7天前已处理的记录，避免表无限增长
    /// 调用时机：全量同步开始前
    /// </summary>
    private async Task CleanupOldStagingRecordsAsync()
    {
        try
        {
            var sql = @"
                DELETE FROM ERP_Order_Staging
                WHERE SyncStatus = 'PENDING';

                DELETE FROM ERP_Order_Staging
                WHERE SyncStatus = 'PROCESSED'
                  AND ProcessedAt < DATEADD(DAY, -7, GETDATE())";

            var deletedCount = await _connectionManager.ExecuteAsync(sql, db: DatabaseId.APS);

            if (deletedCount > 0)
            {
                _logger.LogDebug("清理Staging历史: {Count} 条（含残留PENDING）", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理Staging历史记录失败（非致命）");
        }
    }
}
