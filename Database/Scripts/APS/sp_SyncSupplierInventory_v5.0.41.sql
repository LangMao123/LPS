USE [APS_Production]
GO

-- =============================================
-- 供应商库存同步 v5.0.41 部署脚本
-- 日期: 2026-07-08
-- 变更摘要:
--   1. 新增 SupplierInventorySnapshot 表 - 存储供应商库存+在途采购单快照
--   2. 新增 sp_SyncSupplierInventory 存储过程 - 从采购系统同步
--   3. 数据来源：Procurement_SupplierInventory_View + Procurement_PO_InTransit_View
--   4. 调度频率：每小时执行（文档要求）
--
-- 设计要点：
--   - 供应商库存：供应商仓库中可用的物料数量
--   - 在途采购单：已发货但未到货的 PO 行项
--   - 用于 ATP 计算和采购建议
--   - 支持多供应商同一物料场景
-- =============================================

PRINT N'=== 供应商库存同步 v5.0.41 部署开始 ===';
GO

-- ============================================================
-- PART 1: SYNONYM（跨库访问采购系统视图）
-- ============================================================
PRINT N'[PART 1] SYNONYM 设置...';

IF OBJECT_ID('ext_Procurement_SupplierInventory_View', 'SN') IS NOT NULL
    DROP SYNONYM ext_Procurement_SupplierInventory_View;
GO
CREATE SYNONYM ext_Procurement_SupplierInventory_View
    FOR [procurement].[Procurement_Integration].[dbo].[SupplierInventory_View];
GO

IF OBJECT_ID('ext_Procurement_PO_InTransit_View', 'SN') IS NOT NULL
    DROP SYNONYM ext_Procurement_PO_InTransit_View;
GO
CREATE SYNONYM ext_Procurement_PO_InTransit_View
    FOR [procurement].[Procurement_Integration].[dbo].[PO_InTransit_View];
GO

-- ============================================================
-- PART 2: SupplierInventorySnapshot（供应商库存快照表）
-- ============================================================
PRINT N'[PART 2] SupplierInventorySnapshot...';

IF OBJECT_ID('SupplierInventorySnapshot', 'U') IS NOT NULL
    DROP TABLE SupplierInventorySnapshot;
GO

CREATE TABLE SupplierInventorySnapshot (
    Id                  BIGINT PRIMARY KEY IDENTITY(1,1),

    -- 物料标识
    MaterialCode        NVARCHAR(50) NOT NULL,
    MaterialId          INT NULL,                    -- 映射后的 APS 物料ID

    -- 供应商信息
    SupplierCode        NVARCHAR(50) NOT NULL,       -- 供应商代码
    SupplierName        NVARCHAR(200) NULL,          -- 供应商名称

    -- 库存数量
    AvailableQty        DECIMAL(18,4) NOT NULL,      -- 供应商仓库可用量
    InTransitQty        DECIMAL(18,4) NOT NULL DEFAULT 0, -- 在途采购单数量

    -- 时间信息
    EstimatedArrivalTime DATETIME2 NULL,             -- 在途最早到货时间（ETA）
    LeadTimeDays        INT NULL,                    -- 供应商交期（天）

    -- 采购单信息（在途）
    PONumber            NVARCHAR(100) NULL,          -- 采购订单号
    POLineNumber        INT NULL,                    -- 采购订单行号

    -- 供给类型
    SupplyType          NVARCHAR(20) NOT NULL        -- SUPPLIER_STOCK / PO_IN_TRANSIT
        CONSTRAINT CK_Supplier_SupplyType CHECK (SupplyType IN ('SUPPLIER_STOCK', 'PO_IN_TRANSIT')),

    -- 审计字段
    SyncedAt            DATETIME2 NOT NULL DEFAULT GETDATE(),
    UpdatedAt           DATETIME2 NOT NULL DEFAULT GETDATE(),

    -- 业务唯一键
    CONSTRAINT UQ_Supplier_Inventory UNIQUE (MaterialCode, SupplierCode, SupplyType, PONumber, POLineNumber)
);
GO

-- 索引：按物料查询（ATP 计算）
CREATE INDEX IX_Supplier_Material
    ON SupplierInventorySnapshot(MaterialId, SupplyType)
    INCLUDE (AvailableQty, InTransitQty, EstimatedArrivalTime);
GO

-- 索引：按供应商查询
CREATE INDEX IX_Supplier_Code
    ON SupplierInventorySnapshot(SupplierCode, MaterialId)
    INCLUDE (AvailableQty, InTransitQty);
GO

