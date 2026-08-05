namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 非 Task 类型的供应分配记录（步骤2.8）
/// 记录 ERP库存、MES在制品、外协供应的分配关系
/// 对应文档：PeggingSupplyAllocation 表
/// </summary>
public class PeggingSupplyAllocation
{
    /// <summary>
    /// 主键
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 计划版本ID（分区键）
    /// </summary>
    public int PlanVersionId { get; set; }

    /// <summary>
    /// 需求侧订单ID
    /// </summary>
    public long OrderId { get; set; }

    /// <summary>
    /// 需求侧物料ID
    /// </summary>
    public int DemandMaterialId { get; set; }

    /// <summary>
    /// 供应侧物料ID
    /// </summary>
    public int SupplyMaterialId { get; set; }

    /// <summary>
    /// 分配数量
    /// </summary>
    public decimal AllocatedQuantity { get; set; }

    /// <summary>
    /// 单位
    /// </summary>
    public string UOM { get; set; } = string.Empty;

    /// <summary>
    /// 供应来源类型（对应 SupplySourceType 枚举的字符串值）
    /// 定稿口径：INVENTORY | WIP | PIPELINE | INTER_FACTORY_ORDER | PRODUCTION_INSTRUCTION | PURCHASE_ORDER
    /// NEW_REQUIREMENT 不写此表，走物理 Pegging 表（Task-to-Task 血缘）
    /// </summary>
    public string SupplySourceType { get; set; } = string.Empty;

    /// <summary>
    /// 供应来源引用ID（可选，按来源类型含义不同）
    /// - INVENTORY: InventoryBalance.Id
    /// - WIP: MES 在制工单内部ID（字符串工单号存 SourceReference）
    /// - PIPELINE: 在途单内部ID
    /// - PRODUCTION_INSTRUCTION: ERP Received 记录ID
    /// - PURCHASE_ORDER: 采购订单行ID
    /// </summary>
    public long? SupplySourceId { get; set; }

    /// <summary>
    /// 供应来源引用（字符串类型，用于 MES 工单号等）
    /// </summary>
    public string? SourceReference { get; set; }

    /// <summary>
    /// 工厂代码
    /// </summary>
    public string FactoryCode { get; set; } = string.Empty;

    /// <summary>
    /// 仓库代码（ERP 库存场景）
    /// </summary>
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// 库位代码（ERP 库存场景）
    /// </summary>
    public string? LocationCode { get; set; }

    /// <summary>
    /// 批次号（FEFO 策略时使用）
    /// </summary>
    public string? BatchNumber { get; set; }

    /// <summary>
    /// 到期日期（FEFO 策略时使用）
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// 分配优先级（数字越小优先级越高）
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 分配时间戳（供应可用时间）
    /// </summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>
    /// 是否已消耗（用于库存扣减标记）
    /// </summary>
    public bool IsConsumed { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    public string? Remarks { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 导航属性：关联的计划版本
    /// </summary>
    public PlanVersion? PlanVersion { get; set; }
}
