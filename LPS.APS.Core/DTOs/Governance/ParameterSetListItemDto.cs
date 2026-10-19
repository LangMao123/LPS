namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 参数集列表项 DTO（G1/A8：4号位参数集列表页；Q1 对齐 rule-sets，2026-10-15）
/// 主表字段 + 最新版本摘要（与 <see cref="RuleSetListItemDto"/> 同构）。
/// 真源说明：
///  - CurrentVersionCode / DraftVersionCode 取自 ParameterSetVersion.VersionCode（字符串）；
///  - Status / LastChangeReason / PublishedAt 取最新版本；
///  - 无版本行则版本摘要字段为 null（前端以 status 判「配没配版本」）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class ParameterSetListItemDto
{
    public long Id { get; set; }
    public string ParameterSetCode { get; set; } = string.Empty;
    public string ParameterSetName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>最新 PUBLISHED 版本标识（C-4 身份补全：准确 VersionId；无版本则 null）</summary>
    public long? CurrentVersionId { get; set; }
    /// <summary>最新 PUBLISHED 版本编码（无则为 null）</summary>
    public string? CurrentVersionCode { get; set; }
    /// <summary>最新 DRAFT 版本标识（C-4 身份补全：准确 VersionId；无版本则 null）</summary>
    public long? DraftVersionId { get; set; }
    /// <summary>最新 DRAFT 版本编码（无则为 null）</summary>
    public string? DraftVersionCode { get; set; }
    /// <summary>最新版本状态（六态：DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED；无版本则 null）</summary>
    public string? Status { get; set; }
    /// <summary>最新版本变更原因（ParameterSetVersion.Remarks）</summary>
    public string? LastChangeReason { get; set; }
    /// <summary>最新 PUBLISHED 发布时间</summary>
    public DateTime? PublishedAt { get; set; }
}
