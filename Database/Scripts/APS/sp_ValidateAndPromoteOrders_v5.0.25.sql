USE [APS_Production]
GO

-- =============================================
-- 订单验证与提升存储过程
-- 版本：v5.0.25
-- 日期：2026-05-18
-- v5.0.40: 新增 1f FactoryCode 字典缺失诊断（非阻断 + WARN 日志）
-- 核心原则：Staging 保留 ERP 原值不动，派生结果只写入 Order_Canonical
-- 设计：
--   - Staging 只更新技术字段（SyncStatus/FailureCode/ErrorMessage/ProcessedAt）
--   - 所有业务派生在 MERGE source SELECT 中 inline 计算
--   - #TargetStagingIds 锁定本批次，防并发
--   - Upsert 键：OrderNo（SourceOrderId 跨出荷/生产指示可重复，不能做唯一键）
--   - OrderType 未知值 → FAILED
--   - DemandMaturityStatus V1 严格 NULL
--   - CustomerSegment 无匹配 → UNKNOWN
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
        -- PHASE 0: 锁定本次处理 ID 集合
        -- =================================================================
        CREATE TABLE #TargetStagingIds (StagingId BIGINT PRIMARY KEY);

        INSERT INTO #TargetStagingIds (StagingId)
        SELECT Id
        FROM ERP_Order_Staging WITH (UPDLOCK, ROWLOCK)
        WHERE SyncStatus = 'PENDING'
        ORDER BY SyncedAt ASC;

        -- =================================================================
        -- 步骤1：校验（只改技术字段，不动 ERP 原始业务字段）
        -- =================================================================

        -- 1a. MaterialCode 解析失败（SourceMasterID 无映射）
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = 'MATERIAL_NOT_FOUND',
            stg.NextActionCode = 'MANUAL_REVIEW',
            stg.ErrorMessage = N'SourceMasterID在MaterialMapping中不存在: '
                             + CAST(ISNULL(stg.SourceMasterID, 0) AS NVARCHAR(20)),
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'PENDING'
          AND (stg.SourceMasterID IS NULL
               OR NOT EXISTS (
                   SELECT 1
                   FROM MaterialMapping mm
                   WHERE mm.SourceID = stg.SourceMasterID
                     AND mm.Source = stg.SourceSystem
                     AND mm.IsCurrent = 1
               ));

        SET @FailedCount = @@ROWCOUNT;

        -- 1b. 解析后的MaterialCode在Material主数据中不存在
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = 'MATERIAL_NOT_IN_MASTER',
            stg.NextActionCode = 'MANUAL_REVIEW',
            stg.ErrorMessage = N'MaterialCode在Material主数据中不存在: '
                             + ISNULL(mm.MaterialCode, N'NULL'),
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        INNER JOIN MaterialMapping mm
            ON mm.SourceID = stg.SourceMasterID
           AND mm.Source = stg.SourceSystem
           AND mm.IsCurrent = 1
        WHERE stg.SyncStatus = 'PENDING'
          AND NOT EXISTS (
              SELECT 1 FROM Material m WHERE m.MaterialCode = mm.MaterialCode
          );

        SET @FailedCount = @FailedCount + @@ROWCOUNT;

        -- 1c. OrderType 未知值 → FAILED
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = 'ORDER_TYPE_UNKNOWN',
            stg.NextActionCode = 'MANUAL_REVIEW',
            stg.ErrorMessage = N'OrderType无法映射为APS标准值: ' + ISNULL(stg.OrderType, N'NULL'),
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'PENDING'
          AND stg.OrderType NOT IN ('1', '2', 'MTS', 'SO', 'MTO', 'SS', 'SS_U',
                                     'SALES_ORDER', 'PRODUCTION_INSTRUCTION');

        SET @FailedCount = @FailedCount + @@ROWCOUNT;

        -- 1d. 基础必填字段缺失
        UPDATE stg
        SET stg.SyncStatus = 'FAILED',
            stg.FailureCode = CASE
                WHEN stg.OrderNo IS NULL OR stg.OrderNo = ''             THEN 'ORDERNO_MISSING'
                WHEN stg.Quantity IS NULL OR stg.Quantity <= 0           THEN 'QUANTITY_INVALID'
                WHEN stg.DueDate IS NULL                                 THEN 'DUEDATE_MISSING'
                WHEN stg.SourceOrderId IS NULL OR stg.SourceOrderId = '' THEN 'SOURCEORDERID_MISSING'
                ELSE 'VALIDATION_FAILED'
            END,
            stg.NextActionCode = 'MANUAL_REVIEW',
            stg.ErrorMessage = N'基础必填字段缺失',
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'PENDING'
          AND (stg.OrderNo IS NULL OR stg.OrderNo = ''
               OR stg.Quantity IS NULL OR stg.Quantity <= 0
               OR stg.DueDate IS NULL
               OR stg.SourceOrderId IS NULL OR stg.SourceOrderId = '');

        SET @FailedCount = @FailedCount + @@ROWCOUNT;

        -- 1e. BOMNO=NULL 非阻断诊断（不改 SyncStatus）
        UPDATE stg
        SET stg.FailureCode = 'BOMNO_MISSING',
            stg.NextActionCode = 'WAIT_BOM_WORKSET',
            stg.ErrorMessage = N'BOMNO为空，等待5号位Workset阶段解析BOM入口'
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'PENDING'
          AND (stg.BOMNO IS NULL OR stg.BOMNO = '');

        -- 1f. FactoryCode 字典缺失诊断（非阻断，不改 SyncStatus）
        --     映射后的 FactoryCode 不在 Factory 表中 → 打标 + WARN 日志
        --     订单仍可提升到 Canonical，但装载到 Order 表时会被 INNER JOIN 拦截
        UPDATE stg
        SET stg.FailureCode = CASE
                WHEN stg.FailureCode IS NOT NULL THEN stg.FailureCode + ',FACTORY_NOT_FOUND'
                ELSE 'FACTORY_NOT_FOUND'
            END,
            stg.NextActionCode = CASE
                WHEN stg.NextActionCode IS NOT NULL THEN stg.NextActionCode
                ELSE 'MAINTAIN_FACTORY_DICT'
            END,
            stg.ErrorMessage = N'FactoryCode在Factory字典中不存在: '
                             + ISNULL(ISNULL(pc.FactoryCode, stg.FactoryCode), N'NULL')
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        LEFT JOIN ext_MES_ProcessCode_View pc
            ON pc.ProcessCode = RIGHT('000000' + ISNULL(stg.FactoryCode, ''), 6)
        WHERE stg.SyncStatus = 'PENDING'
          AND NOT EXISTS (
              SELECT 1 FROM Factory f
              WHERE f.Code = ISNULL(pc.FactoryCode, stg.FactoryCode)
                AND f.IsActive = 1
          );

        -- 1f-log: Factory 字典缺失告警（最多 50 条）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT TOP 50
            FORMAT(@ProcessTime, 'yyyyMMdd_HHmmss'),
            'sp_ValidateAndPromoteOrders.FactoryNotFound',
            CONCAT('OrderNo=', stg.OrderNo,
                   ' FactoryCode=', ISNULL(ISNULL(pc.FactoryCode, stg.FactoryCode), 'NULL'),
                   ' SourceSystem=', stg.SourceSystem),
            'WARN', GETDATE()
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        LEFT JOIN ext_MES_ProcessCode_View pc
            ON pc.ProcessCode = RIGHT('000000' + ISNULL(stg.FactoryCode, ''), 6)
        WHERE stg.SyncStatus = 'PENDING'
          AND NOT EXISTS (
              SELECT 1 FROM Factory f
              WHERE f.Code = ISNULL(pc.FactoryCode, stg.FactoryCode)
                AND f.IsActive = 1
          );

        -- 1g. 校验通过 → VALIDATED
        UPDATE stg
        SET stg.SyncStatus = 'VALIDATED'
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'PENDING';

        SET @ValidatedCount = @@ROWCOUNT;

        -- =================================================================
        -- 步骤2：MERGE 到 Order_Canonical
        -- Upsert 键：SourceSystem + SourceOrderId
        -- 所有派生逻辑在 source SELECT 中 inline，Staging 原值不动
        -- =================================================================
        MERGE INTO Order_Canonical AS target
        USING (
            SELECT
                stg.SourceSystem,
                stg.SourceOrderId,
                stg.SourceMasterID,
                stg.OrderNo,
                -- MaterialCode: 从 MaterialMapping 解析
                mm.MaterialCode,
                stg.BOMNO,
                stg.Quantity,
                stg.UOM,
                stg.DueDate,
                -- OrderType: ERP原始值 → APS标准枚举
                CASE
                    WHEN stg.OrderType IN ('1', 'SO', 'MTO')          THEN 'SALES_ORDER'
                    WHEN stg.OrderType IN ('2', 'MTS', 'SS', 'SS_U')  THEN 'PRODUCTION_INSTRUCTION'
                    ELSE stg.OrderType  -- 不会到这里，1c 已过滤
                END AS OrderType,
                stg.Priority,
                -- Status: 退运在此派生，不改 Staging
                CASE
                    WHEN stg.SalesOrderCategory LIKE N'%退运%' THEN 'CANCELLED'
                    ELSE stg.Status
                END AS Status,
                -- FactoryCode: ProcessCode → 映射，无映射保留原值
                ISNULL(pc.FactoryCode, stg.FactoryCode) AS FactoryCode,
                stg.TransportMode,
                stg.CustomerName,
                stg.MTS_InstructionNo,
                -- CustomerSegment: CustomerCodeMap 派生
                CASE
                    WHEN stg.CustomerCode IS NULL OR stg.CustomerCode = '' THEN NULL
                    WHEN ccm.CustomerSegment IS NOT NULL THEN ccm.CustomerSegment
                    ELSE 'UNKNOWN'
                END AS CustomerSegment,
                -- SalesOrderCategory: 派生
                CASE
                    WHEN stg.SalesOrderCategory LIKE N'%退运%' THEN 'RETURN_CANCEL'
                    WHEN stg.JPOrderNo IS NOT NULL
                         AND LEFT(stg.JPOrderNo, 1) IN ('K','B','M','T','F')
                         AND stg.JPOrderNo NOT LIKE '%kc%' THEN 'DIRECT_SALES'
                    WHEN stg.JPOrderNo IS NOT NULL
                         AND LEFT(stg.JPOrderNo, 2) IN ('FB','99')
                         AND stg.JPOrderNo NOT LIKE '%kc%' THEN 'DIRECT_SALES'
                    WHEN stg.CustomerName LIKE N'%日本%' THEN 'DIRECT_SALES'
                    ELSE 'SALES_REPLENISHMENT'
                END AS SalesOrderCategory,
                -- DemandMaturityStatus: V1 严格 NULL
                CAST(NULL AS NVARCHAR(50)) AS DemandMaturityStatus,
                -- CustomerTier
                'GENERAL' AS CustomerTier,
                -- DelayStatus
                CASE
                    WHEN stg.DueDate < CAST(GETDATE() AS DATE) THEN 'FIRST_DELAY'
                    ELSE 'ON_TIME'
                END AS DelayStatus,
                stg.IssueDate,
                stg.OriginalDueDate,
                stg.ReceivedQty
            FROM ERP_Order_Staging stg
            JOIN #TargetStagingIds t ON t.StagingId = stg.Id
            INNER JOIN MaterialMapping mm
                ON mm.SourceID = stg.SourceMasterID
               AND mm.Source = stg.SourceSystem
               AND mm.IsCurrent = 1
            LEFT JOIN ext_MES_ProcessCode_View pc
                ON pc.ProcessCode = RIGHT('000000' + ISNULL(stg.FactoryCode, ''), 6)
            LEFT JOIN CustomerCodeMap ccm
                ON ccm.CustomerCode = stg.CustomerCode
               AND ccm.IsActive = 1
            WHERE stg.SyncStatus = 'VALIDATED'
        ) AS source
        ON target.OrderNo = source.OrderNo

        WHEN MATCHED AND (
            target.Quantity <> source.Quantity
            OR target.DueDate <> source.DueDate
            OR ISNULL(target.BOMNO, '') <> ISNULL(source.BOMNO, '')
            OR ISNULL(target.OrderType, '') <> ISNULL(source.OrderType, '')
            OR ISNULL(target.Priority, 0) <> ISNULL(source.Priority, 0)
            OR ISNULL(target.Status, '') <> ISNULL(source.Status, '')
            OR ISNULL(target.FactoryCode, '') <> ISNULL(source.FactoryCode, '')
            OR ISNULL(target.TransportMode, '') <> ISNULL(source.TransportMode, '')
            OR ISNULL(target.CustomerSegment, '') <> ISNULL(source.CustomerSegment, '')
            OR ISNULL(target.SalesOrderCategory, '') <> ISNULL(source.SalesOrderCategory, '')
            OR ISNULL(target.CustomerTier, '') <> ISNULL(source.CustomerTier, '')
            OR ISNULL(target.DelayStatus, '') <> ISNULL(source.DelayStatus, '')
            OR ISNULL(target.IssueDate, '1900-01-01') <> ISNULL(source.IssueDate, '1900-01-01')
            OR ISNULL(target.OriginalDueDate, '1900-01-01') <> ISNULL(source.OriginalDueDate, '1900-01-01')
            OR ISNULL(target.ReceivedQty, 0) <> ISNULL(source.ReceivedQty, 0)
        ) THEN
            UPDATE SET
                target.MaterialCode = source.MaterialCode,
                target.OrderNo = source.OrderNo,
                target.Quantity = source.Quantity,
                target.DueDate = source.DueDate,
                target.BOMNO = source.BOMNO,
                target.OrderType = source.OrderType,
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
                SourceSystem, SourceOrderId, SourceMasterID,
                OrderNo, MaterialCode, BOMNO, Quantity, UOM, DueDate,
                OrderType, Priority, Status, FactoryCode,
                TransportMode, CustomerName, MTS_InstructionNo,
                CustomerSegment, SalesOrderCategory, DemandMaturityStatus,
                CustomerTier, DelayStatus,
                IssueDate, OriginalDueDate, ReceivedQty,
                CreatedAt, UpdatedAt
            )
            VALUES (
                source.SourceSystem, source.SourceOrderId, source.SourceMasterID,
                source.OrderNo, source.MaterialCode, source.BOMNO,
                source.Quantity, source.UOM, source.DueDate,
                source.OrderType, source.Priority, source.Status, source.FactoryCode,
                source.TransportMode, source.CustomerName, source.MTS_InstructionNo,
                source.CustomerSegment, source.SalesOrderCategory, source.DemandMaturityStatus,
                source.CustomerTier, source.DelayStatus,
                source.IssueDate, source.OriginalDueDate, source.ReceivedQty,
                @ProcessTime, @ProcessTime
            );

        SET @PromotedCount = @@ROWCOUNT;

        -- =================================================================
        -- 步骤3：标记已提升 → PROCESSED
        -- =================================================================
        UPDATE stg
        SET stg.SyncStatus = 'PROCESSED',
            stg.ProcessedAt = @ProcessTime
        FROM ERP_Order_Staging stg
        JOIN #TargetStagingIds t ON t.StagingId = stg.Id
        WHERE stg.SyncStatus = 'VALIDATED';

        -- =================================================================
        -- 步骤4：ETL日志
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

        DROP TABLE IF EXISTS #TargetStagingIds;

        SELECT
            @ValidatedCount AS ValidatedCount,
            @FailedCount AS FailedCount,
            @PromotedCount AS PromotedCount,
            @ProcessTime AS ProcessTime;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        DROP TABLE IF EXISTS #TargetStagingIds;

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
