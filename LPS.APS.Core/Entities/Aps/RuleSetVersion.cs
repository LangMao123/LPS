using LPS.APS.Core.Enum;

namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 规则集版本表
/// 对应 APS_Production.RuleSetVersion（冻结 DDL v5.1.2 §3.10.2）
/// 红线：已发布版本不可原地修改，须创建新版本；正式排程只允许 PUBLISHED 状态。
/// Status 取值见 <see cref="GovernanceVersionStatus"/>（DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED）
/// </summary>
public class RuleSetVersion
{
    public long Id { get; set; }
    public long RuleSetId { get; set; }
    public string VersionCode { get; set; } = string.Empty;
    public string Status { get; set; } = GovernanceVersionStatus.Draft;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Remarks { get; set; }
    /// <summary>需求优先级规则 JSON（发布装配中间态；0号位 方案 A §7.3：退出版本表正式持久化，统一经 ContentSnapshotJson 落库）</summary>
    public string? DemandPriorityJson { get; set; }

    /// <summary>
    /// 换型规则子块 JSON（详情投影中间态；C-2 整改，2026-10-17）：
    /// 镜像 <see cref="DemandPriorityJson"/> 投影——读路径经 <c>GovernanceVersionService.ProjectRuleSetContent</c>
    /// 从 ContentSnapshotJson 的 "SetupTransitionRules" 子块提取到本内存字段，供规则集详情 / published-version 端点
    /// 回放版本对应的 Setup 主题内容（0号位 复核 7.3「详情接口应能返回 Setup 主题内容」）；发布装配仍走
    /// <see cref="ContentSnapshotJson"/> 持久化，本字段仅读路径中间态。
    /// </summary>
    public string? SetupTransitionRulesJson { get; set; }

    /// <summary>
    /// 发布内容快照 JSON（方案 A 落点，契约 §6.10.5；DDL 由 2号位 按变更申请执行）
    /// 该版本发布时的完整规则内容（含 DemandPriority 子块），可重放载体；PUBLISHED 后不可原地修改。
    /// </summary>
    public string? ContentSnapshotJson { get; set; }
}
