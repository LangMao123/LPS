-- =============================================
-- ODS 资源主数据视图契约（方案A：输出NDeptNo原始值，映射在APS侧完成）
-- 版本：v1.0
-- 日期：2026-05-08
-- 负责人：3号位（ODS DBA）
-- 说明：基于 MES 资源表创建标准化视图，供 APS 消费
--
-- 数据源：MES 资源表（资源表.csv 对应的物理表）
-- 消费方：APS.ext_MES_APS_Resource_View → sp_SyncResourceData
--
-- 字段映射规则：
--   MachNO          → ResourceCode       (资源编码，APS统一业务键)
--   MachNM          → ResourceName       (资源名称)
--   Id              → ExternalResourceId (MES物理主键)
--   'MES'           → SourceSystem       (固定值)
--   NDeptNo         → NDeptNo            (部门编码，原始值输出，在APS侧映射)
--   MachClassCode   → ResourceType       (资源类型)
--   MachStateCode   → Status             (设备状态，按规则映射)
--   1.0             → CapacityFactor     (产能系数，默认1.0)
--   IsDeleted       → IsActive           (IsDeleted=0 → IsActive=1)
--
-- 状态映射规则：
--   '量产'          → 'AVAILABLE'
--   '报废未处置'    → 'DECOMMISSIONED'
--   '虚拟设备'      → 'DECOMMISSIONED'
--   其他            → 'MAINTENANCE'
-- =============================================

USE MES_Integration;
GO

CREATE OR ALTER VIEW MES_APS_Resource_View
AS
SELECT
    -- 资源编码（APS统一业务键）
    MachNO                          AS ResourceCode,

    -- 资源名称（如果为空，使用 ResourceCode 作为默认值）
    CASE
        WHEN MachNM IS NULL OR LTRIM(RTRIM(MachNM)) = '' THEN MachNO
        ELSE MachNM
    END                             AS ResourceName,

    -- MES物理主键
    CAST(Id AS NVARCHAR(50))        AS ExternalResourceId,

    -- 来源系统（固定值）
    N'MES'                          AS SourceSystem,

    -- 部门编码（原始值，在APS侧映射到 ProductionDepartment 和 Factory）
    NDeptNo                         AS NDeptNo,

    -- 资源类型（直接使用 MachClassCode，或根据业务规则映射）
    CASE
        WHEN MachClassCode IS NULL OR MachClassCode = '' THEN N'MACHINE'
        ELSE MachClassCode
    END                             AS ResourceType,

    -- 设备状态（按业务规则映射）
    CASE
        WHEN MachStateCode = N'量产' THEN N'AVAILABLE'
        WHEN MachStateCode = N'报废未处置' THEN N'DECOMMISSIONED'
        WHEN MachStateCode = N'虚拟设备' THEN N'DECOMMISSIONED'
        WHEN MachStateCode IS NULL OR MachStateCode = '' THEN N'AVAILABLE'
        ELSE N'MAINTENANCE'
    END                             AS Status,

    -- 产能系数（默认1.0，未来可从其他字段扩展）
    CAST(1.0 AS DECIMAL(18,4))      AS CapacityFactor,

    -- 是否启用（IsDeleted=0 表示启用）
    CASE
        WHEN ISNULL(IsDeleted, 0) = 0 THEN 1
        ELSE 0
    END                             AS IsActive

FROM
    -- ⚠️ 请替换为实际的 MES 资源表名
    -- 如果资源表.csv 已导入到物理表，请修改下面的表名
    SMC_MOM_EquipmentWorkReport.dbo.Devices  -- 示例表名，请根据实际情况调整

WHERE
    -- 过滤条件：排除已删除的虚拟设备（可选）
    -- ISNULL(IsDeleted, 0) = 0
    -- AND MachStateCode <> N'虚拟设备'
    1=1;  -- 占位条件，根据业务需求调整

GO

-- =============================================
-- 验证脚本（创建视图后执行）
-- =============================================
/*
-- 查看视图数据样例
SELECT TOP 20 * FROM MES_APS_Resource_View ORDER BY ResourceCode;

-- 检查状态分布
SELECT Status, COUNT(*) AS Count
FROM MES_APS_Resource_View
GROUP BY Status;

-- 检查部门分布
SELECT NDeptNo, COUNT(*) AS Count
FROM MES_APS_Resource_View
GROUP BY NDeptNo
ORDER BY Count DESC;

-- 检查资源类型分布
SELECT ResourceType, COUNT(*) AS Count
FROM MES_APS_Resource_View
GROUP BY ResourceType;

-- 检查是否有空值
SELECT
    COUNT(*) AS TotalRows,
    SUM(CASE WHEN ResourceCode IS NULL THEN 1 ELSE 0 END) AS NullResourceCode,
    SUM(CASE WHEN ResourceName IS NULL THEN 1 ELSE 0 END) AS NullResourceName,
    SUM(CASE WHEN NDeptNo IS NULL THEN 1 ELSE 0 END) AS NullNDeptNo
FROM MES_APS_Resource_View;
*/
