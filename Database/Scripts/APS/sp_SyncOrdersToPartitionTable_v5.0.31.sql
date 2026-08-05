USE [APS_Production]
GO

-- =============================================
-- sp_SyncOrdersToPartitionTable v5.0.31
-- 日期: 2026-05-26
-- 变更: 新增 OrderCanonicalId 字段透传（oc.Id AS OrderCanonicalId）
--       支持 OrderBomRequestLink 生成时按 PlanVersionId + OrderCanonicalId 查找 OrderId
-- v5.0.38: ProductFamilyId 允许 NULL（Material 产品族解析未上线前为空）
-- v5.0.40: LEFT JOIN Factory → INNER JOIN（字典缺失不装载，写 WARN 日志）
-- =============================================

ALTER PROCEDURE [dbo].[sp_SyncOrdersToPartitionTable]
    @PlanVersionId INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SyncTime DATETIME2 = GETDATE();
    DECLARE @InsertCount INT = 0;

    -- 记录因 Factory 字典缺失而跳过的订单（最多 50 条 WARN）
    INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
    SELECT TOP 50
        'SYNC',
        'SyncOrdersToPartitionTable.FilteredOut',
        CONCAT('FactoryNotFound: OrderNo=', oc.OrderNo,
               ' FactoryCode=', ISNULL(oc.FactoryCode, 'NULL'),
               ' MaterialCode=', oc.MaterialCode),
        'WARN', GETDATE()
    FROM Order_Canonical oc
    INNER JOIN Material m ON oc.MaterialCode = m.MaterialCode
    LEFT JOIN Factory f ON f.Code = oc.FactoryCode
    WHERE oc.Status IN ('Open', 'Released')
      AND oc.DueDate BETWEEN GETDATE() AND DATEADD(DAY, 90, GETDATE())
      AND f.Id IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM [Order] o
          WHERE o.OrderNo = oc.OrderNo
            AND o.PlanVersionId = @PlanVersionId
      );

    INSERT INTO [Order] (
        PlanVersionId,
        OrderNo,
        OrderType,
        MaterialId,
        ProductFamilyId,
        FactoryId,
        Quantity,
        UOM,
        CustomerDueDate,
        PromisedDate,
        Priority,
        PriorityScore,
        Status,
        DomainKey,
        SourceSystem,
        SourceOrderId,
        MaterialCode,
        BOMNO,
        SourceMasterID,
        MTS_InstructionNo,
        TransportMode,
        CustomerName,
        CustomerSegment,
        SalesOrderCategory,
        DemandMaturityStatus,
        CustomerTier,
        IssueDate,
        OriginalDueDate,
        ReceivedQty,
        OrderCanonicalId,
        CreatedAt,
        UpdatedAt
    )
    SELECT
        @PlanVersionId,
        oc.OrderNo,
        oc.OrderType,
        m.Id AS MaterialId,
        m.ProductFamilyId,
        f.Id AS FactoryId,
        oc.Quantity,
        m.UOM,
        oc.DueDate AS CustomerDueDate,
        NULL AS PromisedDate,
        oc.Priority,
        NULL AS PriorityScore,
        oc.Status,
        NULL AS DomainKey,
        oc.SourceSystem,
        oc.SourceOrderId,
        oc.MaterialCode,
        oc.BOMNO,
        oc.SourceMasterID,
        oc.MTS_InstructionNo,
        oc.TransportMode,
        oc.CustomerName,
        oc.CustomerSegment,
        oc.SalesOrderCategory,
        oc.DemandMaturityStatus,
        oc.CustomerTier,
        oc.IssueDate,
        oc.OriginalDueDate,
        oc.ReceivedQty,
        oc.Id AS OrderCanonicalId,
        oc.CreatedAt,
        @SyncTime
    FROM Order_Canonical oc
    INNER JOIN Material m ON oc.MaterialCode = m.MaterialCode
    INNER JOIN Factory f ON f.Code = oc.FactoryCode AND f.IsActive = 1
    WHERE oc.Status IN ('Open', 'Released')
      AND oc.DueDate BETWEEN GETDATE() AND DATEADD(DAY, 90, GETDATE())
      AND NOT EXISTS (
          SELECT 1 FROM [Order] o
          WHERE o.OrderNo = oc.OrderNo
            AND o.PlanVersionId = @PlanVersionId
      );

    SET @InsertCount = @@ROWCOUNT;

    -- 记录日志
    INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
    VALUES ('SYNC', 'SyncOrdersToPartitionTable',
            N'订单装载完成，PlanVersionId: ' + CAST(@PlanVersionId AS NVARCHAR(10))
            + N'，装载订单数: ' + CAST(@InsertCount AS NVARCHAR(10)),
            'SUCCESS', GETDATE());

    -- 返回统计
    SELECT
        @PlanVersionId AS PlanVersionId,
        @InsertCount AS InsertCount,
        @SyncTime AS SyncTime;
END;
GO
