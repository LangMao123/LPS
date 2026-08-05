USE [APS_Production]
GO

-- =============================================
-- 订单验证与提升存储过程
-- 版本：v5.0.24
-- 日期：2026-05-13
-- 变更：
--   v5.0.21 → v5.0.24:
--   - OrderType 标准化：SO/MTO → SALES_ORDER, MTS/SS/SS_U → PRODUCTION_INSTRUCTION
--   - CustomerSegment 改为通过 CustomerCodeMap 本地映射表派生（默认 OVERSEAS）
--   - 新增 DelayStatus 派生：ON_TIME / FIRST_DELAY
--   - DemandMaturityStatus 收窄为 PRE_CONFIRMED / FORECAST
--   - SalesOrderCategory 改用 JPOrderNo 首字母 + Accepter 规则（DIRECT_SALES / SALES_REPLENISHMENT）
--   - ERP_Order_Staging 新增 CustomerCode、JPOrderNo 字段
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
            stg.FailureCode = 'MATERIAL_NOT_FOUND',
            stg.NextActionCode = 'MANUAL_REVIEW',
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
        -- 步骤0.5：业务规则派生（PENDING阶段，验证前）
        -- =================================================================

        -- 0.5a. 规则1：SalesOrderCategory 含"退运" → 标记取消，不排产
        UPDATE stg
        SET stg.Status = 'CANCELLED',
            stg.SalesOrderCategory = 'RETURN_CANCEL'
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.SalesOrderCategory LIKE N'%退运%';

        -- 0.5b. 规则2：ProcessCode → FactoryCode 派生
        UPDATE stg
        SET stg.FactoryCode = ISNULL(pc.FactoryCode, 'CN')
        FROM ERP_Order_Staging stg
        LEFT JOIN ext_MES_ProcessCode_View pc
            ON pc.ProcessCode = RIGHT('000000' + ISNULL(stg.FactoryCode, ''), 6)
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.FactoryCode IS NOT NULL;

        -- 0.5c. 规则3：Remarks → DemandMaturityStatus 解析（v5.0.24: 收窄为 PRE_CONFIRMED/FORECAST）
        UPDATE stg
        SET stg.DemandMaturityStatus = CASE
            WHEN stg.DemandMaturityStatus LIKE '%●%'
              OR stg.DemandMaturityStatus LIKE '%确%' THEN 'PRE_CONFIRMED'
            ELSE 'FORECAST'
        END
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING';

        -- 0.5d. 规则4：SalesOrderCategory 派生（v5.0.24: JPOrderNo首字母 + Accepter规则）
        -- 直销订单：JPOrderNo首字母为 K/B/M/T/F 且不含"kc"，或 Accepter 含"日本"
        -- 否则：销售补库
        -- 注：含"退运"的已在0.5a标记为RETURN_CANCEL，此处跳过
        UPDATE stg
        SET stg.SalesOrderCategory = CASE
            WHEN stg.JPOrderNo IS NOT NULL
                 AND LEFT(stg.JPOrderNo, 1) IN ('K','B','M','T','F')
                 AND stg.JPOrderNo NOT LIKE '%kc%'
                 THEN 'DIRECT_SALES'
            WHEN stg.JPOrderNo IS NOT NULL
                 AND LEFT(stg.JPOrderNo, 2) IN ('FB','99')
                 AND stg.JPOrderNo NOT LIKE '%kc%'
                 THEN 'DIRECT_SALES'
            WHEN stg.CustomerName LIKE N'%日本%'
                 THEN 'DIRECT_SALES'
            ELSE 'SALES_REPLENISHMENT'
        END
        FROM ERP_Order_Staging stg
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.SalesOrderCategory <> 'RETURN_CANCEL';

        -- =================================================================
        -- 步骤1：字段完整性校验（PENDING → VALIDATED / FAILED）
        -- v5.0.21: BOMNO 可空，不校验
        -- =================================================================

        -- 1a. 校验失败：必填字段缺失
        UPDATE ERP_Order_Staging
        SET SyncStatus = 'FAILED',
            FailureCode = CASE
                WHEN OrderNo IS NULL OR OrderNo = ''                   THEN 'ORDERNO_MISSING'
                WHEN MaterialCode IS NULL OR MaterialCode = ''         THEN 'MATERIALCODE_MISSING'
                WHEN Quantity IS NULL OR Quantity <= 0                  THEN 'QUANTITY_INVALID'
                WHEN DueDate IS NULL                                   THEN 'DUEDATE_MISSING'
                WHEN SourceOrderId IS NULL OR SourceOrderId = ''       THEN 'SOURCEORDERID_MISSING'
                WHEN SourceSystem IS NULL OR SourceSystem = ''         THEN 'SOURCESYSTEM_MISSING'
                WHEN FactoryCode IS NULL OR FactoryCode = ''           THEN 'FACTORYCODE_MISSING'
                ELSE 'VALIDATION_FAILED'
            END,
            NextActionCode = 'MANUAL_REVIEW',
            ErrorMessage = CASE
                WHEN OrderNo IS NULL OR OrderNo = ''                   THEN N'OrderNo不能为空'
                WHEN MaterialCode IS NULL OR MaterialCode = ''         THEN N'MaterialCode不能为空（解析后）'
                WHEN Quantity IS NULL OR Quantity <= 0                  THEN N'Quantity必须大于0'
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
            stg.FailureCode = 'MATERIAL_NOT_FOUND',
            stg.NextActionCode = 'MANUAL_REVIEW',
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

        -- v5.0.21：无BOMNO订单标记但不阻断
        UPDATE ERP_Order_Staging
        SET FailureCode = 'BOMNO_MISSING',
            NextActionCode = 'WAIT_BOM_WORKSET',
            ErrorMessage = N'BOMNO为空，等待5号位BOM Workset阶段解析BOM入口'
        WHERE SyncStatus = 'PENDING'
          AND (BOMNO IS NULL OR BOMNO = '');

        -- 1c. 校验通过（包括无BOMNO订单）
        UPDATE ERP_Order_Staging
        SET SyncStatus = 'VALIDATED'
        WHERE SyncStatus = 'PENDING';

        SET @ValidatedCount = @@ROWCOUNT;

        -- =================================================================
        -- 步骤2：提升准备（VALIDATED阶段，MERGE前的字段标准化）
        -- =================================================================

        -- 2a. OrderType 标准化（v5.0.24: 从ERP原始ZPQF值映射）
        -- Staging保留原值('1','2','MTS')，提升时转为APS标准枚举
        UPDATE ERP_Order_Staging
        SET OrderType = CASE
            WHEN OrderType = '1'                     THEN 'SALES_ORDER'
            WHEN OrderType IN ('2', 'MTS')           THEN 'PRODUCTION_INSTRUCTION'
            ELSE OrderType
        END
        WHERE SyncStatus = 'VALIDATED';

        -- 2b. CustomerSegment 派生（v5.0.24: 通过 CustomerCodeMap 查询）
        UPDATE stg
        SET stg.CustomerSegment = ISNULL(ccm.CustomerSegment, 'OVERSEAS')
        FROM ERP_Order_Staging stg
        LEFT JOIN CustomerCodeMap ccm
            ON ccm.CustomerCode = stg.CustomerCode
            AND ccm.IsActive = 1
        WHERE stg.SyncStatus = 'VALIDATED';

        -- 2c. CustomerTier 默认值
        UPDATE ERP_Order_Staging
        SET CustomerTier = ISNULL(CustomerTier, 'GENERAL')
        WHERE SyncStatus = 'VALIDATED'
          AND CustomerTier IS NULL;

        -- 2d. DelayStatus 派生（v5.0.24）
        UPDATE ERP_Order_Staging
        SET DelayStatus = CASE
            WHEN DueDate < CAST(GETDATE() AS DATE) THEN 'FIRST_DELAY'
            ELSE 'ON_TIME'
        END
        WHERE SyncStatus = 'VALIDATED'
          AND DelayStatus IS NULL;

        -- =================================================================
        -- 步骤2e：MERGE 到 Order_Canonical（VALIDATED → PROCESSED）
        -- Upsert键：OrderNo（业务主键）
        -- =================================================================
        MERGE INTO Order_Canonical AS target
        USING (
            SELECT
                OrderNo, MaterialCode, BOMNO, Quantity, UOM,
                DueDate, OrderType, Priority, Status,
                SourceSystem, SourceOrderId, SourceMasterID,
                FactoryCode, TransportMode, CustomerName, MTS_InstructionNo,
                CustomerSegment, SalesOrderCategory, DemandMaturityStatus,
                CustomerTier, DelayStatus,
                IssueDate, OriginalDueDate, ReceivedQty
            FROM ERP_Order_Staging
            WHERE SyncStatus = 'VALIDATED'
        ) AS source
        ON target.OrderNo = source.OrderNo

        WHEN MATCHED AND (
            target.Quantity <> source.Quantity
            OR target.DueDate <> source.DueDate
            OR ISNULL(target.BOMNO, '') <> ISNULL(source.BOMNO, '')
            OR ISNULL(target.Priority, 0) <> ISNULL(source.Priority, 0)
            OR ISNULL(target.Status, '') <> ISNULL(source.Status, '')
            OR ISNULL(target.TransportMode, '') <> ISNULL(source.TransportMode, '')
            OR ISNULL(target.CustomerSegment, '') <> ISNULL(source.CustomerSegment, '')
            OR ISNULL(target.SalesOrderCategory, '') <> ISNULL(source.SalesOrderCategory, '')
            OR ISNULL(target.DemandMaturityStatus, '') <> ISNULL(source.DemandMaturityStatus, '')
            OR ISNULL(target.CustomerTier, '') <> ISNULL(source.CustomerTier, '')
            OR ISNULL(target.DelayStatus, '') <> ISNULL(source.DelayStatus, '')
            OR ISNULL(target.IssueDate, '1900-01-01') <> ISNULL(source.IssueDate, '1900-01-01')
            OR ISNULL(target.OriginalDueDate, '1900-01-01') <> ISNULL(source.OriginalDueDate, '1900-01-01')
            OR ISNULL(target.ReceivedQty, 0) <> ISNULL(source.ReceivedQty, 0)
        ) THEN
            UPDATE SET
                target.Quantity = source.Quantity,
                target.DueDate = source.DueDate,
                target.BOMNO = source.BOMNO,
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
                target.DelayStatus = source.DelayStatus,
                target.IssueDate = source.IssueDate,
                target.OriginalDueDate = source.OriginalDueDate,
                target.ReceivedQty = source.ReceivedQty,
                target.UpdatedAt = @ProcessTime

        WHEN NOT MATCHED BY TARGET THEN
            INSERT (
                OrderNo, MaterialCode, BOMNO, Quantity, UOM, DueDate,
                OrderType, Priority, Status, SourceSystem, SourceOrderId, SourceMasterID,
                FactoryCode, TransportMode, CustomerName, MTS_InstructionNo,
                CustomerSegment, SalesOrderCategory, DemandMaturityStatus,
                CustomerTier, DelayStatus,
                IssueDate, OriginalDueDate, ReceivedQty,
                CreatedAt, UpdatedAt
            )
            VALUES (
                source.OrderNo, source.MaterialCode, source.BOMNO,
                source.Quantity, source.UOM, source.DueDate,
                source.OrderType, source.Priority, source.Status,
                source.SourceSystem, source.SourceOrderId, source.SourceMasterID,
                source.FactoryCode, source.TransportMode, source.CustomerName, source.MTS_InstructionNo,
                source.CustomerSegment, source.SalesOrderCategory, source.DemandMaturityStatus,
                source.CustomerTier, source.DelayStatus,
                source.IssueDate, source.OriginalDueDate, source.ReceivedQty,
                @ProcessTime, @ProcessTime
            );

        SET @PromotedCount = @@ROWCOUNT;

        -- 2f. 标记已提升的Staging记录
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
