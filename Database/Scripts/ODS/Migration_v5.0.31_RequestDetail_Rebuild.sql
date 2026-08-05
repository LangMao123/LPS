USE [MES_Integration]
GO

-- =============================================
-- Migration v5.0.31: MES_API_BOM_Request_Detail 锚点升级
-- 日期: 2026-05-26
-- 变更:
--   锚点从 OrderStagingId → OrderCanonicalId
--   BOMNO → RequestedBOMNO
--   删除 Model / OrderStagingId（v5.0.32收敛）
--   新增 OrderNo / SourceSystem / SourceOrderId
--   唯一约束重建为 (BatchNo, OrderCanonicalId)
-- =============================================

PRINT N'=== ODS Migration v5.0.31/v5.0.32 开始 ===';

-- =============================================
-- 策略：重建表（数据量不大，且结构变化较大）
-- 如果生产环境已有数据需要保留，请先备份再执行
-- =============================================

-- 1. 备份旧表（如果存在）
IF OBJECT_ID(N'[dbo].[MES_API_BOM_Request_Detail]', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[dbo].[MES_API_BOM_Request_Detail_Backup_v5030]', N'U') IS NULL
    BEGIN
        SELECT * INTO MES_API_BOM_Request_Detail_Backup_v5030
        FROM MES_API_BOM_Request_Detail;
        PRINT N'旧表已备份到 MES_API_BOM_Request_Detail_Backup_v5030';
    END

    -- 删除旧约束和索引
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'MES_API_BOM_Request_Detail') AND name = N'IX_BOMRequestDetail_Batch')
        DROP INDEX IX_BOMRequestDetail_Batch ON MES_API_BOM_Request_Detail;

    DROP TABLE MES_API_BOM_Request_Detail;
    PRINT N'旧表已删除';
END
GO

-- 2. 创建新结构（v5.0.32 最终版）
CREATE TABLE MES_API_BOM_Request_Detail (
    Id               BIGINT          PRIMARY KEY NONCLUSTERED IDENTITY(1,1),
    BatchNo          NVARCHAR(50)    NOT NULL,
    OrderCanonicalId BIGINT          NOT NULL,
    OrderNo          NVARCHAR(100)   NULL,
    SourceSystem     NVARCHAR(20)    NULL,
    SourceOrderId    NVARCHAR(100)   NULL,
    MaterialCode     NVARCHAR(100)   NULL,
    FactoryCode      NVARCHAR(50)    NULL,
    OrderType        NVARCHAR(50)    NULL,
    RequestedBOMNO   NVARCHAR(50)    NULL,
    CreatedAt        DATETIME2       NOT NULL DEFAULT GETDATE(),

    FOREIGN KEY (BatchNo) REFERENCES MES_API_BOM_Request(BatchNo),
    CONSTRAINT UQ_BOMRequestDetail_BatchCanonical UNIQUE (BatchNo, OrderCanonicalId)
);
GO

CREATE CLUSTERED INDEX IX_BOMRequestDetail_Batch
ON MES_API_BOM_Request_Detail(BatchNo, OrderCanonicalId);
GO

PRINT N'MES_API_BOM_Request_Detail 已重建为 v5.0.32 结构';
PRINT N'=== ODS Migration v5.0.31/v5.0.32 完成 ===';
GO
