USE [APS_Production]
GO

-- =============================================
-- v5.0.25 索引清理与重建
-- 日期：2026-05-18
-- 说明：Order_Canonical 有大量重复/冗余索引，统一清理后按业务路径重建
-- =============================================

-- ═══════════════════════════════════════════════
-- PART 1: 清理所有非 PK/UQ 的索引
-- ═══════════════════════════════════════════════

-- ─── ERP_Order_Staging ───
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_SourceKey' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_SourceKey ON ERP_Order_Staging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_SourceKey_Processed' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_SourceKey_Processed ON ERP_Order_Staging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_OrderNo_Processed' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_OrderNo_Processed ON ERP_Order_Staging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_Status' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_Status ON ERP_Order_Staging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_SyncStatus' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_SyncStatus ON ERP_Order_Staging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ERP_Order_Staging_NextAction' AND object_id = OBJECT_ID('ERP_Order_Staging'))
    DROP INDEX IX_ERP_Order_Staging_NextAction ON ERP_Order_Staging;

PRINT '清理 ERP_Order_Staging 完成';

-- ─── Order_Canonical ───
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_ActiveOrders' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_ActiveOrders ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_ActiveRoots' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_ActiveRoots ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_BOMNO' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_BOMNO ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_DueDate' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_DueDate ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_Material' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_Material ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_MaterialCode' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_MaterialCode ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_Type' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_Type ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_UpsertKey' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_UpsertKey ON Order_Canonical;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_SourceKey' AND object_id = OBJECT_ID('Order_Canonical'))
    DROP INDEX IX_Order_Canonical_SourceKey ON Order_Canonical;

PRINT '清理 Order_Canonical 完成';
GO

-- ═══════════════════════════════════════════════
-- PART 2: 重建（最小必要集合）
-- ═══════════════════════════════════════════════

-- ─── ERP_Order_Staging ───
-- 已有：PK(Id), UQ(OrderNo, SyncedAt)
-- BOM查询 JOIN ON OrderNo 走 UQ 前导列，不需要额外索引

-- SP PHASE 0: WHERE SyncStatus='PENDING' ORDER BY SyncedAt
-- 失败查询: WHERE SyncStatus='FAILED' ORDER BY SyncedAt DESC
-- 清理: WHERE SyncStatus='PROCESSED' AND ProcessedAt < ...
-- DetectNewBOMNOs: WHERE SyncStatus IN (...) AND ProcessedAt >= ... AND BOMNO ...
CREATE NONCLUSTERED INDEX IX_ERP_Order_Staging_SyncStatus
    ON ERP_Order_Staging(SyncStatus, SyncedAt ASC)
    INCLUDE (ProcessedAt, BOMNO);
PRINT '+ IX_ERP_Order_Staging_SyncStatus';
GO

-- ─── Order_Canonical ───
-- 已有：PK(Id), UQ(OrderNo)
-- MERGE Upsert 走 UQ(OrderNo)

-- 1. 活跃订单查询（覆盖 BOM推送/活跃根统计/订单装载 三个场景）
--    WHERE Status IN ('Open','Released') AND OrderType=... AND DueDate BETWEEN ...
CREATE NONCLUSTERED INDEX IX_Order_Canonical_ActiveOrders
    ON Order_Canonical(Status, OrderType, DueDate)
    INCLUDE (OrderNo, BOMNO, MaterialCode, FactoryCode, Quantity);
PRINT '+ IX_Order_Canonical_ActiveOrders';
GO

-- 2. BOMNO 查找（DetectNewBOMNOs + sp_GetActiveRootBOMNOs GROUP BY）
CREATE NONCLUSTERED INDEX IX_Order_Canonical_BOMNO
    ON Order_Canonical(BOMNO)
    INCLUDE (CreatedAt, Status, OrderType, DueDate, Quantity)
    WHERE BOMNO IS NOT NULL;
PRINT '+ IX_Order_Canonical_BOMNO';
GO

-- 3. MaterialCode（订单装载 JOIN Material + FK 关联）
CREATE NONCLUSTERED INDEX IX_Order_Canonical_MaterialCode
    ON Order_Canonical(MaterialCode, Status);
PRINT '+ IX_Order_Canonical_MaterialCode';
GO
