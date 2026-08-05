-- =============================================
-- APS 资源主数据同步存储过程（方案A：ODS视图输出NDeptNo，SP内完成所有映射）
-- 版本：v1.0
-- 日期：2026-05-08
-- 说明：配合 ResourceSyncService（2号位，每日00:15）使用
--
-- 数据流：
--   ODS.MES_APS_Resource_View (3号位契约视图，输出NDeptNo原始值)
--     ↓ 2号位在 APS 库创建跨库包装视图（ext_ 前缀）
--   APS.ext_MES_APS_Resource_View
--     ↓ sp_SyncResourceData 基于 ProductionDepartment(DeptCode) 映射 NDeptNo → ProductionDepartmentId + FactoryId
--     ↓                    基于 Factory(Id) 映射 FactoryId → FactoryCode
--   APS.Resource (MERGE Upsert；v1暂不自动停用，见注释)
--
-- 架构契约：
--   ✅ 每日 00:15 由 Hangfire Job "resource-sync" 触发
--   ✅ 排在 master-data-sync-erp (00:10) 之后（Factory 字典已就绪）
--   ✅ 排在 routing-sync (00:25) 之前（Resource 表为 OperationResourceEligibility 的前置依赖）
--   ✅ 双字典映射：NDeptNo → ProductionDepartment.Id + Factory.Id → FactoryCode
--   ✅ 映射失败行不阻塞批次，登记 APS_ETL_Log 告警并跳过
--   ⚠️ v1占位策略：源端没有的旧资源暂不自动停用，交由人工审阅后手工处置
--
-- 前置依赖表（需先建好）：
--   APS_Production.Factory              — 工厂字典（CN/BJ）
--   APS_Production.ProductionDepartment — 排程责任部门字典
--   APS_Production.Resource             — 资源目标表
--   APS_Production.APS_ETL_Log          — ETL日志表
-- =============================================

USE APS_Production;
GO

-- =============================================
-- 1. APS 库跨库包装视图（2号位创建，本脚本给出模板）
-- ⚠️ Linked Server 或跨库 SYNONYM 的具体实现请按部署环境调整
-- =============================================

CREATE OR ALTER VIEW ext_MES_APS_Resource_View
AS
SELECT
    ResourceCode,
    ResourceName,
    ExternalResourceId,
    SourceSystem,
    NDeptNo,              -- 原始部门编码，在SP中映射
    ResourceType,
    Status,
    CapacityFactor,
    IsActive
FROM [MES_Integration].[dbo].[MES_APS_Resource_View];
GO


-- =============================================
-- 2. sp_SyncResourceData 存储过程
-- 负责人：2号位
-- 调用方：ResourceSyncService.SyncAsync()（每日 00:15）
--
-- 执行逻辑（v1.0 方案A：SP内完成双字典映射）：
--   Step 1：从 ext_MES_APS_Resource_View 拉源快照
--   Step 2：通过 NDeptNo → ProductionDepartment 映射得到 ProductionDepartmentId + FactoryId
--   Step 3：通过 FactoryId → Factory 映射得到 FactoryCode
--   Step 4：登记映射失败行到 APS_ETL_Log（不阻塞批次）
--   Step 5：MERGE 新增/更新（只处理映射成功的行）
--   Step 6：统计写日志
--
-- 输出参数：@RowsAffected / @Skipped / @ErrorMessage
-- =============================================

