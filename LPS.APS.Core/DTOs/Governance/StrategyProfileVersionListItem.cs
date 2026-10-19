namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 策略包版本列表项（GET /strategy-profile/{id}/versions 响应；4号位 2026-10-14 审计 R2/R3 补字段）。
/// 在 StrategyProfileVersion 实体 14 列之上，JOIN 带出规则集/参数集版本编码与父级主表 Id，
/// 供前端版本列表直显编码（不再外露裸内部 Id）与编辑草稿时一级下拉直接定位父级（消除 N+1 反查）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class StrategyProfileVersionListItem
{
    /// <summary>版本主键</summary>
    public long Id { get; set; }

    /// <summary>父策略包 Id</summary>
    public long StrategyProfileId { get; set; }

    /// <summary>版本编码</summary>
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>引用的规则集版本 Id</summary>
    public long RuleSetVersionId { get; set; }

    /// <summary>引用的参数集版本 Id</summary>
    public long ParameterSetVersionId { get; set; }

    /// <summary>引用的规则集版本编码（JOIN RuleSetVersion；R2 补返）</summary>
    public string? RuleSetVersionCode { get; set; }

    /// <summary>引用的参数集版本编码（JOIN ParameterSetVersion；R2 补返）</summary>
    public string? ParameterSetVersionCode { get; set; }

    /// <summary>规则集版本所属父级规则集主表 Id（JOIN RuleSetVersion.RuleSetId；R3(a) 补返，前端定位一级下拉）</summary>
    public long? RuleSetId { get; set; }

    /// <summary>参数集版本所属父级参数集主表 Id（JOIN ParameterSetVersion.ParameterSetId；R3(a) 补返，前端定位一级下拉）</summary>
    public long? ParameterSetId { get; set; }

    /// <summary>版本状态（DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED）</summary>
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

    /// <summary>审批时间</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>审批人</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>创建人</summary>
    public string? CreatedBy { get; set; }
}
