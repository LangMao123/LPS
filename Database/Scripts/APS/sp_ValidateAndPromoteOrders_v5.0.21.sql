USE [APS_Production]
GO

-- =============================================
-- 订单验证与提升存储过程
-- 版本：v5.0.21
-- 日期：2026-05-09
-- 变更：订单BOM入口解析重构
--   - BOMNO 改为可空（废除必填校验）
--   - 新增 FailureCode 和 NextActionCode 双维度字段
--   - 无BOMNO订单标记为 VALIDATED，由5号位BOM Workset阶段解析
--
-- 业务用途：将ERP_Order_Staging中PENDING记录验证后提升到Order_Canonical
-- 调用时机：白天每小时增量同步后 / 凌晨全量同步后
-- 状态机：PENDING → VALIDATED → PROCESSED（成功）/ PENDING → FAILED（校验失败）
-- =============================================

CREATE OR ALTER PROCEDURE [dbo].[sp_ValidateAndPromoteOrders]
    @PromotedCount INT = 0 OUTPUT,
    @FailedCount   INT = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProcessTime DATETIME2 = GETDATE();
    DECLARE @ValidatedCount INT = 0;
    DECLARE @CancelledCount INT = 0;
    DECLARE @ErrorMessage NVARCHAR(MAX);

    SET @FailedCount = 0;
    SET @PromotedCount = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- =================================================================
        -- 步骤0：MaterialCode 解析（ERP原始型号 → APS MaterialCode）
        -- 通过 SourceMasterID 查 MaterialMapping（IsCurrent=1）获取 APS 物料编码
        -- =================================================================

        -- 0a. 通过 MaterialMapping 解析 APS MaterialCode
        UPDATE stg
        SET stg.MaterialCode = mm.MaterialCode
        FROM ERP_Order_Staging stg
        INNER JOIN MaterialMapping mm
            ON mm.SourceID = CAST(stg.SourceMasterID AS NVARCHAR(50))
            AND mm.Source = stg.SourceSystem
            AND mm.IsCurrent = 1
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.SourceMasterID IS NOT NULL;

        -- 0b. 解析失败：SourceMasterID 在 MaterialMapping 中不存在
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = 'MATERIAL_NOT_FOUND',  -- v5.0.21 新增
            stg.NextActionCode = 'MANUAL_REVIEW',    -- v5.0.21 新增
            stg.ErrorMessage = N'SourceMasterID在MaterialMapping中不存在: '
                             + CAST(ISNULL(stg.SourceMasterID, 0) AS NVARCHAR(20))
                             + N', ERP原始型号: '
                             + ISNULL(stg.MaterialCode, N'NULL'),
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING'
          AND (stg.SourceMasterID IS NULL
               OR NOT EXISTS (
                   SELECT 1
                   FROM MaterialMapping mm
                   WHERE mm.SourceID = CAST(stg.SourceMasterID AS NVARCHAR(50))
                     AND mm.Source = stg.SourceSystem
                     AND mm.IsCurrent = 1
               ));

        SET @FailedCount = @@ROWCOUNT;

        -- =================================================================
        -- 步骤0.5：业务规则派生
        -- 参考：APS_订单提升规则_sp_ValidateAndPromoteOrders_v1.0.md
        -- =================================================================

        -- 0.5a. 规则1：ZPQF → OrderType 映射
        UPDATE stg
        SET stg.OrderType = CASE
            WHEN stg.OrderType = '1'   THEN 'SO'   -- 紫票：客户订单
            WHEN stg.OrderType = '2'   THEN 'MTS'  -- 白票：补库/计划生产
            WHEN stg.OrderType = 'MTS' THEN 'MTS'  -- Part 2 已标记
            ELSE 'OTHER'
        END
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING';

        -- 0.5b. 规则2：ProcessCode → FactoryCode 派生
        UPDATE stg
        SET stg.FactoryCode = ISNULL(pc.FactoryCode, 'CN')  -- 查不到时降级为默认工厂 CN
        FROM ERP_Order_Staging stg
        LEFT JOIN ext_MES_ProcessCode_View pc
            ON pc.ProcessCode = RIGHT('000000' + ISNULL(stg.FactoryCode, ''), 6)  -- 左补0到6位
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.FactoryCode IS NOT NULL;

        -- 0.5c. 规则3：Remarks → DemandMaturityStatus 解析
        UPDATE stg
        SET stg.DemandMaturityStatus = CASE
            WHEN stg.DemandMaturityStatus LIKE '%异常信号%异常信号%' THEN 'CRITICAL'  -- 多次异常信号
            WHEN stg.DemandMaturityStatus LIKE '%异常信号%'          THEN 'ABNORMAL'  -- 单次异常信号
            WHEN stg.DemandMaturityStatus LIKE '%●%'                 THEN 'CONFIRMED' -- 已返信
            WHEN stg.DemandMaturityStatus LIKE '%F-%'
              OR stg.DemandMaturityStatus LIKE '%B-%'
              OR stg.DemandMaturityStatus LIKE '%②%'
              OR stg.DemandMaturityStatus LIKE '%返%'
              OR stg.DemandMaturityStatus LIKE '%济%'                THEN 'SPECIAL'   -- 特殊订单
            WHEN stg.DemandMaturityStatus LIKE '%计划生产%'           THEN 'PLANNED'   -- 计划生产
            ELSE 'NORMAL'
        END
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING';

        -- 0.5d. 规则4：SalesOrderCategory 派生（客户订单 vs 补库订单）
        UPDATE stg
        SET stg.SalesOrderCategory = CASE
            WHEN stg.TransportMode = 'LAND' AND stg.CustomerName LIKE '%补库%' THEN 'REPLENISHMENT'
            WHEN stg.TransportMode = 'LAND'                                    THEN 'CUSTOMER_ORDER'
            WHEN stg.OrderNo LIKE 'K%'                                         THEN 'REPLENISHMENT'  -- 日本订单号 K 开头
            ELSE 'CUSTOMER_ORDER'
        END
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.SalesOrderCategory IS NULL;

        -- =================================================================
        -- 步骤1：字段完整性校验（PENDING → VALIDATED / FAILED）
        -- v5.0.21 变更：BOMNO 改为可空，不再校验
        -- =================================================================

        -- 1a. 校验失败：必填字段缺失
        UPDATE ERP_Order_Staging
        SET SyncStatus = 'FAILED',
            FailureCode = CASE  -- v5.0.21 新增
                WHEN OrderNo IS NULL OR OrderNo = ''                   THEN 'ORDERNO_MISSING'
                WHEN MaterialCode IS NULL OR MaterialCode = ''         THEN 'MATERIALCODE_MISSING'
                WHEN Quantity IS NULL OR Quantity <= 0                 THEN 'QUANTITY_INVALID'
                WHEN DueDate IS NULL                                   THEN 'DUEDATE_MISSING'
                WHEN SourceOrderId IS NULL OR SourceOrderId = ''       THEN 'SOURCEORDERID_MISSING'
                WHEN SourceSystem IS NULL OR SourceSystem = ''         THEN 'SOURCESYSTEM_MISSING'
                WHEN FactoryCode IS NULL OR FactoryCode = ''           THEN 'FACTORYCODE_MISSING'
                ELSE 'VALIDATION_FAILED'
            END,
            NextActionCode = 'MANUAL_REVIEW',  -- v5.0.21 新增
            ErrorMessage = CASE
                WHEN OrderNo IS NULL OR OrderNo = ''                   THEN N'OrderNo不能为空'
                WHEN MaterialCode IS NULL OR MaterialCode = ''         THEN N'MaterialCode不能为空（解析后）'
                WHEN Quantity IS NULL OR Quantity <= 0                 THEN N'Quantity必须大于0'
                WHEN DueDate IS NULL                                   THEN N'DueDate不能为空'
                WHEN SourceOrderId IS NULL OR SourceOrderId = ''       THEN N'SourceOrderId不能为空'
                WHEN SourceSystem IS NULL OR SourceSystem = ''         THEN N'SourceSystem不能为空'
                WHEN FactoryCode IS NULL OR FactoryCode = ''           THEN N'FactoryCode不能为空'
                ELSE N'未知校验错误'
            END,
            ProcessedAt = @ProcessTime
        WHERE SyncStatus = 'PENDING'
          AND (OrderNo IS NULL OR OrderNo = ''
               OR MaterialCode IS NULL OR MaterialCode = ''
               OR Quantity IS NULL OR Quantity <= 0
               OR DueDate IS NULL
               OR SourceOrderId IS NULL OR SourceOrderId = ''
               OR SourceSystem IS NULL OR SourceSystem = ''
               OR FactoryCode IS NULL OR FactoryCode = '');

        SET @FailedCount = @FailedCount + @@ROWCOUNT;

        -- 1b. 校验失败：解析后的MaterialCode在Material表中不存在
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = 'MATERIAL_NOT_FOUND',  -- v5.0.21 新增
            stg.NextActionCode = 'MANUAL_REVIEW',    -- v5.0.21 新增
            stg.ErrorMessage = N'MaterialCode在Material表中不存在: ' + stg.MaterialCode,
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING'
          AND NOT EXISTS (
              SELECT 1
              FROM Material m
              WHERE m.MaterialCode = stg.MaterialCode
          );

        SET @FailedCount = @FailedCount + @@ROWCOUNT;

        -- =================================================================
        -- v5.0.21 新增：处理无BOMNO订单
        -- 无BOMNO订单标记为 VALIDATED，但设置 NextActionCode = 'WAIT_BOM_WORKSET'
        -- 由5号位在BOM Workset阶段解析BOM入口（从Model/MaterialCode推导）
        -- =================================================================
        UPDATE ERP_Order_Staging
        SET FailureCode = 'BOMNO_MISSING',        -- 失败原因维度
            NextActionCode = 'WAIT_BOM_WORKSET',  -- 后续动作维度
            ErrorMessage = N'BOMNO为空，等待5号位BOM Workset阶段解析BOM入口'
        WHERE SyncStatus = 'PENDING'
          AND (BOMNO IS NULL OR BOMNO = '');

        -- 注意：不设置 SyncStatus = 'FAILED'，让它继续走 VALIDATED 流程

        -- 1c. 校验通过（包括无BOMNO订单）
        UPDATE ERP_Order_Staging
        SET SyncStatus = 'VALIDATED'
        WHERE SyncStatus = 'PENDING';

        SET @ValidatedCount = @@ROWCOUNT;

        -- =================================================================
        -- 步骤2：提升到Order_Canonical（VALIDATED → PROCESSED）
        -- Upsert键：OrderNo（业务主键，绝对唯一）
        -- v5.0.21 变更：BOMNO 可空
        -- =================================================================

        -- 2a. CustomerTier 默认值处理
        UPDATE ERP_Order_Staging
        SET CustomerTier = ISNULL(CustomerTier, 'GENERAL')
        WHERE SyncStatus = 'VALIDATED'
          AND CustomerTier IS NULL;

        -- 2b. MERGE 到 Order_Canonical
        MERGE INTO Order_Canonical AS target
        USING (
            SELECT
                OrderNo,
                MaterialCode,
                BOMNO,  -- v5.0.21: 可空
                Quantity,
                UOM,
                DueDate,
                OrderType,
                Priority,
                Status,
                SourceSystem,
                SourceOrderId,
                SourceMasterID,
                FactoryCode,
                TransportMode,
                CustomerName,
                MTS_InstructionNo,
                CustomerSegment,
                SalesOrderCategory,
                DemandMaturityStatus,
                CustomerTier,
                IssueDate,
                OriginalDueDate,
                ReceivedQty
            FROM ERP_Order_Staging
            WHERE SyncStatus = 'VALIDATED'
        ) AS source
        ON target.OrderNo = source.OrderNo

        -- 已存在 → 更新变更字段
        WHEN MATCHED AND (
            target.Quantity <> source.Quantity
            OR target.DueDate <> source.DueDate
            OR ISNULL(target.BOMNO, '') <> ISNULL(source.BOMNO, '')  -- v5.0.21: BOMNO可能变化
            OR ISNULL(target.Priority, 0) <> ISNULL(source.Priority, 0)
            OR ISNULL(target.Status, '') <> ISNULL(source.Status, '')
            OR ISNULL(target.TransportMode, '') <> ISNULL(source.TransportMode, '')
            OR ISNULL(target.CustomerSegment, '') <> ISNULL(source.CustomerSegment, '')
            OR ISNULL(target.SalesOrderCategory, '') <> ISNULL(source.SalesOrderCategory, '')
            OR ISNULL(target.DemandMaturityStatus, '') <> ISNULL(source.DemandMaturityStatus, '')
            OR ISNULL(target.CustomerTier, '') <> ISNULL(source.CustomerTier, '')
            OR ISNULL(target.IssueDate, '1900-01-01') <> ISNULL(source.IssueDate, '1900-01-01')
            OR ISNULL(target.OriginalDueDate, '1900-01-01') <> ISNULL(source.OriginalDueDate, '1900-01-01')
            OR ISNULL(target.ReceivedQty, 0) <> ISNULL(source.ReceivedQty, 0)
        ) THEN
            UPDATE SET
                target.Quantity = source.Quantity,
                target.DueDate = source.DueDate,
                target.BOMNO = source.BOMNO,  -- v5.0.21: 可能从NULL变为有值
                target.Priority = source.Priority,
                target.Status = source.Status,
                target.FactoryCode = source.FactoryCode,
                target.TransportMode = source.TransportMode,
                target.CustomerName = source.CustomerName,
                target.MTS_InstructionNo = source.MTS_InstructionNo,
                target.CustomerSegment = source.CustomerSegment,
                target.SalesOrderCategory = source.SalesOrderCategory,
                target.DemandMaturityStatus = source.DemandMaturityStatus,
                target.CustomerTier = source.CustomerTier,
                target.IssueDate = source.IssueDate,
                target.OriginalDueDate = source.OriginalDueDate,
                target.ReceivedQty = source.ReceivedQty,
                target.UpdatedAt = @ProcessTime

        -- 新订单 → 插入
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (
                OrderNo, MaterialCode, BOMNO, Quantity, UOM, DueDate,
                OrderType, Priority, Status, SourceSystem, SourceOrderId, SourceMasterID,
                FactoryCode, TransportMode, CustomerName, MTS_InstructionNo,
                CustomerSegment, SalesOrderCategory, DemandMaturityStatus, CustomerTier,
                IssueDate, OriginalDueDate, ReceivedQty,
                CreatedAt, UpdatedAt
            )
            VALUES (
                source.OrderNo, source.MaterialCode, source.BOMNO, source.Quantity, source.UOM, source.DueDate,
                source.OrderType, source.Priority, source.Status, source.SourceSystem, source.SourceOrderId, source.SourceMasterID,
                source.FactoryCode, source.TransportMode, source.CustomerName, source.MTS_InstructionNo,
                source.CustomerSegment, source.SalesOrderCategory, source.DemandMaturityStatus, source.CustomerTier,
                source.IssueDate, source.OriginalDueDate, source.ReceivedQty,
                @ProcessTime, @ProcessTime
            );

        SET @PromotedCount = @@ROWCOUNT;

        -- 2c. 标记已提升的Staging记录
        UPDATE ERP_Order_Staging
        SET SyncStatus = 'PROCESSED',
            ProcessedAt = @ProcessTime
        WHERE SyncStatus = 'VALIDATED';

        -- =================================================================
        -- 步骤3：记录ETL日志
        -- =================================================================
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            FORMAT(@ProcessTime, 'yyyyMMdd_HHmmss'),
            'sp_ValidateAndPromoteOrders',
            N'订单验证提升完成 | 校验通过:' + CAST(@ValidatedCount AS NVARCHAR(10))
            + N' | 校验失败:' + CAST(@FailedCount AS NVARCHAR(10))
            + N' | 提升到Canonical:' + CAST(@PromotedCount AS NVARCHAR(10)),
            N'SUCCESS',
            GETDATE()
        );

        COMMIT TRANSACTION;

        -- 返回统计
        SELECT
            @ValidatedCount AS ValidatedCount,
            @FailedCount AS FailedCount,
            @PromotedCount AS PromotedCount,
            @ProcessTime AS ProcessTime;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMessage = ERROR_MESSAGE();

        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            FORMAT(@ProcessTime, 'yyyyMMdd_HHmmss'),
            'sp_ValidateAndPromoteOrders',
            N'订单验证提升失败: ' + @ErrorMessage,
            N'FAILED',
            GETDATE()
        );

        THROW;
    END CATCH
END;
GO

-- =============================================
-- 验证脚本
-- =============================================
/*
-- 测试存储过程
DECLARE @Promoted INT, @Failed INT;

EXEC sp_ValidateAndPromoteOrders
    @PromotedCount = @Promoted OUTPUT,
    @FailedCount = @Failed OUTPUT;

SELECT @Promoted AS PromotedCount, @Failed AS FailedCount;

-- 查看无BOMNO订单的处理结果
SELECT
    Id, OrderNo, MaterialCode, BOMNO,
    SyncStatus, FailureCode, NextActionCode, ErrorMessage
FROM ERP_Order_Staging
WHERE BOMNO IS NULL OR BOMNO = ''
ORDER BY SyncedAt DESC;

-- 查看ETL日志
SELECT TOP 10 *
FROM APS_ETL_Log
WHERE Step = 'sp_ValidateAndPromoteOrders'
ORDER BY CreatedAt DESC;
*/