-- ============================================================
-- PART 3: sp_SyncSupplierInventory 存储过程
-- ============================================================
PRINT N'[PART 3] sp_SyncSupplierInventory...';
GO

CREATE OR ALTER PROCEDURE sp_SyncSupplierInventory
    @BatchNo          NVARCHAR(50) = NULL,           -- 批次号（可选）
    @RowsAffected     INT OUTPUT,                    -- 输出：影响行数
    @ErrorMessage     NVARCHAR(MAX) OUTPUT           -- 输出：错误信息
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartTime DATETIME2 = GETDATE();
    DECLARE @StepName  NVARCHAR(100);
    DECLARE @StockRows INT = 0;
    DECLARE @PORows    INT = 0;

    -- 生成批次号
    IF @BatchNo IS NULL
        SET @BatchNo = FORMAT(GETDATE(), 'yyyyMMddHHmmss');

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ============================================================
        -- Step 1: 从采购系统读取供应商库存（SUPPLIER_STOCK）
        -- ============================================================
        SET @StepName = 'Step1_LoadSupplierStock';

        DROP TABLE IF EXISTS #SupplierStockRaw;

        SELECT
            MaterialCode    = LTRIM(RTRIM(ods.MaterialCode)),
            SupplierCode    = LTRIM(RTRIM(ods.SupplierCode)),
            SupplierName    = LTRIM(RTRIM(ods.SupplierName)),
            AvailableQty    = ods.AvailableQty,
            LeadTimeDays    = ods.LeadTimeDays
        INTO #SupplierStockRaw
        FROM ext_Procurement_SupplierInventory_View ods
        WHERE ods.AvailableQty > 0
          AND ods.MaterialCode IS NOT NULL
          AND ods.SupplierCode IS NOT NULL;

        PRINT N'[' + @StepName + N'] 从采购系统读取库存: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 2: 从采购系统读取在途采购单（PO_IN_TRANSIT）
        -- ============================================================
        SET @StepName = 'Step2_LoadPOInTransit';

        DROP TABLE IF EXISTS #POInTransitRaw;

        SELECT
            MaterialCode    = LTRIM(RTRIM(ods.MaterialCode)),
            SupplierCode    = LTRIM(RTRIM(ods.SupplierCode)),
            SupplierName    = LTRIM(RTRIM(ods.SupplierName)),
            InTransitQty    = ods.Quantity,
            EstimatedArrivalTime = ods.ETA,
            PONumber        = LTRIM(RTRIM(ods.PONumber)),
            POLineNumber    = ods.POLineNumber
        INTO #POInTransitRaw
        FROM ext_Procurement_PO_InTransit_View ods
        WHERE ods.Quantity > 0
          AND ods.MaterialCode IS NOT NULL
          AND ods.SupplierCode IS NOT NULL
          AND ods.PONumber IS NOT NULL;

        PRINT N'[' + @StepName + N'] 从采购系统读取在途PO: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 3: JOIN Material 映射 - 供应商库存
        -- ============================================================
        SET @StepName = 'Step3_MapSupplierStock';

        DROP TABLE IF EXISTS #SupplierStockMapped;

        SELECT
            raw.MaterialCode,
            MaterialId      = m.Id,
            raw.SupplierCode,
            raw.SupplierName,
            raw.AvailableQty,
            InTransitQty    = CAST(0 AS DECIMAL(18,4)),
            EstimatedArrivalTime = CAST(NULL AS DATETIME2),
            raw.LeadTimeDays,
            PONumber        = CAST(NULL AS NVARCHAR(100)),
            POLineNumber    = CAST(NULL AS INT),
            SupplyType      = 'SUPPLIER_STOCK'
        INTO #SupplierStockMapped
        FROM #SupplierStockRaw raw
        INNER JOIN Material m
            ON CAST(m.MasterID AS NVARCHAR(50)) = raw.MaterialCode
           AND m.IsActive = 1;

        SET @StockRows = @@ROWCOUNT;
        PRINT N'[' + @StepName + N'] 映射成功: ' + CAST(@StockRows AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 4: JOIN Material 映射 - 在途PO
        -- ============================================================
        SET @StepName = 'Step4_MapPOInTransit';

        DROP TABLE IF EXISTS #POInTransitMapped;

        SELECT
            raw.MaterialCode,
            MaterialId      = m.Id,
            raw.SupplierCode,
            raw.SupplierName,
            AvailableQty    = CAST(0 AS DECIMAL(18,4)),
            raw.InTransitQty,
            raw.EstimatedArrivalTime,
            LeadTimeDays    = CAST(NULL AS INT),
            raw.PONumber,
            raw.POLineNumber,
            SupplyType      = 'PO_IN_TRANSIT'
        INTO #POInTransitMapped
        FROM #POInTransitRaw raw
        INNER JOIN Material m
            ON CAST(m.MasterID AS NVARCHAR(50)) = raw.MaterialCode
           AND m.IsActive = 1;

        SET @PORows = @@ROWCOUNT;
        PRINT N'[' + @StepName + N'] 映射成功: ' + CAST(@PORows AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 5: 合并两个数据源
        -- ============================================================
        SET @StepName = 'Step5_UnionData';

        DROP TABLE IF EXISTS #SupplierUnion;

        SELECT * INTO #SupplierUnion FROM #SupplierStockMapped
        UNION ALL
        SELECT * FROM #POInTransitMapped;

        PRINT N'[' + @StepName + N'] 合并数据: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 6: TRUNCATE + INSERT SupplierInventorySnapshot（全量替换）
        -- ============================================================
        SET @StepName = 'Step6_RefreshSnapshot';

        TRUNCATE TABLE SupplierInventorySnapshot;

        INSERT INTO SupplierInventorySnapshot (
            MaterialCode, MaterialId,
            SupplierCode, SupplierName,
            AvailableQty, InTransitQty,
            EstimatedArrivalTime, LeadTimeDays,
            PONumber, POLineNumber,
            SupplyType,
            SyncedAt, UpdatedAt
        )
        SELECT
            MaterialCode, MaterialId,
            SupplierCode, SupplierName,
            AvailableQty, InTransitQty,
            EstimatedArrivalTime, LeadTimeDays,
            PONumber, POLineNumber,
            SupplyType,
            GETDATE(), GETDATE()
        FROM #SupplierUnion;

        SET @RowsAffected = @@ROWCOUNT;
        PRINT N'[' + @StepName + N'] 全量刷新: ' + CAST(@RowsAffected AS NVARCHAR(20)) + N' 行';

        -- 成功日志
        INSERT INTO APS_ETL_Log (BatchNo, StepName, LogLevel, Message, CreatedAt)
        VALUES (
            @BatchNo,
            'SyncSupplierInventory',
            'INFO',
            N'供应商库存同步成功: 库存' + CAST(@StockRows AS NVARCHAR(20))
                + N'行 + 在途PO' + CAST(@PORows AS NVARCHAR(20))
                + N'行, 总计' + CAST(@RowsAffected AS NVARCHAR(20))
                + N'行, 耗时 ' + CAST(DATEDIFF(MILLISECOND, @StartTime, GETDATE()) AS NVARCHAR(20)) + N'ms',
            GETDATE()
        );

        COMMIT TRANSACTION;
        SET @ErrorMessage = NULL;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMessage = N'[' + @StepName + N'] ' + ERROR_MESSAGE();
        SET @RowsAffected = 0;

        INSERT INTO APS_ETL_Log (BatchNo, StepName, LogLevel, Message, CreatedAt)
        VALUES (
            @BatchNo,
            @StepName,
            'ERROR',
            @ErrorMessage,
            GETDATE()
        );

        PRINT N'错误: ' + @ErrorMessage;
    END CATCH;
END;
GO

-- ============================================================
-- PART 4: 验证脚本（注释）
-- ============================================================
/*
-- 测试执行
DECLARE @Rows INT, @Err NVARCHAR(MAX);
EXEC sp_SyncSupplierInventory
    @BatchNo = 'TEST_20260708',
    @RowsAffected = @Rows OUTPUT,
    @ErrorMessage = @Err OUTPUT;

SELECT @Rows AS RowsAffected, @Err AS ErrorMessage;

-- 查询供应商库存快照
SELECT
    MaterialCode,
    SupplierCode,
    SupplierName,
    AvailableQty,
    InTransitQty,
    SupplyType,
    EstimatedArrivalTime,
    UpdatedAt
FROM SupplierInventorySnapshot
ORDER BY MaterialCode, SupplierCode, SupplyType;

-- 按物料汇总供应商供给能力
SELECT
    MaterialCode,
    COUNT(DISTINCT SupplierCode) AS SupplierCount,
    SUM(AvailableQty) AS TotalAvailableQty,
    SUM(InTransitQty) AS TotalInTransitQty
FROM SupplierInventorySnapshot
GROUP BY MaterialCode
ORDER BY TotalAvailableQty + TotalInTransitQty DESC;

-- 查询日志
SELECT TOP 20 *
FROM APS_ETL_Log
WHERE BatchNo LIKE 'TEST_%'
ORDER BY CreatedAt DESC;
*/

PRINT N'=== 供应商库存同步 v5.0.41 部署完成 ===';
GO
