-- ============================================================
-- APS MES 生产进度快照同步  v5.0.41
-- 包含:
--   PART 1 - SYNONYM（APS 库跨库访问 MES_Integration ODS 视图）
--   PART 2 - sp_SyncMESWorkOrderSnapshot
--   PART 3 - sp_SyncMESOperationProgressSnapshot
--   PART 4 - sp_SyncMESStageProgressSnapshot
--
-- 执行数据库: APS_Production（在 APS 库中运行）
-- 依赖:
--   MES_Integration.dbo.MES_APS_WorkOrder_View         （5号位收口）
--   MES_Integration.dbo.MES_APS_OperationProgress_View （5号位收口）
--   MES_Integration.dbo.MES_APS_StageProgress_View     （5号位收口）
-- 调用链:
--   Hangfire 00:30 NightlyBatchOrchestrator 创建 ScheduleRun → 确定 DataCutoffTime
--   00:40 sp_SyncMESWorkOrderSnapshot
--   00:45 sp_SyncMESOperationProgressSnapshot
--   00:50 sp_SyncMESStageProgressSnapshot
--
-- OLE DB 跨链接服务器限制说明:
--   OLE DB provider 不支持将 SP 参数（@DataCutoffTime）推送到远端执行比较，
--   因此采用"预建临时表 + 动态SQL INSERT"模式：
--   1. 外层 SP 预建 #临时表（外层作用域可见）
--   2. 动态 SQL 将参数转为字面量字符串，OLE DB 可识别并在远端过滤
--   3. 后续 CAST/CASE 在本地临时表上执行，不涉及跨库
--
-- 历史快照清理策略:
--   每个 SP 成功后保留最近 2 个 ScheduleRunId，其余全删
--   清理逻辑在主事务 COMMIT 后独立执行，失败只记 WARN，不回滚快照
--   ODS 视图限定 6 个月窗口，每日快照约 100 万行，保留 2 份约 200 万行
-- ============================================================


-- ============================================================
-- PART 1: SYNONYM
-- 在 APS 库中建立跨库访问入口，指向 MES_Integration 统一收口视图
-- 5号位 UNION ALL 收口视图建好后，APS 侧无需改动任何 SP
-- ============================================================

-- 工单级
IF OBJECT_ID('dbo.ext_MES_APS_WorkOrder_View', 'SN') IS NOT NULL
    DROP SYNONYM [dbo].[ext_MES_APS_WorkOrder_View];
GO
CREATE SYNONYM [dbo].[ext_MES_APS_WorkOrder_View]
    FOR [MES_Integration].[dbo].[MES_APS_WorkOrder_View];
GO

-- 工序进度级
IF OBJECT_ID('dbo.ext_MES_APS_OperationProgress_View', 'SN') IS NOT NULL
    DROP SYNONYM [dbo].[ext_MES_APS_OperationProgress_View];
GO
CREATE SYNONYM [dbo].[ext_MES_APS_OperationProgress_View]
    FOR [MES_Integration].[dbo].[MES_APS_OperationProgress_View];
GO

-- 大工艺进度级
IF OBJECT_ID('dbo.ext_MES_APS_StageProgress_View', 'SN') IS NOT NULL
    DROP SYNONYM [dbo].[ext_MES_APS_StageProgress_View];
GO
CREATE SYNONYM [dbo].[ext_MES_APS_StageProgress_View]
    FOR [MES_Integration].[dbo].[MES_APS_StageProgress_View];
GO


