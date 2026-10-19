namespace LPS.APS.Core.Dto;

/// <summary>
/// 订单查询列表DTO
///
/// ⚠️ JSON 键名（跨语言调用方必读）：`Program.cs` 全局使用 <c>JsonNamingPolicy.CamelCase</c>
/// + <c>WhenWritingNull</c>（null 字段**整个键省略**，不是返回 null）。
/// CamelCase 策略按「词」切分，**全大写缩写段只小写首字母**，故：
///   <c>MTS_InstructionNo</c> → <c>mtS_InstructionNo</c>（**不是** mts_InstructionNo）
///   <c>BOMNO</c> → <c>bomno</c>；<c>UOM</c> → <c>uom</c>
/// 新增任何 MTS_/ERP_/APS_ 前缀属性都会遵循同一规律，请同步更新本文档与前端类型。
/// </summary>
public sealed class OrderListItemDto
{
    public long Id { get; init; }
    public int PlanVersionId { get; init; }

    /// <summary>订单规范化 ID（v5.0.34；跨 PlanVersion/Domain 稳定，EXPEDITE §10A.1 D00 需求订单标识，B4 补齐透传）。JSON: orderCanonicalId</summary>
    public long? OrderCanonicalId { get; init; }

    /// <summary>JSON: orderNo</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>JSON: orderType（实库值 PRODUCTION_INSTRUCTION / SALES_ORDER，非受控枚举）</summary>
    public string OrderType { get; init; } = string.Empty;

    /// <summary>JSON: materialCode</summary>
    public string MaterialCode { get; init; } = string.Empty;

    /// <summary>JSON: materialId</summary>
    public int MaterialId { get; init; }

    /// <summary>客户名称（v24 有值率 59.9%；缺失源于 ERP 源头未带）。JSON: customerName</summary>
    public string? CustomerName { get; init; }

    /// <summary>JSON: customerSegment</summary>
    public string? CustomerSegment { get; init; }

    /// <summary>JSON: factoryCode</summary>
    public string? FactoryCode { get; init; }

    /// <summary>JSON: factoryId</summary>
    public int FactoryId { get; init; }

    /// <summary>JSON: productFamilyCode</summary>
    public string? ProductFamilyCode { get; init; }

    /// <summary>JSON: domainKey</summary>
    public string? DomainKey { get; init; }

    /// <summary>JSON: quantity</summary>
    public decimal Quantity { get; init; }

    /// <summary>JSON: uom</summary>
    public string UOM { get; init; } = string.Empty;

    /// <summary>客户交期。JSON: customerDueDate</summary>
    public DateTime CustomerDueDate { get; init; }

    /// <summary>
    /// APS 承诺交期（冻结 07:3608，属 APS 计算产物）。
    /// ⚠️ 当前无写入方，实库恒 NULL ⇒ 受 WhenWritingNull 影响，**该键在响应中不出现**。JSON: promisedDate
    /// </summary>
    public DateTime? PromisedDate { get; init; }

    /// <summary>JSON: priority</summary>
    public int Priority { get; init; }

    /// <summary>
    /// 订单状态（<c>[Order].Status</c>）。两表属不同语义层，各按自身冻结值域：
    /// 07:3506/§4.1（<c>Order_Canonical</c>，ERP 准入）= 三值 <c>OPEN/CLOSED/CANCELLED</c>；
    /// 07:3611/§4.3（<c>[Order]</c>，APS 订单生命周期）= 六值 <c>NEW/PLANNED/CONFIRMED/IN_PROGRESS/COMPLETED/CANCELLED</c>。
    /// 07:3521 的禁令限定于 <c>Order_Canonical</c> 的当前准入判断，不约束 <c>[Order]</c> 六值域。
    /// 写入：<c>sp_SyncOrdersToPartitionTable</c> 只搬 <c>WHERE oc.Status='OPEN'</c> 的行，并显式写 <c>N'PLANNED'</c>（不再透传 oc.Status）；
    /// 其余五值当前无产出路径 ⇒ 筛选按 <c>PLANNED</c> 与历史 <c>Open</c> 渲染，勿为未实现状态建下拉。
    /// 实库事实：新行 <c>PLANNED</c>；PV2/PV5/PV24 历史行 <c>Open</c>（CI 等价，不再批量改写）；PV157 一行 <c>CONFIRMED</c>（集成测试夹具残留）。
    /// JSON: status
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>延迟状态。冻结 07:2666 值域 ON_TIME/FIRST_DELAY/REPEATED_DELAY（V1 超期均记 FIRST_DELAY）。JSON: delayStatus</summary>
    public string? DelayStatus { get; init; }

