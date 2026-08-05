-- =============================================
-- APS 订单BOM入口解析重构 - 增量DDL脚本
-- 版本：v5.0.21
-- 日期：2026-05-09
-- 说明：订单BOMNO改可空 + 新增FailureCode/NextActionCode双维度 + MES_API_BOM_Request_Detail表重构
--
-- 执行顺序：
--   1. 备份相关表数据（可选，测试环境可跳过）
--   2. 执行本脚本
--   3. 验证表结构
--   4. 更新存储过程（sp_ValidateAndPromoteOrders / sp_GetActiveRootBOMNOs）
--
-- ⚠️ 注意事项：
--   - MES_API_BOM_Request_Detail 表需要重建（唯一约束变更）
--   - 如果表中有数据，会先清空再重建
--   - 建议在测试环境先执行验证
-- =============================================

USE APS_Production;
GO

PRINT '========================================';
PRINT '开始执行 v5.0.21 订单BOM入口解析重构';
PRINT '========================================';
GO

-- =============================================
-- 1. 修改 ERP_Order_Staging 表
-- =============================================
PRINT '';
PRINT '1. 修改 ERP_Order_Staging 表...';
GO

-- 1.1 BOMNO 改为可空
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ERP_Order_Staging')
      AND name = 'BOMNO'
      AND is_nullable = 0
)
BEGIN
    PRINT '  - 将 BOMNO 列改为可空...';
    ALTER TABLE ERP_Order_Staging
    ALTER COLUMN BOMNO NVARCHAR(50) NULL;
    PRINT '  ✓ BOMNO 列已改为可空';
END
ELSE
BEGIN
    PRINT '  ⊙ BOMNO 列已经是可空，跳过';
END
GO

-- 1.2 新增 FailureCode 列
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ERP_Order_Staging')
      AND name = 'FailureCode'
)
BEGIN
    PRINT '  - 新增 FailureCode 列...';
    ALTER TABLE ERP_Order_Staging
    ADD FailureCode NVARCHAR(50) NULL;
    PRINT '  ✓ FailureCode 列已新增';

    -- 添加列注释
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'失败原因维度（BOMNO_MISSING/MATERIAL_NOT_FOUND/FACTORY_INVALID/VALIDATION_FAILED）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'ERP_Order_Staging',
        @level2type = N'COLUMN', @level2name = N'FailureCode';
END
ELSE
BEGIN
    PRINT '  ⊙ FailureCode 列已存在，跳过';
END
GO

-- 1.3 新增 NextActionCode 列
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ERP_Order_Staging')
      AND name = 'NextActionCode'
)
BEGIN
    PRINT '  - 新增 NextActionCode 列...';
    ALTER TABLE ERP_Order_Staging
    ADD NextActionCode NVARCHAR(50) NULL;
    PRINT '  ✓ NextActionCode 列已新增';

    -- 添加列注释
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'后续动作维度（WAIT_BOM_WORKSET/MANUAL_REVIEW/RETRY/SKIP）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'ERP_Order_Staging',
        @level2type = N'COLUMN', @level2name = N'NextActionCode';
END
ELSE
BEGIN
    PRINT '  ⊙ NextActionCode 列已存在，跳过';
END
GO

PRINT '✓ ERP_Order_Staging 表修改完成';
GO

-- =============================================
-- 2. 修改 Order_Canonical 表
-- =============================================
PRINT '';
PRINT '2. 修改 Order_Canonical 表...';
GO

-- 2.1 BOMNO 改为可空
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('Order_Canonical')
      AND name = 'BOMNO'
      AND is_nullable = 0
)
BEGIN
    PRINT '  - 将 BOMNO 列改为可空...';
    ALTER TABLE Order_Canonical
    ALTER COLUMN BOMNO NVARCHAR(50) NULL;
    PRINT '  ✓ BOMNO 列已改为可空';
END
ELSE
BEGIN
    PRINT '  ⊙ BOMNO 列已经是可空，跳过';
END
GO

PRINT '✓ Order_Canonical 表修改完成';
GO

-- =============================================
-- 3. 重建 MES_API_BOM_Request_Detail 表（ODS库）
-- =============================================
PRINT '';
PRINT '3. 重建 MES_API_BOM_Request_Detail 表（ODS库）...';
GO

USE MES_Integration;
GO