-- ============================================================
-- PART 2: sp_SyncMESWorkOrderSnapshot
-- ============================================================
CREATE OR ALTER PROCEDURE [dbo].[sp_SyncMESWorkOrderSnapshot]
    @ScheduleRunId  INT,
    @DataCutoffTime DATETIME,
    @RowsAffected   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ErrorMessage  NVARCHAR(2000);
    DECLARE @DeletedOld    INT = 0;
    DECLARE @sql           NVARCHAR(MAX);
    DECLARE @CutoffStr     NVARCHAR(30) = CONVERT(NVARCHAR(30), @DataCutoffTime, 120);
    SET @RowsAffected = 0;

    -- Step 1: 幂等清除 + 全量同步（单事务）
    BEGIN TRY
        BEGIN TRANSACTION;

        DELETE FROM [dbo].[MESWorkOrderSnapshot]
        WHERE ScheduleRunId = @ScheduleRunId;

        -- 预建临时表（外层作用域，动态 SQL 可写入）
        CREATE TABLE #WorkOrderRaw (
            ProductionInstructionNo BIGINT          NULL,
            MESWorkOrderNo          NVARCHAR(50)    NULL,
            MaterialCode            NVARCHAR(80)    NULL,
            PlannedQty              DECIMAL(18,2)   NULL,
            WorkOrderStatus         NVARCHAR(50)    NULL,
            SourceUpdatedAt         DATETIME2(7)    NULL
        );

        -- 动态 SQL：将 @DataCutoffTime 转为字面量，OLE DB 可在远端识别并过滤
        SET @sql = N'
            INSERT INTO #WorkOrderRaw
                (ProductionInstructionNo, MESWorkOrderNo, MaterialCode,
                 PlannedQty, WorkOrderStatus, SourceUpdatedAt)
            SELECT v.ProductionInstructionNo, v.MESWorkOrderNo, v.MaterialCode,
                   v.PlannedQty, v.WorkOrderStatus, v.SourceUpdatedAt
            FROM [dbo].[ext_MES_APS_WorkOrder_View] v
            WHERE v.SourceUpdatedAt <= ''' + @CutoffStr + N'''';

        EXEC sp_executesql @sql;

        -- 本地转换后写入快照表
        INSERT INTO [dbo].[MESWorkOrderSnapshot] (
            ScheduleRunId, ProductionInstructionNo, MESWorkOrderNo, MaterialCode,
            PlannedQty, WorkOrderStatus, SourceUpdatedAt, DataCutoffTime, CreatedAt
        )
        SELECT
            @ScheduleRunId,
            CAST(r.ProductionInstructionNo AS NVARCHAR(100)),
            r.MESWorkOrderNo,
            r.MaterialCode,
            r.PlannedQty,
            r.WorkOrderStatus,
            r.SourceUpdatedAt,
            @DataCutoffTime, GETDATE()
        FROM #WorkOrderRaw r;

        SET @RowsAffected = @@ROWCOUNT;
        COMMIT TRANSACTION;

        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESWorkOrderSnapshot',
                CONCAT(N'rows=', @RowsAffected, N' cutoff=', @CutoffStr),
                N'SUCCESS', GETDATE());
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @ErrorMessage = LEFT(ERROR_MESSAGE(), 2000);
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESWorkOrderSnapshot',
                @ErrorMessage, N'FAILED', GETDATE());
        THROW;
    END CATCH;

    -- Step 2: 历史快照清理（保留最近 2 个 ScheduleRunId，主事务外独立执行）
    BEGIN TRY
        DELETE FROM [dbo].[MESWorkOrderSnapshot]
        WHERE ScheduleRunId NOT IN (
            SELECT TOP 2 ScheduleRunId
            FROM [dbo].[MESWorkOrderSnapshot]
            GROUP BY ScheduleRunId
            ORDER BY MAX(CreatedAt) DESC
        );
        SET @DeletedOld = @@ROWCOUNT;

        IF @DeletedOld > 0
            INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
            VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESWorkOrderSnapshot',
                    CONCAT(N'purged old rows=', @DeletedOld), N'INFO', GETDATE());
    END TRY
    BEGIN CATCH
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESWorkOrderSnapshot',
                CONCAT(N'purge failed: ', LEFT(ERROR_MESSAGE(), 500)), N'WARN', GETDATE());
    END CATCH;
END;
GO


