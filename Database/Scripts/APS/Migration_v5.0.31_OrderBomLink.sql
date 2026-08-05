USE [APS_Production]
GO

-- =============================================
-- Migration v5.0.31: Order→BOM追溯链闭合
-- 日期: 2026-05-26
-- 变更:
--   1. [Order] 表新增 OrderCanonicalId 字段 + 索引
--   2. 新建 OrderBomRequestLink 表（APS本地订单-BOM解析结果索引表）
-- =============================================

PRINT N'=== Migration v5.0.31 开始 ===';

-- =============================================
-- PART 1: [Order] 表新增 OrderCanonicalId
-- =============================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[Order]')
      AND name = N'OrderCanonicalId'
)
BEGIN
    ALTER TABLE [Order] ADD OrderCanonicalId BIGINT NULL;
    PRINT N'[Order] 表已新增 OrderCanonicalId 字段';
END
ELSE
BEGIN
    PRINT N'[Order] 表已存在 OrderCanonicalId 字段，跳过';
END
GO

-- 索引：支持 Link 生成时按 PlanVersionId + OrderCanonicalId 查找 OrderId
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[dbo].[Order]')
      AND name = N'IX_Order_PlanVersion_OrderCanonical'
)
BEGIN
    CREATE INDEX IX_Order_PlanVersion_OrderCanonical
    ON [Order](PlanVersionId, OrderCanonicalId)
    INCLUDE (Id);
    PRINT N'索引 IX_Order_PlanVersion_OrderCanonical 已创建';
END
GO

-- =============================================
-- PART 2: 新建 OrderBomRequestLink 表
-- =============================================

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[OrderBomRequestLink]'))
BEGIN
    CREATE TABLE OrderBomRequestLink (
        Id               BIGINT          PRIMARY KEY IDENTITY(1,1),

        PlanVersionId    BIGINT          NOT NULL,
        BatchNo          NVARCHAR(50)    NOT NULL,

        OrderId          BIGINT          NULL,
        OrderCanonicalId BIGINT          NOT NULL,
        OrderNo          NVARCHAR(100)   NULL,
        SourceSystem     NVARCHAR(20)    NULL,
        SourceOrderId    NVARCHAR(100)   NULL,

        RequestDetailId  BIGINT          NOT NULL,

        RequestedBOMNO   NVARCHAR(50)    NULL,
        ResolvedBOMNO    NVARCHAR(50)    NULL,

        RepWorksetId     BIGINT          NULL,

        LinkStatus       NVARCHAR(30)    NOT NULL DEFAULT 'RESOLVED',
        ErrorMessage     NVARCHAR(1000)  NULL,

        SyncedAt         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT UQ_OrderBomRequestLink_Version_Canonical
            UNIQUE (PlanVersionId, OrderCanonicalId)
    );

    PRINT N'OrderBomRequestLink 表已创建';
END
GO

-- 查询索引
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[OrderBomRequestLink]') AND name = N'IX_OrderBomRequestLink_Batch_Request')
BEGIN
    CREATE INDEX IX_OrderBomRequestLink_Batch_Request
    ON OrderBomRequestLink (BatchNo, RequestDetailId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[OrderBomRequestLink]') AND name = N'IX_OrderBomRequestLink_BOMNO')
BEGIN
    CREATE INDEX IX_OrderBomRequestLink_BOMNO
    ON OrderBomRequestLink (BatchNo, ResolvedBOMNO);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[OrderBomRequestLink]') AND name = N'IX_OrderBomRequestLink_RepWorkset')
BEGIN
    CREATE INDEX IX_OrderBomRequestLink_RepWorkset
    ON OrderBomRequestLink (RepWorksetId)
    WHERE RepWorksetId IS NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[OrderBomRequestLink]') AND name = N'IX_OrderBomRequestLink_Order')
BEGIN
    CREATE INDEX IX_OrderBomRequestLink_Order
    ON OrderBomRequestLink (PlanVersionId, OrderId)
    WHERE OrderId IS NOT NULL;
END
GO

PRINT N'=== Migration v5.0.31 完成 ===';
GO
