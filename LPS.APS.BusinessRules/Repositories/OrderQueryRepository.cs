using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 订单查询Repository实现
/// 直接读取[Order]、PeggingSupplyAllocation、Task表
/// </summary>
public class OrderQueryRepository : IOrderQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public OrderQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null)
    {
        // Dapper 列表参数只能出现在 IN 内；@X IS NULL(标量) 会随 @X 一起扩成 (@p1,@p2) → "(@p1,@p2) IS NULL" 非法 SQL 4145。
        // 用 Has 标志替代标量判空；空集 fail-closed 返空，避免空集传入 IN ()。
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new PageResult<OrderListItemDto> { Items = Array.Empty<OrderListItemDto>(), Total = 0, Page = 1, PageSize = take };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        // 共用 FROM / WHERE：列表与 COUNT 的 FROM+WHERE 必须逐字一致，否则 total 与 items 对不上。
        // （10-19 拆分：为「冻结 01:1908 三字段」加的 OUTER APPLY 只挂明细查询，不污染 COUNT 性能与行数语义。）
        const string fromJoin = @"
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN ProductFamily pf ON pf.Id = o.ProductFamilyId";

        const string wherePart = @"
WHERE o.PlanVersionId = @PlanVersionId
    AND (@OrderNo IS NULL OR o.OrderNo LIKE '%' + @OrderNo + '%')
    AND (@MaterialCode IS NULL OR o.MaterialCode LIKE '%' + @MaterialCode + '%')
    AND (@CustomerName IS NULL OR o.CustomerName LIKE '%' + @CustomerName + '%')
    AND (@FactoryCode IS NULL OR f.Code = @FactoryCode)
    AND (@DomainKey IS NULL OR o.DomainKey = @DomainKey)
    AND (@DelayStatus IS NULL OR o.DelayStatus = @DelayStatus)
    AND (@Status IS NULL OR o.Status = @Status)
    AND (@ProductionInstructionNo IS NULL OR o.MTS_InstructionNo LIKE '%' + @ProductionInstructionNo + '%')
    AND (@ProductFamilyCode IS NULL OR pf.Code = @ProductFamilyCode)
    AND (@DueDateFrom IS NULL OR o.CustomerDueDate >= @DueDateFrom)
    AND (@DueDateTo IS NULL OR o.CustomerDueDate <= @DueDateTo)
    AND (@HasFactories = 0 OR f.Code IN @AllowedFactories)
    AND (@HasDomains = 0 OR o.DomainKey IN @AllowedDomains)";

        // 冻结 01:1908 第 2 项：预计完成时间 / 延期时长 / 主要原因（后端算，前端不得自算）。
        // 延期判定走 Q1 日期口径；PrimaryReasonCode 只取非空码（ExplanationFact 约 80% ReasonCode=''
        // 系已登记的联调临时形态，10-12 裁决保留不清历史——空码不当原因、也不造假）。
        const string deliveryApply = @"
OUTER APPLY (SELECT MAX(t.PlannedEndTime) AS PlannedCompletion
             FROM [Task] t WHERE t.OrderId = o.Id AND t.PlanVersionId = o.PlanVersionId) dc
OUTER APPLY (SELECT TOP 1 f.ReasonCode AS PrimaryReasonCode
             FROM ScheduleExplanationFact f
             WHERE f.PlanVersionId = o.PlanVersionId AND f.OrderId = o.Id
               AND f.ReasonCode IS NOT NULL AND f.ReasonCode <> ''
             ORDER BY f.ImpactHours DESC) rs";

        var dataSql = @"
SELECT
    o.Id,
    o.PlanVersionId,
    o.OrderCanonicalId,
    o.OrderNo,
    o.OrderType,
    o.MaterialCode,
    o.MaterialId,
    o.CustomerName,
    o.CustomerSegment,
    f.Code AS FactoryCode,
    o.FactoryId,
    pf.Code AS ProductFamilyCode,
    o.DomainKey,
    o.Quantity,
    o.UOM,
    o.CustomerDueDate,
    o.PromisedDate,
    o.Priority,
    o.Status,
    o.DelayStatus,
    o.DemandMaturityStatus,
    o.MTS_InstructionNo,
    o.BOMNO,
    dc.PlannedCompletion     AS PlannedCompletionTime,
    CASE WHEN dc.PlannedCompletion IS NOT NULL
          AND o.CustomerDueDate IS NOT NULL
          AND CAST(dc.PlannedCompletion AS date) > o.CustomerDueDate
         THEN DATEDIFF(MINUTE, CAST(o.CustomerDueDate AS datetime2), dc.PlannedCompletion) / 60.0
         ELSE NULL END       AS DelayHours,
    rs.PrimaryReasonCode     AS PrimaryReasonCode"
            + fromJoin + deliveryApply + wherePart + @"
ORDER BY o.Priority DESC, o.CustomerDueDate
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var countSql = "SELECT COUNT(*)" + fromJoin + wherePart;

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderNo = orderNo,
            MaterialCode = materialCode,
            CustomerName = customerName,
            FactoryCode = factoryCode,
            DomainKey = domainKey,
            DelayStatus = delayStatus,
            Status = status,
            ProductionInstructionNo = productionInstructionNo,
            ProductFamilyCode = productFamilyCode,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains,
            Skip = skip,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<OrderListItemDto>(
            dataSql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            countSql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return new PageResult<OrderListItemDto>
        {
            Items = results.ToList(),
            Total = total,
            Page = take > 0 ? (skip / take) + 1 : 1,
            PageSize = take
        };
    }

    public async Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default)
    {
        // 1. 查询订单基本信息
        var orderSql = @"
SELECT
    o.Id,
    o.PlanVersionId,
    o.OrderCanonicalId,
    o.OrderNo,
    o.OrderType,
    o.MaterialCode,
    o.MaterialId,
    o.CustomerName,
    o.CustomerSegment,
    f.Code AS FactoryCode,
    o.FactoryId,
    pf.Code AS ProductFamilyCode,
    o.DomainKey,
    o.Quantity,
    o.UOM,
    o.CustomerDueDate,
    o.PromisedDate,
    o.Priority,
    o.Status,
    o.DelayStatus,
    o.DemandMaturityStatus,
    o.MTS_InstructionNo,
    o.BOMNO
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN ProductFamily pf ON pf.Id = o.ProductFamilyId
WHERE o.PlanVersionId = @PlanVersionId AND o.Id = @OrderId";

        var orderParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var order = (await _connectionManager.QueryAsync<OrderListItemDto>(
            orderSql, orderParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .FirstOrDefault();

        if (order == null) return null;

        // 2. 查询Pegging承接
        var peggingSql = @"
SELECT
    AllocationSequence,
    MaterialCode,
    AllocatedQty,
    SupplyType,
    SupplyFactoryCode,
    SupplyWarehouseCode,
    ERPProperty,
    SupplyDocumentNo,
    SupplyDocumentType,
    ETA,
    KnownAvailableTime,
    CommitmentStatus,
    SupplyMode
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId
    AND (RootOrderId = @OrderId OR CurrentOrderId = @OrderId)
ORDER BY AllocationSequence";

        var peggingParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var pegging = (await _connectionManager.QueryAsync<OrderPeggingDto>(
            peggingSql, peggingParams, CommandType.Text, DatabaseId.APS, commandTimeout: 30))
            .ToList();

        // 3. 查询生产计划（FinalTask）
        var taskSql = @"
SELECT
    t.Id AS TaskId,
    t.TaskNo,
    t.OperationCode,
    t.OperationSeq,
    r.ResourceCode,
    r.ResourceName,
    t.Quantity,
    t.PlannedProcessQty,
    t.PlannedStartTime,
    t.PlannedEndTime,
    t.Duration,
    t.Status,
    t.IsCriticalPath,
    t.IsLocked,
    t.MTS_InstructionNo
FROM Task t
LEFT JOIN Resource r ON r.Id = t.ResourceId
WHERE t.PlanVersionId = @PlanVersionId AND t.OrderId = @OrderId
ORDER BY t.OperationSeq";

        var taskParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var tasks = (await _connectionManager.QueryAsync<OrderTaskDto>(
            taskSql, taskParams, CommandType.Text, DatabaseId.APS, commandTimeout: 30))
            .ToList();

        return new OrderDetailDto
        {
            Order = order,
            Pegging = pegging,
            Tasks = tasks
        };
    }

    public async Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        // Dapper 列表参数只进 IN；空集 fail-closed 返空摘要。
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new OrderSummaryDto { PlanVersionId = planVersionId };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        // 口径修正（2026-10-17，4号位 联调缺口）：
        //  ① OnTimeCount 原把 DelayStatus IS NULL 也计入按期 → 全表 DelayStatus 为 NULL 时虚报"全部按期"。
        //     冻结 07:2666 值域 ON_TIME/FIRST_DELAY/REPEATED_DELAY 不含 NULL；NULL = 未计算，不得算按期。
        //  ② RiskCount 原查 DelayStatus='RISK' —— 该值不在 DelayStatus 值域内，恒 0。
        //     风险维度属 07:4965 OrderScheduleSummary.RiskLevel（ON_TRACK/AT_RISK/DELAYED），改从该表取。
        //  ③ UnscheduledCount 原查 Status='UNSCHEDULED' —— 该值**不在两表任一冻结值域**（Order_Canonical 三值 07:3506 /
        //     [Order] 六值 07:3611 均无 UNSCHEDULED），恒 0。两表值域系两个语义层、不互斥——0号位第二十轮（10-17）已裁
        //     （07:3521 禁令限定对象=Order_Canonical+当前准入判断；本注释 10-19 曾误判"冻结内部冲突已转裁决"，据既有裁定撤回，见 2号位 10-19 回函 §1）。
        //     "未排"无权威字段口径 ⇒ 返回 0，不造假。
        var sql = @"
SELECT
    @PlanVersionId AS PlanVersionId,   -- 回填入参（此前漏挑该列 → Dapper 填默认 0，4号位 10-14 R5 报）
    COUNT(*) AS TotalCount,
    SUM(CASE WHEN o.DelayStatus = 'ON_TIME' THEN 1 ELSE 0 END) AS OnTimeCount,
    SUM(CASE WHEN o.DelayStatus IN ('FIRST_DELAY', 'REPEATED_DELAY') THEN 1 ELSE 0 END) AS DelayedCount,
    (SELECT COUNT(*)
       FROM OrderScheduleSummary s
       INNER JOIN [Order] o2 ON o2.Id = s.OrderId AND o2.PlanVersionId = s.PlanVersionId
       LEFT JOIN Factory f2 ON f2.Id = o2.FactoryId
      WHERE s.PlanVersionId = @PlanVersionId
        AND s.RiskLevel IN ('AT_RISK', 'DELAYED')
        AND (@HasFactories = 0 OR f2.Code IN @AllowedFactories)
        AND (@HasDomains = 0 OR o2.DomainKey IN @AllowedDomains)) AS RiskCount,
    CAST(0 AS INT) AS UnscheduledCount
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
WHERE o.PlanVersionId = @PlanVersionId
    AND (@HasFactories = 0 OR f.Code IN @AllowedFactories)
    AND (@HasDomains = 0 OR o.DomainKey IN @AllowedDomains)";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains
        };

        var result = await _connectionManager.QueryFirstOrDefaultAsync<OrderSummaryDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        return result ?? new OrderSummaryDto { PlanVersionId = planVersionId };
    }
}
