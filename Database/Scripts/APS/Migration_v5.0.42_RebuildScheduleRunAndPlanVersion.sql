-- ============================================================
-- Migration v5.0.42: 重建 ScheduleRun + PlanVersion
-- 对齐 schedulerun流程2.md 最终字段口径
-- ⚠️ 此脚本会 DROP 并重建两张表，执行前确认数据已备份
-- ============================================================

-- ------------------------------------------------------------
-- PART 1: ScheduleRun（排程运行编排表）
-- ------------------------------------------------------------
-- 变更说明：
--   删除: OutputPlanVersionId（一次运行可能产出多个版本，不能单字段记录）
--   新增: BasePlanVersionId（重排/仿真基于哪个已有版本）
--   新增: ScopeJson（局部重排范围，V1 可为空）
--   新增: DurationSeconds（运行耗时，由 SchedulingOrchestrator 回填）
-- ------------------------------------------------------------

IF OBJECT_ID('dbo.ScheduleRun', 'U') IS NOT NULL
    DROP TABLE dbo.ScheduleRun;

CREATE TABLE dbo.ScheduleRun (
    Id                INT           PRIMARY KEY IDENTITY(1,1),

    -- 运行类型: FULL_SCHEDULE / MANUAL_RESCHEDULE / LOCAL_RESCHEDULE / SIMULATION / INSERT_ORDER_WHATIF
    RunType           NVARCHAR(50)  NOT NULL,

    -- 运行状态: RUNNING / COMPLETED / FAILED
    Status            NVARCHAR(20)  NOT NULL,

    -- 重排/仿真基于哪个已有计划版本；凌晨全量为空
    BasePlanVersionId INT           NULL,

    -- 关联场景（仿真/插单分析填写；人工重排、凌晨全量为空）
    ScenarioId        INT           NULL,

    -- 局部重排范围 JSON（LOCAL_RESCHEDULE 时填写，记录工厂/资源组/订单集合/冻结规则）
    ScopeJson         NVARCHAR(MAX) NULL,

    -- 触发来源: 'Hangfire' / UserId字符串 / 'API' / 'Agent'
    TriggeredBy       NVARCHAR(100) NOT NULL,

    -- 数据截止时间（在 NightlyBatchOrchestrator 00:30 锁定，MES快照三表共享）
    DataCutoffTime    DATETIME2     NOT NULL,

    StartedAt         DATETIME2     NULL,
    CompletedAt       DATETIME2     NULL,

    -- 运行耗时（秒），由 SchedulingOrchestrator 完成/失败时回填
    DurationSeconds   INT           NULL,

    ErrorMessage      NVARCHAR(MAX) NULL,
    CreatedAt         DATETIME2     NOT NULL DEFAULT GETDATE()
);

CREATE INDEX IX_ScheduleRun_Status_CreatedAt
    ON dbo.ScheduleRun (Status, CreatedAt DESC);

-- ------------------------------------------------------------
-- PART 2: PlanVersion（计划版本表）
-- ------------------------------------------------------------
-- 变更说明：
--   改名: VersionType → VersionCategory（避免与 ScheduleRun.RunType 语义混淆）
--   新增: SourceScheduleRunId（来源排程运行ID，在创建时写入）
--   新增: SourceSimulationRunId（来源仿真运行ID，仅仿真版本填写）
--   新增: ActivatedAt / ActivatedBy（版本正式采用时记录）
--   新增: ComputedAt（APS 排程计算完成时间，由 SchedulingOrchestrator 回填）
--   删除: StartedAt / CompletedAt / DurationSeconds / ErrorMessage
--         （运行过程字段，权威归 ScheduleRun；PlanVersion 只记录结果）
-- ------------------------------------------------------------

IF OBJECT_ID('dbo.PlanVersion', 'U') IS NOT NULL
    DROP TABLE dbo.PlanVersion;

CREATE TABLE dbo.PlanVersion (
    Id                     INT            PRIMARY KEY IDENTITY(1,1),

    -- 业务可读版本号，例如 NIGHTLY_20260623
    VersionCode            NVARCHAR(50)   NOT NULL,

    -- 版本类别: DAILY_BASELINE / RESCHEDULE_CANDIDATE / LOCAL_RESCHEDULE_CANDIDATE
    --           SIMULATION_CANDIDATE / WHATIF_CANDIDATE
    VersionCategory        NVARCHAR(50)   NOT NULL,

    -- 分域标识（V1 可为空，多域排程后填写）
    DomainKey              NVARCHAR(100)  NULL,

    PlanHorizonStart       DATE           NOT NULL,
    PlanHorizonEnd         DATE           NOT NULL,

    -- 计算模式: FULL / PARTIAL
    ComputeMode            NVARCHAR(50)   NOT NULL,

    -- 版本状态（口径保持现有，不对齐文档的 BUILDING/CANDIDATE/ACTIVE）
    -- Created / Computing / Computed / ComputeFailed / Archived
    Status                 NVARCHAR(50)   NOT NULL,

    -- APS 排程计算完成时间（由 SchedulingOrchestrator 回填）
    ComputedAt             DATETIME2      NULL,

    -- 来源排程运行ID（在 NightlyBatchOrchestrator 创建时同步写入）
    SourceScheduleRunId    INT            NULL,

    -- 来源仿真运行ID（仅仿真候选版本填写）
    SourceSimulationRunId  INT            NULL,

    -- 正式采用记录（当前 V1 暂不启用，预留字段）
    ActivatedAt            DATETIME2      NULL,
    ActivatedBy            NVARCHAR(100)  NULL,

    -- 列表展示冗余字段（权威统计在 PlanKpiSummary）
    TotalOrders            INT            NULL,
    TotalTasks             INT            NULL,

    -- 数据批次号（关联 NightlyBatch 的 BatchNo）
    BatchNo                NVARCHAR(50)   NULL,

    -- 快照封存字段（由 SnapshotService 回填）
    SnapshotFilePath       NVARCHAR(500)  NULL,
    SnapshotFileSize       BIGINT         NULL,
    SnapshotCompressedSize BIGINT         NULL,
    SnapshotFileHash       NVARCHAR(64)   NULL,
    SnapshotCreatedAt      DATETIME2      NULL,

    CreatedBy              NVARCHAR(100)  NOT NULL,
    CreatedByUserId        INT            NULL,
    CreatedByUserName      NVARCHAR(100)  NULL,
    CreatedAt              DATETIME2      NOT NULL DEFAULT GETDATE(),
    ArchivedAt             DATETIME2      NULL,

    CONSTRAINT UQ_PlanVersion_VersionCode UNIQUE (VersionCode)
);

CREATE INDEX IX_PlanVersion_Status_CreatedAt
    ON dbo.PlanVersion (Status, CreatedAt DESC);

CREATE INDEX IX_PlanVersion_SourceScheduleRunId
    ON dbo.PlanVersion (SourceScheduleRunId);
