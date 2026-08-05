-- ============================================================
-- Pegging 编排器集成验证脚本（多场景合并版）
--
-- 场景列表：
--   S1: A(10) → B(qty=2,库存5) → C(qty=3,无库存)  部分满足
--       期望: Task=3, Pegging=2, Allocation=1, MatB剩余=0, ShortageQty=45
--   S2: A(10) → B(qty=2) → C(qty=3)  完全短缺（无库存）
--       期望: Task=3, Pegging=2, Allocation=0, ShortageQty=60
--   S3: 两笔订单竞争 B 库存=5（A→B qty=1，每笔需B=5）
--       期望: Task=2, Pegging=1(B→A), Allocation=1(5单位), 订单2 B短缺产生NEW_REQUIREMENT
--   S4: A(10) → B(qty=2,库存=20)  完全满足
--       期望: Task=0, Pegging=0, Allocation=1, MatB剩余=0
--
-- 使用方法：
--   1. 执行 Part 1 (Setup)，记录输出的各场景 PlanVersionId
--   2. 通过 API/Hangfire 对每个 PlanVersionId 触发 Pegging
--   3. 执行 Part 2 (Verify) 查看汇总结果
--   4. 执行 Part 3 (Cleanup) 清除所有测试数据
-- ============================================================


-- ============================================================
-- Part 1: Setup — 四场景一次性注入
-- ============================================================
BEGIN TRANSACTION;

DECLARE @RunId NVARCHAR(20) = CONVERT(NVARCHAR,GETDATE(),112)
    + '_' + RIGHT('000'+CONVERT(NVARCHAR,DATEPART(MILLISECOND,GETDATE())),3);

-- ── 公共辅助：每个场景独立工厂+产品族，互不干扰 ──────────────

-- ── S1 ────────────────────────────────────────────────────────
DECLARE @S1_FC NVARCHAR(50)='TF_S1_'+@RunId, @S1_PF NVARCHAR(50)='PF_S1_'+@RunId;
INSERT INTO Factory        (Code,Name,Location,TimeZone,IsActive,CreatedAt,UpdatedAt) VALUES (@S1_FC,'S1 Factory','Loc','UTC+8',1,GETDATE(),GETDATE());
DECLARE @S1_FId INT=SCOPE_IDENTITY();
INSERT INTO ProductFamily  (Code,Name,IsActive,CreatedAt,UpdatedAt)                  VALUES (@S1_PF,'S1 PF',1,GETDATE(),GETDATE());
DECLARE @S1_PFId INT=SCOPE_IDENTITY();

DECLARE @S1_A NVARCHAR(50)='S1_A_'+@RunId, @S1_B NVARCHAR(50)='S1_B_'+@RunId, @S1_C NVARCHAR(50)='S1_C_'+@RunId;
INSERT INTO Material (MaterialCode,MaterialName,MaterialType,UOM,LeadTimeDays,SafetyStock,LowLevelCode,IsPurchased,IsSimpleItem,IsActive,CreatedAt,UpdatedAt) VALUES
    (@S1_A,'S1 Mat A','MFG','EA',0,0,0,0,0,1,GETDATE(),GETDATE()),
    (@S1_B,'S1 Mat B','MFG','EA',0,0,1,0,0,1,GETDATE(),GETDATE()),
    (@S1_C,'S1 Mat C','PUR','EA',0,0,2,1,0,1,GETDATE(),GETDATE());
DECLARE @S1_AId INT=(SELECT Id FROM Material WHERE MaterialCode=@S1_A);
DECLARE @S1_BId INT=(SELECT Id FROM Material WHERE MaterialCode=@S1_B);

DECLARE @S1_Batch NVARCHAR(50)='BATCH_S1_'+@RunId;
INSERT INTO APS_BOM_RAW (BatchNo,BOMNO,ParentMaterialCode,ChildMaterialCode,Quantity,Level,LLC,IsLeaf,ChildRequiredStageCode,SyncedAt) VALUES
    (@S1_Batch,@S1_Batch,@S1_A,@S1_B,2,1,1,0,NULL,GETDATE()),
    (@S1_Batch,@S1_Batch,@S1_B,@S1_C,3,2,2,1,NULL,GETDATE());
