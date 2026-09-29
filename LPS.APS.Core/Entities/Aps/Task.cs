namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 任务表（分区表）
/// 对应 APS_Production.Task
/// v5.0变更：移除 ResourceGroupId（已废弃），保留 OperationSeq 用于向前兼容
/// </summary>
public class Task
{
    public long Id { get; set; }
    public int PlanVersionId { get; set; }
    public string TaskNo { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public int MaterialId { get; set; }

    /// <summary>
    /// 工序序号（向前兼容保留，新逻辑应使用 OperationCode + RoutingDependency 图模型）
    /// </summary>
    public int OperationSeq { get; set; }

    public string OperationCode { get; set; } = string.Empty;
    public int? ResourceId { get; set; }

    /// <summary>
    /// ⚠️ v5.0废弃，保留仅为DDL兼容（不再参与排程逻辑）
    /// </summary>
    [Obsolete("v5.0废弃，保留仅为DDL兼容")]
    public int? ResourceGroupId { get; set; }

    /// <summary>
    /// 工艺路径编码（v5.0新增，V1固定'DEFAULT'）
    /// </summary>
    public string RouteCode { get; set; } = "DEFAULT";

    /// <summary>
    /// 路径序号（v5.0新增，V1固定1）
    /// </summary>
    public int PathId { get; set; } = 1;

    public decimal Quantity { get; set; }
    public string UOM { get; set; } = string.Empty;
    public DateTime? PlannedStartTime { get; set; }
    public DateTime? PlannedEndTime { get; set; }
    public decimal? Duration { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public bool IsCriticalPath { get; set; }
    public string TaskType { get; set; } = string.Empty;

    /// <summary>
    /// 生产指示号（v5.0.3：从Order冗余，避免反查）
    /// </summary>
    public string? MTS_InstructionNo { get; set; }

    /// <summary>
    /// 换型来源（唯一落库值 = 4 态：INITIAL_SETUP_STATE / EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK，
    /// 0号位 2026-09-22 裁决）。
    /// 实体列（非计算属性）；1号位 填充、2号位 原样落库不映射；禁止查询层反推。
    /// </summary>
    public string? SetupSource { get; set; }

    /// <summary>
    /// 大工艺阶段码（= RoutingOperation.StageCode，1号位 FinalTaskDraft.StageCode 透传）。
    /// 同一物料可跨多个大工艺（机加工/表面处理…），Task 落库必须带 StageCode，
    /// 否则下发 MES 时区分不出「这是哪个 Stage 的指示」（2026-09-21，TaskDispatch 引申缺口）。
    /// </summary>
    public string? StageCode { get; set; }

    /// <summary>
    /// 人工能力槽号（PM 0923 资源模型裁决）：人工能力槽在运行时投影为合成 ResourceId，结果落库时拆回——
    /// 存 ManualSlotId、ResourceId 置 NULL（不落负号/不落合成大数）。设备 Task 此列为 NULL。
    /// 逻辑 FK → ManualCapacitySlot.ManualSlotId。
    /// </summary>
    public int? ManualSlotId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
