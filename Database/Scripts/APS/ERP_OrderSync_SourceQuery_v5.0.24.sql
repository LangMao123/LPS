-- =============================================
-- ERP 订单同步源查询（v5.0.24）
-- 日期：2026-05-13
-- 变更：新增 CustomerCode、JPOrderNo 字段同步到 ERP_Order_Staging
-- 用途：此查询由同步Job调用，将ERP订单数据写入 ERP_Order_Staging
-- =============================================

-- Part 1: 出荷指示（Sales Orders / MTO）
SELECT
    CAST(s.RequisitionID AS NVARCHAR(100)) AS SourceOrderId,
    'O' + CAST(s.ApprovedID AS NVARCHAR(50)) AS OrderNo,
    'ERP' AS SourceSystem,
    s.MasterID AS SourceMasterID,
    CAST(s.ZPQF AS NVARCHAR(50)) AS OrderType,
    CAST(s.Model AS NVARCHAR(100)) AS MaterialCode,
    COALESCE(NULLIF(NULLIF(CAST(s.BOMNo AS NVARCHAR(50)), ''), '0'),
             CAST(pi.BOMNo AS NVARCHAR(50)), '') AS BOMNO,
    CAST(s.ProcessCode AS NVARCHAR(50)) AS FactoryCode,
    s.RequestQty AS Quantity,
    'PCS' AS UOM,
    CAST(s.ReceiveDate AS DATE) AS DueDate,
    50 AS Priority,
    CASE
        WHEN s.InstrCancelDate IS NOT NULL THEN 'CANCELLED'
        WHEN s.Completion IS NOT NULL THEN 'CLOSED'
        ELSE 'Open'
    END AS Status,
    s.DlvyWay AS TransportMode,
    s.Accepter AS CustomerName,
    CAST(s.ProdId AS NVARCHAR(50)) AS MTS_InstructionNo,
    r.CustomerCode AS CustomerCode,          -- v5.0.24: 原始客户代码（用于CustomerCodeMap查询）
    s.InstrType AS SalesOrderCategory,
    CAST(s.Remarks AS NVARCHAR(200)) AS DemandMaturityStatus,
    r.JPOrderNo AS JPOrderNo,               -- v5.0.24: 日本订单号（用于SalesOrderCategory派生）
    s.InstrIssueDate AS CreatedAt,
    ISNULL(s.ComputeDate, s.InstrIssueDate) AS UpdatedAt
FROM [manu].[Manufacture].[dbo].[V_AllSalesInstr] s
LEFT JOIN [manu].[Manufacture].[dbo].[ODBC_V_U13_Requisition] r
    ON s.RequisitionID = r.IssueId
LEFT JOIN [manu].[Manufacture].[dbo].V_AllProdInstr pi
    ON s.ProdId = pi.IssueID
WHERE (s.InstrCancelDate IS NULL AND s.Completion IS NULL)
   OR s.InstrCancelDate >= DATEADD(DAY, -7, GETDATE())
   OR s.Completion >= DATEADD(DAY, -7, GETDATE())

UNION ALL

-- Part 2: 生产指示（MTS / Production Instructions）
SELECT
    CAST(p.IssueID AS NVARCHAR(100)) AS SourceOrderId,
    'P' + CAST(p.IssueID AS NVARCHAR(50)) AS OrderNo,
    'ERP' AS SourceSystem,
    p.MasterID AS SourceMasterID,
    'MTS' AS OrderType,
    CAST(p.Model AS NVARCHAR(100)) AS MaterialCode,
    ISNULL(CAST(p.BOMNo AS NVARCHAR(50)), '') AS BOMNO,
    CAST(p.ProcessCode AS NVARCHAR(50)) AS FactoryCode,
    p.Quantity AS Quantity,
    'PCS' AS UOM,
    CAST(p.InstrDlvyDate AS DATE) AS DueDate,
    50 AS Priority,
    CASE
        WHEN p.InstrCancelDate IS NOT NULL THEN 'CANCELLED'
        WHEN p.Completion IS NOT NULL THEN 'CLOSED'
        ELSE 'Open'
    END AS Status,
    CAST(NULL AS NVARCHAR(20)) AS TransportMode,
    CAST(NULL AS NVARCHAR(200)) AS CustomerName,
    CAST(p.IssueID AS NVARCHAR(50)) AS MTS_InstructionNo,
    CAST(NULL AS NVARCHAR(20)) AS CustomerCode,     -- MTS无客户代码
    CAST(NULL AS NVARCHAR(50)) AS SalesOrderCategory,
    CAST(NULL AS NVARCHAR(50)) AS DemandMaturityStatus,
    CAST(NULL AS NVARCHAR(50)) AS JPOrderNo,        -- MTS无日本订单号
    p.InstrIssueDate AS CreatedAt,
    ISNULL(p.StartWorkTime, p.InstrIssueDate) AS UpdatedAt
FROM [manu].[Manufacture].[dbo].V_AllProdInstr p
WHERE NOT EXISTS (
    SELECT 1
    FROM [manu].[Manufacture].[dbo].[V_AllSalesInstr] s
    WHERE s.ProdId = p.IssueID
)
AND ((p.InstrCancelDate IS NULL AND p.Completion IS NULL)
     OR p.InstrCancelDate >= DATEADD(DAY, -7, GETDATE())
     OR p.Completion >= DATEADD(DAY, -7, GETDATE()));