INSERT INTO InventoryBalance (MaterialCode,ProductFamilyId,FactoryId,OnHandQty,AllocatedQty,Source,BatchNo,LastUpdatedAt,CreatedAt) VALUES
    (@S1_B,@S1_PFId,@S1_FId,5,0,'TEST',@S1_Batch,GETDATE(),GETDATE());

INSERT INTO ScheduleRun (RunType,Status,TriggeredBy,DataCutoffTime,StartedAt,CreatedAt) VALUES ('FULL_SCHEDULE','RUNNING','TestScript',GETDATE(),GETDATE(),GETDATE());
DECLARE @S1_SRId INT=SCOPE_IDENTITY();
INSERT INTO PlanVersion (VersionCode,VersionCategory,PlanHorizonStart,PlanHorizonEnd,ComputeMode,Status,SourceScheduleRunId,CreatedBy,CreatedAt) VALUES
    ('PV_S1_'+@RunId,'TEST',CAST(GETDATE() AS DATE),DATEADD(DAY,90,CAST(GETDATE() AS DATE)),'AUTO','Created',@S1_SRId,'TestScript',GETDATE());
DECLARE @S1_PVId INT=SCOPE_IDENTITY();
INSERT INTO [Order] (PlanVersionId,OrderNo,OrderType,MaterialId,ProductFamilyId,FactoryId,Quantity,UOM,CustomerDueDate,Priority,Status,SourceSystem,MaterialCode,CreatedAt,UpdatedAt) VALUES
    (@S1_PVId,'ORD_S1_'+@RunId,'PRODUCTION',@S1_AId,@S1_PFId,@S1_FId,10,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S1_A,GETDATE(),GETDATE());
DECLARE @S1_OId BIGINT=SCOPE_IDENTITY();
INSERT INTO OrderBomRequestLink (PlanVersionId,BatchNo,OrderId,OrderCanonicalId,OrderNo,SourceSystem,RequestDetailId) VALUES
    (@S1_PVId,@S1_Batch,@S1_OId,@S1_OId,'ORD_S1_'+@RunId,'TEST',@S1_OId);

-- ── S2：完全短缺（无库存）─────────────────────────────────────
DECLARE @S2_FC NVARCHAR(50)='TF_S2_'+@RunId, @S2_PF NVARCHAR(50)='PF_S2_'+@RunId;
INSERT INTO Factory        (Code,Name,Location,TimeZone,IsActive,CreatedAt,UpdatedAt) VALUES (@S2_FC,'S2 Factory','Loc','UTC+8',1,GETDATE(),GETDATE());
DECLARE @S2_FId INT=SCOPE_IDENTITY();
INSERT INTO ProductFamily  (Code,Name,IsActive,CreatedAt,UpdatedAt)                  VALUES (@S2_PF,'S2 PF',1,GETDATE(),GETDATE());
DECLARE @S2_PFId INT=SCOPE_IDENTITY();

DECLARE @S2_A NVARCHAR(50)='S2_A_'+@RunId, @S2_B NVARCHAR(50)='S2_B_'+@RunId, @S2_C NVARCHAR(50)='S2_C_'+@RunId;
INSERT INTO Material (MaterialCode,MaterialName,MaterialType,UOM,LeadTimeDays,SafetyStock,LowLevelCode,IsPurchased,IsSimpleItem,IsActive,CreatedAt,UpdatedAt) VALUES
    (@S2_A,'S2 Mat A','MFG','EA',0,0,0,0,0,1,GETDATE(),GETDATE()),
    (@S2_B,'S2 Mat B','MFG','EA',0,0,1,0,0,1,GETDATE(),GETDATE()),
    (@S2_C,'S2 Mat C','PUR','EA',0,0,2,1,0,1,GETDATE(),GETDATE());
DECLARE @S2_AId INT=(SELECT Id FROM Material WHERE MaterialCode=@S2_A);

DECLARE @S2_Batch NVARCHAR(50)='BATCH_S2_'+@RunId;
INSERT INTO APS_BOM_RAW (BatchNo,BOMNO,ParentMaterialCode,ChildMaterialCode,Quantity,Level,LLC,IsLeaf,ChildRequiredStageCode,SyncedAt) VALUES
    (@S2_Batch,@S2_Batch,@S2_A,@S2_B,2,1,1,0,NULL,GETDATE()),
    (@S2_Batch,@S2_Batch,@S2_B,@S2_C,3,2,2,1,NULL,GETDATE());
-- 无库存，不插 InventoryBalance

