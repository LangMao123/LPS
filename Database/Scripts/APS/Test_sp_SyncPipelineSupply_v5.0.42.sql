-- =============================================
-- 测试脚本: sp_SyncPipelineSupply v5.0.42
-- 用途: 验证管道供给同步存储过程部署和 V1 空跑模式
-- =============================================

USE APS_Production;
GO

PRINT '====================================================================';
PRINT '测试 1: 验证表结构';
PRINT '====================================================================';

-- 验证 SupplyFact_Pipeline 表结构
PRINT '';
PRINT '-- SupplyFact_Pipeline 字段列表:';
SELECT
    ORDINAL_POSITION AS [序号],
    COLUMN_NAME AS [字段名],
    DATA_TYPE AS [数据类型],
    CHARACTER_MAXIMUM_LENGTH AS [最大长度],
    IS_NULLABLE AS [可空]
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'SupplyFact_Pipeline'
ORDER BY ORDINAL_POSITION;

-- 验证 SupplyAvailabilityRule 表结构
PRINT '';
PRINT '-- SupplyAvailabilityRule 字段列表:';
SELECT
    ORDINAL_POSITION AS [序号],
    COLUMN_NAME AS [字段名],
    DATA_TYPE AS [数据类型],
    CHARACTER_MAXIMUM_LENGTH AS [最大长度],
    IS_NULLABLE AS [可空]
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'SupplyAvailabilityRule'
ORDER BY ORDINAL_POSITION;

-- 验证索引
PRINT '';
PRINT '-- SupplyFact_Pipeline 索引列表:';
SELECT
    i.name AS [索引名],
    i.type_desc AS [类型],
    i.is_unique AS [唯一],
    COL_NAME(ic.object_id, ic.column_id) AS [列名]
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
WHERE i.object_id = OBJECT_ID('SupplyFact_Pipeline')
ORDER BY i.name, ic.key_ordinal;

PRINT '';
PRINT '====================================================================';
PRINT '测试 2: 验证视图';
PRINT '====================================================================';

-- 验证统一输入视图
PRINT '';
PRINT '-- ext_PipelineSupply_Source_View 行数（V1 应为 0）:';
SELECT COUNT(*) AS [行数] FROM ext_PipelineSupply_Source_View;

PRINT '';
PRINT '-- ext_ERP_InterplantInTransit_View 行数（V1 应为 0）:';
SELECT COUNT(*) AS [行数] FROM ext_ERP_InterplantInTransit_View;

PRINT '';
PRINT '====================================================================';
PRINT '测试 3: 执行存储过程（V1 空跑模式）';
PRINT '====================================================================';

-- 执行前清理测试数据
DELETE FROM SupplyFact_Pipeline WHERE BatchNo LIKE 'TEST_%';
DELETE FROM APS_ETL_Log WHERE Step = 'sp_SyncPipelineSupply' AND BatchNo LIKE 'TEST_%';

-- 执行存储过程
DECLARE @BatchNo NVARCHAR(50) = 'TEST_' + CONVERT(NVARCHAR(14), GETDATE(), 112) + '_' + REPLACE(CONVERT(NVARCHAR(8), GETDATE(), 108), ':', '');
DECLARE @RowsAffected INT;
DECLARE @ErrorMessage NVARCHAR(MAX);

PRINT '';
PRINT '-- 执行 sp_SyncPipelineSupply:';
PRINT '   BatchNo: ' + @BatchNo;

EXEC dbo.sp_SyncPipelineSupply
    @BatchNo = @BatchNo,
    @DataCutoffTime = GETDATE(),
    @RowsAffected = @RowsAffected OUTPUT,
    @ErrorMessage = @ErrorMessage OUTPUT;

-- 显示结果
PRINT '';
PRINT '-- 执行结果:';
SELECT
    [@BatchNo] = @BatchNo,
    [@RowsAffected] = @RowsAffected,
    [@ErrorMessage] = @ErrorMessage,
    [IsSuccess] = CASE WHEN @ErrorMessage IS NULL THEN 'YES' ELSE 'NO' END;

PRINT '';
PRINT '====================================================================';
PRINT '测试 4: 验证执行结果';
PRINT '====================================================================';

-- 查看 ETL 日志
PRINT '';
PRINT '-- APS_ETL_Log 最新记录:';
SELECT TOP 5
    Step,
    BatchNo,
    Status,
    Message,
    CreatedAt
FROM APS_ETL_Log
WHERE Step = 'sp_SyncPipelineSupply'
ORDER BY CreatedAt DESC;

-- 查看 SupplyFact_Pipeline 行数（V1 应为 0）
PRINT '';
PRINT '-- SupplyFact_Pipeline 行数（V1 应为 0）:';
SELECT COUNT(*) AS [行数] FROM SupplyFact_Pipeline WHERE IsActive = 1;

-- 查看 SupplyAvailabilityRule 行数
PRINT '';
PRINT '-- SupplyAvailabilityRule 行数:';
SELECT COUNT(*) AS [行数] FROM SupplyAvailabilityRule WHERE IsActive = 1;

PRINT '';
PRINT '====================================================================';
PRINT '测试完成总结';
PRINT '====================================================================';
PRINT '预期结果:';
PRINT '  1. SupplyFact_Pipeline 表已创建，包含 30 个字段';
PRINT '  2. SupplyAvailabilityRule 表已创建，包含 13 个字段';
PRINT '  3. ext_PipelineSupply_Source_View 返回 0 行（V1 模式）';
PRINT '  4. sp_SyncPipelineSupply 执行成功，@RowsAffected = 0';
PRINT '  5. APS_ETL_Log 记录 INFO 级别日志："V1 空跑模式"';
PRINT '  6. SupplyFact_Pipeline 表为空（已 TRUNCATE）';
PRINT '====================================================================';
GO
