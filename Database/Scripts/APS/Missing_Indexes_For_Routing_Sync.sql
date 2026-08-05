-- =============================================
-- 工艺路线同步缺失索引补充脚本
-- 版本：v1.0
-- 日期：2026-05-08
-- 说明：补充 sp_SyncRoutingData 性能优化所需的索引
--
-- 背景：DDL v5.0 已定义了大部分索引，但缺少映射查询的关键索引
-- =============================================

USE APS_Production;
GO

-- =============================================
-- 1. MaterialMapping 映射查询优化（关键）
-- =============================================
-- 用途：sp_SyncRoutingData 中的 MES_ID → MaterialId 映射
-- 查询模式：WHERE Source = 'MES' AND IsCurrent = 1，然后 JOIN Material ON MaterialCode

-- 检查索引是否存在
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('MaterialMapping')
      AND name = 'IX_MaterialMapping_SourceLookup'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_MaterialMapping_SourceLookup
    ON MaterialMapping(Source, IsCurrent, SourceID)
    INCLUDE (MaterialCode)
    WHERE IsCurrent = 1;

    PRINT '✅ 已创建索引: IX_MaterialMapping_SourceLookup';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_MaterialMapping_SourceLookup';
END
GO

-- =============================================
-- 2. ProductionDepartment 部门映射优化（关键）
-- =============================================
-- 用途：sp_SyncRoutingData 中的 ProductionDeptCode → ProductionDepartmentId 映射
-- 查询模式：WHERE DeptCode = @ProductionDeptCode AND IsActive = 1

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('ProductionDepartment')
      AND name = 'IX_ProductionDepartment_DeptCode'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_ProductionDepartment_DeptCode
    ON ProductionDepartment(DeptCode, IsActive)
    INCLUDE (Id, FactoryId)
    WHERE IsActive = 1;

    PRINT '✅ 已创建索引: IX_ProductionDepartment_DeptCode';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_ProductionDepartment_DeptCode';
END
GO

-- =============================================
-- 3. Resource 资源映射优化（关键）
-- =============================================
-- 用途：sp_SyncRoutingData 中的 ResourceCode → ResourceId 映射
-- 查询模式：WHERE ResourceCode = @ResourceCode AND IsActive = 1

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Resource')
      AND name = 'IX_Resource_ResourceCode'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Resource_ResourceCode
    ON Resource(ResourceCode, IsActive)
    INCLUDE (Id)
    WHERE IsActive = 1;

    PRINT '✅ 已创建索引: IX_Resource_ResourceCode';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_Resource_ResourceCode';
END
GO

-- =============================================
-- 4. RoutingOperation MERGE 优化（可选，唯一约束已覆盖）
-- =============================================
-- DDL 中的唯一约束已经创建了索引，但可以考虑添加 INCLUDE 列优化 UPDATE 判断
-- 当前唯一约束：UQ_RoutingOperation (MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode)
-- MERGE 的 WHEN MATCHED 需要比较：OperationName, ProcessType, StageCode, StandardDuration, SetupTime, IsActive

-- 注意：唯一约束索引不能添加 INCLUDE 列，所以这里创建一个覆盖索引
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('RoutingOperation')
      AND name = 'IX_RoutingOperation_MergeCovering'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_RoutingOperation_MergeCovering
    ON RoutingOperation(MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode)
    INCLUDE (OperationName, ProcessType, StageCode, StandardDuration, SetupTime, IsActive, UpdatedAt)
    WHERE IsActive = 1;

    PRINT '✅ 已创建索引: IX_RoutingOperation_MergeCovering';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_RoutingOperation_MergeCovering';
END
GO

-- =============================================
-- 5. RoutingDependency MERGE 优化（可选）
-- =============================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('RoutingDependency')
      AND name = 'IX_RoutingDependency_MergeCovering'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_RoutingDependency_MergeCovering
    ON RoutingDependency(MaterialId, ProductionDepartmentId, RouteCode, PathId, FromOperationCode, ToOperationCode)
    INCLUDE (DependencyType, LagTime, IsActive, UpdatedAt)
    WHERE IsActive = 1;

    PRINT '✅ 已创建索引: IX_RoutingDependency_MergeCovering';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_RoutingDependency_MergeCovering';
END
GO

-- =============================================
-- 6. OperationResourceEligibility MERGE 优化（可选）
-- =============================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('OperationResourceEligibility')
      AND name = 'IX_OperationResourceEligibility_MergeCovering'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_OperationResourceEligibility_MergeCovering
    ON OperationResourceEligibility(MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, ResourceId)
    INCLUDE (Priority, CapacityFactor, IsPrimary, IsActive, UpdatedAt)
    WHERE IsActive = 1;

    PRINT '✅ 已创建索引: IX_OperationResourceEligibility_MergeCovering';
END
ELSE
BEGIN
    PRINT '⚠️ 索引已存在: IX_OperationResourceEligibility_MergeCovering';
END
GO

-- =============================================
-- 验证索引创建结果
-- =============================================
PRINT '';
PRINT '========================================';
PRINT '索引创建完成，验证结果：';
PRINT '========================================';

SELECT
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns ic
        JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        ORDER BY ic.key_ordinal
        FOR XML PATH('')
    ), 1, 2, '') AS KeyColumns,
    STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns ic
        JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
        FOR XML PATH('')
    ), 1, 2, '') AS IncludedColumns
FROM sys.indexes i
JOIN sys.tables t ON i.object_id = t.object_id
WHERE t.name IN ('MaterialMapping', 'ProductionDepartment', 'Resource', 'RoutingOperation', 'RoutingDependency', 'OperationResourceEligibility')
  AND i.name LIKE 'IX_%'
ORDER BY t.name, i.name;

PRINT '';
PRINT '========================================';
PRINT '索引大小统计：';
PRINT '========================================';

SELECT
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName,
    SUM(s.used_page_count) * 8 / 1024.0 AS IndexSizeMB
FROM sys.dm_db_partition_stats s
JOIN sys.indexes i ON s.object_id = i.object_id AND s.index_id = i.index_id
WHERE OBJECT_NAME(i.object_id) IN ('MaterialMapping', 'ProductionDepartment', 'Resource', 'RoutingOperation', 'RoutingDependency', 'OperationResourceEligibility')
  AND i.name LIKE 'IX_%'
GROUP BY i.object_id, i.name
ORDER BY OBJECT_NAME(i.object_id), IndexSizeMB DESC;

GO

-- =============================================
-- 使用说明
-- =============================================
/*
索引优先级：

【必须创建】（关键性能提升）：
1. IX_MaterialMapping_SourceLookup - MES_ID 映射查询
2. IX_ProductionDepartment_DeptCode - 部门映射查询
3. IX_Resource_ResourceCode - 资源映射查询

【可选创建】（进一步优化）：
4. IX_RoutingOperation_MergeCovering - MERGE UPDATE 判断优化
5. IX_RoutingDependency_MergeCovering - MERGE UPDATE 判断优化
6. IX_OperationResourceEligibility_MergeCovering - MERGE UPDATE 判断优化

预期效果：
- 创建前3个索引：预计提升 30-50%
- 创建全部6个索引：预计提升 50-70%

注意事项：
1. 索引会占用额外存储空间（预计每个索引 10-50MB）
2. 索引会略微降低 INSERT/UPDATE 性能（可忽略）
3. 定期更新统计信息：UPDATE STATISTICS <表名> WITH FULLSCAN;
*/
