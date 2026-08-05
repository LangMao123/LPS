-- =============================================
-- ERP_Order_Staging 表结构变更（v5.0.24）
-- 日期：2026-05-13
-- 变更：
--   1. 新增 CustomerCode 列（存储ERP原始客户代码，用于 CustomerCodeMap 查询）
--   2. 新增 JPOrderNo 列（存储日本订单号，用于 SalesOrderCategory 派生）
--   3. 新增 DelayStatus 列（派生字段，ON_TIME/FIRST_DELAY）
--   4. 创建 CustomerCodeMap 映射表
-- =============================================

USE [APS_Production]
GO

-- 1. ERP_Order_Staging 新增列
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ERP_Order_Staging') AND name = 'CustomerCode')
    ALTER TABLE ERP_Order_Staging ADD CustomerCode NVARCHAR(20) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ERP_Order_Staging') AND name = 'JPOrderNo')
    ALTER TABLE ERP_Order_Staging ADD JPOrderNo NVARCHAR(50) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ERP_Order_Staging') AND name = 'DelayStatus')
    ALTER TABLE ERP_Order_Staging ADD DelayStatus NVARCHAR(20) NULL;
GO

-- 1b. OrderType 扩列：PRODUCTION_INSTRUCTION = 22字符，原 NVARCHAR(20) 不够
ALTER TABLE ERP_Order_Staging ALTER COLUMN OrderType NVARCHAR(50) NULL;
GO

-- 1c. Order_Canonical 同步扩列
IF COL_LENGTH('Order_Canonical', 'OrderType') IS NOT NULL
    ALTER TABLE Order_Canonical ALTER COLUMN OrderType NVARCHAR(50) NULL;
GO

-- 2. Order_Canonical 新增 DelayStatus 列
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Order_Canonical') AND name = 'DelayStatus')
    ALTER TABLE Order_Canonical ADD DelayStatus NVARCHAR(20) NULL;
GO

-- 3. CustomerCodeMap 映射表
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('CustomerCodeMap') AND type = 'U')
BEGIN
    CREATE TABLE CustomerCodeMap (
        CustomerCode    NVARCHAR(20)  NOT NULL,
        CustomerSegment NVARCHAR(50)  NOT NULL,
        IsActive        BIT           NOT NULL DEFAULT 1,
        DescriptionChn  NVARCHAR(200) NULL,
        UpdatedAt       DATETIME2     NOT NULL DEFAULT GETDATE(),
        CONSTRAINT PK_CustomerCodeMap PRIMARY KEY (CustomerCode)
    );
END
GO
