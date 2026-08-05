-- ============================================================
-- Pegging 集成测试 - Part 1 Setup Only (单场景精简版)
-- ============================================================
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @RunId NVARCHAR(20) = CONVERT(NVARCHAR,GETDATE(),112) + '_' + RIGHT('000'+CONVERT(NVARCHAR,DATEPART(MILLISECOND,GETDATE())),3);

-- Factory & ProductFamily
DECLARE @S1_FC NVARCHAR(50)='TF_S1_'+@RunId, @S1_PF NVARCHAR(50)='PF_S1_'+@RunId;
INSERT INTO Factory (Code,Name,Location,TimeZone,IsActive,CreatedAt,UpdatedAt)
VALUES (@S1_FC,'S1 Factory','Loc','UTC+8',1,GETDATE(),GETDATE());
DECLARE @S1_FId INT=SCOPE_IDENTITY();

INSERT INTO ProductFamily (Code,Name,IsActive,CreatedAt,UpdatedAt)
VALUES (@S1_PF,'S1 PF',1,GETDATE(),GETDATE());
DECLARE @S1_PFId INT=SCOPE_IDENTITY();

-- Materials
DECLARE @S1_A NVARCHAR(50)='S1_A_'+@RunId, @S1_B NVARCHAR(50)='S1_B_'+@RunId, @S1_C NVARCHAR(50)='S1_C_'+@RunId;
INSERT INTO Material (MaterialCode,MaterialName,MaterialType,UOM,LeadTimeDays,SafetyStock,LowLevelCode,IsPurchased,IsSimpleItem,IsActive,CreatedAt,UpdatedAt) VALUES
    (@S1_A,'S1 Mat A','MFG','EA',0,0,0,0,0,1,GETDATE(),GETDATE()),
    (@S1_B,'S1 Mat B','MFG','EA',0,0,1,0,0,1,GETDATE(),GETDATE()),
    (@S1_C,'S1 Mat C','PUR','EA',0,0,2,1,0,1,GETDATE(),GETDATE());
DECLARE @S1_AId INT=(SELECT Id FROM Material WHERE MaterialCode=@S1_A);
DECLARE @S1_BId INT=(SELECT Id FROM Material WHERE MaterialCode=@S1_B);

-- BOM
DECLARE @S1_Batch NVARCHAR(50)='BATCH_S1_'+@RunId;
INSERT INTO APS_BOM_RAW (BatchNo,BOMNO,ParentMaterialCode,ChildMaterialCode,Quantity,Level,LLC,IsLeaf,ChildRequiredStageCode,SyncedAt) VALUES
    (@S1_Batch,@S1_Batch,@S1_A,@S1_B,2,1,1,0,NULL,GETDATE()),
    (@S1_Batch,@S1_Batch,@S1_B,@S1_C,3,2,2,1,NULL,GETDATE());

-- Inventory
INSERT INTO InventoryBalance (MaterialCode,ProductFamilyId,FactoryId,OnHandQty,AllocatedQty,Source,BatchNo,LastUpdatedAt,CreatedAt) VALUES
    (@S1_B,@S1_PFId,@S1_FId,5,0,'TEST',@S1_Batch,GETDATE(),GETDATE());

-- ScheduleRun & PlanVersion
INSERT INTO ScheduleRun (RunType,Status,TriggeredBy,DataCutoffTime,StartedAt,CreatedAt)
VALUES ('FULL_SCHEDULE','RUNNING','TestScript',GETDATE(),GETDATE(),GETDATE());
DECLARE @S1_SRId INT=SCOPE_IDENTITY();

INSERT INTO PlanVersion (VersionCode,VersionCategory,PlanHorizonStart,PlanHorizonEnd,ComputeMode,Status,SourceScheduleRunId,CreatedBy,CreatedAt) VALUES
    ('PV_S1_'+@RunId,'TEST',CAST(GETDATE() AS DATE),DATEADD(DAY,90,CAST(GETDATE() AS DATE)),'AUTO','Created',@S1_SRId,'TestScript',GETDATE());
DECLARE @S1_PVId INT=SCOPE_IDENTITY();

-- Order
INSERT INTO [Order] (PlanVersionId,OrderNo,OrderType,MaterialId,ProductFamilyId,FactoryId,Quantity,UOM,CustomerDueDate,Priority,Status,SourceSystem,MaterialCode,CreatedAt,UpdatedAt) VALUES
    (@S1_PVId,'ORD_S1_'+@RunId,'PRODUCTION',@S1_AId,@S1_PFId,@S1_FId,10,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S1_A,GETDATE(),GETDATE());
DECLARE @S1_OId BIGINT=SCOPE_IDENTITY();

-- OrderBomRequestLink
INSERT INTO OrderBomRequestLink (PlanVersionId,BatchNo,OrderId,OrderCanonicalId,OrderNo,SourceSystem,RequestDetailId) VALUES
    (@S1_PVId,@S1_Batch,@S1_OId,@S1_OId,'ORD_S1_'+@RunId,'TEST',@S1_OId);

COMMIT TRANSACTION;

-- 立即验证
PRINT 'RunId: ' + @RunId;
PRINT 'PlanVersionId: ' + CAST(@S1_PVId AS NVARCHAR);

SELECT @RunId AS RunId, @S1_PVId AS PlanVersionId, @S1_FId AS FactoryId, @S1_B AS MatBCode;

-- 数据完整性检查
SELECT 'Material' AS TableName, COUNT(*) AS [Count] FROM Material WHERE MaterialCode LIKE '%'+@RunId+'%'
UNION ALL
SELECT 'Order', COUNT(*) FROM [Order] WHERE PlanVersionId = @S1_PVId
UNION ALL
SELECT 'PlanVersion', COUNT(*) FROM PlanVersion WHERE Id = @S1_PVId
UNION ALL
SELECT 'APS_BOM_RAW', COUNT(*) FROM APS_BOM_RAW WHERE BatchNo = @S1_Batch
UNION ALL
SELECT 'InventoryBalance', COUNT(*) FROM InventoryBalance WHERE MaterialCode LIKE '%'+@RunId+'%';

PRINT '===== Setup 完成！请对 PlanVersionId 触发 ISchedulingOrchestrator =====';
