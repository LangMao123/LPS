namespace LPS.APS.Core.Dto;

/// <summary>
/// 标准化既存MES执行上下文（5号位 → 2号位，跨版本连续性专用）
///
/// 职责（v1.4 §8.5）：
/// 5号位结合 WorkOrder + OperationProgress + StageProgress + PI Position + NextOperationContext，
/// 输出2号位可直接消费的"该MES工单当前执行到哪里、下一步从哪里继续"上下文。
///
/// 消费方：2号位在当前逻辑生产需求（LogicalProductionDemand）形成后，做 Continuation 分桶：
///   - 该工单可承接的连续份额 = DerivedRemainingQty（受当前Q上限约束）
///   - 承接后进入1号位有限产能求解
///
/// 5号位不做：当前Q形成 / E<=Q分桶 / ContinuationKey/TaskNo身份 / FinalTask持久化。
///
/// 冻结红线：
/// - DerivedRemainingQty 是运行时派生结果，不是MES工单级原生字段
/// - 不要求也不允许在 MESWorkOrderSnapshot 新增 RemainingNetQty / RemainingQty / LastReportResourceCode
/// - WorkOrderStatus 沿用既有值：RELEASED / IN_PROGRESS / CLOSED / DELETED
/// - LastReportResourceCode 仅存在于工序级（OperationProgressSnapshot），不来自工单级
/// </summary>
public sealed class ExistingExecutionContextDto
{
    /// <summary>
    /// 生产指示号
    /// </summary>
    public string ProductionInstructionNo { get; init; } = string.Empty;

    /// <summary>
    /// MES工单号
    /// </summary>
    public string MESWorkOrderNo { get; init; } = string.Empty;

    /// <summary>
    /// 工单状态（冻结值域：RELEASED / IN_PROGRESS / CLOSED / DELETED）
    ///
    /// ⚠️ 禁止改名为 ExecutionStatus，禁止使用 NOT_STARTED / STARTED_INCOMPLETE / COMPLETED。
    /// </summary>
    public string WorkOrderStatus { get; init; } = string.Empty;

    /// <summary>
    /// 当前有效执行起点 Stage 代码
    ///
    /// 来源：PI Position 最前 Stage 或 OperationProgress 当前 Stage
    /// </summary>
    public string StartStageCode { get; init; } = string.Empty;

    /// <summary>
    /// 当前有效执行起点工序代码（可空：仅能确定到Stage级时为null）
    ///
    /// 来源：OperationProgress 最后报工工序的下一工序（如有OperationProgress数据）
    /// </summary>
    public string? StartOperationCode { get; init; }

    /// <summary>
    /// 运行时派生的剩余数量（DerivedRemainingQty）
    ///
    /// 来源：OperationProgressSnapshot.RemainingQty 或 StageProgressSnapshot.RemainingQty + PI Position
    /// 语义：该工单在当前有效执行起点上的剩余数量
    /// ⚠️ 这是运行时派生结果，不是MES工单级原生字段
    /// </summary>
    public decimal DerivedRemainingQty { get; init; }

    /// <summary>
    /// 工序级最后报工设备编码（可空，v5.1.6 新增）
    ///
    /// 来源：OperationProgressSnapshot.LastReportResourceCode
    /// 用途：资源连续性偏好事实（求解偏好，不自动形成Hard Lock）
    /// ⚠️ 只存在于工序级；禁止来自工单级
    /// </summary>
    public string? LastReportResourceCode { get; init; }

    /// <summary>
    /// 数据截止时间（本次快照对应的DataCutoffTime）
    /// </summary>
    public DateTime DataCutoffTime { get; init; }

    /// <summary>
    /// 是否存在异常/降级（如工单状态与进度不一致等）
    /// </summary>
    public bool HasIssue { get; init; }

    /// <summary>
    /// 异常/降级描述（HasIssue=true时非空）
    /// </summary>
    public string? IssueDescription { get; init; }
}