INSERT INTO ScheduleRun (RunType,Status,TriggeredBy,DataCutoffTime,StartedAt,CreatedAt) VALUES ('FULL_SCHEDULE','RUNNING','TestScript',GETDATE(),GETDATE(),GETDATE());
DECLARE @S2_SRId INT=SCOPE_IDENTITY();
INSERT INTO PlanVersion (VersionCode,VersionCategory,PlanHorizonStart,PlanHorizonEnd,ComputeMode,Status,SourceScheduleRunId,CreatedBy,CreatedAt) VALUES
    ('PV_S2_'+@RunId,'TEST',CAST(GETDATE() AS DATE),DATEADD(DAY,90,CAST(GETDATE() AS DATE)),'AUTO','Created',@S2_SRId,'TestScript',GETDATE());
DECLARE @S2_PVId INT=SCOPE_IDENTITY();
INSERT INTO [Order] (PlanVersionId,OrderNo,OrderType,MaterialId,ProductFamilyId,FactoryId,Quantity,UOM,CustomerDueDate,Priority,Status,SourceSystem,MaterialCode,CreatedAt,UpdatedAt) VALUES
    (@S2_PVId,'ORD_S2_'+@RunId,'PRODUCTION',@S2_AId,@S2_PFId,@S2_FId,10,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S2_A,GETDATE(),GETDATE());
DECLARE @S2_OId BIGINT=SCOPE_IDENTITY();
INSERT INTO OrderBomRequestLink (PlanVersionId,BatchNo,OrderId,OrderCanonicalId,OrderNo,SourceSystem,RequestDetailId) VALUES
    (@S2_PVId,@S2_Batch,@S2_OId,@S2_OId,'ORD_S2_'+@RunId,'TEST',@S2_OId);

-- ── S3：多订单竞争库存（B库存=5，两笔订单各需B=5）────────────
DECLARE @S3_FC NVARCHAR(50)='TF_S3_'+@RunId, @S3_PF NVARCHAR(50)='PF_S3_'+@RunId;
INSERT INTO Factory       (Code,Name,Location,TimeZone,IsActive,CreatedAt,UpdatedAt) VALUES (@S3_FC,'S3 Factory','Loc','UTC+8',1,GETDATE(),GETDATE());
DECLARE @S3_FId INT=SCOPE_IDENTITY();
INSERT INTO ProductFamily (Code,Name,IsActive,CreatedAt,UpdatedAt)                  VALUES (@S3_PF,'S3 PF',1,GETDATE(),GETDATE());
DECLARE @S3_PFId INT=SCOPE_IDENTITY();

DECLARE @S3_A NVARCHAR(50)='S3_A_'+@RunId, @S3_B NVARCHAR(50)='S3_B_'+@RunId;
INSERT INTO Material (MaterialCode,MaterialName,MaterialType,UOM,LeadTimeDays,SafetyStock,LowLevelCode,IsPurchased,IsSimpleItem,IsActive,CreatedAt,UpdatedAt) VALUES
    (@S3_A,'S3 Mat A','MFG','EA',0,0,0,0,0,1,GETDATE(),GETDATE()),
    (@S3_B,'S3 Mat B','PUR','EA',0,0,1,1,0,1,GETDATE(),GETDATE());
DECLARE @S3_AId INT=(SELECT Id FROM Material WHERE MaterialCode=@S3_A);

DECLARE @S3_Batch NVARCHAR(50)='BATCH_S3_'+@RunId;
INSERT INTO APS_BOM_RAW (BatchNo,BOMNO,ParentMaterialCode,ChildMaterialCode,Quantity,Level,LLC,IsLeaf,ChildRequiredStageCode,SyncedAt) VALUES
    (@S3_Batch,@S3_Batch,@S3_A,@S3_B,1,1,1,1,NULL,GETDATE());
INSERT INTO InventoryBalance (MaterialCode,ProductFamilyId,FactoryId,OnHandQty,AllocatedQty,Source,BatchNo,LastUpdatedAt,CreatedAt) VALUES
    (@S3_B,@S3_PFId,@S3_FId,5,0,'TEST',@S3_Batch,GETDATE(),GETDATE());

