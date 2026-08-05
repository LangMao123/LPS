-- =============================================
-- 管道供给同步 - 完整部署脚本
-- 版本: v5.0.42 (2026-06-15)
-- 依据: APS_数据库字段说明文档_v5.0.md §6.6 / APS_数据库表结构设计_v5.0.sql
--
-- 部署顺序:
--   PART 0: MES_Integration 库 - ODS 层视图（5号位负责，V1 空契约）
--   PART 1: APS_Production 库 - ext_ERP_InterplantInTransit_View（显式列 VIEW）
--   PART 2: APS_Production 库 - SupplyAvailabilityRule 规则表
--   PART 3: APS_Production 库 - SupplyFact_Pipeline 事实表
--   PART 4: APS_Production 库 - ext_PipelineSupply_Source_View（UNION ALL 4来源）
--   PART 5: APS_Production 库 - ext_ERP_Received_ByDocument_View（Pegging 消费）
--   PART 6: APS_Production 库 - sp_SyncPipelineSupply（V1 空跑）
-- =============================================

-- ============================================================
-- PART 0: ODS 层视图（MES_Integration 库，5号位负责，V1 空契约）
-- 四个来源各一个视图，14字段契约相同
-- ============================================================

USE [MES_Integration];
GO

-- 分支1: ERP 厂间在途（V1 已建好，保持空契约骨架）
CREATE OR ALTER VIEW dbo.ERP_InterplantInTransit_View AS
SELECT
    CAST(NULL AS INT)              AS MasterID,
    CAST(NULL AS NVARCHAR(100))    AS MaterialCode,
    CAST(NULL AS NVARCHAR(50))     AS SourceFactoryCode,
    CAST(NULL AS NVARCHAR(50))     AS FactoryCode,
    CAST('INTERPLANT_IN_TRANSIT'   AS NVARCHAR(50))  AS SupplyType,
    CAST('OWNED'                   AS NVARCHAR(20))  AS OwnershipType,
    CAST('AVAILABLE'               AS NVARCHAR(20))  AS QualityStatus,
    CAST(NULL AS DECIMAL(18,4))    AS Quantity,
    CAST(NULL AS DATETIME2)        AS ETA,
    CAST(NULL AS NVARCHAR(50))     AS StorageCode,
    CAST(NULL AS NVARCHAR(50))     AS SupplierCode,
    CAST(NULL AS NVARCHAR(100))    AS SourceDocumentNo,
    CAST(NULL AS NVARCHAR(50))     AS SourceDocumentLineNo,
    CAST(NULL AS DATETIME2)        AS SourceUpdatedAt
WHERE 1 = 0;
GO

-- 分支2: ERP 采购在途（V1 空契约，未来由 5 号位实现）
CREATE OR ALTER VIEW dbo.ERP_PurchaseInTransit_View AS
SELECT
    CAST(NULL AS INT)              AS MasterID,
    CAST(NULL AS NVARCHAR(100))    AS MaterialCode,
    CAST(NULL AS NVARCHAR(50))     AS SourceFactoryCode,
    CAST(NULL AS NVARCHAR(50))     AS FactoryCode,
    CAST('PURCHASE_IN_TRANSIT'     AS NVARCHAR(50))  AS SupplyType,
    CAST('OWNED'                   AS NVARCHAR(20))  AS OwnershipType,
    CAST('AVAILABLE'               AS NVARCHAR(20))  AS QualityStatus,
    CAST(NULL AS DECIMAL(18,4))    AS Quantity,
    CAST(NULL AS DATETIME2)        AS ETA,
    CAST(NULL AS NVARCHAR(50))     AS StorageCode,
    CAST(NULL AS NVARCHAR(50))     AS SupplierCode,
    CAST(NULL AS NVARCHAR(100))    AS SourceDocumentNo,
    CAST(NULL AS NVARCHAR(50))     AS SourceDocumentLineNo,
    CAST(NULL AS DATETIME2)        AS SourceUpdatedAt
