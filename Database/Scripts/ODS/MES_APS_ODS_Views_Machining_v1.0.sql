-- ============================================================
-- ODS 防腐层 - 加工大工艺子视图  v1.0
-- 命名规则: MES_APS_{Entity}_Mach_View（_Mach_ 为加工类子视图前缀）
-- 数据源:   SMC_MES_DeviceProcessReport + SMC_MOM_SYS
-- 数据范围: StartWorkTime 近 6 个月
-- 消费方:   5号位 UNION ALL 统一收口后，APS 通过 SYNONYM 消费
-- 分工:     子视图由 2号位 建立；UNION ALL 统一收口由 5号位 维护
--
-- 字段契约（与 MES_Integration.dbo.MES_APS_*_View UNION ALL 严格对齐）：
--   MES_APS_WorkOrder_Mach_View:
--     ProductionInstructionNo / MESWorkOrderNo / MaterialCode /
--     PlannedQty / WorkOrderStatus / SourceUpdatedAt
--
--   MES_APS_OperationProgress_Mach_View:
--     ProductionInstructionNo / MESWorkOrderNo / MaterialCode / OperationName /
--     StageCode / StageName / PlannedQty / GoodQty /
--     ScrapQty(NULL) / ReworkQty(NULL) / LastReportTime / SourceUpdatedAt
--
--   MES_APS_StageProgress_Mach_View:
--     ProductionInstructionNo / MaterialCode / StageCode / StageName /
--     PlannedQty / GoodCompletedQty /
--     ScrapQty(NULL) / ReworkQty(NULL) / LastReportTime / SourceUpdatedAt
--
-- StageCode 推导规则（写死）：
--   LEFT(org.Code,4) = '3118'  → 'BJ_MOLD' / '北京注塑'
--   LEFT(org.Code,1) = '1'     → 'CN_MACH' / '中国机加'
--   LEFT(org.Code,1) = '3'     → 'BJ_MACH' / '北京机加'
--   LEFT(org.Code,1) = '5'     → 'TJ_MACH' / '天津机加'
--   其余                        → 'UNKNOWN' / 'UNKNOWN'
-- ============================================================


-- ============================================================
-- 视图 1：工单级  MES_APS_WorkOrder_Mach_View
-- 粒度: 一行 = 一个 WorkOrderOnWork 记录
-- 契约字段: ProductionInstructionNo / MESWorkOrderNo / MaterialCode /
--           PlannedQty / WorkOrderStatus / SourceUpdatedAt
-- ============================================================
CREATE OR ALTER VIEW [dbo].[MES_APS_WorkOrder_Mach_View] AS
WITH Dedup AS (
    SELECT
        CAST(w.ProcessNo AS NVARCHAR(100))  AS ProductionInstructionNo,
        w.WorkOrder                          AS MESWorkOrderNo,
        w.Model                              AS MaterialCode,
        w.WorkOrderCount                     AS PlannedQty,
        CASE w.Status
            WHEN 1 THEN 'CLOSED'
            ELSE 'IN_PROGRESS'
        END                                  AS WorkOrderStatus,
        CASE
            WHEN LEFT(org.Code, 4) = '3118' THEN 'BJ_MOLD'
            WHEN LEFT(org.Code, 1) = '1'    THEN 'CN_MACH'
            WHEN LEFT(org.Code, 1) = '3'    THEN 'BJ_MACH'
            WHEN LEFT(org.Code, 1) = '5'    THEN 'TJ_MACH'
            ELSE 'UNKNOWN'
        END                                  AS StageCode,
        ISNULL(w.LastModificationTime, w.CreationTime) AS SourceUpdatedAt,
        ROW_NUMBER() OVER (
            PARTITION BY w.ProcessNo, w.WorkOrder, w.Model
            ORDER BY w.LastModificationTime DESC, w.Id DESC
        ) AS rn
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork] w
    LEFT JOIN [SMC_MOM_SYS].[dbo].[AbpOrganizationUnits] org
        ON  org.Id        = w.OrgId
        AND org.IsDeleted = 0
    WHERE w.IsDeleted     = 0
      AND w.ProcessNo     IS NOT NULL
      AND w.WorkOrder     IS NOT NULL
      AND w.StartWorkTime >= DATEADD(MONTH, -6, GETDATE())
)
SELECT
    ProductionInstructionNo,
    MESWorkOrderNo,
    MaterialCode,
    PlannedQty,
    WorkOrderStatus,
    StageCode,
    SourceUpdatedAt
