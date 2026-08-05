-- ============================================================
-- Migration v5.0.41 - MES 生产进度快照三表
-- 执行数据库: APS_Production
-- 包含:
--   MESWorkOrderSnapshot
--   OperationProgressSnapshot（含 RemainingQty PERSISTED 计算列）
--   StageProgressSnapshot    （含 RemainingQty PERSISTED 计算列）
-- ============================================================


-- ============================================================
-- 表 1: MESWorkOrderSnapshot（MES 工单快照）
-- 粒度: ScheduleRunId + ProductionInstructionNo + MESWorkOrderNo
-- 消费: 1号位工单追溯 / 3号位进度看板
-- ============================================================
IF OBJECT_ID('dbo.MESWorkOrderSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MESWorkOrderSnapshot] (
        [Id]                      BIGINT         NOT NULL IDENTITY(1,1),
        [ScheduleRunId]           INT            NOT NULL,
        [ProductionInstructionNo] NVARCHAR(100)  NOT NULL,
        [MESWorkOrderNo]          NVARCHAR(100)  NOT NULL,
        [MaterialCode]            NVARCHAR(100)  NOT NULL,
        [PlannedQty]              DECIMAL(18,4)  NOT NULL,
        [WorkOrderStatus]         NVARCHAR(50)   NOT NULL,
        [SourceUpdatedAt]         DATETIME2      NULL,
        [DataCutoffTime]          DATETIME2      NOT NULL,
        [CreatedAt]               DATETIME2      NOT NULL CONSTRAINT DF_MESWorkOrderSnapshot_CreatedAt DEFAULT GETDATE(),

        CONSTRAINT PK_MESWorkOrderSnapshot
            PRIMARY KEY CLUSTERED ([Id] ASC),

        CONSTRAINT UQ_MESWorkOrderSnapshot_Key
            UNIQUE ([ScheduleRunId], [ProductionInstructionNo], [MESWorkOrderNo], [MaterialCode])
    );

    CREATE INDEX IX_MESWorkOrderSnapshot_Run_InstructionNo
        ON [dbo].[MESWorkOrderSnapshot] ([ScheduleRunId], [ProductionInstructionNo]);

    PRINT 'MESWorkOrderSnapshot created.';
END
ELSE
    PRINT 'MESWorkOrderSnapshot already exists, skipped.';
GO


-- ============================================================
-- 表 2: OperationProgressSnapshot（工序进度快照）
-- 粒度: ScheduleRunId + ProductionInstructionNo + MESWorkOrderNo
--        + MaterialCode + OperationName + StageCode
-- RemainingQty: PERSISTED 计算列 = MAX(0, PlannedQty - GoodQty)
-- 消费: 1号位按工序扣减剩余 Task 数量（V1 主字段 = OperationName）
-- ============================================================
IF OBJECT_ID('dbo.OperationProgressSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[OperationProgressSnapshot] (
        [Id]                      BIGINT         NOT NULL IDENTITY(1,1),
        [ScheduleRunId]           INT            NOT NULL,
        [ProductionInstructionNo] NVARCHAR(100)  NOT NULL,
        [MESWorkOrderNo]          NVARCHAR(100)  NOT NULL,
        [MaterialCode]            NVARCHAR(100)  NOT NULL,
        [OperationName]           NVARCHAR(200)  NOT NULL,
        [StageCode]               NVARCHAR(20)   NOT NULL,
        [StageName]               NVARCHAR(100)  NULL,
        [PlannedQty]              DECIMAL(18,4)  NOT NULL,
        [GoodQty]                 DECIMAL(18,4)  NOT NULL CONSTRAINT DF_OperationProgressSnapshot_GoodQty DEFAULT 0,
        [ScrapQty]                DECIMAL(18,4)  NULL,
        [ReworkQty]               DECIMAL(18,4)  NULL,
        -- 1号位消费此字段裁剪剩余 Task，不低于 0
        [RemainingQty]            AS (
                                      CASE
                                          WHEN [PlannedQty] IS NULL THEN NULL
                                          WHEN [PlannedQty] - ISNULL([GoodQty], 0) < 0
                                              THEN CAST(0 AS DECIMAL(18,4))
                                          ELSE [PlannedQty] - ISNULL([GoodQty], 0)
                                      END
                                  ) PERSISTED,
        [LastReportTime]          DATETIME2      NULL,
        [SourceUpdatedAt]         DATETIME2      NULL,
        [DataCutoffTime]          DATETIME2      NOT NULL,
        [CreatedAt]               DATETIME2      NOT NULL CONSTRAINT DF_OperationProgressSnapshot_CreatedAt DEFAULT GETDATE(),

        CONSTRAINT PK_OperationProgressSnapshot
            PRIMARY KEY CLUSTERED ([Id] ASC),

        CONSTRAINT UQ_OperationProgressSnapshot_Key
            UNIQUE ([ScheduleRunId], [ProductionInstructionNo], [MESWorkOrderNo],
                    [MaterialCode], [OperationName], [StageCode])
    );

    CREATE INDEX IX_OperationProgressSnapshot_Run_InstructionNo
        ON [dbo].[OperationProgressSnapshot] ([ScheduleRunId], [ProductionInstructionNo]);

    PRINT 'OperationProgressSnapshot created.';
