using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 参数集版本摘要 DTO（R2 修复，2026-10-14）：
/// 读端点（版本列表 / published-version）仅交付版本元数据，不含 <see cref="ParameterSetVersion.ContentSnapshotJson"/>
/// 及五主题中间态 JSON 载体列——避免 PSV 1985（PS-PROD-V1.0，837,954 行 BatchPolicy 全量注入 ≈ 2.15 亿字符）
/// 序列化触发 System.Text.Json 值上限异常（裸 500 + 堆栈泄露，4号位 复现属实）。
/// 快照 JSON 为内部可重放载体，前端不消费；前端内容经详情端点（GetParameterSetVersionAsync）投影的五主题子块获取。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class ParameterSetVersionSummaryDto
{
    public long Id { get; init; }
    public long ParameterSetId { get; init; }
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

    /// <summary>由版本实体装配摘要（纯函数；排除 ContentSnapshotJson 与五主题 JSON 载体列）</summary>
    public static ParameterSetVersionSummaryDto From(ParameterSetVersion v) => new()
    {
        Id = v.Id,
        ParameterSetId = v.ParameterSetId,
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
