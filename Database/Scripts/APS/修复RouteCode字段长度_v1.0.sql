USE [APS_Production]
GO

-- =============================================
-- 修复 RouteCode 字段长度不足问题
-- 版本: v1.0
-- 日期: 2026-05-12
-- 原因: RouteCode 实际数据最长 115+ 字符，原定义 NVARCHAR(50) 导致截断
-- 变更: 所有 RouteCode 列统一扩展为 NVARCHAR(200)
-- 注意: 涉及索引的列需先 DROP 再 ALTER 再 CREATE
-- =============================================

PRINT N'开始修复 RouteCode 字段长度...';

-- ═══════════════════════════════════════════
-- 1. RoutingOperation（2个索引包含 RouteCode）
-- ═══════════════════════════════════════════
DROP INDEX IF EXISTS IX_RoutingOp_Material ON [dbo].[RoutingOperation];
DROP INDEX IF EXISTS IX_RoutingOp_MaterialDept ON [dbo].[RoutingOperation];

ALTER TABLE [dbo].[RoutingOperation] ALTER COLUMN [RouteCode] NVARCHAR(200) NOT NULL;

CREATE INDEX IX_RoutingOp_Material ON RoutingOperation(MaterialId, RouteCode, PathId) WHERE IsActive = 1;
CREATE INDEX IX_RoutingOp_MaterialDept ON RoutingOperation(MaterialId, ProductionDepartmentId, StageCode, RouteCode, PathId) WHERE IsActive = 1;
PRINT N'  RoutingOperation.RouteCode → NVARCHAR(200) ✓';

-- ═══════════════════════════════════════════
-- 2. RoutingDependency（2个索引包含 RouteCode）
-- ═══════════════════════════════════════════
DROP INDEX IF EXISTS IX_RoutingDep_Material ON [dbo].[RoutingDependency];
DROP INDEX IF EXISTS IX_RoutingDep_MaterialDept ON [dbo].[RoutingDependency];

ALTER TABLE [dbo].[RoutingDependency] ALTER COLUMN [RouteCode] NVARCHAR(200) NOT NULL;

CREATE INDEX IX_RoutingDep_Material ON RoutingDependency(MaterialId, RouteCode, PathId) WHERE IsActive = 1;
CREATE INDEX IX_RoutingDep_MaterialDept ON RoutingDependency(MaterialId, ProductionDepartmentId, RouteCode, PathId) WHERE IsActive = 1;
PRINT N'  RoutingDependency.RouteCode → NVARCHAR(200) ✓';

-- ═══════════════════════════════════════════
-- 3. RoutingStage（1个索引包含 RouteCode）
-- ═══════════════════════════════════════════
DROP INDEX IF EXISTS IX_RoutingStage_Material ON [dbo].[RoutingStage];

ALTER TABLE [dbo].[RoutingStage] ALTER COLUMN [RouteCode] NVARCHAR(200) NOT NULL;

CREATE INDEX IX_RoutingStage_Material ON RoutingStage(MaterialId, RouteCode, PathId) WHERE IsActive = 1;
PRINT N'  RoutingStage.RouteCode → NVARCHAR(200) ✓';

-- ═══════════════════════════════════════════
-- 4. OperationResourceEligibility（1个索引包含 RouteCode）
-- ═══════════════════════════════════════════
DROP INDEX IF EXISTS IX_OpResElig_Operation ON [dbo].[OperationResourceEligibility];

ALTER TABLE [dbo].[OperationResourceEligibility] ALTER COLUMN [RouteCode] NVARCHAR(200) NOT NULL;

CREATE INDEX IX_OpResElig_Operation ON OperationResourceEligibility(MaterialId, RouteCode, PathId, OperationCode) WHERE IsActive = 1;
PRINT N'  OperationResourceEligibility.RouteCode → NVARCHAR(200) ✓';

-- ═══════════════════════════════════════════
-- 5. RoutingPlanningParam（无索引引用 RouteCode）
-- ═══════════════════════════════════════════
ALTER TABLE [dbo].[RoutingPlanningParam] ALTER COLUMN [RouteCode] NVARCHAR(200) NOT NULL;
PRINT N'  RoutingPlanningParam.RouteCode → NVARCHAR(200) ✓';

PRINT N'RouteCode 字段长度修复完成（5张表，6个索引重建）';
GO