END
ELSE
    PRINT 'OperationProgressSnapshot already exists, skipped.';
GO


-- ============================================================
-- 表 3: StageProgressSnapshot（大工艺进度快照）
-- 粒度: ScheduleRunId + ProductionInstructionNo + MaterialCode + StageCode
-- RemainingQty: PERSISTED 计算列 = MAX(0, PlannedQty - GoodCompletedQty)
-- 消费: 1号位优先消费（粒度粗、性能好）
-- ============================================================
IF OBJECT_ID('dbo.StageProgressSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StageProgressSnapshot] (
        [Id]                      BIGINT         NOT NULL IDENTITY(1,1),
        [ScheduleRunId]           INT            NOT NULL,
        [ProductionInstructionNo] NVARCHAR(100)  NOT NULL,
        [MaterialCode]            NVARCHAR(100)  NOT NULL,
        [StageCode]               NVARCHAR(20)   NOT NULL,
        [StageName]               NVARCHAR(100)  NULL,
        [PlannedQty]              DECIMAL(18,4)  NOT NULL,
        [GoodCompletedQty]        DECIMAL(18,4)  NOT NULL CONSTRAINT DF_StageProgressSnapshot_GoodCompletedQty DEFAULT 0,
        [ScrapQty]                DECIMAL(18,4)  NULL,
        [ReworkQty]               DECIMAL(18,4)  NULL,
        -- 1号位优先消费此字段（粒度粗、性能好），不低于 0
        [RemainingQty]            AS (
                                      CASE
                                          WHEN [PlannedQty] IS NULL THEN NULL
                                          WHEN [PlannedQty] - ISNULL([GoodCompletedQty], 0) < 0
                                              THEN CAST(0 AS DECIMAL(18,4))
                                          ELSE [PlannedQty] - ISNULL([GoodCompletedQty], 0)
                                      END
                                  ) PERSISTED,
        [LastReportTime]          DATETIME2      NULL,
        [SourceUpdatedAt]         DATETIME2      NULL,
        [DataCutoffTime]          DATETIME2      NOT NULL,
        [CreatedAt]               DATETIME2      NOT NULL CONSTRAINT DF_StageProgressSnapshot_CreatedAt DEFAULT GETDATE(),

        CONSTRAINT PK_StageProgressSnapshot
            PRIMARY KEY CLUSTERED ([Id] ASC),

        CONSTRAINT UQ_StageProgressSnapshot_Key
            UNIQUE ([ScheduleRunId], [ProductionInstructionNo], [MaterialCode], [StageCode])
    );

    CREATE INDEX IX_StageProgressSnapshot_Run_InstructionNo
        ON [dbo].[StageProgressSnapshot] ([ScheduleRunId], [ProductionInstructionNo]);

    PRINT 'StageProgressSnapshot created.';
END
ELSE
    PRINT 'StageProgressSnapshot already exists, skipped.';
GO


-- ============================================================
-- 验证（执行后检查）
-- ============================================================
/*
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME IN (
    'MESWorkOrderSnapshot',
    'OperationProgressSnapshot',
    'StageProgressSnapshot'
)
ORDER BY TABLE_NAME, ORDINAL_POSITION;
*/
