USE [APS_Production]
GO

-- =============================================
-- 库存快照同步 v5.0.39 / v5.0.40 部署脚本
-- 日期: 2026-06-08
-- 变更摘要:
--   1. 旧三表规则（ProductFamilyInventoryScope / InventorySourceRule / InventorySourcePriority）删除
--   2. 统一为 InventoryAvailabilityRule 一张规则配置表
--   3. 新增 InventoryAvailableSupplyDetail 明细层（规则命中后、汇总前）
--   4. 旧 SP（sp_SyncInventory + sp_RefreshInventoryBalance）替换为 sp_SyncInventorySnapshot 六步 ETL
--   5. InventoryBalance.ProductFamilyId 来源改为规则输出（非 Material.ProductFamilyId）
--   6. 候选池改为白名单模式（IsEligible 默认 0）
-- =============================================

PRINT N'=== 库存快照同步 v5.0.39 部署开始 ===';
GO

-- ============================================================
-- PART 1: SYNONYM（跨库访问 ODS 契约视图）
-- ============================================================
PRINT N'[PART 1] SYNONYM 设置...';

IF OBJECT_ID('ext_ERP_Inventory_View', 'SN') IS NOT NULL
    DROP SYNONYM ext_ERP_Inventory_View;
GO
CREATE SYNONYM ext_ERP_Inventory_View
    FOR [mes].[MES_Integration].[dbo].[ERP_Inventory_View];
GO

IF OBJECT_ID('ext_MES_Inventory_View', 'SN') IS NOT NULL
    DROP SYNONYM ext_MES_Inventory_View;
GO
CREATE SYNONYM ext_MES_Inventory_View
    FOR [mes].[MES_Integration].[dbo].[MES_Inventory_View];
GO

-- ============================================================
-- PART 2: InventoryFact_ERP（L1 ERP 事实层）
-- ============================================================
PRINT N'[PART 2] InventoryFact_ERP...';

IF OBJECT_ID('InventoryFact_ERP', 'U') IS NOT NULL
    DROP TABLE InventoryFact_ERP;
GO

CREATE TABLE InventoryFact_ERP (
    Id            BIGINT PRIMARY KEY IDENTITY(1,1),
    MasterID      INT NOT NULL,
    WarehouseCode NVARCHAR(50) NOT NULL,
    FactoryCode   NVARCHAR(50) NULL,
    Quantity      DECIMAL(18,4) NOT NULL,
    SyncedAt      DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT UQ_Inventory_ERP UNIQUE (MasterID, WarehouseCode, FactoryCode)
);
GO

CREATE INDEX IX_InventoryFact_ERP_Query
    ON InventoryFact_ERP(MasterID, WarehouseCode)
    INCLUDE (Quantity, FactoryCode, SyncedAt);
GO

-- ============================================================
-- PART 3: InventoryFact_MES（L1 MES 事实层）
-- ============================================================
PRINT N'[PART 3] InventoryFact_MES...';

IF OBJECT_ID('InventoryFact_MES', 'U') IS NOT NULL
    DROP TABLE InventoryFact_MES;
GO

CREATE TABLE InventoryFact_MES (
    Id            BIGINT PRIMARY KEY IDENTITY(1,1),
    MES_ID        INT NOT NULL,
    Location      NVARCHAR(50) NOT NULL,
    WarehouseCode NVARCHAR(50) NULL,
    FactoryCode   NVARCHAR(50) NULL,
    Quantity      DECIMAL(18,4) NOT NULL,
    SyncedAt      DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT UQ_Inventory_MES UNIQUE (MES_ID, WarehouseCode, FactoryCode)
);
GO

CREATE INDEX IX_InventoryFact_MES_Query
    ON InventoryFact_MES(MES_ID, WarehouseCode, FactoryCode)
    INCLUDE (Quantity, Location, SyncedAt);
GO