WHERE 1 = 0;
GO

-- 分支3: VMI（V1 空契约，未来由 5 号位实现）
CREATE OR ALTER VIEW dbo.ERP_VMI_View AS
SELECT
    CAST(NULL AS INT)              AS MasterID,
    CAST(NULL AS NVARCHAR(100))    AS MaterialCode,
    CAST(NULL AS NVARCHAR(50))     AS SourceFactoryCode,
    CAST(NULL AS NVARCHAR(50))     AS FactoryCode,
    CAST('VMI_ONSITE'              AS NVARCHAR(50))  AS SupplyType,
    CAST('CONSIGNMENT'             AS NVARCHAR(20))  AS OwnershipType,
    CAST('AVAILABLE'               AS NVARCHAR(20))  AS QualityStatus,
    CAST(NULL AS DECIMAL(18,4))    AS Quantity,
    CAST(NULL AS DATETIME2)        AS ETA,
    CAST(NULL AS NVARCHAR(50))     AS StorageCode,
    CAST(NULL AS NVARCHAR(50))     AS SupplierCode,
    CAST(NULL AS NVARCHAR(100))    AS SourceDocumentNo,
    CAST(NULL AS NVARCHAR(50))     AS SourceDocumentLineNo,
    CAST(NULL AS DATETIME2)        AS SourceUpdatedAt
WHERE 1 = 0;
GO

-- 分支4: 已到厂未入库（V1 空契约，未来由 5 号位实现）
CREATE OR ALTER VIEW dbo.ERP_ArrivedNotReceived_View AS
SELECT
    CAST(NULL AS INT)              AS MasterID,
    CAST(NULL AS NVARCHAR(100))    AS MaterialCode,
    CAST(NULL AS NVARCHAR(50))     AS SourceFactoryCode,
    CAST(NULL AS NVARCHAR(50))     AS FactoryCode,
    CAST('ARRIVED_NOT_RECEIVED'    AS NVARCHAR(50))  AS SupplyType,
    CAST('OWNED'                   AS NVARCHAR(20))  AS OwnershipType,
    CAST('AVAILABLE'               AS NVARCHAR(20))  AS QualityStatus,
    CAST(NULL AS DECIMAL(18,4))    AS Quantity,
    CAST(NULL AS DATETIME2)        AS ETA,
    CAST(NULL AS NVARCHAR(50))     AS StorageCode,
    CAST(NULL AS NVARCHAR(50))     AS SupplierCode,
    CAST(NULL AS NVARCHAR(100))    AS SourceDocumentNo,
    CAST(NULL AS NVARCHAR(50))     AS SourceDocumentLineNo,
    CAST(NULL AS DATETIME2)        AS SourceUpdatedAt
WHERE 1 = 0;
GO

-- ERP Received 按单据汇总（Pegging 消费，非管道供给同步）
CREATE OR ALTER VIEW dbo.ERP_Received_ByDocument_View AS
SELECT
    CAST(NULL AS NVARCHAR(50))   AS FactoryCode,
    CAST(NULL AS NVARCHAR(50))   AS WarehouseCode,
    CAST(NULL AS INT)            AS MasterID,
    CAST(NULL AS NVARCHAR(100))  AS MaterialCode,
    CAST(NULL AS NVARCHAR(50))   AS DocumentType,
    CAST(NULL AS NVARCHAR(100))  AS DocumentNo,
    CAST(NULL AS DECIMAL(18,4))  AS ReceivedQty,
    CAST(NULL AS DATETIME2)      AS LastReceivedAt,
    CAST(NULL AS DATETIME2)      AS SourceUpdatedAt,
    CAST(NULL AS BIT)            AS IsActive
WHERE 1 = 0;
GO

PRINT '[ODS][MES_Integration] PART 0: 5个 ODS 契约视图已创建（V1 空契约）';
GO

-- ============================================================
-- PART 1: APS 层 SYNONYM（四个单来源包装 + Received）
-- 负责：2号位
-- ============================================================