FROM Dedup
WHERE rn = 1;
GO


-- ============================================================
-- 视图 2：工序进度级  MES_APS_OperationProgress_Mach_View
-- 粒度: 一行 = 一个工单（ProductionInstructionNo + MESWorkOrderNo + MaterialCode + OperationName）
-- GoodQty: 跨所有开工记录（WorkOrderOnWork）汇总，避免重复开工导致漏计
-- 性能: ReportAgg CTE 预聚合报工，避免逐行 OUTER APPLY 相关子查询
-- ============================================================
CREATE OR ALTER VIEW [dbo].[MES_APS_OperationProgress_Mach_View] AS
WITH
-- Step 1: 跨所有开工记录预聚合报工数量（一次扫描，不逐行关联）
ReportAgg AS (
    SELECT
        w.ProcessNo,
        w.WorkOrder,
        w.Model,
        w.ProcessName,
        SUM(r.AmountByManual)        AS GoodQty,
        MAX(r.ReportTime)            AS LastReportTime,
        MAX(r.LastModificationTime)  AS ReportUpdatedAt
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork] w
    INNER JOIN [SMC_MES_DeviceProcessReport].[dbo].[DeviceProcessReportLog] r
        ON  r.WorkOrderOnWorkId = w.Id
        AND r.IsDeleted         = 0
    WHERE w.IsDeleted     = 0
      AND w.ProcessNo     IS NOT NULL
      AND w.WorkOrder     IS NOT NULL
      AND w.StartWorkTime >= DATEADD(MONTH, -6, GETDATE())
    GROUP BY w.ProcessNo, w.WorkOrder, w.Model, w.ProcessName
),
-- Step 2: 工单去重，取最新一条开工记录的基本属性
Dedup AS (
    SELECT
        w.ProcessNo,
        w.WorkOrder,
        w.Model,
        w.ProcessName,
        w.WorkOrderCount,
        w.OrgId,
        w.LastModificationTime,
        ROW_NUMBER() OVER (
            PARTITION BY w.ProcessNo, w.WorkOrder, w.Model, w.ProcessName
            ORDER BY w.LastModificationTime DESC, w.Id DESC
        ) AS rn
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork] w
    WHERE w.IsDeleted     = 0
      AND w.ProcessNo     IS NOT NULL
      AND w.WorkOrder     IS NOT NULL
      AND w.StartWorkTime >= DATEADD(MONTH, -6, GETDATE())
)
SELECT
    CAST(d.ProcessNo AS NVARCHAR(100))  AS ProductionInstructionNo,
    d.WorkOrder                          AS MESWorkOrderNo,
    d.Model                              AS MaterialCode,
    ISNULL(ic.Name, d.ProcessName)       AS OperationName,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN 'BJ_MOLD'
        WHEN LEFT(org.Code, 1) = '1'    THEN 'CN_MACH'
        WHEN LEFT(org.Code, 1) = '3'    THEN 'BJ_MACH'
        WHEN LEFT(org.Code, 1) = '5'    THEN 'TJ_MACH'
        ELSE 'UNKNOWN'
    END                                  AS StageCode,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN N'北京注塑'
        WHEN LEFT(org.Code, 1) = '1'    THEN N'中国机加'
        WHEN LEFT(org.Code, 1) = '3'    THEN N'北京机加'
        WHEN LEFT(org.Code, 1) = '5'    THEN N'天津机加'
        ELSE N'UNKNOWN'
    END                                  AS StageName,
    d.WorkOrderCount                     AS PlannedQty,
    ISNULL(ra.GoodQty, 0)               AS GoodQty,
    NULL                                 AS ScrapQty,
    NULL                                 AS ReworkQty,
    ra.LastReportTime,
    ISNULL(ra.ReportUpdatedAt, d.LastModificationTime) AS SourceUpdatedAt
