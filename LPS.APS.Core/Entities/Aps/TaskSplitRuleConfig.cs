using System.ComponentModel.DataAnnotations.Schema;

namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 执行批拆分规则（Batch Policy / 批量策略）主题表
/// 对应 APS_Production.TaskSplitRuleConfig（冻结 DDL v5.1.8.3 §2.14；0号位 2026-10-07 裁决：本轮落码）。
///
/// 两级承接（0号位 2026-09-28 裁决 + 2026-10-07 翻案「不落码→落码」）：
/// 1. 本表（主题表）              —— 3号位 治理 CRUD + 生效区间，直接生效（非版本化 JSON）。
/// 2. FrozenStrategySnapshot.BatchPolicy —— 发布时把 active + 生效区间内规则投影进版本 ContentSnapshotJson.BatchPolicy 子块，
///    供 1号位 Solver 经快照消费（缺策略 fail-closed：BATCH_POLICY_MISSING）。
///
/// 正式匹配粒度（V1）：Material + ProductionDepartment（MaterialId NOT NULL；ProductionDepartmentId NULL = Material 级默认）。
/// 兼容字段（ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity）仅历史兼容，不参与 V1 正式匹配、不投快照。
/// </summary>
[Table("TaskSplitRuleConfig")]
public class TaskSplitRuleConfig
{
    public int Id { get; set; }

    /// <summary>物料（正式业务键，NOT NULL）</summary>
    public int MaterialId { get; set; }

    /// <summary>生产部门（可空 = Material 级默认）</summary>
    public int? ProductionDepartmentId { get; set; }

    /// <summary>硬最小执行批量</summary>
    public decimal? MinExecutionBatchQty { get; set; }

    /// <summary>硬最大执行批量（NULL = 无硬上限）</summary>
    public decimal? MaxExecutionBatchQty { get; set; }

    /// <summary>软偏好切点批量</summary>
    public decimal? PreferredBatchQty { get; set; }

    /// <summary>是否允许拆分</summary>
    public bool AllowSplit { get; set; } = true;

    /// <summary>是否允许合并</summary>
    public bool AllowMerge { get; set; }

    /// <summary>仅限制优化性拆分搜索的最大拆分次数（不限制硬 Max 强制拆分）</summary>
    public int? MaxOptimizationSplitCount { get; set; }

    /// <summary>单问题最多评估的候选数</summary>
    public int? MaxBatchCandidates { get; set; }

    /// <summary>兼容字段（v5.0 已废弃 ResourceGroup，不为 V1 正式粒度）</summary>
    public int? ResourceGroupId { get; set; }

    /// <summary>兼容字段（旧 MOQ）</summary>
    public decimal? MinimumOrderQuantity { get; set; }

    /// <summary>兼容字段（旧 EOQ）</summary>
    public decimal? EconomicOrderQuantity { get; set; }

    /// <summary>瓶颈资源拆分/合并策略（PREFER_SPLIT / PREFER_MERGE）</summary>
    public string? BottleneckSplitStrategy { get; set; }

    /// <summary>非瓶颈批量策略（PREFER_LARGE_BATCH / PREFER_SMALL_BATCH）</summary>
    public string? NonBottleneckStrategy { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}