-- ============================================================
-- PART 4: InventorySupplyCandidate（L2 候选供给池，白名单模式）
-- ============================================================
PRINT N'[PART 4] InventorySupplyCandidate...';

IF OBJECT_ID('InventoryAvailableSupplyDetail', 'U') IS NOT NULL
    DROP TABLE InventoryAvailableSupplyDetail;
IF OBJECT_ID('InventorySupplyCandidate', 'U') IS NOT NULL
    DROP TABLE InventorySupplyCandidate;
GO

CREATE TABLE InventorySupplyCandidate (
    Id           BIGINT PRIMARY KEY IDENTITY(1,1),
    MaterialCode NVARCHAR(50) NOT NULL,
    FactoryId    INT NOT NULL FOREIGN KEY REFERENCES Factory(Id),
    SourceSystem NVARCHAR(20) NOT NULL,
    StorageCode  NVARCHAR(50) NOT NULL,
    Quantity     DECIMAL(18,4) NOT NULL,
    ERP_MasterID INT NULL,
    MES_ID       INT NULL,
    IsEligible   BIT NOT NULL DEFAULT 0,
    RejectReason NVARCHAR(500) NULL,
    SyncedAt     DATETIME2 NOT NULL,
    CreatedAt    DATETIME2 NOT NULL DEFAULT GETDATE()
);
GO

CREATE INDEX IX_InventorySupplyCandidate_Material
ON InventorySupplyCandidate(MaterialCode, FactoryId, SourceSystem);

CREATE INDEX IX_InventorySupplyCandidate_Eligible
ON InventorySupplyCandidate(MaterialCode, FactoryId, IsEligible)
WHERE IsEligible = 1;
GO
-- ============================================================
-- PART 5: InventoryAvailabilityRule（统一规则表）
-- ============================================================
PRINT N'[PART 5] InventoryAvailabilityRule...';

IF OBJECT_ID('InventoryAvailabilityRule', 'U') IS NULL
BEGIN
    CREATE TABLE InventoryAvailabilityRule (
        Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
        ProductFamilyId     INT NOT NULL,
        FactoryId           INT NOT NULL,
        MaterialCodePattern NVARCHAR(100) NULL,
        SourceSystem        NVARCHAR(20) NOT NULL,
        StorageCode         NVARCHAR(50) NOT NULL,
        IsAvailable         BIT NOT NULL DEFAULT 1,
        Priority            INT NOT NULL DEFAULT 100,
        IsActive            BIT NOT NULL DEFAULT 1,
        Remark              NVARCHAR(500) NULL,
        CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_InventoryAvailabilityRule_ProductFamily
            FOREIGN KEY (ProductFamilyId) REFERENCES ProductFamily(Id),
        CONSTRAINT FK_InventoryAvailabilityRule_Factory
            FOREIGN KEY (FactoryId) REFERENCES Factory(Id)
    );

    CREATE INDEX IX_InventoryAvailabilityRule_Context
    ON InventoryAvailabilityRule(ProductFamilyId, FactoryId, SourceSystem, StorageCode, IsActive);

    CREATE INDEX IX_InventoryAvailabilityRule_Priority
    ON InventoryAvailabilityRule(ProductFamilyId, FactoryId, Priority);

    PRINT N'  InventoryAvailabilityRule 创建完成';
END
ELSE
BEGIN
    PRINT N'  InventoryAvailabilityRule 已存在，跳过';
END
GO

-- ============================================================
-- PART 6: InventoryAvailableSupplyDetail（规则命中明细层）
-- ============================================================
PRINT N'[PART 6] InventoryAvailableSupplyDetail...';

