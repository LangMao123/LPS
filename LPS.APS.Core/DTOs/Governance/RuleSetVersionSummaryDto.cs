using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 规则集版本摘要 DTO（rule-set 同族，2026-10-15，镜像 <see cref="ParameterSetVersionSummaryDto"/> R2 修复）：
/// 版本列表读端点仅交付版本元数据，不含 <see cref="RuleSetVersion.ContentSnapshotJson"/>
/// 及 DemandPriorityJson 主题中间态 JSON 载体列——与参数集同款「摘要 DTO」方案（4号位 来文
/// 20261015 登记，0号位 知会后落码），规避版本列表序列化大 JSON 载体的一致性与体积风险。
/// 快照 JSON 为内部可重放载体，前端不消费；前端内容经详情端点（GetRuleSetVersionAsync）投影的
/// DemandPriorityJson 子块获取。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class RuleSetVersionSummaryDto
{
    public long Id { get; init; }
    public long RuleSetId { get; init; }
    public string VersionCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? PublishedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public string? Remarks { get; init; }

    /// <summary>由版本实体装配摘要（纯函数；排除 ContentSnapshotJson 与 DemandPriorityJson JSON 载体列）</summary>
    public static RuleSetVersionSummaryDto From(RuleSetVersion v) => new()
    {
        Id = v.Id,
        RuleSetId = v.RuleSetId,
        VersionCode = v.VersionCode,
        Status = v.Status,
        EffectiveFrom = v.EffectiveFrom,
        EffectiveTo = v.EffectiveTo,
        PublishedAt = v.PublishedAt,
        PublishedBy = v.PublishedBy,
        ApprovedAt = v.ApprovedAt,
        ApprovedBy = v.ApprovedBy,
        CreatedAt = v.CreatedAt,
        CreatedBy = v.CreatedBy,
        Remarks = v.Remarks
    };
}