INSERT INTO ScheduleRun (RunType,Status,TriggeredBy,DataCutoffTime,StartedAt,CreatedAt) VALUES ('FULL_SCHEDULE','RUNNING','TestScript',GETDATE(),GETDATE(),GETDATE());
DECLARE @S3_SRId INT=SCOPE_IDENTITY();
INSERT INTO PlanVersion (VersionCode,VersionCategory,PlanHorizonStart,PlanHorizonEnd,ComputeMode,Status,SourceScheduleRunId,CreatedBy,CreatedAt) VALUES
    ('PV_S3_'+@RunId,'TEST',CAST(GETDATE() AS DATE),DATEADD(DAY,90,CAST(GETDATE() AS DATE)),'AUTO','Created',@S3_SRId,'TestScript',GETDATE());
DECLARE @S3_PVId INT=SCOPE_IDENTITY();
INSERT INTO [Order] (PlanVersionId,OrderNo,OrderType,MaterialId,ProductFamilyId,FactoryId,Quantity,UOM,CustomerDueDate,Priority,Status,SourceSystem,MaterialCode,CreatedAt,UpdatedAt) VALUES
    (@S3_PVId,'ORD_S3_1_'+@RunId,'PRODUCTION',@S3_AId,@S3_PFId,@S3_FId,5,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S3_A,GETDATE(),GETDATE()),
    (@S3_PVId,'ORD_S3_2_'+@RunId,'PRODUCTION',@S3_AId,@S3_PFId,@S3_FId,5,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S3_A,GETDATE(),GETDATE());
DECLARE @S3_OId2 BIGINT=SCOPE_IDENTITY();
DECLARE @S3_OId1 BIGINT=@S3_OId2-1;
INSERT INTO OrderBomRequestLink (PlanVersionId,BatchNo,OrderId,OrderCanonicalId,OrderNo,SourceSystem,RequestDetailId) VALUES
    (@S3_PVId,@S3_Batch,@S3_OId1,@S3_OId1,'ORD_S3_1_'+@RunId,'TEST',@S3_OId1),
    (@S3_PVId,@S3_Batch,@S3_OId2,@S3_OId2,'ORD_S3_2_'+@RunId,'TEST',@S3_OId2);

-- ── S4：单层 BOM + 完全满足（B库存=20，需求B=20）────────────
DECLARE @S4_FC NVARCHAR(50)='TF_S4_'+@RunId, @S4_PF NVARCHAR(50)='PF_S4_'+@RunId;
INSERT INTO Factory       (Code,Name,Location,TimeZone,IsActive,CreatedAt,UpdatedAt) VALUES (@S4_FC,'S4 Factory','Loc','UTC+8',1,GETDATE(),GETDATE());
DECLARE @S4_FId INT=SCOPE_IDENTITY();
INSERT INTO ProductFamily (Code,Name,IsActive,CreatedAt,UpdatedAt)                  VALUES (@S4_PF,'S4 PF',1,GETDATE(),GETDATE());
DECLARE @S4_PFId INT=SCOPE_IDENTITY();

DECLARE @S4_A NVARCHAR(50)='S4_A_'+@RunId, @S4_B NVARCHAR(50)='S4_B_'+@RunId;
INSERT INTO Material (MaterialCode,MaterialName,MaterialType,UOM,LeadTimeDays,SafetyStock,LowLevelCode,IsPurchased,IsSimpleItem,IsActive,CreatedAt,UpdatedAt) VALUES
    (@S4_A,'S4 Mat A','MFG','EA',0,0,0,0,0,1,GETDATE(),GETDATE()),
    (@S4_B,'S4 Mat B','PUR','EA',0,0,1,1,0,1,GETDATE(),GETDATE());
DECLARE @S4_AId INT=(SELECT Id FROM Material WHERE MaterialCode=@S4_A);

DECLARE @S4_Batch NVARCHAR(50)='BATCH_S4_'+@RunId;
INSERT INTO APS_BOM_RAW (BatchNo,BOMNO,ParentMaterialCode,ChildMaterialCode,Quantity,Level,LLC,IsLeaf,ChildRequiredStageCode,SyncedAt) VALUES
    (@S4_Batch,@S4_Batch,@S4_A,@S4_B,2,1,1,1,NULL,GETDATE());
INSERT INTO InventoryBalance (MaterialCode,ProductFamilyId,FactoryId,OnHandQty,AllocatedQty,Source,BatchNo,LastUpdatedAt,CreatedAt) VALUES
    (@S4_B,@S4_PFId,@S4_FId,20,0,'TEST',@S4_Batch,GETDATE(),GETDATE());

