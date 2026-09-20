namespace LPS.APS.Core.Dto;

/// <summary>
/// 工序进度事实（2号位提供给5号位的输入数据）
///
/// 来源：OperationProgressSnapshot表
/// V1工序识别主字段：OperationName（MES工序名称）
/// </summary>
public sealed class OperationProgressFact
{
    /// <summary>
    /// 工序代码（MES工序编码，不跨大工艺稳定，仅作辅助）
    /// </summary>
    public string OperationCode { get; init; } = string.Empty;

    /// <summary>
    /// 工序名称（MES工序名称，V1工序识别主字段）
    /// </summary>
    public string OperationName { get; init; } = string.Empty;

    /// <summary>
    /// 所属Stage代码
    /// </summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>
    /// MES工单号（同一PI可1:N分批）
    ///
    /// 来源：OperationProgressSnapshot.MESWorkOrderNo
    /// 用途：5号位 ExistingExecutionContext 按工单独立输出连续份额，不合并多工单。
    /// </summary>
    public string MESWorkOrderNo { get; init; } = string.Empty;

    /// <summary>
    /// 工序计划数量
    ///
    /// 来源：OperationProgressSnapshot.PlannedQty
    /// 用途：DerivedRemainingQty = max(PlannedQty - GoodQty, 0) 运行时派生。
    /// </summary>
    public decimal PlannedQty { get; init; }

    /// <summary>
    /// 工序累计良品完成数量（GoodQty，冻结字段名，不得改名）
    ///
    /// 来源：OperationProgressSnapshot.GoodQty
    /// 5号位 PI Position/NextOperationContext 计算器消费此字段算执行起点。
    /// </summary>
    public decimal GoodQty { get; init; }

    /// <summary>
    /// 工序剩余数量（= max(PlannedQty - GoodQty, 0)）
    ///
    /// 来源：OperationProgressSnapshot.RemainingQty（PERSISTED 计算列）
    /// 用途：5号位 ExistingExecutionContext 逐工单 DerivedRemainingQty 派生。
    /// ⚠️ 只存在于进度快照层，不新增到工单级（28-0 v2.6 §十七 冻结）。
    /// </summary>
    public decimal RemainingQty { get; init; }

    /// <summary>
    /// 工序序号
    /// </summary>
    public int OperationSequence { get; init; }

    /// <summary>
    /// 最后报工时间
    ///
    /// 来源：OperationProgressSnapshot.LastReportTime
    /// 用途：资源连续性辅助参考（不能当"当前工序"判据——各工序都会报工，补报/返工会污染）。
    /// </summary>
    public DateTime? LastReportTime { get; init; }

    /// <summary>
    /// 工序级最后报工设备编码（v5.1.6 新增，DDL 已冻结）
    ///
    /// 来源：OperationProgressSnapshot.LastReportResourceCode
    /// 含义：同一MES工单该工序在DataCutoffTime前最后一次有效报工使用的设备。
    /// 用途：资源连续性偏好事实（求解偏好，不自动形成Hard Lock）。
    /// ⚠️ 只存在于工序级；禁止加入 MES_APS_WorkOrder_View / MESWorkOrderSnapshot（v1.6 裁决 §15）。
    /// </summary>
    public string? LastReportResourceCode { get; init; }

    /// <summary>
    /// 数据来源快照ID
    /// </summary>
    public long? SnapshotId { get; init; }

    /// <summary>
    /// 数据更新时间
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}