USE [APS_Production];
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_InterplantInTransit_View')
    CREATE SYNONYM dbo.ext_ERP_InterplantInTransit_View
        FOR [MES_Integration].[dbo].[ERP_InterplantInTransit_View];
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_PurchaseInTransit_View')
    CREATE SYNONYM dbo.ext_ERP_PurchaseInTransit_View
        FOR [MES_Integration].[dbo].[ERP_PurchaseInTransit_View];
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_VMI_View')
    CREATE SYNONYM dbo.ext_ERP_VMI_View
        FOR [MES_Integration].[dbo].[ERP_VMI_View];
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_ArrivedNotReceived_View')
    CREATE SYNONYM dbo.ext_ERP_ArrivedNotReceived_View
        FOR [MES_Integration].[dbo].[ERP_ArrivedNotReceived_View];
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_Received_ByDocument_View')
    CREATE SYNONYM dbo.ext_ERP_Received_ByDocument_View
        FOR [MES_Integration].[dbo].[ERP_Received_ByDocument_View];
GO

PRINT '[APS][APS_Production] PART 1: 5个 SYNONYM 已创建';
GO

-- ============================================================
-- PART 2: SupplyAvailabilityRule（供给可用性规则表）
-- ============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SupplyAvailabilityRule')
BEGIN
    CREATE TABLE dbo.SupplyAvailabilityRule
    (
        Id              INT           IDENTITY(1,1) PRIMARY KEY,
        ProductFamilyId INT           NULL,           -- NULL = 全产品族通配；无 FK，允许独立存在
        FactoryId       INT           NULL,           -- NULL = 全工厂通配；无 FK
        SupplyType      NVARCHAR(50)  NULL,           -- NULL = 全类型通配（INTERPLANT_IN_TRANSIT / PURCHASE_IN_TRANSIT / VMI_ONSITE / ARRIVED_NOT_RECEIVED）
        OwnershipType   NVARCHAR(20)  NULL,           -- NULL = 全所有权类型通配
        QualityStatus   NVARCHAR(20)  NULL,           -- NULL = 全质量状态通配

        IncludeFlag     BIT           NOT NULL DEFAULT 1,   -- 1=纳入排程，0=排除
        Priority        INT           NOT NULL DEFAULT 50,  -- 数字越小越优先；默认 50
        LeadTimeOffset  INT           NOT NULL DEFAULT 0,   -- 提前期偏移（小时）：AvailableTime = ETA + LeadTimeOffset

        EffectiveFrom   DATETIME2     NULL,           -- 规则生效开始时间（NULL=永久有效）
        EffectiveTo     DATETIME2     NULL,           -- 规则生效结束时间（NULL=永久有效）

        IsActive        BIT           NOT NULL DEFAULT 1,
        Remark          NVARCHAR(500) NULL,
        CreatedAt       DATETIME2     NOT NULL DEFAULT GETDATE(),
        UpdatedAt       DATETIME2     NOT NULL DEFAULT GETDATE()
    );

    -- 五维组合唯一约束，防止同一维度组合出现多条活跃规则
    CREATE UNIQUE NONCLUSTERED INDEX UX_SupplyAvailabilityRule_NoDupRule
    ON dbo.SupplyAvailabilityRule(ProductFamilyId, FactoryId, SupplyType, OwnershipType, QualityStatus)
    WHERE IsActive = 1;

    PRINT '[TABLE] 已创建 SupplyAvailabilityRule';
END
ELSE
    PRINT '[TABLE] SupplyAvailabilityRule 已存在';
GO

