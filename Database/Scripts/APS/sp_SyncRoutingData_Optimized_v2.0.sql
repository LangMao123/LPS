-- =============================================
-- APS 工艺路线同步存储过程（性能优化版）
-- 版本：v2.0
-- 日期：2026-05-08
-- 优化目标：解决 80万+ 数据量的性能问题
--
-- 主要优化点：
--   ✅ 一次性加载视图到临时表（避免重复扫描）
--   ✅ 去除 WHEN NOT MATCHED BY SOURCE（改为定期全量对账）
--   ✅ 增加批处理提示（OPTION (MAXDOP 4)）
--   ✅ 显式事务控制
--   ✅ 统计信息优化
--
-- 性能对比（预估）：
--   v1.0: 80万行 × 3次扫描 = 240万行扫描，预计 15-30 分钟
--   v2.0: 80万行 × 1次扫描 + 内存临时表，预计 3-5 分钟
--
-- 数据量：
--   ext_mes_aps_routing_operation_view: 80万行
--   ext_mes_aps_routing_dependency_view: 80万行
--   ext_APS_OperationResourceEligibility_View: 12万行
-- =============================================

USE APS_Production;
GO

CREATE OR ALTER PROCEDURE sp_SyncRoutingData
    @BatchNo              NVARCHAR(50),
    -- RoutingOperation 统计
    @OperationInserted    INT = 0 OUTPUT,
    @OperationUpdated     INT = 0 OUTPUT,
    @OperationDeactivated INT = 0 OUTPUT,
    -- RoutingDependency 统计
    @DependencyInserted    INT = 0 OUTPUT,
    @DependencyUpdated     INT = 0 OUTPUT,
    @DependencyDeactivated INT = 0 OUTPUT,
    -- RoutingStage 统计
    @StageInserted    INT = 0 OUTPUT,
    @StageUpdated     INT = 0 OUTPUT,
    @StageDeactivated INT = 0 OUTPUT,
    -- OperationResourceEligibility 统计
    @EligibilityInserted    INT = 0 OUTPUT,
    @EligibilityUpdated     INT = 0 OUTPUT,
    @EligibilityDeactivated INT = 0 OUTPUT,
    -- 合计未映射跳过行数
    @UnmappedSkipped INT = 0 OUTPUT,
    @ResourceUnmappedSkipped INT = 0 OUTPUT,
    @DeptUnmappedSkipped INT = 0 OUTPUT,
    -- 错误信息
    @ErrorMessage NVARCHAR(4000) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 初始化输出参数
    SET @OperationInserted = 0;    SET @OperationUpdated = 0;    SET @OperationDeactivated = 0;
    SET @DependencyInserted = 0;   SET @DependencyUpdated = 0;   SET @DependencyDeactivated = 0;
    SET @StageInserted = 0;        SET @StageUpdated = 0;        SET @StageDeactivated = 0;
    SET @EligibilityInserted = 0;  SET @EligibilityUpdated = 0;  SET @EligibilityDeactivated = 0;
    SET @UnmappedSkipped = 0;
    SET @ResourceUnmappedSkipped = 0;
    SET @DeptUnmappedSkipped = 0;
    SET @ErrorMessage = NULL;

    DECLARE @StartTime DATETIME2 = GETDATE();

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ═══════════════════════════════════════════════════════════════
        -- Step 1：构建映射字典（一次性加载）
        -- ═══════════════════════════════════════════════════════════════
        IF OBJECT_ID('tempdb..#MesMaterialMap') IS NOT NULL DROP TABLE #MesMaterialMap;
        CREATE TABLE #MesMaterialMap (
            SourceID   NVARCHAR(100) NOT NULL,
            Source     NVARCHAR(20)  NOT NULL,
            MaterialId INT           NOT NULL,
            PRIMARY KEY (SourceID, Source)
        );

        INSERT INTO #MesMaterialMap (SourceID, Source, MaterialId)
        SELECT CAST(mm.SourceID AS NVARCHAR(100)), mm.[Source], m.Id
        FROM MaterialMapping mm WITH (NOLOCK)
        INNER JOIN Material m WITH (NOLOCK) ON m.MaterialCode = mm.MaterialCode
        WHERE mm.[Source] IN ('ERP', 'MES') AND mm.IsCurrent = 1;

        IF OBJECT_ID('tempdb..#ResourceMap') IS NOT NULL DROP TABLE #ResourceMap;
        CREATE TABLE #ResourceMap (
            ResourceCode NVARCHAR(50) NOT NULL PRIMARY KEY,
            ResourceId   INT          NOT NULL
        );

        INSERT INTO #ResourceMap (ResourceCode, ResourceId)
        SELECT ResourceCode, Id
        FROM Resource WITH (NOLOCK)
        WHERE IsActive = 1;

        IF OBJECT_ID('tempdb..#DeptMap') IS NOT NULL DROP TABLE #DeptMap;
        CREATE TABLE #DeptMap (
            DeptCode              NVARCHAR(50) NOT NULL PRIMARY KEY,
            ProductionDepartmentId INT          NOT NULL
        );

        INSERT INTO #DeptMap (DeptCode, ProductionDepartmentId)
        SELECT DeptCode, Id
        FROM ProductionDepartment WITH (NOLOCK)
        WHERE IsActive = 1;



        -- ═══════════════════════════════════════════════════════════════
        -- Step 2：一次性加载 RoutingOperation 视图到临时表（关键优化）
        -- ═══════════════════════════════════════════════════════════════
        IF OBJECT_ID('tempdb..#RoutingOperationSource') IS NOT NULL DROP TABLE #RoutingOperationSource;
        CREATE TABLE #RoutingOperationSource (
            MES_ID               NVARCHAR(100),
            Source               NVARCHAR(20),
            MaterialId           INT,
            ProductionDeptCode   NVARCHAR(50),
            ProductionDepartmentId INT,
            RouteCode            NVARCHAR(200),
            PathId               INT,
            OperationCode        NVARCHAR(50),
            OperationName        NVARCHAR(200),
            ProcessType          NVARCHAR(50),
            StageCode            NVARCHAR(50),
            StandardDuration     DECIMAL(18,4),
            SetupTime            DECIMAL(18,4),
            IsActive             BIT
        );

        -- 一次性加载并完成映射（避免重复扫描视图）
        INSERT INTO #RoutingOperationSource
        SELECT
            v.MES_ID,
            v.Source,
            mm.MaterialId,
            v.ProductionDeptCode,
            dm.ProductionDepartmentId,
            ISNULL(v.RouteCode, 'DEFAULT'),
            ISNULL(v.PathId, 1),
            v.OperationCode,
            v.OperationName,
            v.ProcessType,
            v.StageCode,
            v.StandardTime,
            ISNULL(v.SetupTime, 0),
            ISNULL(v.IsActive, 1)
        FROM ext_MES_APS_Routing_Operation_View v WITH (NOLOCK)
        LEFT JOIN #MesMaterialMap mm ON mm.SourceID = CAST(v.MES_ID AS NVARCHAR(100)) AND mm.Source = v.Source
        LEFT JOIN #DeptMap dm ON dm.DeptCode = v.ProductionDeptCode
        WHERE ISNULL(v.IsActive, 1) = 1
        OPTION (MAXDOP 4);

        -- 统计未映射（从临时表统计，不再扫描视图）
        DECLARE @UnmappedOp INT, @DeptUnmappedOp INT;
        SELECT @UnmappedOp = COUNT(*) FROM #RoutingOperationSource WHERE MaterialId IS NULL;
        SELECT @DeptUnmappedOp = COUNT(*) FROM #RoutingOperationSource WHERE MaterialId IS NOT NULL AND ProductionDepartmentId IS NULL;

        -- MERGE（只处理映射成功的行）
        DECLARE @OpMerge TABLE (Action NVARCHAR(10), NewIsActive BIT);

        MERGE INTO RoutingOperation AS T
        USING (
            SELECT * FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode ORDER BY MES_ID DESC) AS _rn
                FROM #RoutingOperationSource
                WHERE MaterialId IS NOT NULL AND ProductionDepartmentId IS NOT NULL
            ) x WHERE _rn = 1
        ) AS S
           ON T.MaterialId             = S.MaterialId
          AND T.ProductionDepartmentId = S.ProductionDepartmentId
          AND T.RouteCode              = S.RouteCode
          AND T.PathId                 = S.PathId
          AND T.OperationCode          = S.OperationCode
        WHEN MATCHED AND (
                T.OperationName    <> S.OperationName
             OR T.ProcessType      <> S.ProcessType
             OR ISNULL(T.StageCode,'') <> ISNULL(S.StageCode,'')
             OR T.StandardDuration <> S.StandardDuration
             OR T.SetupTime        <> S.SetupTime
             OR T.IsActive         = 0
        ) THEN UPDATE SET
            OperationName    = S.OperationName,
            ProcessType      = S.ProcessType,
            StageCode        = S.StageCode,
            StandardDuration = S.StandardDuration,
            SetupTime        = S.SetupTime,
            IsActive         = 1,
            UpdatedAt        = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, OperationName, ProcessType, StageCode, StandardDuration, SetupTime, IsActive, CreatedAt, UpdatedAt)
            VALUES (S.MaterialId, S.ProductionDepartmentId, S.RouteCode, S.PathId, S.OperationCode, S.OperationName, S.ProcessType, S.StageCode, S.StandardDuration, S.SetupTime, 1, GETDATE(), GETDATE())
        -- ⚠️ v2.0 优化：去除 WHEN NOT MATCHED BY SOURCE（避免全表扫描）
        -- 软删除改为定期全量对账任务处理
        OUTPUT $action, INSERTED.IsActive INTO @OpMerge(Action, NewIsActive)
        OPTION (MAXDOP 4);

        SELECT
            @OperationInserted    = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
            @OperationUpdated     = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 1 THEN 1 ELSE 0 END), 0),
            @OperationDeactivated = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 0 THEN 1 ELSE 0 END), 0)
        FROM @OpMerge;

        -- ═══════════════════════════════════════════════════════════════
        -- Step 3：一次性加载 RoutingDependency 视图到临时表
        -- ═══════════════════════════════════════════════════════════════
        IF OBJECT_ID('tempdb..#RoutingDependencySource') IS NOT NULL DROP TABLE #RoutingDependencySource;
        CREATE TABLE #RoutingDependencySource (
            MES_ID                NVARCHAR(100),
            Source                NVARCHAR(20),
            MaterialId            INT,
            ProductionDeptCode    NVARCHAR(50),
            ProductionDepartmentId INT,
            RouteCode             NVARCHAR(200),
            PathId                INT,
            FromOperationCode     NVARCHAR(50),
            ToOperationCode       NVARCHAR(50),
            DependencyType        NVARCHAR(10),
            LagTime               DECIMAL(18,4),
            IsActive              BIT
        );

        INSERT INTO #RoutingDependencySource
        SELECT
            v.MES_ID,
            v.Source,
            mm.MaterialId,
            v.ProductionDeptCode,
            dm.ProductionDepartmentId,
            ISNULL(v.RouteCode, 'DEFAULT'),
            ISNULL(v.PathId, 1),
            v.FromOperationCode,
            v.ToOperationCode,
            ISNULL(v.DependencyType, 'ES'),
            ISNULL(v.LagTime, 0),
            ISNULL(v.IsActive, 1)
        FROM ext_MES_APS_Routing_Dependency_View v WITH (NOLOCK)
        LEFT JOIN #MesMaterialMap mm ON mm.SourceID = CAST(v.MES_ID AS NVARCHAR(100)) AND mm.Source = v.Source
        LEFT JOIN #DeptMap dm ON dm.DeptCode = v.ProductionDeptCode
        WHERE ISNULL(v.IsActive, 1) = 1
        OPTION (MAXDOP 4);

        DECLARE @UnmappedDep INT, @DeptUnmappedDep INT;
        SELECT @UnmappedDep = COUNT(*) FROM #RoutingDependencySource WHERE MaterialId IS NULL;
        SELECT @DeptUnmappedDep = COUNT(*) FROM #RoutingDependencySource WHERE MaterialId IS NOT NULL AND ProductionDepartmentId IS NULL;

        DECLARE @DepMerge TABLE (Action NVARCHAR(10), NewIsActive BIT);

        MERGE INTO RoutingDependency AS T
        USING (
            SELECT * FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MaterialId, ProductionDepartmentId, RouteCode, PathId, FromOperationCode, ToOperationCode ORDER BY MES_ID DESC) AS _rn
                FROM #RoutingDependencySource
                WHERE MaterialId IS NOT NULL AND ProductionDepartmentId IS NOT NULL
            ) x WHERE _rn = 1
        ) AS S
           ON T.MaterialId             = S.MaterialId
          AND T.ProductionDepartmentId = S.ProductionDepartmentId
          AND T.RouteCode              = S.RouteCode
          AND T.PathId                 = S.PathId
          AND T.FromOperationCode      = S.FromOperationCode
          AND T.ToOperationCode        = S.ToOperationCode
        WHEN MATCHED AND (
                T.DependencyType <> S.DependencyType
             OR T.LagTime        <> S.LagTime
             OR T.IsActive        = 0
        ) THEN UPDATE SET
            DependencyType = S.DependencyType,
            LagTime        = S.LagTime,
            IsActive       = 1,
            UpdatedAt      = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (MaterialId, ProductionDepartmentId, RouteCode, PathId, FromOperationCode, ToOperationCode, DependencyType, LagTime, IsActive, CreatedAt, UpdatedAt)
            VALUES (S.MaterialId, S.ProductionDepartmentId, S.RouteCode, S.PathId, S.FromOperationCode, S.ToOperationCode, S.DependencyType, S.LagTime, 1, GETDATE(), GETDATE())
        OUTPUT $action, INSERTED.IsActive INTO @DepMerge(Action, NewIsActive)
        OPTION (MAXDOP 4);

        SELECT
            @DependencyInserted    = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
            @DependencyUpdated     = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 1 THEN 1 ELSE 0 END), 0),
            @DependencyDeactivated = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 0 THEN 1 ELSE 0 END), 0)
        FROM @DepMerge;

        -- ═══════════════════════════════════════════════════════════════
        -- Step 4：RoutingStage（数据量相对较小，保持原逻辑）
        -- ═══════════════════════════════════════════════════════════════
        IF OBJECT_ID('tempdb..#RoutingStageSource') IS NOT NULL DROP TABLE #RoutingStageSource;
        CREATE TABLE #RoutingStageSource (
            MES_ID       NVARCHAR(100),
            Source       NVARCHAR(20),
            MaterialId   INT,
            RouteCode    NVARCHAR(200),
            PathId       INT,
            StageCode    NVARCHAR(50),
            StageName    NVARCHAR(200),
            IsOutsource  BIT,
            IsStockPoint BIT,
            IsActive     BIT
        );

        INSERT INTO #RoutingStageSource
        SELECT
            v.MES_ID,
            v.Source,
            mm.MaterialId,
            ISNULL(v.RouteCode, 'DEFAULT'),
            ISNULL(v.PathId, 1),
            v.StageCode,
            v.StageName,
            ISNULL(v.IsOutsource, 0),
            ISNULL(v.IsStockPoint, 0),
            ISNULL(v.IsActive, 1)
        FROM ext_MES_APS_Routing_Stage_View v WITH (NOLOCK)
        LEFT JOIN #MesMaterialMap mm ON mm.SourceID = CAST(v.MES_ID AS NVARCHAR(100)) AND mm.Source = v.Source
        WHERE ISNULL(v.IsActive, 1) = 1;

        DECLARE @UnmappedStage INT;
        SELECT @UnmappedStage = COUNT(*) FROM #RoutingStageSource WHERE MaterialId IS NULL;

        DECLARE @StgMerge TABLE (Action NVARCHAR(10), NewIsActive BIT);

        MERGE INTO RoutingStage AS T
        USING (
            SELECT * FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MaterialId, RouteCode, PathId, StageCode ORDER BY MES_ID DESC) AS _rn
                FROM #RoutingStageSource WHERE MaterialId IS NOT NULL
            ) x WHERE _rn = 1
        ) AS S
           ON T.MaterialId = S.MaterialId
          AND T.RouteCode  = S.RouteCode
          AND T.PathId     = S.PathId
          AND T.StageCode  = S.StageCode
        WHEN MATCHED AND (
                T.StageName    <> S.StageName
             OR T.IsOutsource  <> S.IsOutsource
             OR T.IsStockPoint <> S.IsStockPoint
             OR T.IsActive      = 0
        ) THEN UPDATE SET
            StageName    = S.StageName,
            IsOutsource  = S.IsOutsource,
            IsStockPoint = S.IsStockPoint,
            IsActive     = 1,
            UpdatedAt    = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (MaterialId, RouteCode, PathId, StageCode, StageName, IsOutsource, IsStockPoint, IsActive, CreatedAt, UpdatedAt)
            VALUES (S.MaterialId, S.RouteCode, S.PathId, S.StageCode, S.StageName, S.IsOutsource, S.IsStockPoint, 1, GETDATE(), GETDATE())
        OUTPUT $action, INSERTED.IsActive INTO @StgMerge(Action, NewIsActive);

        SELECT
            @StageInserted    = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
            @StageUpdated     = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 1 THEN 1 ELSE 0 END), 0),
            @StageDeactivated = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 0 THEN 1 ELSE 0 END), 0)
        FROM @StgMerge;

        -- ═══════════════════════════════════════════════════════════════
        -- Step 5：一次性加载 OperationResourceEligibility 视图到临时表
        -- ═══════════════════════════════════════════════════════════════
        IF OBJECT_ID('tempdb..#EligibilitySource') IS NOT NULL DROP TABLE #EligibilitySource;
        CREATE TABLE #EligibilitySource (
            MES_ID                NVARCHAR(100),
            Source                NVARCHAR(20),
            MaterialId            INT,
            ProductionDeptCode    NVARCHAR(50),
            ProductionDepartmentId INT,
            ResourceCode          NVARCHAR(50),
            ResourceId            INT,
            RouteCode             NVARCHAR(200),
            PathId                INT,
            OperationCode         NVARCHAR(50),
            Priority              INT,
            CapacityFactor        DECIMAL(18,4),
            IsActive              BIT
        );

        INSERT INTO #EligibilitySource
        SELECT
            v.MES_ID,
            v.Source,
            mm.MaterialId,
            v.ProductionDeptCode,
            dm.ProductionDepartmentId,
            v.ResourceCode,
            rm.ResourceId,
            ISNULL(v.RouteCode, 'DEFAULT'),
            ISNULL(v.PathId, 1),
            v.OperationCode,
            ISNULL(v.Priority, 1),
            ISNULL(v.CapacityFactor, 1.0),
            ISNULL(v.IsActive, 1)
        FROM ext_APS_OperationResourceEligibility_View v WITH (NOLOCK)
        LEFT JOIN #MesMaterialMap mm ON mm.SourceID = CAST(v.MES_ID AS NVARCHAR(100)) AND mm.Source = v.Source
        LEFT JOIN #DeptMap dm ON dm.DeptCode = v.ProductionDeptCode
        LEFT JOIN #ResourceMap rm ON rm.ResourceCode = v.ResourceCode
        WHERE ISNULL(v.IsActive, 1) = 1
        OPTION (MAXDOP 4);

        DECLARE @UnmappedElig INT, @DeptUnmappedElig INT;
        SELECT @UnmappedElig = COUNT(*) FROM #EligibilitySource WHERE MaterialId IS NULL;
        SELECT @ResourceUnmappedSkipped = COUNT(*) FROM #EligibilitySource WHERE MaterialId IS NOT NULL AND ResourceId IS NULL;
        SELECT @DeptUnmappedElig = COUNT(*) FROM #EligibilitySource WHERE MaterialId IS NOT NULL AND ProductionDepartmentId IS NULL;

        DECLARE @EligMerge TABLE (Action NVARCHAR(10), NewIsActive BIT);

        MERGE INTO OperationResourceEligibility AS T
        USING (
            SELECT * FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, ResourceId ORDER BY MES_ID DESC) AS _rn
                FROM #EligibilitySource
                WHERE MaterialId IS NOT NULL
                  AND ProductionDepartmentId IS NOT NULL
                  AND ResourceId IS NOT NULL
            ) x WHERE _rn = 1
        ) AS S
           ON T.MaterialId             = S.MaterialId
          AND T.ProductionDepartmentId = S.ProductionDepartmentId
          AND T.RouteCode              = S.RouteCode
          AND T.PathId                 = S.PathId
          AND T.OperationCode          = S.OperationCode
          AND T.ResourceId             = S.ResourceId
        WHEN MATCHED AND (
                T.Priority       <> S.Priority
             OR T.CapacityFactor <> S.CapacityFactor
             OR T.IsActive        = 0
        ) THEN UPDATE SET
            Priority       = S.Priority,
            CapacityFactor = S.CapacityFactor,
            IsActive       = 1,
            UpdatedAt      = GETDATE()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, ResourceId, Priority, CapacityFactor, IsActive, EffectiveFrom, CreatedAt, UpdatedAt)
            VALUES (S.MaterialId, S.ProductionDepartmentId, S.RouteCode, S.PathId, S.OperationCode, S.ResourceId, S.Priority, S.CapacityFactor, 1, CAST(GETDATE() AS DATE), GETDATE(), GETDATE())
        OUTPUT $action, INSERTED.IsActive INTO @EligMerge(Action, NewIsActive)
        OPTION (MAXDOP 4);

        SELECT
            @EligibilityInserted    = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
            @EligibilityUpdated     = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 1 THEN 1 ELSE 0 END), 0),
            @EligibilityDeactivated = ISNULL(SUM(CASE WHEN Action = 'UPDATE' AND NewIsActive = 0 THEN 1 ELSE 0 END), 0)
        FROM @EligMerge;

        -- ═══════════════════════════════════════════════════════════════
        -- Step 6：汇总统计 + 写日志
        -- ═══════════════════════════════════════════════════════════════
        SET @UnmappedSkipped = ISNULL(@UnmappedOp, 0) + ISNULL(@UnmappedDep, 0) + ISNULL(@UnmappedStage, 0) + ISNULL(@UnmappedElig, 0);
        SET @DeptUnmappedSkipped = ISNULL(@DeptUnmappedOp, 0) + ISNULL(@DeptUnmappedDep, 0) + ISNULL(@DeptUnmappedElig, 0);

        DECLARE @ElapsedMs INT = DATEDIFF(MILLISECOND, @StartTime, GETDATE());

        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            @BatchNo,
            'sp_SyncRoutingData_v2.0',
            CONCAT(
                'Op(I/U/D)=',  @OperationInserted,  '/', @OperationUpdated,  '/', @OperationDeactivated,
                ', Dep(I/U/D)=', @DependencyInserted, '/', @DependencyUpdated, '/', @DependencyDeactivated,
                ', Stage(I/U/D)=', @StageInserted,  '/', @StageUpdated,    '/', @StageDeactivated,
                ', Elig(I/U/D)=', @EligibilityInserted, '/', @EligibilityUpdated, '/', @EligibilityDeactivated,
                ', Unmapped=', @UnmappedSkipped,
                ', ResUnmapped=', @ResourceUnmappedSkipped,
                ', DeptUnmapped=', @DeptUnmappedSkipped,
                ', Elapsed=', @ElapsedMs, 'ms'
            ),
            'SUCCESS',
            GETDATE()
        );

        -- 清理临时表
        DROP TABLE IF EXISTS #MesMaterialMap;
        DROP TABLE IF EXISTS #ResourceMap;
        DROP TABLE IF EXISTS #DeptMap;
        DROP TABLE IF EXISTS #RoutingOperationSource;
        DROP TABLE IF EXISTS #RoutingDependencySource;
        DROP TABLE IF EXISTS #RoutingStageSource;
        DROP TABLE IF EXISTS #EligibilitySource;

        COMMIT TRANSACTION;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMessage = CONCAT(
            'sp_SyncRoutingData_v2.0 失败 [Error ', ERROR_NUMBER(), ' Line ', ERROR_LINE(), ']: ',
            ERROR_MESSAGE()
        );

        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (@BatchNo, 'sp_SyncRoutingData_v2.0', @ErrorMessage, 'FAILED', GETDATE());
    END CATCH
END;
GO

-- =============================================
-- 性能优化说明
-- =============================================
/*
v2.0 主要优化点：

1. 【关键】一次性加载视图到临时表
   - 避免重复扫描：80万行 × 3次 → 80万行 × 1次
   - 预估节省时间：15-20分钟

2. 【关键】去除 WHEN NOT MATCHED BY SOURCE
   - 避免全表扫描目标表（80万行）
   - 软删除改为定期全量对账任务
   - 预估节省时间：5-10分钟

3. 增加 MAXDOP 4 提示
   - 利用多核并行处理
   - 预估提升：20-30%

4. 增加 WITH (NOLOCK) 提示
   - 减少锁等待
   - 允许脏读（主数据同步场景可接受）

5. 显式事务控制
   - 确保数据一致性
   - 失败时完整回滚

预估性能：
- v1.0: 15-30分钟
- v2.0: 3-5分钟
- 提升：5-10倍

后续优化方向（如果仍不满足）：
- 分批处理（按MaterialId分批）
- 增量同步（只同步变更数据）
- 异步并行（4个表并行同步）
*/