INSERT INTO ScheduleRun (RunType,Status,TriggeredBy,DataCutoffTime,StartedAt,CreatedAt) VALUES ('FULL_SCHEDULE','RUNNING','TestScript',GETDATE(),GETDATE(),GETDATE());
DECLARE @S4_SRId INT=SCOPE_IDENTITY();
INSERT INTO PlanVersion (VersionCode,VersionCategory,PlanHorizonStart,PlanHorizonEnd,ComputeMode,Status,SourceScheduleRunId,CreatedBy,CreatedAt) VALUES
    ('PV_S4_'+@RunId,'TEST',CAST(GETDATE() AS DATE),DATEADD(DAY,90,CAST(GETDATE() AS DATE)),'AUTO','Created',@S4_SRId,'TestScript',GETDATE());
DECLARE @S4_PVId INT=SCOPE_IDENTITY();
INSERT INTO [Order] (PlanVersionId,OrderNo,OrderType,MaterialId,ProductFamilyId,FactoryId,Quantity,UOM,CustomerDueDate,Priority,Status,SourceSystem,MaterialCode,CreatedAt,UpdatedAt) VALUES
    (@S4_PVId,'ORD_S4_'+@RunId,'PRODUCTION',@S4_AId,@S4_PFId,@S4_FId,10,'EA',DATEADD(DAY,30,GETDATE()),50,'Open','TEST',@S4_A,GETDATE(),GETDATE());
DECLARE @S4_OId BIGINT=SCOPE_IDENTITY();
INSERT INTO OrderBomRequestLink (PlanVersionId,BatchNo,OrderId,OrderCanonicalId,OrderNo,SourceSystem,RequestDetailId) VALUES
    (@S4_PVId,@S4_Batch,@S4_OId,@S4_OId,'ORD_S4_'+@RunId,'TEST',@S4_OId);

COMMIT TRANSACTION;

-- 保存到临时表，供 Part 2 / Part 3 直接读取（同一 SSMS 会话内有效）
IF OBJECT_ID('tempdb..#PeggingTestRun') IS NOT NULL DROP TABLE #PeggingTestRun;
CREATE TABLE #PeggingTestRun (
    RunId   NVARCHAR(20),
    S1_PVId INT, S2_PVId INT, S3_PVId INT, S4_PVId INT,
    S1_B    NVARCHAR(50), S1_FId INT,
    S3_B    NVARCHAR(50), S3_FId INT,
    S4_B    NVARCHAR(50), S4_FId INT
);
INSERT INTO #PeggingTestRun VALUES (
    @RunId, @S1_PVId,@S2_PVId,@S3_PVId,@S4_PVId,
    @S1_B,@S1_FId, @S3_B,@S3_FId, @S4_B,@S4_FId
);

SELECT
    @RunId   AS RunId,
    @S1_PVId AS S1_PlanVersionId,
    @S2_PVId AS S2_PlanVersionId,
    @S3_PVId AS S3_PlanVersionId,
    @S4_PVId AS S4_PlanVersionId,
    @S1_B    AS S1_MatBCode, @S1_FId AS S1_FactoryId,
    @S3_B    AS S3_MatBCode, @S3_FId AS S3_FactoryId,
    @S4_B    AS S4_MatBCode, @S4_FId AS S4_FactoryId;

PRINT '===== Setup 完成，请对以上四个 PlanVersionId 分别触发 Pegging，然后执行 Part 2 =====';

GO
-- ============================================================
-- Part 2: Verify（Pegging 执行后运行）
-- ============================================================

