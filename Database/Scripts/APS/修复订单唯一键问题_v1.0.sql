-- =============================================
-- ERP订单同步唯一键修复脚本
-- 版本: v1.0
-- 日期: 2026-05-11
-- 问题: SourceOrderId 不是唯一标识，应该使用 OrderNo
-- =============================================

USE [APS_Production]
GO

BEGIN TRANSACTION;

BEGIN TRY
    PRINT '========================================';
    PRINT '开始修复 ERP 订单同步唯一键问题';
    PRINT '========================================';

    -- =============================================
    -- 步骤1: 修复 ERP_Order_Staging 唯一约束
    -- =============================================
    PRINT '';
    PRINT '步骤1: 修复 ERP_Order_Staging 唯一约束';
    PRINT '----------------------------------------';

    -- 删除旧约束
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ERP_Order_Staging')
    BEGIN
        ALTER TABLE ERP_Order_Staging DROP CONSTRAINT UQ_ERP_Order_Staging;
        PRINT '✓ 已删除旧约束: UQ_ERP_Order_Staging (SourceOrderId, SyncedAt)';
    END
    ELSE
    BEGIN
        PRINT '⚠ 旧约束不存在，跳过删除';
    END

    -- 创建新约束（基于 OrderNo）
    ALTER TABLE ERP_Order_Staging
    ADD CONSTRAINT UQ_ERP_Order_Staging UNIQUE (OrderNo, SyncedAt);
    PRINT '✓ 已创建新约束: UQ_ERP_Order_Staging (OrderNo, SyncedAt)';

    -- =============================================
    -- 步骤2: 删除 Order_Canonical 的错误索引
    -- =============================================
    PRINT '';
    PRINT '步骤2: 删除 Order_Canonical 的错误索引';
    PRINT '----------------------------------------';

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Order_Canonical_UpsertKey')
    BEGIN
        DROP INDEX IX_Order_Canonical_UpsertKey ON Order_Canonical;
        PRINT '✓ 已删除索引: IX_Order_Canonical_UpsertKey (SourceSystem, SourceOrderId)';
        PRINT '  说明: OrderNo 已有 UNIQUE 约束，不需要额外索引';
    END
    ELSE
    BEGIN
        PRINT '⚠ 索引不存在，跳过删除';
    END

    -- =============================================
    -- 步骤3: 验证 Order_Canonical 的 OrderNo 唯一约束
    -- =============================================
    PRINT '';
    PRINT '步骤3: 验证 Order_Canonical 的 OrderNo 唯一约束';
    PRINT '----------------------------------------';

    IF EXISTS (
        SELECT 1
        FROM sys.key_constraints kc
        INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE kc.parent_object_id = OBJECT_ID('Order_Canonical')
          AND c.name = 'OrderNo'
          AND kc.type = 'UQ'
    )
    BEGIN
        PRINT '✓ Order_Canonical.OrderNo 已有 UNIQUE 约束';
    END
    ELSE
    BEGIN
        PRINT '⚠ Order_Canonical.OrderNo 缺少 UNIQUE 约束，正在创建...';
        ALTER TABLE Order_Canonical
        ADD CONSTRAINT UQ_Order_Canonical_OrderNo UNIQUE (OrderNo);
        PRINT '✓ 已创建约束: UQ_Order_Canonical_OrderNo';
    END

    -- =============================================
    -- 步骤4: 检查数据完整性
    -- =============================================
    PRINT '';
    PRINT '步骤4: 检查数据完整性';
    PRINT '----------------------------------------';

    -- 检查 Order_Canonical 是否有重复的 OrderNo
    DECLARE @DuplicateOrderNo INT;
    SELECT @DuplicateOrderNo = COUNT(*)
    FROM (
        SELECT OrderNo, COUNT(*) AS Cnt
        FROM Order_Canonical
        GROUP BY OrderNo
        HAVING COUNT(*) > 1
    ) AS Duplicates;

    IF @DuplicateOrderNo > 0
    BEGIN
        PRINT '⚠ 警告: Order_Canonical 中存在 ' + CAST(@DuplicateOrderNo AS NVARCHAR(10)) + ' 个重复的 OrderNo';
        PRINT '  请手动检查并清理重复数据：';
        PRINT '  SELECT OrderNo, COUNT(*) FROM Order_Canonical GROUP BY OrderNo HAVING COUNT(*) > 1';

        -- 不回滚，只是警告
    END
    ELSE
    BEGIN
        PRINT '✓ Order_Canonical 中没有重复的 OrderNo';
    END

    -- 检查 ERP_Order_Staging 是否有重复的 OrderNo（同一 SyncedAt）
    DECLARE @DuplicateStaging INT;
    SELECT @DuplicateStaging = COUNT(*)
    FROM (
        SELECT OrderNo, SyncedAt, COUNT(*) AS Cnt
        FROM ERP_Order_Staging
        GROUP BY OrderNo, SyncedAt
        HAVING COUNT(*) > 1
    ) AS Duplicates;

    IF @DuplicateStaging > 0
    BEGIN
        PRINT '⚠ 警告: ERP_Order_Staging 中存在 ' + CAST(@DuplicateStaging AS NVARCHAR(10)) + ' 个重复的 (OrderNo, SyncedAt)';
        PRINT '  请手动检查并清理重复数据：';
        PRINT '  SELECT OrderNo, SyncedAt, COUNT(*) FROM ERP_Order_Staging GROUP BY OrderNo, SyncedAt HAVING COUNT(*) > 1';

        -- 不回滚，只是警告
    END
    ELSE
    BEGIN
        PRINT '✓ ERP_Order_Staging 中没有重复的 (OrderNo, SyncedAt)';
    END

    -- =============================================
    -- 提交事务
    -- =============================================
    COMMIT TRANSACTION;

    PRINT '';
    PRINT '========================================';
    PRINT '✓ 修复完成！';
    PRINT '========================================';
    PRINT '';
    PRINT '后续步骤:';
    PRINT '1. 更新存储过程 sp_ValidateAndPromoteOrders (MERGE ON 条件改为 OrderNo)';
    PRINT '2. 更新代码 BulkWriteToStagingAsync (按 OrderNo 去重)';
    PRINT '3. 重新测试订单同步流程';
    PRINT '';

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();

    PRINT '';
    PRINT '========================================';
    PRINT '✗ 修复失败！';
    PRINT '========================================';
    PRINT '错误信息: ' + @ErrorMessage;
    PRINT '';

    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH;
GO
