USE [APS_Production]
GO

-- =============================================
-- 在途库存同步 v5.0.41 部署脚本
-- 日期: 2026-07-08
-- 变更摘要:
--   1. 新增 InTransitInventoryFact 表（L1 事实层）- 存储厂间在途库存快照
--   2. 新增 sp_SyncInTransitInventory 存储过程 - 从 ODS 视图同步在途数据
--   3. 数据来源：ERP_InterplantInTransit_View（ODS 层已就绪）
--   4. 架构职责：2号位数据引擎负责同步，3号位MES网关负责状态更新
--
-- 设计要点：
--   - 只同步同域跨厂在途（INTER_FACTORY_ORDER 场景）
--   - 异域跨族不存在"在途管理"，使用虚拟库存
--   - 状态：IN_TRANSIT（在途）/ DELAYED（延迟）/ DELIVERED（已到货）
--   - ETA（预计到货时间）用于 ATP 计算
-- =============================================

PRINT N'=== 在途库存同步 v5.0.41 部署开始 ===';
GO

-- ============================================================
-- PART 1: SYNONYM（跨库访问 ODS 契约视图）
-- ============================================================
PRINT N'[PART 1] SYNONYM 设置...';

IF OBJECT_ID('ext_ERP_InterplantInTransit_View', 'SN') IS NOT NULL
    DROP SYNONYM ext_ERP_InterplantInTransit_View;
GO
CREATE SYNONYM ext_ERP_InterplantInTransit_View
    FOR [mes].[MES_Integration].[dbo].[ERP_InterplantInTransit_View];
GO

-- ============================================================
-- PART 2: InTransitInventoryFact（L1 在途库存事实层）
-- ============================================================
PRINT N'[PART 2] InTransitInventoryFact...';

IF OBJECT_ID('InTransitInventoryFact', 'U') IS NOT NULL
    DROP TABLE InTransitInventoryFact;
GO

CREATE TABLE InTransitInventoryFact (
    Id                  BIGINT PRIMARY KEY IDENTITY(1,1),

    -- 物料标识
    MaterialCode        NVARCHAR(50) NOT NULL,
    MaterialId          INT NULL,                    -- 映射后的 APS 物料ID

    -- 厂间物流信息
    SourceFactoryCode   NVARCHAR(50) NOT NULL,       -- 发出工厂代码
    SourceFactoryId     INT NULL,                    -- 映射后的 APS 工厂ID
    TargetFactoryCode   NVARCHAR(50) NOT NULL,       -- 目标工厂代码
    TargetFactoryId     INT NULL,                    -- 映射后的 APS 工厂ID

    -- 数量与时间
    Quantity            DECIMAL(18,4) NOT NULL,      -- 在途数量
    EstimatedArrivalTime DATETIME2 NULL,             -- 预计到货时间（ETA）
    ShippedAt           DATETIME2 NULL,              -- 发货时间

    -- 单据信息
    ShipmentDocNo       NVARCHAR(100) NULL,          -- 发货单号
    TransferOrderNo     NVARCHAR(100) NULL,          -- 调拨单号

    -- 状态
    Status              NVARCHAR(20) NOT NULL        -- IN_TRANSIT / DELAYED / DELIVERED
        CONSTRAINT CK_InTransit_Status CHECK (Status IN ('IN_TRANSIT', 'DELAYED', 'DELIVERED')),

    -- 审计字段
    SyncedAt            DATETIME2 NOT NULL DEFAULT GETDATE(),
    UpdatedAt           DATETIME2 NOT NULL DEFAULT GETDATE(),

    -- 业务唯一键
    CONSTRAINT UQ_InTransit_Doc UNIQUE (ShipmentDocNo, MaterialCode, SourceFactoryCode, TargetFactoryCode)
);
GO

-- 索引：按目标工厂+物料查询（Pegging 供给池装载）
CREATE INDEX IX_InTransit_Target_Material
    ON InTransitInventoryFact(TargetFactoryId, MaterialId, Status)
    INCLUDE (Quantity, EstimatedArrivalTime)
    WHERE Status = 'IN_TRANSIT';  -- 过滤索引：只索引在途状态
GO

-- 索引：按 ETA 查询（ATP 计算）
CREATE INDEX IX_InTransit_ETA
    ON InTransitInventoryFact(EstimatedArrivalTime, Status)
    INCLUDE (MaterialId, TargetFactoryId, Quantity)
    WHERE Status = 'IN_TRANSIT';
