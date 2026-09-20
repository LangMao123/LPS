using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 产品转换换型规则（SetupTransitionRule）数据访问契约。
/// 由 2号位（数据引擎）实现；3号位 治理服务仅依赖本接口，不感知具体存储。
/// 作用域：按 RuleSetVersionId 整版本读写，供「发布前冲突校验（§十）」在内存层完成。
/// 审计不在此层——规则写审计统一走 <see cref="ISetupTransitionRuleService"/> 落 <c>AuditLog</c>。
/// </summary>
public interface ISetupTransitionRuleRepository
{
    /// <summary>按规则集版本查询全部规则（含 Inactive，供内存层冻结/冲突校验自行筛选）</summary>
    Task<IReadOnlyList<SetupTransitionRule>> GetByRuleSetVersionAsync(long ruleSetVersionId, CancellationToken cancellationToken = default);

    /// <summary>按主键查询单条规则</summary>
    Task<SetupTransitionRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>新增规则，返回生成的主键</summary>
    Task<long> AddAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default);

    /// <summary>按主键更新规则</summary>
    System.Threading.Tasks.Task UpdateAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default);

    /// <summary>按主键删除规则</summary>
    System.Threading.Tasks.Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}