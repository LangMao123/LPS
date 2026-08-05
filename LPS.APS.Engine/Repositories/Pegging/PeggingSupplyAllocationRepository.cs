using System.Data;
using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Pegging;

public class PeggingSupplyAllocationRepository : IPeggingSupplyAllocationRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PeggingSupplyAllocationRepository> _logger;

    public PeggingSupplyAllocationRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<PeggingSupplyAllocationRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<int> BulkInsertAsync(
        IEnumerable<PeggingSupplyAllocation> allocations,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[PeggingSupplyAllocation]
                ([PlanVersionId], [OrderId], [DemandMaterialId], [SupplyMaterialId],
                 [AllocatedQuantity], [UOM], [SupplySourceType], [SupplySourceId],
                 [SourceReference], [FactoryCode], [WarehouseCode], [LocationCode],
                 [BatchNumber], [ExpiryDate], [Priority], [AllocatedAt],
                 [IsConsumed], [Remarks], [CreatedAt])
            VALUES
                (@PlanVersionId, @OrderId, @DemandMaterialId, @SupplyMaterialId,
                 @AllocatedQuantity, @UOM, @SupplySourceType, @SupplySourceId,
                 @SourceReference, @FactoryCode, @WarehouseCode, @LocationCode,
                 @BatchNumber, @ExpiryDate, @Priority, @AllocatedAt,
                 @IsConsumed, @Remarks, @CreatedAt)";

        var rows = allocations.ToList();
        if (rows.Count == 0) return 0;

        return await _connectionManager.ExecuteInTransactionAsync<int>(
            async (conn, tx) =>
            {
                var affected = 0;
                foreach (var batch in rows.Chunk(1000))
                {
                    affected += await conn.ExecuteAsync(sql, batch, tx);
                }
                return affected;
            },
            DatabaseId.APS);
    }

    public async Task<IEnumerable<PeggingSupplyAllocation>> GetByPlanVersionIdAsync(
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[PeggingSupplyAllocation]
            WHERE [PlanVersionId] = @PlanVersionId
            ORDER BY [Priority], [Id]";

        return await _connectionManager.QueryAsync<PeggingSupplyAllocation>(
            sql, new { PlanVersionId = planVersionId });
    }

    public async Task<IEnumerable<PeggingSupplyAllocation>> GetByOrderIdAsync(
        int planVersionId,
        long orderId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[PeggingSupplyAllocation]
            WHERE [PlanVersionId] = @PlanVersionId
              AND [OrderId] = @OrderId
            ORDER BY [Priority], [Id]";

        return await _connectionManager.QueryAsync<PeggingSupplyAllocation>(
            sql, new { PlanVersionId = planVersionId, OrderId = orderId });
    }

    public async Task<IEnumerable<PeggingSupplyAllocation>> GetBySupplySourceAsync(
        int planVersionId,
        string supplySourceType,
        long? supplySourceId = null,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT * FROM [dbo].[PeggingSupplyAllocation]
            WHERE [PlanVersionId] = @PlanVersionId
              AND [SupplySourceType] = @SupplySourceType";

        if (supplySourceId.HasValue)
            sql += " AND [SupplySourceId] = @SupplySourceId";

        return await _connectionManager.QueryAsync<PeggingSupplyAllocation>(
            sql, new { PlanVersionId = planVersionId, SupplySourceType = supplySourceType, SupplySourceId = supplySourceId });
    }

    public async Task<IEnumerable<PeggingSupplyAllocation>> GetByBatchNumberAsync(
        int planVersionId,
        string batchNumber,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[PeggingSupplyAllocation]
            WHERE [PlanVersionId] = @PlanVersionId
              AND [BatchNumber] = @BatchNumber
            ORDER BY [ExpiryDate], [Id]";

        return await _connectionManager.QueryAsync<PeggingSupplyAllocation>(
            sql, new { PlanVersionId = planVersionId, BatchNumber = batchNumber });
    }

    public async Task<int> MarkAsConsumedAsync(
        int planVersionId,
        IEnumerable<long> allocationIds,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE [dbo].[PeggingSupplyAllocation]
            SET [IsConsumed] = 1
            WHERE [PlanVersionId] = @PlanVersionId
              AND [Id] IN @Ids";

        return await _connectionManager.ExecuteAsync(
            sql, new { PlanVersionId = planVersionId, Ids = allocationIds });
    }

    public async Task<int> DeleteByPlanVersionIdAsync(
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM [dbo].[PeggingSupplyAllocation] WHERE [PlanVersionId] = @PlanVersionId";

        return await _connectionManager.ExecuteAsync(sql, new { PlanVersionId = planVersionId });
    }

    public async Task<int> CountUnconsumedAsync(
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT COUNT(1) FROM [dbo].[PeggingSupplyAllocation]
            WHERE [PlanVersionId] = @PlanVersionId
              AND [IsConsumed] = 0";

        return await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, new { PlanVersionId = planVersionId });
    }
}