FROM Dedup d
LEFT JOIN [SMC_MOM_SYS].[dbo].[AbpOrganizationUnits] org
    ON  org.Id        = d.OrgId
    AND org.IsDeleted = 0
-- OUTER APPLY TOP 1 保证 ProcessName 对应多条 V_ItemCode 时不产生笛卡尔积
OUTER APPLY (
    SELECT TOP 1 ic2.Name
    FROM [SMC_MES_DeviceProcessReport].[dbo].[V_ItemCode] ic2
    WHERE ic2.Code      = d.ProcessName
      AND ic2.IsDeleted = 0
      AND ic2.Category  = N'设备管理'
    ORDER BY ic2.Id
) ic
LEFT JOIN ReportAgg ra
    ON  ra.ProcessNo   = d.ProcessNo
    AND ra.WorkOrder   = d.WorkOrder
    AND ra.Model       = d.Model
    AND ra.ProcessName = d.ProcessName
WHERE d.rn = 1;
GO


-- ============================================================
-- 视图 3：大工艺进度级  MES_APS_StageProgress_Mach_View
-- 粒度: 一行 = ProductionInstructionNo × MaterialCode × StageCode
-- PlannedQty: 去重后工单计划量之和（避免重复开工导致重复累加）
-- GoodCompletedQty: 跨所有开工记录汇总良品
-- 性能: ReportAgg CTE 预聚合，Deduped CTE 工单去重后再聚合到大工艺粒度
-- ============================================================
CREATE OR ALTER VIEW [dbo].[MES_APS_StageProgress_Mach_View] AS
WITH
-- Step 1: 找出每个工单最后一道工序的 ProcessNum 末两位序号
LastProcess AS (
    SELECT
        ProcessNo, WorkOrder, Model,
        MAX(TRY_CAST(RIGHT(ProcessNum, 2) AS INT)) AS MaxSeq
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork]
    WHERE IsDeleted     = 0
      AND ProcessNo     IS NOT NULL
      AND WorkOrder     IS NOT NULL
      AND ProcessNum    IS NOT NULL
      AND StartWorkTime >= DATEADD(MONTH, -6, GETDATE())
    GROUP BY ProcessNo, WorkOrder, Model
),
-- Step 2: 对最后一道工序的所有开工记录（含多次开工）汇总报工量
ReportAgg AS (
    SELECT
        w.ProcessNo,
        w.WorkOrder,
        w.Model,
        SUM(r.AmountByManual)        AS GoodQty,
        MAX(r.ReportTime)            AS LastReportTime,
        MAX(r.LastModificationTime)  AS ReportUpdatedAt
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork] w
    INNER JOIN LastProcess lp
        ON  lp.ProcessNo = w.ProcessNo
        AND lp.WorkOrder = w.WorkOrder
        AND lp.Model     = w.Model
        AND TRY_CAST(RIGHT(w.ProcessNum, 2) AS INT) = lp.MaxSeq
    INNER JOIN [SMC_MES_DeviceProcessReport].[dbo].[DeviceProcessReportLog] r
        ON  r.WorkOrderOnWorkId = w.Id
        AND r.IsDeleted         = 0
    WHERE w.IsDeleted  = 0
      AND w.ProcessNum IS NOT NULL
    GROUP BY w.ProcessNo, w.WorkOrder, w.Model
),
-- Step 3: 工单去重，每个工单只保留最新一条开工记录（避免 PlannedQty 重复累加）
Deduped AS (
    SELECT
        w.ProcessNo,
        w.WorkOrder,
        w.Model,
        w.WorkOrderCount,
        w.OrgId,
        w.LastModificationTime,
        ROW_NUMBER() OVER (
            PARTITION BY w.ProcessNo, w.WorkOrder, w.Model
            ORDER BY w.LastModificationTime DESC, w.Id DESC
        ) AS rn
    FROM [SMC_MES_DeviceProcessReport].[dbo].[WorkOrderOnWork] w
    WHERE w.IsDeleted     = 0
      AND w.ProcessNo     IS NOT NULL
      AND w.WorkOrder     IS NOT NULL
      AND w.StartWorkTime >= DATEADD(MONTH, -6, GETDATE())
)
SELECT
    CAST(d.ProcessNo AS NVARCHAR(100))  AS ProductionInstructionNo,
    d.Model                              AS MaterialCode,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN 'BJ_MOLD'
        WHEN LEFT(org.Code, 1) = '1'    THEN 'CN_MACH'
        WHEN LEFT(org.Code, 1) = '3'    THEN 'BJ_MACH'
        WHEN LEFT(org.Code, 1) = '5'    THEN 'TJ_MACH'
        ELSE 'UNKNOWN'
    END                                  AS StageCode,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN N'北京注塑'
        WHEN LEFT(org.Code, 1) = '1'    THEN N'中国机加'
        WHEN LEFT(org.Code, 1) = '3'    THEN N'北京机加'
        WHEN LEFT(org.Code, 1) = '5'    THEN N'天津机加'
        ELSE N'UNKNOWN'
    END                                  AS StageName,
    SUM(d.WorkOrderCount)                AS PlannedQty,
    ISNULL(SUM(ra.GoodQty), 0)          AS GoodCompletedQty,
    NULL                                 AS ScrapQty,
    NULL                                 AS ReworkQty,
    MAX(ra.LastReportTime)               AS LastReportTime,
    MAX(ISNULL(ra.ReportUpdatedAt, d.LastModificationTime)) AS SourceUpdatedAt
