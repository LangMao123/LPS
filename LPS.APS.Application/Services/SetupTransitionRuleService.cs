using System.Text.Json;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services;

/// <summary>
/// 产品转换换型规则（SetupTransitionRule）治理服务（3号位）。
/// 职责：CRUD 编排 + 发布前唯一键冲突校验（§十，复用 <see cref="SetupTransitionRuleConflictValidator"/>）+ 统一审计落库（<c>AuditLog</c>）。
/// 红线：
///   - 规则随 RuleSetVersionId 冻结，已 PUBLISHED 版本禁止原地改——由版本治理层兜底拦截，本服务先做冲突校验；
///   - 冲突校验失败抛 <see cref="InvalidOperationException"/>（fail-closed）；
///   - 审计不可写/写入失败即抛（可追溯性不降级）。
/// 本服务为无状态服务（Stateless），validator 亦为纯计算，均不依赖 I/O。
/// </summary>
public sealed class SetupTransitionRuleService : ISetupTransitionRuleService
{
    /// <summary>统一审计实体类型常量（AuditLog.EntityType）</summary>
    public const string EntityTypeSetupTransitionRule = "SetupTransitionRule";

    private const string ActionCreate = "Create";
    private const string ActionUpdate = "Update";
    private const string ActionDelete = "Delete";

    private static readonly JsonSerializerOptions AuditJsonOptions = new() { WriteIndented = false };

    private readonly ISetupTransitionRuleRepository _repository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly SetupTransitionRuleConflictValidator _conflictValidator = new();

    public SetupTransitionRuleService(
        ISetupTransitionRuleRepository repository,
        IAuditLogRepository auditLogRepository)
    {
        _repository = repository;
        _auditLogRepository = auditLogRepository;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SetupTransitionRule>> ListAsync(long ruleSetVersionId, CancellationToken cancellationToken = default)
        => _repository.GetByRuleSetVersionAsync(ruleSetVersionId, cancellationToken);

    /// <inheritdoc/>
    public Task<SetupTransitionRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc/>
    public async Task<long> CreateAsync(SetupTransitionRule rule, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await _auditLogRepository.EnsureWritableAsync(cancellationToken);

        await EnsureNoConflictAsync(rule, excludingId: 0, cancellationToken);

        var id = await _repository.AddAsync(rule, cancellationToken);

        await WriteAuditAsync(
            ActionCreate, id, rule.RuleSetVersionId,
            oldValue: null, newValue: Serialize(rule),
            Describe(rule), actorUserId, actorUserCode, cancellationToken);

        return id;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task UpdateAsync(SetupTransitionRule rule, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await _auditLogRepository.EnsureWritableAsync(cancellationToken);

        var existing = await _repository.GetByIdAsync(rule.Id, cancellationToken)
            ?? throw new InvalidOperationException($"换型规则不存在（Id={rule.Id}），无法更新。");

        await EnsureNoConflictAsync(rule, excludingId: rule.Id, cancellationToken);

        var oldValue = Serialize(existing);
        await _repository.UpdateAsync(rule, cancellationToken);

        await WriteAuditAsync(
            ActionUpdate, rule.Id, rule.RuleSetVersionId,
            oldValue, newValue: Serialize(rule),
            Describe(rule), actorUserId, actorUserCode, cancellationToken);
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task DeleteAsync(long id, int actorUserId, string actorUserCode, CancellationToken cancellationToken = default)
    {
        await _auditLogRepository.EnsureWritableAsync(cancellationToken);

        var existing = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"换型规则不存在（Id={id}），无法删除。");

        await _repository.DeleteAsync(id, cancellationToken);

        await WriteAuditAsync(
            ActionDelete, existing.Id, existing.RuleSetVersionId,
            oldValue: Serialize(existing), newValue: null,
            Describe(existing), actorUserId, actorUserCode, cancellationToken);
    }

    /// <summary>
    /// 发布前唯一键冲突校验（§十）：将候选规则并入当前版本「有效规则」集合后整体校验。
    /// <paramref name="excludingId"/> 用于排除被更新规则自身（Create 传 0）。
    /// 冲突时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    private async System.Threading.Tasks.Task EnsureNoConflictAsync(SetupTransitionRule candidate, long excludingId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByRuleSetVersionAsync(candidate.RuleSetVersionId, cancellationToken);

        var combined = existing
            .Where(r => r.IsActive && r.Id != excludingId)
            .Append(candidate)
            .ToList();

        var result = _conflictValidator.Validate(combined);
        if (!result.IsValid)
        {
            throw new InvalidOperationException($"换型规则唯一键冲突（§十）：{result.GetErrorMessage()}");
        }
    }

    /// <summary>落一条统一审计记录（fail-closed：AddAsync 抛异常即向上传播，可追溯性不降级）</summary>
    private async System.Threading.Tasks.Task WriteAuditAsync(
        string actionCode,
        long entityId,
        long ruleSetVersionId,
        string? oldValue,
        string? newValue,
        string remark,
        int actorUserId,
        string actorUserCode,
        CancellationToken cancellationToken)
    {
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = actionCode,
            EntityType = EntityTypeSetupTransitionRule,
            EntityId = entityId.ToString(),
            VersionCode = ruleSetVersionId.ToString(),
            OldValue = oldValue,
            NewValue = newValue,
            UserId = actorUserId,
            UserCode = actorUserCode,
            OccurredAt = DateTime.UtcNow,
            Remark = remark,
        }, cancellationToken);
    }

    /// <summary>业务字段快照（不含主键/审计时间戳，EntityId 由审计列承载）</summary>
    private static string Serialize(SetupTransitionRule rule) => JsonSerializer.Serialize(new
    {
        rule.RuleSetVersionId,
        rule.ProductionDepartmentId,
        rule.StageCode,
        rule.OperationCode,
        rule.ResourceId,
        rule.FromMaterialId,
        rule.ToMaterialId,
        rule.RuleType,
        rule.SetupMinutes,
        rule.IsActive,
    }, AuditJsonOptions);

    /// <summary>规则业务键描述（审计 Remark 用）</summary>
    private static string Describe(SetupTransitionRule rule) =>
        rule.RuleType == SetupTransitionRuleType.Exact
            ? $"换型规则 工序[{rule.OperationCode}] 设备[{rule.ResourceId}] {rule.FromMaterialId}→{rule.ToMaterialId}"
            : $"换型规则 工序[{rule.OperationCode}] 设备[{rule.ResourceId}] 默认";
}