    /// <summary>
    /// 需求成熟度。冻结 07:244 明确 **V1 严格 NULL：禁止从任何字段临时推断**。
    /// ⇒ 实库恒 NULL，该键在响应中**永不出现**，调用方勿据此建列。JSON: demandMaturityStatus
    /// </summary>
    public string? DemandMaturityStatus { get; init; }

    /// <summary>
    /// 生产指示号（PI 物理身份）。
    /// ⚠️ JSON 键为 <c>mtS_InstructionNo</c>（CamelCase 对 MTS_ 前缀只小写首字母），**不是** mts_InstructionNo。
    /// </summary>
    public string? MTS_InstructionNo { get; init; }

    /// <summary>BOM 号。JSON: bomno</summary>
    public string? BOMNO { get; init; }

    /// <summary>
    /// APS 预计完成时间 = 该订单在当前版本全部 Task 的 MAX(PlannedEndTime)（后端算，冻结 01:1908 第 2 项；
    /// 13 §十一 前端禁算排程）。无 Task/未排 → null（WhenWritingNull 下键省略）。JSON: plannedCompletionTime
    /// </summary>
    public DateTime? PlannedCompletionTime { get; init; }

    /// <summary>
    /// 延期时长（自然小时）：仅当 预计完成日期 > CustomerDueDate（Q1 日期口径，与甘特/summary/candidate 同判据家族）
    /// 时有值；未延期/未排 = null（两态不混）。JSON: delayHours
    /// </summary>
    public decimal? DelayHours { get; init; }

    /// <summary>
    /// 主要原因码 = 该订单 Explanation 事实中 ImpactHours 最大的非空 ReasonCode（07 §八.1 词表；
    /// 空码系已登记联调形态（10-12 裁决），不当原因）。null = 无可用原因（≠无问题）。JSON: primaryReasonCode
    /// </summary>
    public string? PrimaryReasonCode { get; init; }
}

/// <summary>
/// 订单详情DTO（含Pegging和生产计划）
/// </summary>
public sealed class OrderDetailDto
{
    /// <summary>订单基本信息</summary>
    public OrderListItemDto Order { get; init; } = new();

    /// <summary>Pegging承接列表</summary>
    public List<OrderPeggingDto> Pegging { get; init; } = new();

    /// <summary>生产计划（FinalTask）列表</summary>
    public List<OrderTaskDto> Tasks { get; init; } = new();
}

/// <summary>
/// 订单Pegging承接DTO
/// </summary>
public sealed class OrderPeggingDto
{
    public long AllocationSequence { get; init; }
    public string MaterialCode { get; init; } = string.Empty;
    public decimal AllocatedQty { get; init; }
    public string SupplyType { get; init; } = string.Empty;
    public string? SupplyFactoryCode { get; init; }
    public string? SupplyWarehouseCode { get; init; }
    public string? ERPProperty { get; init; }
    public string? SupplyDocumentNo { get; init; }
    public string? SupplyDocumentType { get; init; }
    public DateTime? ETA { get; init; }
    public DateTime? KnownAvailableTime { get; init; }
    public string? CommitmentStatus { get; init; }
    public string? SupplyMode { get; init; }
}

/// <summary>
/// 订单生产计划（FinalTask）DTO
/// </summary>
public sealed class OrderTaskDto
{
    public long TaskId { get; init; }
    public string TaskNo { get; init; } = string.Empty;
    public string? OperationCode { get; init; }
    public int OperationSeq { get; init; }
    public string? ResourceCode { get; init; }
    public string? ResourceName { get; init; }
    public decimal Quantity { get; init; }
    public decimal? PlannedProcessQty { get; init; }
    public DateTime? PlannedStartTime { get; init; }
    public DateTime? PlannedEndTime { get; init; }
    public decimal? Duration { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsCriticalPath { get; init; }
    public bool IsLocked { get; init; }
    public string? MTS_InstructionNo { get; init; }
}