-- ============================================================
-- PART 3: SupplyFact_Pipeline（管道供给事实表，28字段）
-- ============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SupplyFact_Pipeline')
BEGIN
    CREATE TABLE dbo.SupplyFact_Pipeline
    (
        Id                   BIGINT        IDENTITY(1,1) PRIMARY KEY,
        MaterialCode         NVARCHAR(100) NOT NULL,
        MaterialId           INT           NOT NULL,
            CONSTRAINT FK_SupplyFact_Pipeline_Material
                FOREIGN KEY (MaterialId) REFERENCES Material(Id),
        FactoryCode          NVARCHAR(50)  NOT NULL,          -- 目的工厂编码（ODS 原始值）
        FactoryId            INT           NOT NULL,
            CONSTRAINT FK_SupplyFact_Pipeline_Factory
                FOREIGN KEY (FactoryId) REFERENCES Factory(Id),
        ProductFamilyId      INT           NULL,              -- 可为 NULL（物料未配置产品族时）
        SupplyType           NVARCHAR(50)  NOT NULL,          -- INTERPLANT_IN_TRANSIT / PURCHASE_IN_TRANSIT / VMI_ONSITE / ARRIVED_NOT_RECEIVED
        OwnershipType        NVARCHAR(20)  NOT NULL DEFAULT 'OWNED',
        QualityStatus        NVARCHAR(20)  NOT NULL DEFAULT 'AVAILABLE',
        Quantity             DECIMAL(18,4) NOT NULL,
        ETA                  DATETIME2     NULL,              -- ODS 原始事实字段，不在本地修改
        AvailableTime        DATETIME2     NULL,              -- 本地派生：= ETA + LeadTimeOffset，sp 装载时计算落库

        -- v5.0.42 来源追溯字段
        SourceMasterID       INT           NULL,              -- ODS MasterID 直通
        SourceFactoryCode    NVARCHAR(50)  NULL,              -- 发出工厂（仅物流追溯，非可用工厂判定）
        SourceDocumentNo     NVARCHAR(100) NULL,
        SourceDocumentLineNo NVARCHAR(50)  NULL,
        SourceUpdatedAt      DATETIME2     NULL,
        StorageCode          NVARCHAR(50)  NULL,              -- 目的仓库/预计收货仓库
        SupplierCode         NVARCHAR(50)  NULL,              -- VMI/采购在途时有效
        SourceSystem         NVARCHAR(50)  NOT NULL DEFAULT 'ERP',

        -- 幂等键（计算列，防止同来源记录重复同步）
        SourceRowKey AS CONCAT(
            ISNULL(SourceSystem, ''),          '|',
            ISNULL(SupplyType, ''),            '|',
            ISNULL(SourceDocumentNo, ''),      '|',
            ISNULL(SourceDocumentLineNo, ''),  '|',
            ISNULL(CAST(SourceMasterID AS NVARCHAR(20)), ''), '|',
            ISNULL(StorageCode, ''),           '|',
            ISNULL(FactoryCode, '')
        ) PERSISTED,

        -- v5.0.42 规则裁决追溯字段
        SupplyAvailabilityRuleId INT     NULL,                -- 命中规则 ID
        AppliedLeadTimeOffset    INT     NULL,                -- 命中规则的提前期偏移（小时）
        RulePriority             INT     NULL,                -- 命中规则的 Priority 值
        RuleEvaluatedAt          DATETIME2 NULL,              -- 规则裁决时间（= @DataCutoffTime，禁止 GETDATE()）

        BatchNo  NVARCHAR(50)  NULL,                          -- nullable：夜间全量=批次号，白天实时=NULL
        IsActive BIT           NOT NULL DEFAULT 1,
        SyncedAt DATETIME2     NOT NULL DEFAULT GETDATE()
    );

    -- FK：ProductFamily（表建完后追加，允许 NULL）
    ALTER TABLE dbo.SupplyFact_Pipeline
        ADD CONSTRAINT FK_SupplyFact_Pipeline_ProductFamily
        FOREIGN KEY (ProductFamilyId) REFERENCES ProductFamily(Id);

    -- FK：SupplyAvailabilityRule（表建完后追加）
    ALTER TABLE dbo.SupplyFact_Pipeline
        ADD CONSTRAINT FK_SupplyFact_Pipeline_AvailabilityRule
        FOREIGN KEY (SupplyAvailabilityRuleId) REFERENCES SupplyAvailabilityRule(Id);

    -- 唯一索引：同一来源行可存在于多个批次（支持历史快照）
    CREATE UNIQUE NONCLUSTERED INDEX UX_SupplyFact_Pipeline_SourceRow_Batch
    ON dbo.SupplyFact_Pipeline(SourceRowKey, BatchNo)
    WHERE BatchNo IS NOT NULL;

    -- 主查询索引
    CREATE NONCLUSTERED INDEX IX_SupplyFact_Pipeline_Query
    ON dbo.SupplyFact_Pipeline(MaterialCode, FactoryId, ProductFamilyId, SupplyType)
    WHERE IsActive = 1;

    -- 批次快照查询索引
    CREATE NONCLUSTERED INDEX IX_SupplyFact_Pipeline_Batch
    ON dbo.SupplyFact_Pipeline(BatchNo)
    WHERE BatchNo IS NOT NULL;

    PRINT '[TABLE] 已创建 SupplyFact_Pipeline（28字段，对齐官方 DDL）';