IF OBJECT_ID('InventoryAvailableSupplyDetail', 'U') IS NULL
BEGIN
    CREATE TABLE InventoryAvailableSupplyDetail (
        Id                         BIGINT PRIMARY KEY IDENTITY(1,1),
        BatchNo                    NVARCHAR(50) NOT NULL,
        MaterialCode               NVARCHAR(50) NOT NULL,
        ProductFamilyId            INT NOT NULL,
        FactoryId                  INT NOT NULL,
        SourceSystem               NVARCHAR(20) NOT NULL,
        StorageCode                NVARCHAR(50) NOT NULL,
        Quantity                   DECIMAL(18,4) NOT NULL,
        AvailabilityRuleId         BIGINT NOT NULL,
        RulePriority               INT NOT NULL,
        ERP_MasterID               INT NULL,
        MES_ID                     INT NULL,
        InventorySupplyCandidateId BIGINT NULL,
        CreatedAt                  DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_IASD_ProductFamily FOREIGN KEY (ProductFamilyId) REFERENCES ProductFamily(Id),
        CONSTRAINT FK_IASD_Factory       FOREIGN KEY (FactoryId) REFERENCES Factory(Id),
        CONSTRAINT FK_IASD_Rule          FOREIGN KEY (AvailabilityRuleId) REFERENCES InventoryAvailabilityRule(Id)
    );

    CREATE INDEX IX_IASD_Deduction
    ON InventoryAvailableSupplyDetail(MaterialCode, ProductFamilyId, FactoryId, RulePriority);

    CREATE INDEX IX_IASD_Batch
    ON InventoryAvailableSupplyDetail(BatchNo);

    CREATE INDEX IX_IASD_Candidate
    ON InventoryAvailableSupplyDetail(InventorySupplyCandidateId);

    PRINT N'  InventoryAvailableSupplyDetail 创建完成';
END
ELSE
BEGIN
    PRINT N'  InventoryAvailableSupplyDetail 已存在，跳过';
END
GO
-- ============================================================
-- PART 7: InventoryBalance（L4 可用库存余额）
-- ============================================================
PRINT N'[PART 7] InventoryBalance...';

IF OBJECT_ID('InventoryBalance', 'U') IS NOT NULL
    DROP TABLE InventoryBalance;
GO

CREATE TABLE InventoryBalance (
    Id              BIGINT PRIMARY KEY IDENTITY(1,1),
    MaterialCode    NVARCHAR(50) NOT NULL,
    ProductFamilyId INT NOT NULL FOREIGN KEY REFERENCES ProductFamily(Id),
    FactoryId       INT NOT NULL FOREIGN KEY REFERENCES Factory(Id),
    OnHandQty       DECIMAL(18,4) NOT NULL,
    AllocatedQty    DECIMAL(18,4) NOT NULL DEFAULT 0,
    AvailableQty    AS (OnHandQty - AllocatedQty) PERSISTED,
    Source          NVARCHAR(20) NOT NULL,
    BatchNo         NVARCHAR(50) NULL,
    LastUpdatedAt   DATETIME2 NOT NULL DEFAULT GETDATE(),
    CreatedAt       DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT UQ_Inventory_Balance UNIQUE (MaterialCode, ProductFamilyId, FactoryId)
);
GO

CREATE INDEX IX_InventoryBalance_Query
    ON InventoryBalance(MaterialCode, ProductFamilyId, FactoryId)
    INCLUDE (OnHandQty, AllocatedQty, Source);

CREATE INDEX IX_InventoryBalance_Batch
    ON InventoryBalance(BatchNo)
    WHERE BatchNo IS NOT NULL;
GO
-- ============================================================
-- PART 8: sp_SyncInventorySnapshot 六步 ETL 存储过程
-- ============================================================
PRINT N'[PART 8] sp_SyncInventorySnapshot...';
GO

