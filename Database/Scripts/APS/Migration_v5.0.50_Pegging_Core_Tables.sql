/*
 * =====================================================================
 * Pegging 核心表结构 DDL（SQL Server）
 * 版本：v5.0.50
 * 创建日期：2026-07-07
 * 对应文档：APS 核心排产全流程走查 V3.14 - 阶段2（步骤2.1-2.8）
 * =====================================================================
 *
 * 本脚本包含以下表：
 * 1. PeggingLedger - Pegging 血缘账本（步骤2.1-2.5 输出）
 * 2. PeggingSupplyAllocation - 非 Task 供应分配记录（步骤2.8）
 * 3. FrozenZoneSnapshot - 冻结区快照（步骤2.3）
 * 4. VirtualInventoryBalance - 虚拟库存余额（步骤2.4）
 *
 * 架构设计原则：
 * - 所有表按 PlanVersionId 分区（对齐 Task/Pegging 表）
 * - CreatedAt 字段默认值为 GETDATE()
 * - 主键使用 BIGINT IDENTITY(1,1)
 * - 外键引用不强制约束（性能考虑）
 * - 索引设计遵循查询模式（按 PlanVersionId + 业务键）
 *
 * =====================================================================
 */

USE [APS_Production];
GO

-- =====================================================================
-- PART 1: PeggingLedger（血缘账本）
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PeggingLedger]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[PeggingLedger] (
        [Id] BIGINT IDENTITY(1,1) NOT NULL,
        [PlanVersionId] INT NOT NULL,
        [OrderId] BIGINT NOT NULL,
        [DemandMaterialId] INT NOT NULL,
        [DemandQuantity] DECIMAL(18, 4) NOT NULL,
        [SupplyMaterialId] INT NOT NULL,
        [AllocatedQuantity] DECIMAL(18, 4) NOT NULL,
        [SupplySourceType] NVARCHAR(50) NOT NULL, -- ERP_INVENTORY | MES_WIP | UPSTREAM_TASK | OUTSOURCE | VIRTUAL_INVENTORY
        [SupplySourceId] BIGINT NULL,
        [BomLevel] INT NOT NULL DEFAULT 0,
        [FactoryCode] NVARCHAR(20) NOT NULL,
        [ProductFamilyId] INT NOT NULL,
        [IsInFrozenZone] BIT NOT NULL DEFAULT 0,
        [PeggingStrategy] NVARCHAR(50) NOT NULL, -- FIFO | FEFO | NEAREST_STAGE | CROSS_FACTORY
        [PlannedStartTime] DATETIME2(7) NULL,
        [PlannedEndTime] DATETIME2(7) NULL,
        [Remarks] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_PeggingLedger] PRIMARY KEY CLUSTERED ([Id], [PlanVersionId])
    );

    PRINT 'Table [PeggingLedger] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [PeggingLedger] already exists.';
END
GO

-- 索引：按 PlanVersionId + OrderId 查询
CREATE NONCLUSTERED INDEX [IX_PeggingLedger_PlanVersionId_OrderId]
ON [dbo].[PeggingLedger] ([PlanVersionId], [OrderId])
INCLUDE ([DemandMaterialId], [AllocatedQuantity], [SupplySourceType]);
GO

-- 索引：按供应来源查询（反向追溯）
CREATE NONCLUSTERED INDEX [IX_PeggingLedger_SupplySource]
ON [dbo].[PeggingLedger] ([PlanVersionId], [SupplySourceType], [SupplySourceId])
INCLUDE ([OrderId], [AllocatedQuantity]);
GO

-- 索引：按产品族查询（虚拟库存传递）
CREATE NONCLUSTERED INDEX [IX_PeggingLedger_ProductFamily]
ON [dbo].[PeggingLedger] ([PlanVersionId], [ProductFamilyId], [BomLevel])
INCLUDE ([SupplyMaterialId], [AllocatedQuantity]);
GO