-- ============================================================
-- PART 3: sp_SyncMESOperationProgressSnapshot
-- RemainingQty 为 PERSISTED 计算列，INSERT 列表不含此字段
-- V1 工序识别主字段 = OperationName
-- ============================================================
CREATE OR ALTER PROCEDURE [dbo].[sp_SyncMESOperationProgressSnapshot]
    @ScheduleRunId  INT,
    @DataCutoffTime DATETIME,
    @RowsAffected   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ErrorMessage  NVARCHAR(2000);
    DECLARE @DeletedOld    INT = 0;
    DECLARE @sql           NVARCHAR(MAX);
    DECLARE @CutoffStr     NVARCHAR(30) = CONVERT(NVARCHAR(30), @DataCutoffTime, 120);
    SET @RowsAffected = 0;

    -- Step 1: 幂等清除 + 全量同步
    BEGIN TRY
        BEGIN TRANSACTION;

        DELETE FROM [dbo].[OperationProgressSnapshot]
        WHERE ScheduleRunId = @ScheduleRunId;

        -- 预建临时表
        CREATE TABLE #OperationProgressRaw (
            ProductionInstructionNo BIGINT          NULL,
            MESWorkOrderNo          NVARCHAR(50)    NULL,
            MaterialCode            NVARCHAR(80)    NULL,
            OperationName           NVARCHAR(200)   NULL,
            StageCode               VARCHAR(7)      NULL,
            StageName               NVARCHAR(100)   NULL,
            PlannedQty              DECIMAL(18,2)   NULL,
            GoodQty                 DECIMAL(18,2)   NULL,
            ScrapQty                DECIMAL(18,2)   NULL,
            ReworkQty               DECIMAL(18,2)   NULL,
            LastReportTime          DATETIME2(7)    NULL,
            SourceUpdatedAt         DATETIME2(7)    NULL
        );

        -- 动态 SQL：字面量 DataCutoffTime，OLE DB 可识别
        SET @sql = N'
            INSERT INTO #OperationProgressRaw
                (ProductionInstructionNo, MESWorkOrderNo, MaterialCode,
                 OperationName, StageCode, StageName,
                 PlannedQty, GoodQty, ScrapQty, ReworkQty,
                 LastReportTime, SourceUpdatedAt)
            SELECT v.ProductionInstructionNo, v.MESWorkOrderNo, v.MaterialCode,
                   v.OperationName, v.StageCode, v.StageName,
                   v.PlannedQty, v.GoodQty, v.ScrapQty, v.ReworkQty,
                   v.LastReportTime, v.SourceUpdatedAt
            FROM [dbo].[ext_MES_APS_OperationProgress_View] v
            WHERE COALESCE(v.SourceUpdatedAt, v.LastReportTime) <= ''' + @CutoffStr + N'''';

        EXEC sp_executesql @sql;

        -- 本地转换后写入快照表（RemainingQty 为 PERSISTED 计算列，不参与 INSERT）
        INSERT INTO [dbo].[OperationProgressSnapshot] (
            ScheduleRunId, ProductionInstructionNo, MESWorkOrderNo, MaterialCode,
            OperationName, StageCode, StageName,
            PlannedQty, GoodQty, ScrapQty, ReworkQty,
            LastReportTime, SourceUpdatedAt, DataCutoffTime, CreatedAt
        )
        SELECT
            @ScheduleRunId,
            CAST(r.ProductionInstructionNo AS NVARCHAR(100)),
            r.MESWorkOrderNo,
            r.MaterialCode,
            r.OperationName,
            CAST(r.StageCode AS NVARCHAR(20)),
            r.StageName,
            r.PlannedQty, r.GoodQty, r.ScrapQty, r.ReworkQty,
            r.LastReportTime, r.SourceUpdatedAt,
            @DataCutoffTime, GETDATE()
        FROM #OperationProgressRaw r;

        SET @RowsAffected = @@ROWCOUNT;
        COMMIT TRANSACTION;

        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESOperationProgressSnapshot',
                CONCAT(N'rows=', @RowsAffected, N' cutoff=', @CutoffStr),
                N'SUCCESS', GETDATE());
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @ErrorMessage = LEFT(ERROR_MESSAGE(), 2000);
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESOperationProgressSnapshot',
                @ErrorMessage, N'FAILED', GETDATE());
        THROW;
    END CATCH;

    -- Step 2: 历史快照清理（保留最近 2 个 ScheduleRunId）
    BEGIN TRY
        DELETE FROM [dbo].[OperationProgressSnapshot]
        WHERE ScheduleRunId NOT IN (
            SELECT TOP 2 ScheduleRunId
            FROM [dbo].[OperationProgressSnapshot]
            GROUP BY ScheduleRunId
            ORDER BY MAX(CreatedAt) DESC
        );
        SET @DeletedOld = @@ROWCOUNT;

        IF @DeletedOld > 0
            INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
            VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESOperationProgressSnapshot',
                    CONCAT(N'purged old rows=', @DeletedOld), N'INFO', GETDATE());
    END TRY
    BEGIN CATCH
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESOperationProgressSnapshot',
                CONCAT(N'purge failed: ', LEFT(ERROR_MESSAGE(), 500)), N'WARN', GETDATE());
    END CATCH;