END
ELSE
    PRINT '[TABLE] SupplyFact_Pipeline 已存在';
GO

-- ============================================================
-- PART 4: ext_PipelineSupply_Source_View（UNION ALL 四来源，15列契约）
-- V1：sp_SyncPipelineSupply 不读取本视图，V1.1/V2 启用后只读此视图
-- ============================================================

CREATE OR ALTER VIEW dbo.ext_PipelineSupply_Source_View AS
-- 分支1: ERP 厂间在途
SELECT MasterID, MaterialCode, SourceFactoryCode, FactoryCode,
       SupplyType, OwnershipType, QualityStatus, Quantity, ETA,
       StorageCode, SupplierCode, SourceDocumentNo, SourceDocumentLineNo,
       SourceUpdatedAt, CAST('ERP' AS NVARCHAR(50)) AS SourceSystem
FROM dbo.ext_ERP_InterplantInTransit_View

UNION ALL

-- 分支2: ERP 采购在途
SELECT MasterID, MaterialCode, SourceFactoryCode, FactoryCode,
       SupplyType, OwnershipType, QualityStatus, Quantity, ETA,
       StorageCode, SupplierCode, SourceDocumentNo, SourceDocumentLineNo,
       SourceUpdatedAt, CAST('PROCUREMENT' AS NVARCHAR(50)) AS SourceSystem
FROM dbo.ext_ERP_PurchaseInTransit_View

UNION ALL

-- 分支3: VMI
SELECT MasterID, MaterialCode, SourceFactoryCode, FactoryCode,
       SupplyType, OwnershipType, QualityStatus, Quantity, ETA,
       StorageCode, SupplierCode, SourceDocumentNo, SourceDocumentLineNo,
       SourceUpdatedAt, CAST('VMI' AS NVARCHAR(50)) AS SourceSystem
FROM dbo.ext_ERP_VMI_View

UNION ALL

-- 分支4: 已到厂未入库
SELECT MasterID, MaterialCode, SourceFactoryCode, FactoryCode,
       SupplyType, OwnershipType, QualityStatus, Quantity, ETA,
       StorageCode, SupplierCode, SourceDocumentNo, SourceDocumentLineNo,
       SourceUpdatedAt, CAST('ERP' AS NVARCHAR(50)) AS SourceSystem
FROM dbo.ext_ERP_ArrivedNotReceived_View;
GO

PRINT '[VIEW] PART 4: ext_PipelineSupply_Source_View - 15列契约，UNION ALL 4来源';
GO

-- ============================================================
-- PART 5: ext_ERP_Received_ByDocument_View（SYNONYM）
-- 负责：2号位。V1 不建本地快照，Pegging 装载时通过本视图实时读取。
-- 消费场景：INTER_FACTORY_ORDER 跨厂 Pegging，查 ZP/BP 出口库已入库数量
-- ============================================================

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'ext_ERP_Received_ByDocument_View')
    CREATE SYNONYM dbo.ext_ERP_Received_ByDocument_View
        FOR [MES_Integration].[dbo].[ERP_Received_ByDocument_View];