-- =====================================================================
-- PART 2: PeggingSupplyAllocation（非 Task 供应分配）
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[PeggingSupplyAllocation]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[PeggingSupplyAllocation] (
        [Id] BIGINT IDENTITY(1,1) NOT NULL,
        [PlanVersionId] INT NOT NULL,
        [OrderId] BIGINT NOT NULL,
        [DemandMaterialId] INT NOT NULL,
        [SupplyMaterialId] INT NOT NULL,
        [AllocatedQuantity] DECIMAL(18, 4) NOT NULL,
        [UOM] NVARCHAR(20) NOT NULL,
        [SupplySourceType] NVARCHAR(50) NOT NULL, -- ERP_INVENTORY | MES_WIP | OUTSOURCE_SUPPLY
        [SupplySourceId] BIGINT NULL,
        [SourceReference] NVARCHAR(100) NULL, -- MES 工单号等字符串引用
        [FactoryCode] NVARCHAR(20) NOT NULL,
        [WarehouseCode] NVARCHAR(20) NULL,
        [LocationCode] NVARCHAR(50) NULL,
        [BatchNumber] NVARCHAR(50) NULL,
        [ExpiryDate] DATE NULL,
        [Priority] INT NOT NULL DEFAULT 100,
        [AllocatedAt] DATETIME2(7) NOT NULL,
        [IsConsumed] BIT NOT NULL DEFAULT 0,
        [Remarks] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_PeggingSupplyAllocation] PRIMARY KEY CLUSTERED ([Id], [PlanVersionId])
    );

    PRINT 'Table [PeggingSupplyAllocation] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [PeggingSupplyAllocation] already exists.';
END
GO

-- 索引：按 PlanVersionId + OrderId 查询
CREATE NONCLUSTERED INDEX [IX_PeggingSupplyAllocation_PlanVersionId_OrderId]
ON [dbo].[PeggingSupplyAllocation] ([PlanVersionId], [OrderId])
INCLUDE ([SupplyMaterialId], [AllocatedQuantity], [SupplySourceType]);
GO

-- 索引：按供应来源查询（库存扣减）
CREATE NONCLUSTERED INDEX [IX_PeggingSupplyAllocation_SupplySource]
ON [dbo].[PeggingSupplyAllocation] ([PlanVersionId], [SupplySourceType], [SupplySourceId])
INCLUDE ([AllocatedQuantity], [IsConsumed]);
GO

-- 索引：按批次号查询（FEFO 策略）
CREATE NONCLUSTERED INDEX [IX_PeggingSupplyAllocation_Batch]
ON [dbo].[PeggingSupplyAllocation] ([PlanVersionId], [BatchNumber], [ExpiryDate])
WHERE [BatchNumber] IS NOT NULL;
GO