FROM Deduped d
LEFT JOIN [SMC_MOM_SYS].[dbo].[AbpOrganizationUnits] org
    ON  org.Id        = d.OrgId
    AND org.IsDeleted = 0
LEFT JOIN ReportAgg ra
    ON  ra.ProcessNo = d.ProcessNo
    AND ra.WorkOrder = d.WorkOrder
    AND ra.Model     = d.Model
WHERE d.rn = 1
GROUP BY
    d.ProcessNo,
    d.Model,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN 'BJ_MOLD'
        WHEN LEFT(org.Code, 1) = '1'    THEN 'CN_MACH'
        WHEN LEFT(org.Code, 1) = '3'    THEN 'BJ_MACH'
        WHEN LEFT(org.Code, 1) = '5'    THEN 'TJ_MACH'
        ELSE 'UNKNOWN'
    END,
    CASE
        WHEN LEFT(org.Code, 4) = '3118' THEN N'北京注塑'
        WHEN LEFT(org.Code, 1) = '1'    THEN N'中国机加'
        WHEN LEFT(org.Code, 1) = '3'    THEN N'北京机加'
        WHEN LEFT(org.Code, 1) = '5'    THEN N'天津机加'
        ELSE N'UNKNOWN'
    END;
GO


-- ============================================================
-- 快速验证（部署后执行）
-- ============================================================
/*
SELECT TOP 10 * FROM [dbo].[MES_APS_WorkOrder_Mach_View]          ORDER BY SourceUpdatedAt DESC;
SELECT TOP 10 * FROM [dbo].[MES_APS_OperationProgress_Mach_View]  ORDER BY SourceUpdatedAt DESC;
SELECT TOP 10 * FROM [dbo].[MES_APS_StageProgress_Mach_View]      ORDER BY SourceUpdatedAt DESC;

-- StageCode 分布
SELECT StageCode, StageName, COUNT(*) AS Rows
FROM [dbo].[MES_APS_OperationProgress_Mach_View]
GROUP BY StageCode, StageName ORDER BY Rows DESC;

-- UNKNOWN 排查
SELECT ProductionInstructionNo, MaterialCode, OperationName
FROM [dbo].[MES_APS_OperationProgress_Mach_View]
WHERE StageCode = 'UNKNOWN';

-- 唯一键冲突前置检查（部署后，运行SP前执行）
-- 若有结果则说明视图仍有重复行，需进一步排查
SELECT ProductionInstructionNo, MESWorkOrderNo, MaterialCode, OperationName, StageCode,
       COUNT(*) AS Cnt
FROM [dbo].[MES_APS_OperationProgress_Mach_View]
GROUP BY ProductionInstructionNo, MESWorkOrderNo, MaterialCode, OperationName, StageCode
HAVING COUNT(*) > 1;
*/
