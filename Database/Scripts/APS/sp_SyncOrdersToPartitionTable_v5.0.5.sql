USE [APS_Production]
GO

-- =============================================
-- sp_SyncOrdersToPartitionTable 更新脚本
-- 版本: v5.0.5（补齐 v5.0.4 + v5.0.5 缺失字段）
-- 日期: 2026-05-11
-- 变更: 补充 CustomerTier / IssueDate / OriginalDueDate / ReceivedQty 4个字段透传
-- =============================================

ALTER PROCEDURE [dbo].[sp_SyncOrdersToPartitionTable]
    @PlanVersionId INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SyncTime DATETIME2 = GETDATE();
    DECLARE @InsertCount INT = 0;

    -- 从Order_Canonical同步到Order分区表
    -- 只同步状态为Open/Released且在计划窗口内的订单
    -- Order_Canonical 中的数据已经过 sp_ValidateAndPromoteOrders 校验
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
        CreatedAt,
        UpdatedAt
    )
    SELECT
        @PlanVersionId,
        oc.OrderNo,
        oc.OrderType,
        m.Id AS MaterialId,
        m.ProductFamilyId,
        ISNULL(f.Id, 1) AS FactoryId,
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
        oc.CreatedAt,
        @SyncTime
    FROM Order_Canonical oc
    INNER JOIN Material m ON oc.MaterialCode = m.MaterialCode
    LEFT JOIN Factory f ON f.Code = oc.FactoryCode
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
