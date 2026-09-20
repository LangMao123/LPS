using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 产品转换换型规则（SetupTransitionRule）治理服务契约（3号位）。
/// 编排 CRUD + 发布前唯一键冲突校验（§十）+ 统一审计落库（<c>AuditLog</c>，EntityType=SetupTransitionRule）。
/// 权限在端点层另行挂 <c>RuleView/RuleMaintain/RulePublish</c> 权限码，本契约不承载权限。
/// 冲突校验失败抛 <see cref="System.InvalidOperationException"/>（fail-closed）；审计不可写/写入失败即抛、不降级。
/// </summary>
public interface ISetupTransitionRuleService
{
    /// <summary>按规则集版本列出全部规则（治理侧只读）</summary>
    Task<IReadOnlyList<SetupTransitionRule>> ListAsync(long ruleSetVersionId, CancellationToken cancellationToken = default);

    /// <summary>按主键查询单条规则</summary>
    Task<SetupTransitionRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>新增规则（先校验审计可写再落库，校验唯一键 + 落审计），返回新主键</summary>
    Task<long> CreateAsync(SetupTransitionRule rule, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default);

    /// <summary>更新规则（校验唯一键 + 落审计，含变更前后快照）</summary>
    System.Threading.Tasks.Task UpdateAsync(SetupTransitionRule rule, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default);

    /// <summary>删除规则（落审计，含删除前快照）</summary>
    System.Threading.Tasks.Task DeleteAsync(long id, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default);
}