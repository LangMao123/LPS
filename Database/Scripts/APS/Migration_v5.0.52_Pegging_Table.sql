/*
 * =====================================================================
 * [Pegging] 表 DDL（SQL Server 分区表）
 * 版本：v5.0.52
 * 创建日期：2026-07-31
 * 对应文档：APS_数据库表结构设计_v5.2.3 § 2.10
 * =====================================================================
 *
 * Task-to-Task 物理血缘表。
 * 由 PeggingOrchestrator.PersistDomainAndPeggingInTransactionAsync 在统一事务中写入。
 * =====================================================================
 */

USE [APS_Production];
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Pegging]') AND type = N'U')
BEGIN
    CREATE TABLE [dbo].[Pegging] (
        [Id]                   BIGINT IDENTITY(1,1) NOT NULL,
        [PlanVersionId]        INT            NOT NULL,
        [UpstreamTaskId]       BIGINT         NOT NULL,
        [DownstreamTaskId]     BIGINT         NOT NULL,
        [UpstreamMaterialId]   INT            NOT NULL,
        [DownstreamMaterialId] INT            NOT NULL,
        [Quantity]             DECIMAL(18,4)  NOT NULL,
        [UOM]                  NVARCHAR(20)   NOT NULL,
        [PeggingType]          NVARCHAR(50)   NOT NULL,   -- TASK_TO_TASK
        [LeadTimeDays]         INT            NOT NULL DEFAULT 0,
        [IsCrossDomain]        BIT            NOT NULL DEFAULT 0,
        [AllocatedQuantity]    DECIMAL(18,4)  NULL,
        [InheritedPriority]    INT            NULL,
        [AllocationReason]     NVARCHAR(200)  NULL,
        [CreatedAt]            DATETIME2(7)   NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_Pegging] PRIMARY KEY CLUSTERED ([Id], [PlanVersionId])
    ) ON PS_PlanVersion([PlanVersionId]);

    PRINT 'Table [Pegging] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [Pegging] already exists.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = N'IX_Pegging_Upstream' AND object_id = OBJECT_ID(N'[dbo].[Pegging]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Pegging_Upstream]
    ON [dbo].[Pegging] ([UpstreamTaskId], [PlanVersionId])
    ON PS_PlanVersion([PlanVersionId]);
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = N'IX_Pegging_Downstream' AND object_id = OBJECT_ID(N'[dbo].[Pegging]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Pegging_Downstream]
    ON [dbo].[Pegging] ([DownstreamTaskId], [PlanVersionId])
    ON PS_PlanVersion([PlanVersionId]);
END
GO

PRINT '===================================================================';
PRINT '[Pegging] 表结构创建完成！';
PRINT '===================================================================';
GO