CREATE OR ALTER PROCEDURE sp_SyncInventorySnapshot
    @BatchNo       NVARCHAR(50),
    @RowsAffected  INT OUTPUT,
    @ErrorMessage  NVARCHAR(MAX) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @RowsAffected = 0;
    SET @ErrorMessage = NULL;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ============================================================
        -- Step 1: ext_ERP_Inventory_View → InventoryFact_ERP
        -- ============================================================
        TRUNCATE TABLE InventoryFact_ERP;

        INSERT INTO InventoryFact_ERP (MasterID, WarehouseCode, FactoryCode, Quantity, SyncedAt)
        SELECT v.MasterID, v.WarehouseCode, v.FactoryCode, v.Quantity, GETDATE()
        FROM ext_ERP_Inventory_View v
        WHERE v.Quantity > 0;

        -- ============================================================
        -- Step 2: ext_MES_Inventory_View → InventoryFact_MES
        -- ============================================================
        TRUNCATE TABLE InventoryFact_MES;

        INSERT INTO InventoryFact_MES (MES_ID, Location, WarehouseCode, FactoryCode, Quantity, SyncedAt)
        SELECT v.MES_ID, v.WarehouseCode, v.WarehouseCode, v.FactoryCode, v.Quantity, GETDATE()
        FROM ext_MES_Inventory_View v
        WHERE v.Quantity > 0;

        -- ============================================================
        -- Step 3: MaterialMapping 桥接 → InventorySupplyCandidate
        --         白名单模式：IsEligible 默认 0
        -- ============================================================
        TRUNCATE TABLE InventoryAvailableSupplyDetail;
        TRUNCATE TABLE InventorySupplyCandidate;

        -- Step 3a: ERP 候选
        INSERT INTO InventorySupplyCandidate
            (MaterialCode, FactoryId, SourceSystem, StorageCode, Quantity, ERP_MasterID, MES_ID, IsEligible, SyncedAt, CreatedAt)
        SELECT
            m.MaterialCode, f.Id, 'ERP', e.WarehouseCode,
            e.Quantity, e.MasterID, NULL,
            0,
            GETDATE(), GETDATE()
        FROM InventoryFact_ERP e
        INNER JOIN MaterialMapping m
            ON  m.SourceID       = e.MasterID
            AND m.Source         = 'ERP'
            AND m.IsCurrent      = 1
            AND m.Warehouse_Norm = ISNULL(e.WarehouseCode, 'N/A')
        INNER JOIN Factory f ON f.Code = e.FactoryCode AND f.IsActive = 1;
        -- Step 3a-log: ERP 被过滤行（最多 50 条 WARN）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT TOP 50
            @BatchNo,
            'sp_SyncInventorySnapshot.Step3.ERP.FilteredOut',
            CONCAT('FilteredOut: MasterID=', CAST(e.MasterID AS NVARCHAR(20)),
                   ' WarehouseCode=', ISNULL(e.WarehouseCode, 'NULL'),
                   ' FactoryCode=',  ISNULL(e.FactoryCode, 'NULL'),
                   ' Reason=', CASE
                       WHEN m.SourceID IS NULL THEN 'NoMaterialMapping'
                       ELSE 'NoFactory'
                   END),
            'WARN', GETDATE()
        FROM InventoryFact_ERP e
        LEFT JOIN MaterialMapping m
            ON  m.SourceID       = e.MasterID
            AND m.Source         = 'ERP'
            AND m.IsCurrent      = 1
            AND m.Warehouse_Norm = ISNULL(e.WarehouseCode, 'N/A')
        LEFT JOIN Factory f ON f.Code = e.FactoryCode AND f.IsActive = 1
        WHERE m.SourceID IS NULL OR f.Id IS NULL;

        -- Step 3b: MES 候选
        INSERT INTO InventorySupplyCandidate
            (MaterialCode, FactoryId, SourceSystem, StorageCode, Quantity, ERP_MasterID, MES_ID, IsEligible, SyncedAt, CreatedAt)
        SELECT
            m.MaterialCode, f.Id, 'MES', ms.WarehouseCode,
            ms.Quantity, NULL, ms.MES_ID,
            0,
            GETDATE(), GETDATE()
        FROM InventoryFact_MES ms
        INNER JOIN MaterialMapping m
            ON  m.SourceID       = ms.MES_ID
            AND m.Source         = 'MES'
            AND m.IsCurrent      = 1
            AND m.Warehouse_Norm = ISNULL(ms.WarehouseCode, 'N/A')
        INNER JOIN Factory f ON f.Code = ms.FactoryCode AND f.IsActive = 1;

        -- Step 3b-log: MES 被过滤行（最多 50 条 WARN）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT TOP 50
            @BatchNo,
            'sp_SyncInventorySnapshot.Step3.MES.FilteredOut',
            CONCAT('FilteredOut: MES_ID=', CAST(ms.MES_ID AS NVARCHAR(20)),
                   ' WarehouseCode=', ISNULL(ms.WarehouseCode, 'NULL'),
                   ' FactoryCode=',   ISNULL(ms.FactoryCode, 'NULL'),
                   ' Reason=', CASE
                       WHEN m.SourceID IS NULL THEN 'NoMaterialMapping'
                       ELSE 'NoFactory'
                   END),
            'WARN', GETDATE()
        FROM InventoryFact_MES ms
        LEFT JOIN MaterialMapping m
            ON  m.SourceID       = ms.MES_ID
            AND m.Source         = 'MES'
            AND m.IsCurrent      = 1
            AND m.Warehouse_Norm = ISNULL(ms.WarehouseCode, 'N/A')
        LEFT JOIN Factory f ON f.Code = ms.FactoryCode AND f.IsActive = 1
        WHERE m.SourceID IS NULL OR f.Id IS NULL;
        -- ============================================================
        -- Step 4: 规则裁决（胜出规则模式）
        --   按 (CandidateId, ProductFamilyId) 分组选唯一胜出规则
        --   排序: 精确匹配优先 → Priority ASC → Id ASC (tiebreak)
        -- ============================================================
        IF OBJECT_ID('tempdb..#WinnerRules') IS NOT NULL DROP TABLE #WinnerRules;

        SELECT
            c.Id              AS CandidateId,
            c.MaterialCode,
            c.FactoryId,
            c.SourceSystem,
            c.StorageCode,
            c.Quantity,
            c.ERP_MasterID,
            c.MES_ID,
            r.Id              AS RuleId,
            r.ProductFamilyId,
            r.IsAvailable,
            r.Priority,
            ROW_NUMBER() OVER (
                PARTITION BY c.Id, r.ProductFamilyId
                ORDER BY
                    CASE WHEN r.MaterialCodePattern IS NOT NULL THEN 0 ELSE 1 END ASC,
                    r.Priority ASC,
                    r.Id ASC
            ) AS Rn,
            COUNT(*) OVER (
                PARTITION BY c.Id, r.ProductFamilyId,
                    CASE WHEN r.MaterialCodePattern IS NOT NULL THEN 0 ELSE 1 END,
                    r.Priority
            ) AS TieCount
        INTO #WinnerRules
        FROM InventorySupplyCandidate c
        INNER JOIN InventoryAvailabilityRule r
            ON  r.FactoryId    = c.FactoryId
            AND r.SourceSystem = c.SourceSystem
            AND r.StorageCode  = c.StorageCode
            AND (r.MaterialCodePattern IS NULL OR r.MaterialCodePattern = '' OR c.MaterialCode LIKE r.MaterialCodePattern)
            AND r.IsActive     = 1;

        -- 4b: 并列告警（最多 50 条）
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT TOP 50
            @BatchNo,
            'sp_SyncInventorySnapshot.Step4.TieWarn',
            CONCAT('RuleTie: MaterialCode=', w.MaterialCode,
                   ' ProductFamilyId=', CAST(w.ProductFamilyId AS NVARCHAR(10)),
                   ' TieCount=', CAST(w.TieCount AS NVARCHAR(5)),
                   ' WinnerRuleId=', CAST(w.RuleId AS NVARCHAR(20))),
            'WARN', GETDATE()
        FROM #WinnerRules w
        WHERE w.Rn = 1 AND w.TieCount > 1;
        -- 4c: 胜出规则 IsAvailable=1 → 写明细层
        INSERT INTO InventoryAvailableSupplyDetail
            (BatchNo, MaterialCode, ProductFamilyId, FactoryId, SourceSystem, StorageCode,
             Quantity, AvailabilityRuleId, RulePriority, ERP_MasterID, MES_ID,
             InventorySupplyCandidateId, CreatedAt)
        SELECT
            @BatchNo, w.MaterialCode, w.ProductFamilyId, w.FactoryId, w.SourceSystem, w.StorageCode,
            w.Quantity, w.RuleId, w.Priority, w.ERP_MasterID, w.MES_ID,
            w.CandidateId, GETDATE()
        FROM #WinnerRules w
        WHERE w.Rn = 1 AND w.IsAvailable = 1;

        -- 4d: 回标 IsEligible=1
        UPDATE c
        SET c.IsEligible = 1
        FROM InventorySupplyCandidate c
        WHERE EXISTS (
            SELECT 1 FROM #WinnerRules w
            WHERE w.CandidateId = c.Id AND w.Rn = 1 AND w.IsAvailable = 1
        );

        -- 4e: 胜出规则 IsAvailable=0 → 回标 RejectReason
        UPDATE c
        SET c.RejectReason = CONCAT('ExcludedByRule: RuleId=', w.RuleId,
                                    ' Priority=', CAST(w.Priority AS NVARCHAR(10)),
                                    ' ProductFamilyId=', CAST(w.ProductFamilyId AS NVARCHAR(10)))
        FROM InventorySupplyCandidate c
        INNER JOIN (
            SELECT CandidateId,
                   MIN(RuleId)         AS RuleId,
                   MIN(Priority)       AS Priority,
                   MIN(ProductFamilyId) AS ProductFamilyId
            FROM #WinnerRules
            WHERE Rn = 1 AND IsAvailable = 0
            GROUP BY CandidateId
        ) w ON w.CandidateId = c.Id
        WHERE c.IsEligible = 0;

        -- 4f: 无匹配规则 → RejectReason + WARN
        UPDATE c
        SET c.RejectReason = 'NoRuleMatch: no active InventoryAvailabilityRule matched'
        FROM InventorySupplyCandidate c
        WHERE c.IsEligible = 0 AND c.RejectReason IS NULL;

        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        SELECT TOP 50
            @BatchNo,
            'sp_SyncInventorySnapshot.Step4.NoRuleMatch',
            CONCAT('NoRuleMatch: MaterialCode=', c.MaterialCode,
                   ' SourceSystem=', c.SourceSystem,
                   ' StorageCode=', c.StorageCode,
                   ' FactoryId=', CAST(c.FactoryId AS NVARCHAR(20))),
            'WARN', GETDATE()
        FROM InventorySupplyCandidate c
        WHERE c.RejectReason = 'NoRuleMatch: no active InventoryAvailabilityRule matched';

        DROP TABLE #WinnerRules;
        -- ============================================================
        -- Step 5: InventoryAvailableSupplyDetail → InventoryBalance（汇总）
        --   ProductFamilyId 来源：明细层（= 规则输出，非 Material.ProductFamilyId）
        -- ============================================================
        TRUNCATE TABLE InventoryBalance;

        INSERT INTO InventoryBalance
            (MaterialCode, ProductFamilyId, FactoryId, OnHandQty, AllocatedQty, Source, BatchNo, LastUpdatedAt, CreatedAt)
        SELECT
            d.MaterialCode,
            d.ProductFamilyId,
            d.FactoryId,
            SUM(d.Quantity)         AS OnHandQty,
            0                       AS AllocatedQty,
            CASE
                WHEN COUNT(DISTINCT d.SourceSystem) > 1 THEN 'BOTH'
                ELSE MAX(d.SourceSystem)
            END                     AS Source,
            @BatchNo,
            GETDATE(),
            GETDATE()
        FROM InventoryAvailableSupplyDetail d
        WHERE d.BatchNo = @BatchNo
        GROUP BY d.MaterialCode, d.ProductFamilyId, d.FactoryId;

        SET @RowsAffected = @@ROWCOUNT;

        -- ============================================================
        -- Step 6: ETL 成功日志
        -- ============================================================
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (@BatchNo, 'sp_SyncInventorySnapshot',
                CONCAT('InventoryBalance rows=', @RowsAffected,
                       '; Detail rows=', (SELECT COUNT(*) FROM InventoryAvailableSupplyDetail WHERE BatchNo=@BatchNo)),
                'SUCCESS', GETDATE());

        COMMIT TRANSACTION;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @ErrorMessage = ERROR_MESSAGE();
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (@BatchNo, 'sp_SyncInventorySnapshot', @ErrorMessage, 'FAILED', GETDATE());
    END CATCH