-- 3.1 检查表是否存在数据
DECLARE @RowCount INT;
SELECT @RowCount = COUNT(*) FROM MES_API_BOM_Request_Detail;

IF @RowCount > 0
BEGIN
    PRINT '  ⚠️  警告：MES_API_BOM_Request_Detail 表中有 ' + CAST(@RowCount AS NVARCHAR(20)) + ' 行数据';
    PRINT '  ⚠️  表结构需要重建，现有数据将被清空';
    PRINT '  ⚠️  如需保留数据，请先备份';
    PRINT '';
    PRINT '  按任意键继续，或 Ctrl+C 取消...';
    -- WAITFOR DELAY '00:00:05';  -- 等待5秒，给用户反应时间
END
GO

-- 3.2 删除旧表
IF OBJECT_ID('MES_API_BOM_Request_Detail', 'U') IS NOT NULL
BEGIN
    PRINT '  - 删除旧的 MES_API_BOM_Request_Detail 表...';
    DROP TABLE MES_API_BOM_Request_Detail;
    PRINT '  ✓ 旧表已删除';
END
GO

-- 3.3 创建新表（v5.0.21结构）
PRINT '  - 创建新的 MES_API_BOM_Request_Detail 表...';
GO

CREATE TABLE MES_API_BOM_Request_Detail (
    Id BIGINT PRIMARY KEY IDENTITY(1,1),
    BatchNo NVARCHAR(50) NOT NULL,

    -- v5.0.21 新增：订单级粒度字段
    OrderStagingId BIGINT NOT NULL,          -- 订单ID（唯一约束键）
    BOMNO NVARCHAR(50) NULL,                 -- 改为可空
    Model NVARCHAR(100) NULL,                -- 物料型号（用于BOM入口解析）
    MaterialCode NVARCHAR(50) NULL,          -- 物料编码（用于BOM入口解析）
    FactoryCode NVARCHAR(20) NULL,           -- 工厂编码（用于BOM入口解析）

    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),

    -- v5.0.21 唯一约束：从 (BatchNo, BOMNO) 改为 (BatchNo, OrderStagingId)
    CONSTRAINT UQ_BOMRequestDetail_BatchOrder UNIQUE (BatchNo, OrderStagingId)
);
GO

-- 3.4 创建索引
CREATE NONCLUSTERED INDEX IX_BOMRequestDetail_BatchNo
ON MES_API_BOM_Request_Detail(BatchNo, CreatedAt);
GO

CREATE NONCLUSTERED INDEX IX_BOMRequestDetail_BOMNO
ON MES_API_BOM_Request_Detail(BOMNO)
WHERE BOMNO IS NOT NULL;  -- 过滤索引，只索引非空BOMNO
GO

PRINT '✓ MES_API_BOM_Request_Detail 表重建完成';
GO

-- =============================================
-- 4. 创建急单临时桥接表（APS库）
-- =============================================
PRINT '';
PRINT '4. 创建急单临时桥接表...';
GO

USE APS_Production;
GO

-- 4.1 OrderEmergencyMaterialOverride
IF OBJECT_ID('OrderEmergencyMaterialOverride', 'U') IS NULL
BEGIN
    PRINT '  - 创建 OrderEmergencyMaterialOverride 表...';

    CREATE TABLE OrderEmergencyMaterialOverride (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        OrderStagingId BIGINT NOT NULL,
        OriginalMaterialCode NVARCHAR(50) NOT NULL,
        OverrideMaterialCode NVARCHAR(50) NOT NULL,
        OverrideReason NVARCHAR(200) NULL,
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        IsActive BIT NOT NULL DEFAULT 1
    );

    CREATE INDEX IX_OrderEmergencyMaterialOverride_Order
    ON OrderEmergencyMaterialOverride(OrderStagingId, IsActive);

    PRINT '  ✓ OrderEmergencyMaterialOverride 表已创建';
END
ELSE
BEGIN
    PRINT '  ⊙ OrderEmergencyMaterialOverride 表已存在，跳过';
END
GO