DECLARE @V_RunId   NVARCHAR(20) = (SELECT RunId   FROM #PeggingTestRun);
DECLARE @V_S1_PVId INT          = (SELECT S1_PVId FROM #PeggingTestRun);
DECLARE @V_S2_PVId INT          = (SELECT S2_PVId FROM #PeggingTestRun);
DECLARE @V_S3_PVId INT          = (SELECT S3_PVId FROM #PeggingTestRun);
DECLARE @V_S4_PVId INT          = (SELECT S4_PVId FROM #PeggingTestRun);
DECLARE @V_S1_B    NVARCHAR(50) = (SELECT S1_B    FROM #PeggingTestRun);
DECLARE @V_S1_FId  INT          = (SELECT S1_FId  FROM #PeggingTestRun);
DECLARE @V_S3_B    NVARCHAR(50) = (SELECT S3_B    FROM #PeggingTestRun);
DECLARE @V_S3_FId  INT          = (SELECT S3_FId  FROM #PeggingTestRun);
DECLARE @V_S4_B    NVARCHAR(50) = (SELECT S4_B    FROM #PeggingTestRun);
DECLARE @V_S4_FId  INT          = (SELECT S4_FId  FROM #PeggingTestRun);

-- ── 2A: 行数汇总（含新表 PeggingAllocationLedger）────────────
PRINT '===== 2A: 行数汇总 =====';
SELECT
    'S1 部分满足'  AS Scene,
    3  AS Task_Exp,    2  AS Pegging_Exp,    1  AS Alloc_Exp,    0  AS MatB_Rem_Exp,
    (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S1_PVId) AS Task_Act,
    (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S1_PVId) AS Pegging_Act,
    (SELECT COUNT(*) FROM PeggingSupplyAllocation WHERE PlanVersionId=@V_S1_PVId) AS Alloc_Act,
    (SELECT ISNULL(OnHandQty-AllocatedQty,0) FROM InventoryBalance WHERE MaterialCode=@V_S1_B AND FactoryId=@V_S1_FId) AS MatB_Rem_Act,
    (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S1_PVId) AS Ledger_Act
UNION ALL SELECT
    'S2 完全短缺',
    3, 2, 0, NULL,
    (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S2_PVId),
    (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S2_PVId),
    (SELECT COUNT(*) FROM PeggingSupplyAllocation WHERE PlanVersionId=@V_S2_PVId),
    NULL,
    (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S2_PVId)
UNION ALL SELECT
    'S3 多订单竞争',
    2, 1, 1, 0,
    (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S3_PVId),
    (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S3_PVId),
    (SELECT COUNT(*) FROM PeggingSupplyAllocation WHERE PlanVersionId=@V_S3_PVId),
    (SELECT ISNULL(OnHandQty-AllocatedQty,0) FROM InventoryBalance WHERE MaterialCode=@V_S3_B AND FactoryId=@V_S3_FId),
    (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S3_PVId)
UNION ALL SELECT
    'S4 完全满足',
    0, 0, 1, 0,
    (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S4_PVId),
    (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S4_PVId),
    (SELECT COUNT(*) FROM PeggingSupplyAllocation WHERE PlanVersionId=@V_S4_PVId),
    (SELECT ISNULL(OnHandQty-AllocatedQty,0) FROM InventoryBalance WHERE MaterialCode=@V_S4_B AND FactoryId=@V_S4_FId),
    (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S4_PVId);

-- ── 2B: Pegging 外键完整性（UpstreamTaskId / DownstreamTaskId 必须存在于 Task）────
PRINT '===== 2B: Pegging FK 完整性（期望 0 行）=====';
SELECT p.PlanVersionId, p.Id AS PeggingId,
       p.UpstreamTaskId, p.DownstreamTaskId,
       CASE WHEN up.Id IS NULL THEN 'MISSING_UPSTREAM'   ELSE '' END AS UpstreamCheck,
       CASE WHEN dn.Id IS NULL THEN 'MISSING_DOWNSTREAM' ELSE '' END AS DownstreamCheck
FROM [Pegging] p
LEFT JOIN [Task] up ON up.Id = p.UpstreamTaskId
LEFT JOIN [Task] dn ON dn.Id = p.DownstreamTaskId
WHERE p.PlanVersionId IN (@V_S1_PVId, @V_S2_PVId, @V_S3_PVId, @V_S4_PVId)
  AND (up.Id IS NULL OR dn.Id IS NULL);

-- ── 2C: Pegging 物料 ID 正确性（UpstreamMaterialId / DownstreamMaterialId 与 Task.MaterialId 一致）──
PRINT '===== 2C: Pegging 物料 ID 一致性（期望 0 行）=====';
SELECT p.PlanVersionId, p.Id AS PeggingId,
       p.UpstreamMaterialId,   up.MaterialId AS Task_UpMaterialId,
       p.DownstreamMaterialId, dn.MaterialId AS Task_DnMaterialId
FROM [Pegging] p
JOIN [Task] up ON up.Id = p.UpstreamTaskId
JOIN [Task] dn ON dn.Id = p.DownstreamTaskId
WHERE p.PlanVersionId IN (@V_S1_PVId, @V_S2_PVId, @V_S3_PVId, @V_S4_PVId)
  AND (p.UpstreamMaterialId <> up.MaterialId OR p.DownstreamMaterialId <> dn.MaterialId);

-- ── 2D: Task.TaskType 分布（NEW_REQUIREMENT / VIRTUAL 等）────────
PRINT '===== 2D: Task.TaskType 分布 =====';
SELECT PlanVersionId, TaskType, COUNT(*) AS Cnt
FROM [Task]
WHERE PlanVersionId IN (@V_S1_PVId, @V_S2_PVId, @V_S3_PVId, @V_S4_PVId)
GROUP BY PlanVersionId, TaskType
ORDER BY PlanVersionId, TaskType;

-- ── 2E: PeggingAllocationLedger FK 完整性（FinalTaskId 必须存在于 Task）────
PRINT '===== 2E: PeggingAllocationLedger FK 完整性（期望 0 行）=====';
SELECT l.PlanVersionId, l.FinalTaskId, l.AllocationSequence
FROM PeggingAllocationLedger l
LEFT JOIN [Task] t ON t.Id = l.FinalTaskId AND t.PlanVersionId = l.PlanVersionId
WHERE l.PlanVersionId IN (@V_S1_PVId, @V_S2_PVId, @V_S3_PVId, @V_S4_PVId)
  AND l.FinalTaskId IS NOT NULL
  AND t.Id IS NULL;

-- ── 2F: PeggingAllocationLedger 数量非负校验────────────────────
PRINT '===== 2F: PeggingAllocationLedger 数量异常（期望 0 行）=====';
SELECT PlanVersionId, FinalTaskId, AllocationSequence, AllocatedQty, TaskComponentQty
FROM PeggingAllocationLedger
WHERE PlanVersionId IN (@V_S1_PVId, @V_S2_PVId, @V_S3_PVId, @V_S4_PVId)
  AND (AllocatedQty <= 0 OR (TaskComponentQty IS NOT NULL AND TaskComponentQty <= 0));

-- ── 2G: S4 完全满足 → Task 应为 0，Pegging 应为 0，Ledger 应为 0 ──
PRINT '===== 2G: S4 完全满足三零校验 =====';
SELECT
    (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S4_PVId) AS S4_Task,
    (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S4_PVId) AS S4_Pegging,
    (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S4_PVId) AS S4_Ledger,
    CASE WHEN
        (SELECT COUNT(*) FROM [Task]                  WHERE PlanVersionId=@V_S4_PVId) = 0 AND
        (SELECT COUNT(*) FROM [Pegging]               WHERE PlanVersionId=@V_S4_PVId) = 0 AND
        (SELECT COUNT(*) FROM PeggingAllocationLedger WHERE PlanVersionId=@V_S4_PVId) = 0
    THEN 'PASS' ELSE 'FAIL' END AS Result;

GO
-- ============================================================
-- Part 3: Cleanup
-- ============================================================

DECLARE @C_RunId NVARCHAR(20) = (SELECT RunId FROM #PeggingTestRun);

BEGIN TRANSACTION;

DELETE FROM [Pegging]               WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM PeggingSupplyAllocation WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM PeggingAllocationLedger WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM [Task]                  WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM OrderBomRequestLink     WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM [Order]                 WHERE PlanVersionId IN (SELECT Id FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);

DELETE FROM ScheduleRun  WHERE Id IN (SELECT SourceScheduleRunId FROM PlanVersion WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId);
DELETE FROM PlanVersion  WHERE VersionCode LIKE 'PV_S[1234]_'+@C_RunId;

DELETE FROM InventoryBalance WHERE MaterialCode LIKE 'S[1234]_%_'+@C_RunId;
DELETE FROM APS_BOM_RAW      WHERE BatchNo      LIKE 'BATCH_S[1234]_'+@C_RunId;
DELETE FROM Material         WHERE MaterialCode LIKE 'S[1234]_%_'+@C_RunId;
DELETE FROM Factory          WHERE Code         LIKE 'TF_S[1234]_'+@C_RunId;
DELETE FROM ProductFamily    WHERE Code         LIKE 'PF_S[1234]_'+@C_RunId;

COMMIT TRANSACTION;
PRINT '===== Cleanup 完成 =====';