-- =====================================================================
-- PART 3: FrozenZoneSnapshot（冻结区快照）
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FrozenZoneSnapshot]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[FrozenZoneSnapshot] (
        [Id] BIGINT IDENTITY(1,1) NOT NULL,
        [PlanVersionId] INT NOT NULL,
        [TaskId] BIGINT NOT NULL,
        [MaterialId] INT NOT NULL,
        [FactoryCode] NVARCHAR(20) NOT NULL,
        [ProductFamilyId] INT NOT NULL,
        [MESWorkOrderNo] NVARCHAR(50) NOT NULL,
        [PlannedStartTime] DATETIME2(7) NOT NULL,
        [PlannedEndTime] DATETIME2(7) NOT NULL,
        [FrozenWindowStart] DATETIME2(7) NOT NULL,
        [FrozenWindowEnd] DATETIME2(7) NOT NULL, -- 通常为 FrozenWindowStart + 2小时
        [IsDispatched] BIT NOT NULL DEFAULT 0,
        [DispatchedAt] DATETIME2(7) NULL,
        [Quantity] DECIMAL(18, 4) NOT NULL,
        [UOM] NVARCHAR(20) NOT NULL,
        [ResourceId] INT NULL,
        [ResourceCode] NVARCHAR(50) NULL,
        [FrozenReason] NVARCHAR(50) NOT NULL, -- MES_DISPATCHED | MANUAL_LOCK | CONSTRAINT_FIXED | IN_EXECUTION
        [CrossFactoryMode] NVARCHAR(50) NULL, -- STAGE_HANDOFF | INTER_FACTORY_ORDER | null
        [UpstreamFactoryCode] NVARCHAR(20) NULL,
        [Remarks] NVARCHAR(500) NULL,
        [SnapshotAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        [CreatedAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_FrozenZoneSnapshot] PRIMARY KEY CLUSTERED ([Id], [PlanVersionId])
    );

    PRINT 'Table [FrozenZoneSnapshot] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [FrozenZoneSnapshot] already exists.';
END
GO

-- 索引：按 PlanVersionId + TaskId 查询
CREATE NONCLUSTERED INDEX [IX_FrozenZoneSnapshot_PlanVersionId_TaskId]
ON [dbo].[FrozenZoneSnapshot] ([PlanVersionId], [TaskId])
INCLUDE ([FrozenWindowStart], [FrozenWindowEnd], [IsDispatched]);
GO

-- 索引：按冻结窗口查询（步骤2.3 冻结区判断）
CREATE NONCLUSTERED INDEX [IX_FrozenZoneSnapshot_FrozenWindow]
ON [dbo].[FrozenZoneSnapshot] ([PlanVersionId], [FrozenWindowStart], [FrozenWindowEnd])
INCLUDE ([TaskId], [MaterialId]);
GO

-- 索引：按 MES 工单号查询
CREATE NONCLUSTERED INDEX [IX_FrozenZoneSnapshot_MESWorkOrderNo]
ON [dbo].[FrozenZoneSnapshot] ([MESWorkOrderNo])
INCLUDE ([PlanVersionId], [TaskId], [IsDispatched]);
GO

-- =====================================================================
-- PART 4: VirtualInventoryBalance（虚拟库存余额）
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[VirtualInventoryBalance]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[VirtualInventoryBalance] (
        [Id] BIGINT IDENTITY(1,1) NOT NULL,
        [PlanVersionId] INT NOT NULL,
        [MaterialId] INT NOT NULL,
        [FactoryCode] NVARCHAR(20) NOT NULL,
        [SourceProductFamilyId] INT NOT NULL, -- 上游产品族
        [TargetProductFamilyId] INT NOT NULL, -- 下游产品族
        [VirtualAvailableQuantity] DECIMAL(18, 4) NOT NULL,
        [AllocatedQuantity] DECIMAL(18, 4) NOT NULL DEFAULT 0,
        [RemainingQuantity] DECIMAL(18, 4) NOT NULL,
        [UOM] NVARCHAR(20) NOT NULL,
        [AvailableAt] DATETIME2(7) NOT NULL,
        [UpstreamTaskId] BIGINT NULL,
        [BomLevel] INT NOT NULL,
        [TopologicalOrder] INT NOT NULL, -- 01:50 静态扫描结果
        [IsPropagated] BIT NOT NULL DEFAULT 0,
        [DependencyType] NVARCHAR(50) NOT NULL, -- CROSS_DOMAIN | CROSS_FACTORY | SAME_DOMAIN | CROSS_STAGE
        [UpstreamFactoryCode] NVARCHAR(20) NULL,
        [DownstreamFactoryCode] NVARCHAR(20) NULL,
        [CrossFactoryMode] NVARCHAR(50) NULL, -- STAGE_HANDOFF | INTER_FACTORY_ORDER | null
        [ComputedAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        [Remarks] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2(7) NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_VirtualInventoryBalance] PRIMARY KEY CLUSTERED ([Id], [PlanVersionId])
    );

    PRINT 'Table [VirtualInventoryBalance] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [VirtualInventoryBalance] already exists.';
END
GO

-- 索引：按拓扑序查询（单向传播）
CREATE NONCLUSTERED INDEX [IX_VirtualInventoryBalance_TopologicalOrder]
ON [dbo].[VirtualInventoryBalance] ([PlanVersionId], [TopologicalOrder])
INCLUDE ([MaterialId], [VirtualAvailableQuantity], [RemainingQuantity]);
GO

-- 索引：按产品族查询（跨域依赖）
CREATE NONCLUSTERED INDEX [IX_VirtualInventoryBalance_ProductFamily]
ON [dbo].[VirtualInventoryBalance] ([PlanVersionId], [SourceProductFamilyId], [TargetProductFamilyId])
INCLUDE ([MaterialId], [RemainingQuantity]);
GO

-- 索引：按上游任务查询（血缘追溯）
CREATE NONCLUSTERED INDEX [IX_VirtualInventoryBalance_UpstreamTask]
ON [dbo].[VirtualInventoryBalance] ([PlanVersionId], [UpstreamTaskId])
INCLUDE ([MaterialId], [VirtualAvailableQuantity])
WHERE [UpstreamTaskId] IS NOT NULL;
GO

-- =====================================================================
-- PART 5: 验证脚本（注释块）
-- =====================================================================

/*
-- 验证表是否创建成功
SELECT
    t.name AS TableName,
    p.rows AS RowCounts,
    SUM(a.total_pages) * 8 AS TotalSpaceKB
FROM sys.tables t
INNER JOIN sys.indexes i ON t.object_id = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE t.name IN ('PeggingLedger', 'PeggingSupplyAllocation', 'FrozenZoneSnapshot', 'VirtualInventoryBalance')
GROUP BY t.name, p.rows
ORDER BY t.name;

-- 查看索引
SELECT
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
WHERE t.name IN ('PeggingLedger', 'PeggingSupplyAllocation', 'FrozenZoneSnapshot', 'VirtualInventoryBalance')
ORDER BY t.name, i.index_id;
*/

PRINT '===================================================================';
PRINT 'Pegging 核心表结构创建完成！';
PRINT '===================================================================';
GO