CREATE OR ALTER PROCEDURE sp_SyncResourceData
    @SourceType    NVARCHAR(20),                  -- 'MES'（v1） / 'EAM'（v1 未实现）
    @BatchNo       NVARCHAR(50) = 'DAILY',
    @RowsAffected  INT OUTPUT,
    @Skipped       INT OUTPUT,
    @ErrorMessage  NVARCHAR(MAX) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StepName NVARCHAR(100) = N'sp_SyncResourceData[' + @SourceType + N']';

    BEGIN TRY
        -- 参数校验
        IF @SourceType NOT IN (N'MES', N'EAM')
        BEGIN
            SET @ErrorMessage = N'Invalid @SourceType: ' + ISNULL(@SourceType, N'NULL') + N'. Expected MES or EAM.';
            SET @RowsAffected = 0;
            SET @Skipped = 0;
            INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
            VALUES (@BatchNo, @StepName, @ErrorMessage, N'FAILED', GETDATE());
            RETURN;
        END

        -- v1 占位：EAM 分支未实现
        IF @SourceType = N'EAM'
        BEGIN
            SET @ErrorMessage = N'NOT_IMPLEMENTED: EAM branch reserved; create ext_EAM_APS_Resource_View first and extend this SP.';
            SET @RowsAffected = 0;
            SET @Skipped = 0;
            INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
            VALUES (@BatchNo, @StepName, @ErrorMessage, N'SKIPPED', GETDATE());
            RETURN;
        END

        BEGIN TRANSACTION;

        -- ============================================================
        -- Step 1：从 ext_MES_APS_Resource_View 拉源快照
        -- ============================================================
        IF OBJECT_ID('tempdb..#Resource_Source') IS NOT NULL DROP TABLE #Resource_Source;

        SELECT
            v.ResourceCode,
            v.ResourceName,
            v.ExternalResourceId,
            v.SourceSystem,
            v.NDeptNo,
            v.ResourceType,
            v.Status,
            v.CapacityFactor,
            v.IsActive
        INTO #Resource_Source
        FROM ext_MES_APS_Resource_View v
        WHERE v.IsActive = 1;  -- 只同步激活的资源

        -- ============================================================
        -- Step 2：通过 NDeptNo → ProductionDepartment 映射
        -- 映射规则：NDeptNo → ProductionDepartment.SourceDeptCode
        -- ============================================================
        IF OBJECT_ID('tempdb..#Resource_Mapped') IS NOT NULL DROP TABLE #Resource_Mapped;

        SELECT
            s.ResourceCode,
            s.ResourceName,
            s.ExternalResourceId,
            s.SourceSystem,
            s.NDeptNo                AS SourceNDeptNo,
            d.Id                     AS ProductionDepartmentId,
            d.DeptCode               AS ProductionDeptCode,
            d.FactoryId              AS FactoryId,
            s.ResourceType,
            s.Status,
            s.CapacityFactor,
            s.IsActive
        INTO #Resource_Mapped
        FROM #Resource_Source s
        LEFT JOIN ProductionDepartment d ON d.SourceDeptCode = CAST(s.NDeptNo AS NVARCHAR(50)) AND d.IsActive = 1;

        -- ============================================================
        -- Step 3：通过 FactoryId → Factory 映射得到 FactoryCode
        -- ============================================================
        IF OBJECT_ID('tempdb..#Resource_Final') IS NOT NULL DROP TABLE #Resource_Final;

        SELECT
            m.ResourceCode,
            m.ResourceName,
            m.ExternalResourceId,
            m.SourceSystem,
            m.SourceNDeptNo,
            m.ProductionDepartmentId,
            m.ProductionDeptCode,
            m.FactoryId,
            f.Code                   AS FactoryCode,
            m.ResourceType,
            m.Status,
            m.CapacityFactor,
            m.IsActive
        INTO #Resource_Final
        FROM #Resource_Mapped m
        LEFT JOIN Factory f ON f.Id = m.FactoryId;

        -- ============================================================
        -- Step 4：登记映射失败行（不阻塞批次）
        -- ============================================================
        SET @Skipped = 0;

        -- NDeptNo 映射失败（ProductionDepartment 未找到）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT
            @BatchNo, @StepName,
            N'NDeptNo not found in ProductionDepartment table, row skipped. ResourceCode=' + s.ResourceCode
                + N', NDeptNo=' + ISNULL(CAST(s.SourceNDeptNo AS NVARCHAR(50)), N'NULL'),
            N'WARN', GETDATE()
        FROM #Resource_Final s
        WHERE s.ProductionDepartmentId IS NULL;

        SET @Skipped = @@ROWCOUNT;

        -- FactoryId 映射失败（Factory 未找到，理论上不应该发生）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT
            @BatchNo, @StepName,
            N'FactoryId not found in Factory table, row skipped. ResourceCode=' + s.ResourceCode
                + N', FactoryId=' + ISNULL(CAST(s.FactoryId AS NVARCHAR(50)), N'NULL'),
            N'WARN', GETDATE()
        FROM #Resource_Final s
        WHERE s.ProductionDepartmentId IS NOT NULL AND s.FactoryId IS NULL;

        SET @Skipped = @Skipped + @@ROWCOUNT;

        -- ============================================================
        -- Step 5：MERGE 新增/更新（只处理双映射成功的行）
        -- ============================================================
        MERGE Resource AS tgt
        USING (
            SELECT * FROM #Resource_Final
            WHERE ProductionDepartmentId IS NOT NULL
              AND FactoryId IS NOT NULL
        ) AS src
           ON tgt.ResourceCode = src.ResourceCode
        WHEN MATCHED AND (
                ISNULL(tgt.ResourceName, N'')                 <> ISNULL(src.ResourceName, N'')
             OR ISNULL(tgt.ExternalResourceId, N'')           <> ISNULL(src.ExternalResourceId, N'')
             OR ISNULL(tgt.SourceSystem, N'')                 <> ISNULL(src.SourceSystem, N'')
             OR tgt.FactoryId                                  <> src.FactoryId
             OR tgt.ProductionDepartmentId                     <> src.ProductionDepartmentId
             OR ISNULL(tgt.SourceProductionDeptCode, N'')     <> ISNULL(src.ProductionDeptCode, N'')
             OR ISNULL(tgt.ResourceType, N'')                 <> ISNULL(src.ResourceType, N'')
             OR ISNULL(tgt.Status, N'')                       <> ISNULL(src.Status, N'')
             OR ISNULL(tgt.CapacityFactor, 0)                 <> ISNULL(src.CapacityFactor, 0)
             OR tgt.IsActive                                   <> src.IsActive
        ) THEN UPDATE SET
            ResourceName             = src.ResourceName,
            ExternalResourceId       = src.ExternalResourceId,
            SourceSystem             = src.SourceSystem,
            FactoryId                = src.FactoryId,
            ProductionDepartmentId   = src.ProductionDepartmentId,
            SourceProductionDeptCode = src.ProductionDeptCode,
            ResourceType             = src.ResourceType,
            Status                   = src.Status,
            CapacityFactor           = src.CapacityFactor,
            IsActive                 = src.IsActive,
            UpdatedAt                = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (ResourceCode, ResourceName, ExternalResourceId, SourceSystem, FactoryId,
                    ProductionDepartmentId, SourceProductionDeptCode,
                    ResourceType, Status, CapacityFactor, IsActive, CreatedAt, UpdatedAt)
            VALUES (src.ResourceCode, src.ResourceName, src.ExternalResourceId, src.SourceSystem, src.FactoryId,
                    src.ProductionDepartmentId, src.ProductionDeptCode,
                    src.ResourceType, src.Status, src.CapacityFactor, src.IsActive, GETDATE(), GETDATE())
        -- ⚠️ v1 占位策略：源端没有的旧资源暂不自动停用（避免误删），交由人工审阅后手工处置
        -- 未来若改为"源为权威"，在此补：WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystem = @SourceType THEN UPDATE SET IsActive=0
        ;

        SET @RowsAffected = @@ROWCOUNT;

        -- ============================================================
        -- Step 6：写日志
        -- ============================================================
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            @BatchNo, @StepName,
            N'资源同步完成[' + @SourceType + N'] | 影响行数=' + CAST(@RowsAffected AS NVARCHAR(10))
                + N' | 跳过(NDeptNo/FactoryId 任一未命中)=' + CAST(@Skipped AS NVARCHAR(10)),
            N'SUCCESS', GETDATE()
        );

        DROP TABLE IF EXISTS #Resource_Source;
        DROP TABLE IF EXISTS #Resource_Mapped;
        DROP TABLE IF EXISTS #Resource_Final;

        COMMIT TRANSACTION;
        SET @ErrorMessage = NULL;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMessage = ERROR_MESSAGE();
        SET @RowsAffected = 0;

        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            @BatchNo,
            @StepName,
            N'同步失败[' + @SourceType + N']: ' + @ErrorMessage,
            N'FAILED',
            GETDATE()
        );
    END CATCH
END;
GO

-- =============================================
-- 验证脚本（部署后手工执行一次）
-- =============================================
/*
DECLARE @RowsAffected INT, @Skipped INT, @Err NVARCHAR(MAX);

EXEC sp_SyncResourceData
    @SourceType = 'MES',
    @BatchNo = 'RESOURCE_MANUAL_TEST',
    @RowsAffected = @RowsAffected OUTPUT,
    @Skipped = @Skipped OUTPUT,
    @ErrorMessage = @Err OUTPUT;

SELECT @RowsAffected AS RowsAffected, @Skipped AS Skipped, @Err AS ErrorMessage;

-- 检查 ETL 日志
SELECT TOP 10 * FROM APS_ETL_Log WHERE Step LIKE '%SyncResourceData%' ORDER BY CreatedAt DESC;

-- 检查 Resource 表数据
SELECT TOP 20 * FROM Resource WHERE IsActive = 1 ORDER BY UpdatedAt DESC;

-- 检查映射统计
SELECT
    f.Code AS FactoryCode,
    COUNT(*) AS ResourceCount
FROM Resource r
JOIN Factory f ON f.Id = r.FactoryId
WHERE r.IsActive = 1
GROUP BY f.Code;
*/