END;
GO


-- ============================================================
-- PART 4: sp_SyncMESStageProgressSnapshot
-- 1号位优先消费此表（粒度粗、性能好）
-- RemainingQty 为 PERSISTED 计算列
-- ============================================================
CREATE OR ALTER PROCEDURE [dbo].[sp_SyncMESStageProgressSnapshot]
    @ScheduleRunId  INT,
    @DataCutoffTime DATETIME,
    @RowsAffected   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ErrorMessage  NVARCHAR(2000);
    DECLARE @DeletedOld    INT = 0;
    DECLARE @sql           NVARCHAR(MAX);
    DECLARE @CutoffStr     NVARCHAR(30) = CONVERT(NVARCHAR(30), @DataCutoffTime, 120);
    SET @RowsAffected = 0;

    -- Step 1: 幂等清除 + 全量同步
    BEGIN TRY
        BEGIN TRANSACTION;

        DELETE FROM [dbo].[StageProgressSnapshot]
        WHERE ScheduleRunId = @ScheduleRunId;

        -- 预建临时表
        CREATE TABLE #StageProgressRaw (
            ProductionInstructionNo BIGINT          NULL,
            MaterialCode            NVARCHAR(80)    NULL,
            StageCode               VARCHAR(7)      NULL,
            StageName               NVARCHAR(100)   NULL,
            PlannedQty              DECIMAL(18,2)   NULL,
            GoodCompletedQty        DECIMAL(18,2)   NULL,
            ScrapQty                DECIMAL(18,2)   NULL,
            ReworkQty               DECIMAL(18,2)   NULL,
            LastReportTime          DATETIME2(7)    NULL,
            SourceUpdatedAt         DATETIME2(7)    NULL
        );

        -- 动态 SQL：字面量 DataCutoffTime，OLE DB 可识别
        SET @sql = N'
            INSERT INTO #StageProgressRaw
                (ProductionInstructionNo, MaterialCode,
                 StageCode, StageName,
                 PlannedQty, GoodCompletedQty, ScrapQty, ReworkQty,
                 LastReportTime, SourceUpdatedAt)
            SELECT v.ProductionInstructionNo, v.MaterialCode,
                   v.StageCode, v.StageName,
                   v.PlannedQty, v.GoodCompletedQty, v.ScrapQty, v.ReworkQty,
                   v.LastReportTime, v.SourceUpdatedAt
            FROM [dbo].[ext_MES_APS_StageProgress_View] v
            WHERE COALESCE(v.SourceUpdatedAt, v.LastReportTime) <= ''' + @CutoffStr + N'''';

        EXEC sp_executesql @sql;

        -- 本地转换后写入快照表（RemainingQty 为 PERSISTED 计算列，不参与 INSERT）
        INSERT INTO [dbo].[StageProgressSnapshot] (
            ScheduleRunId, ProductionInstructionNo, MaterialCode,
            StageCode, StageName,
            PlannedQty, GoodCompletedQty, ScrapQty, ReworkQty,
            LastReportTime, SourceUpdatedAt, DataCutoffTime, CreatedAt
        )
        SELECT
            @ScheduleRunId,
            CAST(r.ProductionInstructionNo AS NVARCHAR(100)),
            r.MaterialCode,
            CAST(r.StageCode AS NVARCHAR(20)),
            r.StageName,
            r.PlannedQty, r.GoodCompletedQty, r.ScrapQty, r.ReworkQty,
            r.LastReportTime, r.SourceUpdatedAt,
            @DataCutoffTime, GETDATE()
        FROM #StageProgressRaw r;

        SET @RowsAffected = @@ROWCOUNT;
        COMMIT TRANSACTION;

        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESStageProgressSnapshot',
                CONCAT(N'rows=', @RowsAffected, N' cutoff=', @CutoffStr),
                N'SUCCESS', GETDATE());
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @ErrorMessage = LEFT(ERROR_MESSAGE(), 2000);
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESStageProgressSnapshot',
                @ErrorMessage, N'FAILED', GETDATE());
        THROW;
    END CATCH;

    -- Step 2: 历史快照清理（保留最近 2 个 ScheduleRunId）
    BEGIN TRY
        DELETE FROM [dbo].[StageProgressSnapshot]
        WHERE ScheduleRunId NOT IN (
            SELECT TOP 2 ScheduleRunId
            FROM [dbo].[StageProgressSnapshot]
            GROUP BY ScheduleRunId
            ORDER BY MAX(CreatedAt) DESC
        );
        SET @DeletedOld = @@ROWCOUNT;

        IF @DeletedOld > 0
            INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
            VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESStageProgressSnapshot',
                    CONCAT(N'purged old rows=', @DeletedOld), N'INFO', GETDATE());
    END TRY
    BEGIN CATCH
        INSERT INTO [dbo].[APS_ETL_Log] (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (CAST(@ScheduleRunId AS NVARCHAR(50)), N'sp_SyncMESStageProgressSnapshot',
                CONCAT(N'purge failed: ', LEFT(ERROR_MESSAGE(), 500)), N'WARN', GETDATE());
    END CATCH;
END;
GO


-- ============================================================
-- 快速验证（部署后执行）
-- ============================================================
/*
-- 1. 确认三个 SYNONYM 指向正确
SELECT name, base_object_name FROM sys.synonyms
WHERE name IN (
    'ext_MES_APS_WorkOrder_View',
    'ext_MES_APS_OperationProgress_View',
    'ext_MES_APS_StageProgress_View'
);

-- 2. 手动执行三个 SP（使用测试 ScheduleRunId）
DECLARE @Rows INT;
EXEC sp_SyncMESWorkOrderSnapshot
    @ScheduleRunId = 9999, @DataCutoffTime = GETDATE(), @RowsAffected = @Rows OUTPUT;
SELECT @Rows AS WorkOrderRows;

EXEC sp_SyncMESOperationProgressSnapshot
    @ScheduleRunId = 9999, @DataCutoffTime = GETDATE(), @RowsAffected = @Rows OUTPUT;
SELECT @Rows AS OperationProgressRows;

EXEC sp_SyncMESStageProgressSnapshot
    @ScheduleRunId = 9999, @DataCutoffTime = GETDATE(), @RowsAffected = @Rows OUTPUT;
SELECT @Rows AS StageProgressRows;

-- 3. 检查快照与清理日志
SELECT * FROM APS_ETL_Log WHERE BatchNo = '9999' ORDER BY CreatedAt;

-- 4. 确认 RemainingQty 计算列正确（不低于 0）
SELECT TOP 5 ProductionInstructionNo, OperationName, PlannedQty, GoodQty, RemainingQty
FROM OperationProgressSnapshot WHERE ScheduleRunId = 9999 AND GoodQty > 0;

-- 5. 清理测试数据
DELETE FROM MESWorkOrderSnapshot      WHERE ScheduleRunId = 9999;
DELETE FROM OperationProgressSnapshot WHERE ScheduleRunId = 9999;
DELETE FROM StageProgressSnapshot     WHERE ScheduleRunId = 9999;
*/
