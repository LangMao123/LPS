USE [APS_Production]
GO

-- =============================================
-- Migration: [Order].ProductFamilyId 改为可空
-- 日期: 2026-06-05
-- 原因: Material.ProductFamilyId 是可空的（不是所有物料都需要产品族）
--       sp_SyncOrdersToPartitionTable 透传 m.ProductFamilyId，如果为 NULL 会违反 NOT NULL 约束
--       产品族解析上线前大量物料 ProductFamilyId = NULL，必须允许 Order 表也为空
-- =============================================

PRINT N'=== [Order].ProductFamilyId 改为可空 ===';

-- 1. 删除 FK 约束（如果存在）
DECLARE @fk_name NVARCHAR(200);
SELECT @fk_name = fk.name
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
INNER JOIN sys.columns c ON fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
WHERE fk.parent_object_id = OBJECT_ID('[Order]') AND c.name = 'ProductFamilyId';

IF @fk_name IS NOT NULL
BEGIN
    EXEC('ALTER TABLE [Order] DROP CONSTRAINT ' + @fk_name);
    PRINT N'已删除 FK 约束: ' + @fk_name;
END
GO

-- 2. 改为可空
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('[Order]')
      AND name = 'ProductFamilyId'
      AND is_nullable = 0
)
BEGIN
    ALTER TABLE [Order] ALTER COLUMN ProductFamilyId INT NULL;
    PRINT N'[Order].ProductFamilyId 已改为 NULL';
END
ELSE
BEGIN
    PRINT N'[Order].ProductFamilyId 已经是可空，跳过';
END
GO

-- 3. 重建 FK（允许 NULL 的 FK 是合法的，NULL 值不参与约束检查）
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('[Order]')
      AND name = 'FK_Order_ProductFamily'
)
BEGIN
    ALTER TABLE [Order]
    ADD CONSTRAINT FK_Order_ProductFamily
    FOREIGN KEY (ProductFamilyId) REFERENCES ProductFamily(Id);
    PRINT N'FK_Order_ProductFamily 重建完成（允许 NULL）';
END
GO

-- 4. 确认索引 IX_Order_Query 仍可用（含 NULL 值的索引正常工作）
-- 原索引: IX_Order_Query ON [Order](Status, ProductFamilyId, FactoryId, CustomerDueDate)
-- NULL 值在索引中正常存储，无需重建

PRINT N'=== Migration 完成 ===';
GO