GO

PRINT '[APS][APS_Production] PART 5: ext_ERP_Received_ByDocument_View - SYNONYM';
GO

-- ============================================================
-- PART 6: sp_SyncPipelineSupply（V1 空跑，签名对齐官方 DDL）
-- ============================================================

CREATE OR ALTER PROCEDURE dbo.sp_SyncPipelineSupply
    @BatchNo        NVARCHAR(50),       -- 纯输入，由调用方（NightlyBatchOrchestrator）传入
    @DataCutoffTime DATETIME2,          -- 统一切片边界，V1 忽略值但参数必须存在
    @RowsAffected   INT          OUTPUT,
    @ErrorMessage   NVARCHAR(MAX) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @RowsAffected = 0;
    SET @ErrorMessage = NULL;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- V1：只执行 TRUNCATE + SUCCESS 日志，不读取任何视图或数据源
        TRUNCATE TABLE dbo.SupplyFact_Pipeline;

        INSERT INTO dbo.APS_ETL_Log (BatchNo, Step, Status, Message, CreatedAt)
        VALUES (
            @BatchNo,
            'sp_SyncPipelineSupply',
            'SUCCESS',
            'V1 空跑完成：SupplyFact_Pipeline 已清空；未读取任何管道供给来源，未写入业务数据',
            GETDATE()
        );

        COMMIT TRANSACTION;
        RETURN 0;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMessage = ERROR_MESSAGE();

        -- 技术失败必须传播，不得伪装为空结果集
        INSERT INTO dbo.APS_ETL_Log (BatchNo, Step, Status, Message, CreatedAt)
        VALUES (@BatchNo, 'sp_SyncPipelineSupply', 'FAILED', @ErrorMessage, GETDATE());

        THROW;
    END CATCH
END
GO

PRINT '[PROCEDURE] PART 6: sp_SyncPipelineSupply v5.0.42 - V1 空跑（@BatchNo 输入，@DataCutoffTime 输入）';
GO

-- ============================================================
-- 部署完成汇总
-- ============================================================

PRINT '====================================================================';
PRINT '部署完成: 管道供给骨架 v5.0.42';
PRINT '--------------------------------------------------------------------';
PRINT 'MES_Integration (5号位 ODS 空契约):';
PRINT '  ERP_InterplantInTransit_View          - 14列，V1 WHERE 1=0';
PRINT '  ERP_PurchaseInTransit_View            - 14列，V1 WHERE 1=0';
PRINT '  ERP_VMI_View                          - 14列，V1 WHERE 1=0';
PRINT '  ERP_ArrivedNotReceived_View           - 14列，V1 WHERE 1=0';
PRINT '  ERP_Received_ByDocument_View          - 10列，V1 WHERE 1=0';
PRINT 'APS_Production (2号位):';
PRINT '  ext_ERP_InterplantInTransit_View      - SYNONYM';
PRINT '  ext_ERP_PurchaseInTransit_View        - SYNONYM';
PRINT '  ext_ERP_VMI_View                      - SYNONYM';
PRINT '  ext_ERP_ArrivedNotReceived_View       - SYNONYM';
PRINT '  ext_ERP_Received_ByDocument_View      - SYNONYM（Pegging INTER_FACTORY_ORDER 消费）';
PRINT '  SupplyAvailabilityRule                - 15字段，UX 唯一索引，Priority 默认 50';
PRINT '  SupplyFact_Pipeline                   - 28字段，3索引，FK×4';
PRINT '  ext_PipelineSupply_Source_View        - 15列，UNION ALL 4来源';
PRINT '  ext_ERP_Received_ByDocument_View      - SYNONYM → MES_Integration，Pegging INTER_FACTORY_ORDER 消费';
PRINT '  sp_SyncPipelineSupply                 - V1 空跑（TRUNCATE + SUCCESS 日志）';
PRINT '====================================================================';
GO