END;
GO

-- ============================================================
-- PART 9: 清理旧表 + 验证
-- ============================================================
PRINT N'[PART 9] 清理旧规则表...';

IF OBJECT_ID('ProductFamilyInventoryScope', 'U') IS NOT NULL
BEGIN
    DROP TABLE ProductFamilyInventoryScope;
    PRINT N'  已删除 ProductFamilyInventoryScope';
END

IF OBJECT_ID('InventorySourceRule', 'U') IS NOT NULL
BEGIN
    DROP TABLE InventorySourceRule;
    PRINT N'  已删除 InventorySourceRule';
END

IF OBJECT_ID('InventorySourcePriority', 'U') IS NOT NULL
BEGIN
    DROP TABLE InventorySourcePriority;
    PRINT N'  已删除 InventorySourcePriority';
END

IF OBJECT_ID('sp_RefreshInventoryBalance', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE sp_RefreshInventoryBalance;
    PRINT N'  已删除 sp_RefreshInventoryBalance';
END

IF OBJECT_ID('sp_SyncInventory', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE sp_SyncInventory;
    PRINT N'  已删除旧 sp_SyncInventory';
END
GO

PRINT N'=== 库存快照同步 v5.0.39 部署完成 ===';
GO

-- ============================================================
-- 验证脚本（手动执行）
-- ============================================================
/*
-- 检查所有对象是否就绪
SELECT 'SYNONYM' AS Type, name FROM sys.synonyms WHERE name IN ('ext_ERP_Inventory_View','ext_MES_Inventory_View')
UNION ALL
SELECT 'TABLE', name FROM sys.tables WHERE name IN ('InventoryFact_ERP','InventoryFact_MES','InventorySupplyCandidate','InventoryAvailabilityRule','InventoryAvailableSupplyDetail','InventoryBalance')
UNION ALL
SELECT 'SP', name FROM sys.procedures WHERE name = 'sp_SyncInventorySnapshot';

-- 空跑测试（无规则配置时应正常完成，BalanceRows=0）
DECLARE @BatchNo NVARCHAR(50) = 'TEST_' + FORMAT(GETDATE(), 'yyyyMMdd_HHmmss');
DECLARE @Rows INT, @Err NVARCHAR(MAX);
EXEC sp_SyncInventorySnapshot @BatchNo=@BatchNo, @RowsAffected=@Rows OUTPUT, @ErrorMessage=@Err OUTPUT;
SELECT @BatchNo AS BatchNo, @Rows AS RowsAffected, @Err AS ErrorMessage;

-- 查看日志
SELECT TOP 20 * FROM APS_ETL_Log WHERE BatchNo = @BatchNo ORDER BY Id DESC;
*/
