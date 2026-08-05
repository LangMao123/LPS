-- =============================================
-- sp_GetActiveRootBOMNOs v5.0.24
-- 对齐变更：
--   1. OrderType 枚举改为 SALES_ORDER / PRODUCTION_INSTRUCTION（v5.0.24）
--   2. 移除 BOMNO IS NOT NULL 过滤（v5.0.21: BOMNO可空，无BOMNO订单也纳入活跃集合）
--   3. TotalOrderCount 统计含无BOMNO订单
--   4. 步骤2 仍按有BOMNO的订单做BOMNO聚合（RootCount = 去重BOMNO数，用于日志统计）
-- =============================================
ALTER PROCEDURE [dbo].[sp_GetActiveRootBOMNOs]
    @StartDate       DATE = NULL,
    @EndDate         DATE = NULL,
    @RootCount       INT = 0 OUTPUT,
    @TotalOrderCount INT = 0 OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProcessTime DATETIME2 = GETDATE();
    DECLARE @ErrorMessage NVARCHAR(MAX);

    -- 默认计划窗口：今天起90天
    IF @StartDate IS NULL
        SET @StartDate = CAST(GETDATE() AS DATE);
    IF @EndDate IS NULL
        SET @EndDate = DATEADD(DAY, 90, @StartDate);

    SET @RootCount = 0;
    SET @TotalOrderCount = 0;

    BEGIN TRY
        -- ═══════════════════════════════════════════════════════════════
        -- 步骤1：统计活跃订单总数（含无BOMNO订单）
        -- PRODUCTION_INSTRUCTION 不做日期过滤，SALES_ORDER 按交期窗口
        -- ═══════════════════════════════════════════════════════════════
        SELECT @TotalOrderCount = COUNT(*)
        FROM Order_Canonical
        WHERE Status IN ('Open', 'Released')
          AND (
              (OrderType = 'SALES_ORDER'
               AND DueDate BETWEEN @StartDate AND @EndDate)
              OR
              (OrderType = 'PRODUCTION_INSTRUCTION')
          );

        -- ═══════════════════════════════════════════════════════════════
        -- 步骤2：划定活跃根集合（按BOMNO去重聚合，仅统计有BOMNO的）
        -- 用于日志统计 RootCount；实际推送Detail已改为订单粒度（C#侧）
        -- ═══════════════════════════════════════════════════════════════
        SELECT oc.BOMNO,
               COUNT(*)         AS OrderCount,
               SUM(oc.Quantity) AS TotalQuantity,
               MIN(oc.DueDate)  AS EarliestDueDate,
               MAX(oc.DueDate)  AS LatestDueDate
        FROM Order_Canonical oc
        WHERE oc.Status IN ('Open', 'Released')
          AND oc.BOMNO IS NOT NULL
          AND oc.BOMNO <> ''
          AND (
              (oc.OrderType = 'SALES_ORDER'
               AND oc.DueDate BETWEEN @StartDate AND @EndDate)
              OR
              (oc.OrderType = 'PRODUCTION_INSTRUCTION')
          )
        GROUP BY oc.BOMNO
        ORDER BY MIN(oc.DueDate), oc.BOMNO;

        SET @RootCount = @@ROWCOUNT;

        -- ═══════════════════════════════════════════════════════════════
        -- 步骤3：记录ETL日志
        -- ═══════════════════════════════════════════════════════════════
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            FORMAT(@ProcessTime, 'yyyyMMdd_HHmmss'),
            'sp_GetActiveRootBOMNOs',
            N'活跃根集合划定完成 | BOMNO数:' + CAST(@RootCount AS NVARCHAR(10))
                + N' | 订单数:' + CAST(@TotalOrderCount AS NVARCHAR(10))
                + N' | 窗口:' + CONVERT(NVARCHAR(10), @StartDate, 120)
                + N'~' + CONVERT(NVARCHAR(10), @EndDate, 120),
            N'SUCCESS',
            GETDATE()
        );
    END TRY
    BEGIN CATCH
        SET @ErrorMessage = ERROR_MESSAGE();
        INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
        VALUES (
            FORMAT(@ProcessTime, 'yyyyMMdd_HHmmss'),
            'sp_GetActiveRootBOMNOs',
            N'活跃根集合划定失败: ' + @ErrorMessage,
            N'FAILED',
            GETDATE()
        );
        THROW;
    END CATCH
END;
GO