-- 4.2 OrderEmergencyBomWorkset
IF OBJECT_ID('OrderEmergencyBomWorkset', 'U') IS NULL
BEGIN
    PRINT '  - 创建 OrderEmergencyBomWorkset 表...';

    CREATE TABLE OrderEmergencyBomWorkset (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        OrderStagingId BIGINT NOT NULL,
        BOMNO NVARCHAR(50) NOT NULL,
        ParentMaterialCode NVARCHAR(50) NOT NULL,
        ChildMaterialCode NVARCHAR(50) NOT NULL,
        Quantity DECIMAL(18,6) NOT NULL,
        BOMLevel INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX IX_OrderEmergencyBomWorkset_Order
    ON OrderEmergencyBomWorkset(OrderStagingId, BOMNO);

    PRINT '  ✓ OrderEmergencyBomWorkset 表已创建';
END
ELSE
BEGIN
    PRINT '  ⊙ OrderEmergencyBomWorkset 表已存在，跳过';
END
GO

-- 4.3 OrderEmergencyBomStageDetail
IF OBJECT_ID('OrderEmergencyBomStageDetail', 'U') IS NULL
BEGIN
    PRINT '  - 创建 OrderEmergencyBomStageDetail 表...';

    CREATE TABLE OrderEmergencyBomStageDetail (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        OrderStagingId BIGINT NOT NULL,
        BOMNO NVARCHAR(50) NOT NULL,
        ChildMaterialCode NVARCHAR(50) NOT NULL,
        StageSeq INT NOT NULL,
        StageCode NVARCHAR(50) NOT NULL,
        IsSupplyThreshold BIT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX IX_OrderEmergencyBomStageDetail_Order
    ON OrderEmergencyBomStageDetail(OrderStagingId, BOMNO);

    PRINT '  ✓ OrderEmergencyBomStageDetail 表已创建';
END
ELSE
BEGIN
    PRINT '  ⊙ OrderEmergencyBomStageDetail 表已存在，跳过';
END
GO

PRINT '✓ 急单临时桥接表创建完成';
GO

-- =============================================
-- 5. 验证脚本
-- =============================================
PRINT '';
PRINT '========================================';
PRINT '5. 验证表结构...';
PRINT '========================================';
GO

-- 5.1 验证 ERP_Order_Staging
PRINT '';
PRINT 'ERP_Order_Staging 表结构：';
SELECT
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ERP_Order_Staging'
  AND COLUMN_NAME IN ('BOMNO', 'FailureCode', 'NextActionCode')
ORDER BY ORDINAL_POSITION;
GO

-- 5.2 验证 Order_Canonical
PRINT '';
PRINT 'Order_Canonical 表 BOMNO 列：';
SELECT
    COLUMN_NAME,
    DATA_TYPE,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'Order_Canonical'
  AND COLUMN_NAME = 'BOMNO';
GO

-- 5.3 验证 MES_API_BOM_Request_Detail
USE MES_Integration;
GO

PRINT '';
PRINT 'MES_API_BOM_Request_Detail 表结构：';
SELECT
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'MES_API_BOM_Request_Detail'
ORDER BY ORDINAL_POSITION;
GO

PRINT '';
PRINT 'MES_API_BOM_Request_Detail 唯一约束：';
SELECT
    tc.CONSTRAINT_NAME,
    STRING_AGG(ccu.COLUMN_NAME, ', ') AS Columns
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu
    ON tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
WHERE tc.TABLE_NAME = 'MES_API_BOM_Request_Detail'
  AND tc.CONSTRAINT_TYPE = 'UNIQUE'
GROUP BY tc.CONSTRAINT_NAME;
GO

-- 5.4 验证急单临时表
USE APS_Production;
GO

PRINT '';
PRINT '急单临时桥接表：';
SELECT
    TABLE_NAME,
    (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_NAME = t.TABLE_NAME) AS ColumnCount
FROM INFORMATION_SCHEMA.TABLES t
WHERE TABLE_NAME IN (
    'OrderEmergencyMaterialOverride',
    'OrderEmergencyBomWorkset',
    'OrderEmergencyBomStageDetail'
)
ORDER BY TABLE_NAME;
GO

PRINT '';
PRINT '========================================';
PRINT '✓ v5.0.21 订单BOM入口解析重构完成！';
PRINT '========================================';
PRINT '';
PRINT '下一步：';
PRINT '  1. 更新存储过程 sp_ValidateAndPromoteOrders';
PRINT '  2. 重构存储过程 sp_GetActiveRootBOMNOs → sp_GetActiveOrders';
PRINT '  3. 修改代码实体类和服务类';
PRINT '';
GO