GO

-- ============================================================
-- PART 3: sp_SyncInTransitInventory 存储过程
-- ============================================================
PRINT N'[PART 3] sp_SyncInTransitInventory...';
GO

CREATE OR ALTER PROCEDURE sp_SyncInTransitInventory
    @BatchNo          NVARCHAR(50) = NULL,           -- 批次号（可选）
    @RowsAffected     INT OUTPUT,                    -- 输出：影响行数
    @ErrorMessage     NVARCHAR(MAX) OUTPUT           -- 输出：错误信息
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartTime DATETIME2 = GETDATE();
    DECLARE @StepName  NVARCHAR(100);

    -- 生成批次号
    IF @BatchNo IS NULL
        SET @BatchNo = FORMAT(GETDATE(), 'yyyyMMddHHmmss');

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ============================================================
        -- Step 1: 从 ODS 读取在途数据到临时表
        -- ============================================================
        SET @StepName = 'Step1_LoadFromODS';

        DROP TABLE IF EXISTS #InTransitRaw;

        SELECT
            MaterialCode        = LTRIM(RTRIM(ods.MaterialCode)),
            SourceFactoryCode   = LTRIM(RTRIM(ods.SourceFactoryCode)),
            TargetFactoryCode   = LTRIM(RTRIM(ods.TargetFactoryCode)),
            Quantity            = ods.Quantity,
            EstimatedArrivalTime = ods.ETA,
            ShippedAt           = ods.ShippedDate,
            ShipmentDocNo       = LTRIM(RTRIM(ods.ShipmentDocNo)),
            TransferOrderNo     = LTRIM(RTRIM(ods.TransferOrderNo)),
            Status              = CASE
                                    WHEN ods.ETA < GETDATE() THEN 'DELAYED'
                                    ELSE 'IN_TRANSIT'
                                  END
        INTO #InTransitRaw
        FROM ext_ERP_InterplantInTransit_View ods
        WHERE ods.Quantity > 0
          AND ods.MaterialCode IS NOT NULL
          AND ods.SourceFactoryCode IS NOT NULL
          AND ods.TargetFactoryCode IS NOT NULL;

        PRINT N'[' + @StepName + N'] 从 ODS 读取: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 2: JOIN Material / Factory 映射
        -- ============================================================
        SET @StepName = 'Step2_MapToAPS';

        DROP TABLE IF EXISTS #InTransitMapped;

        SELECT
            raw.MaterialCode,
            MaterialId          = m.Id,
            raw.SourceFactoryCode,
            SourceFactoryId     = sf.Id,
            raw.TargetFactoryCode,
            TargetFactoryId     = tf.Id,
            raw.Quantity,
            raw.EstimatedArrivalTime,
            raw.ShippedAt,
            raw.ShipmentDocNo,
            raw.TransferOrderNo,
            raw.Status
        INTO #InTransitMapped
        FROM #InTransitRaw raw
        INNER JOIN Material m
            ON CAST(m.MasterID AS NVARCHAR(50)) = raw.MaterialCode
           AND m.IsActive = 1
        LEFT JOIN Factory sf
            ON sf.Code = raw.SourceFactoryCode
           AND sf.IsActive = 1
        LEFT JOIN Factory tf
            ON tf.Code = raw.TargetFactoryCode
           AND tf.IsActive = 1
        WHERE m.Id IS NOT NULL
          AND sf.Id IS NOT NULL
          AND tf.Id IS NOT NULL;

        DECLARE @MappedRows INT = @@ROWCOUNT;
        PRINT N'[' + @StepName + N'] 映射成功: ' + CAST(@MappedRows AS NVARCHAR(20)) + N' 行';

        -- 记录过滤的行到日志
        DECLARE @FilteredRows INT = (SELECT COUNT(*) FROM #InTransitRaw) - @MappedRows;
        IF @FilteredRows > 0
        BEGIN
            INSERT INTO APS_ETL_Log (BatchNo, StepName, LogLevel, Message, CreatedAt)
            SELECT TOP 50
                @BatchNo,
                @StepName,
                'WARN',
                N'在途库存过滤: MaterialCode=' + raw.MaterialCode
                    + N', Source=' + raw.SourceFactoryCode
                    + N', Target=' + raw.TargetFactoryCode
                    + N' (Material/Factory 映射失败)',
                GETDATE()
            FROM #InTransitRaw raw
            WHERE NOT EXISTS (
                SELECT 1 FROM #InTransitMapped m
                WHERE m.MaterialCode = raw.MaterialCode
                  AND m.SourceFactoryCode = raw.SourceFactoryCode
                  AND m.TargetFactoryCode = raw.TargetFactoryCode
            );

            PRINT N'[' + @StepName + N'] 过滤行数: ' + CAST(@FilteredRows AS NVARCHAR(20)) + N' (已记录前50条到日志)';
        END;

        -- ============================================================
        -- Step 3: MERGE 到 InTransitInventoryFact（UPSERT）
        -- ============================================================
        SET @StepName = 'Step3_MergeToFact';

        MERGE InTransitInventoryFact AS target
        USING #InTransitMapped AS source
        ON target.ShipmentDocNo = source.ShipmentDocNo
           AND target.MaterialCode = source.MaterialCode
           AND target.SourceFactoryCode = source.SourceFactoryCode
           AND target.TargetFactoryCode = source.TargetFactoryCode
        WHEN MATCHED THEN
            UPDATE SET
                target.MaterialId = source.MaterialId,
                target.SourceFactoryId = source.SourceFactoryId,
                target.TargetFactoryId = source.TargetFactoryId,
                target.Quantity = source.Quantity,
                target.EstimatedArrivalTime = source.EstimatedArrivalTime,
                target.ShippedAt = source.ShippedAt,
                target.TransferOrderNo = source.TransferOrderNo,
                target.Status = source.Status,
                target.UpdatedAt = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (
                MaterialCode, MaterialId,
                SourceFactoryCode, SourceFactoryId,
                TargetFactoryCode, TargetFactoryId,
                Quantity, EstimatedArrivalTime, ShippedAt,
                ShipmentDocNo, TransferOrderNo,
                Status, SyncedAt, UpdatedAt
            )
            VALUES (
                source.MaterialCode, source.MaterialId,
                source.SourceFactoryCode, source.SourceFactoryId,
                source.TargetFactoryCode, source.TargetFactoryId,
                source.Quantity, source.EstimatedArrivalTime, source.ShippedAt,
                source.ShipmentDocNo, source.TransferOrderNo,
                source.Status, GETDATE(), GETDATE()
            );

        SET @RowsAffected = @@ROWCOUNT;
        PRINT N'[' + @StepName + N'] MERGE 完成: ' + CAST(@RowsAffected AS NVARCHAR(20)) + N' 行';

        -- ============================================================
        -- Step 4: 清理已到货的历史数据（可选：保留7天）
        -- ============================================================
        SET @StepName = 'Step4_CleanDelivered';

        DELETE FROM InTransitInventoryFact
        WHERE Status = 'DELIVERED'
          AND UpdatedAt < DATEADD(DAY, -7, GETDATE());

        IF @@ROWCOUNT > 0
            PRINT N'[' + @StepName + N'] 清理已到货记录: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' 行';

        -- 成功日志
        INSERT INTO APS_ETL_Log (BatchNo, StepName, LogLevel, Message, CreatedAt)
        VALUES (
            @BatchNo,
            'SyncInTransitInventory',
            'INFO',
            N'在途库存同步成功: ' + CAST(@RowsAffected AS NVARCHAR(20)) + N' 行, 耗时 '
                + CAST(DATEDIFF(MILLISECOND, @StartTime, GETDATE()) AS NVARCHAR(20)) + N'ms',
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
EXEC sp_SyncInTransitInventory
    @BatchNo = 'TEST_20260708',
    @RowsAffected = @Rows OUTPUT,
    @ErrorMessage = @Err OUTPUT;

SELECT @Rows AS RowsAffected, @Err AS ErrorMessage;

-- 查询在途库存
SELECT
    MaterialCode,
    SourceFactoryCode,
    TargetFactoryCode,
    Quantity,
    EstimatedArrivalTime,
    Status,
    UpdatedAt
FROM InTransitInventoryFact
WHERE Status = 'IN_TRANSIT'
ORDER BY EstimatedArrivalTime;

-- 查询延迟在途
SELECT *
FROM InTransitInventoryFact
WHERE Status = 'DELAYED';

-- 查询日志
SELECT TOP 20 *
FROM APS_ETL_Log
WHERE BatchNo LIKE 'TEST_%'
ORDER BY CreatedAt DESC;
*/

PRINT N'=== 在途库存同步 v5.0.41 部署完成 ===';
GO
