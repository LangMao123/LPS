namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// Run 引用追溯结果（P0-06：3号位治理域）
/// StrategyProfileVersion → 父 StrategyProfile + 引用的 RuleSetVersion / ParameterSetVersion。
/// 供 ScheduleRun.StrategyProfileVersionId → 版本 → 规则集/参数集 完整链路审计追溯。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class RunStrategyProfileTrace
{
    /// <summary>策略包版本 ID</summary>
    public long StrategyProfileVersionId { get; set; }

    /// <summary>版本编码</summary>
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>父策略包 ID</summary>
    public long StrategyProfileId { get; set; }

    /// <summary>父策略包编码</summary>
    public string? StrategyProfileCode { get; set; }

    /// <summary>父策略包 RunType（StrategyProfile.RunType）</summary>
    public string? RunType { get; set; }

    /// <summary>引用的规则集版本 ID</summary>
    public long RuleSetVersionId { get; set; }

    /// <summary>引用的规则集版本编码</summary>
    public string? RuleSetVersionCode { get; set; }

    /// <summary>引用的参数集版本 ID</summary>
    public long ParameterSetVersionId { get; set; }

    /// <summary>引用的参数集版本编码</summary>
    public string? ParameterSetVersionCode { get; set; }

    /// <summary>策略包版本状态</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>生效起始</summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>生效截止</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>是否默认版本</summary>
    public bool IsDefault { get; set; }

    /// <summary>发布时间</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>发布人</summary>
    public string? PublishedBy { get; set; }

    /// <summary>
    /// 引用本版本的所有 ScheduleRun（仅展示用；V1 不真查 Run 维，恒返空数组——4号位 2026-10-14 审计 R4 止血；
    /// 真追溯范围归 Q1 待 0号位 裁决，见 RunReferenceTrace 的 ScheduleRun 维能力）。
    /// </summary>
    public IReadOnlyList<RunReferenceItem> ReferencingRuns { get; set; } = [];
}

/// <summary>
/// Run 引用项（trace 响应 referencingRuns 元素；字段与前端契约对齐：runId/runCode/runType/status/createdAt）。
/// </summary>
public sealed class RunReferenceItem
{
    /// <summary>ScheduleRun.Id</summary>
    public long RunId { get; set; }

    /// <summary>ScheduleRun 运行编码</summary>
    public string? RunCode { get; set; }

    /// <summary>运行类型（FULL_SCHEDULE / MANUAL_RESCHEDULE / ...）</summary>
    public string? RunType { get; set; }

    /// <summary>运行状态（RUNNING / COMPLETED / PARTIAL_SUCCESS / FAILED）</summary>
    public string? Status { get; set; }

    /// <summary>运行创建时间</summary>
    public DateTime? CreatedAt { get; set; }
}
