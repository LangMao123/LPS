using LPS.APS.Application.Models;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 治理版本发布服务（阶段 A-4/A-5：3号位 Application 编排）
/// 六态状态机发布流程实现（R01/R02 验收）。
/// A-7 扩展：发布时记录审计日志到 Auth 库。
/// 红线：
/// - 已 PUBLISHED 版本不可再次发布（历史不可覆盖，R01）；
/// - 发布产生新 PUBLISHED 记录，旧版本记录不变（新 Run 可引用新版本、旧 Run 引用不变，R02）；
/// - 仅 DRAFT/SUBMITTED/APPROVED 为合法发布前驱；DISABLED/ARCHIVED 不可发布。
/// 完整发布前校验（引用有效、参数越界、Guardrail 为正、On-time 0~100 等）由阶段 A-5 扩展。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class GovernanceVersionService : IGovernanceVersionService
{
    private readonly IRuleSetVersionRepository _ruleSetVersionRepository;
    private readonly IParameterSetVersionRepository _parameterSetVersionRepository;
    private readonly IStrategyProfileRepository _strategyProfileRepository;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    /// <summary>权威仓库事实源只读访问（0号位 裁决5：INVALID_WAREHOUSE_REF 校验；可空——未接入事实源时跳过校验，见清单三 2号位/5号位 确认项）</summary>
    private readonly DatabaseConnectionManager? _connectionManager;

    /// <summary>
    /// 发布期观测日志（**可空** —— 沿用本类既有 <c>_connectionManager</c> 的「可选可空」风格，
    /// 从而**不破坏** 6 处既有 <c>new GovernanceVersionService(...)</c> 调用点）。
    ///
    /// 2026-10-14 新增：本类此前**完全没有 logger** ⇒ 发布若因体积 OOM/超时挂掉，连一行日志都不留。
    /// （旧⑧块 BatchPolicy 注入曾把全表 837,957 行≈601 MB 整块写入快照，2026-10-15 终裁退出后不再有此量级。）
    /// </summary>
    private readonly ILogger<GovernanceVersionService>? _logger;
    /// <summary>Demand Priority 业务校验器（无状态纯校验，P0-05 强制接入发布前校验）</summary>
    private readonly DemandPriorityValidator _demandPriorityValidator = new();

    /// <summary>Solver Strategy 业务校验器（无状态纯校验，E-4；P0-02b 接入参数集发布前校验）</summary>
    private readonly SolverStrategyValidator _solverStrategyValidator = new();

    /// <summary>Candidate Guardrail 业务校验器（无状态纯校验，E-4；P0-02b 接入参数集发布前校验）</summary>
    private readonly CandidateGuardrailValidator _candidateGuardrailValidator = new();

    public GovernanceVersionService(
        IRuleSetVersionRepository ruleSetVersionRepository,
        IParameterSetVersionRepository parameterSetVersionRepository,
        IStrategyProfileRepository strategyProfileRepository,
        IStrategyProfileVersionRepository strategyProfileVersionRepository,
        IAuditLogRepository auditLogRepository,
        DatabaseConnectionManager? connectionManager = null,
        ILogger<GovernanceVersionService>? logger = null)
    {
        _ruleSetVersionRepository = ruleSetVersionRepository;
        _parameterSetVersionRepository = parameterSetVersionRepository;
        _strategyProfileRepository = strategyProfileRepository;
        _strategyProfileVersionRepository = strategyProfileVersionRepository;
        _auditLogRepository = auditLogRepository;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>合法发布前驱状态（其余状态发布一律拒绝）</summary>
    private static readonly string[] PublishableStatuses = [GovernanceVersionStatus.Draft, GovernanceVersionStatus.Submitted, GovernanceVersionStatus.Approved];

    public async Task PublishRuleSetVersionAsync(long ruleSetVersionId, string? publishedBy, int actorUserId, CancellationToken ct = default, string? changeReason = null)
    {
        var version = await _ruleSetVersionRepository.GetByIdAsync(ruleSetVersionId, ct)
            ?? throw new InvalidOperationException($"规则集版本不存在：{ruleSetVersionId}");

        // P0-05：正式 Publish 强制发布前完整校验，无绕过路径（Validate → 有 Error 拒绝 → Publish）
        var validation = await ValidateRuleSetVersionForPublishAsync(ruleSetVersionId, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"规则集版本发布前校验失败：{validation.GetErrorMessage()}");
        }

        EnsurePublishable(version.Status, ruleSetVersionId);

        var beforeStatus = version.Status;

        // P0-02b：发布时聚合 DemandPriority 子块 → ContentSnapshotJson（契约 §6.10.5，Run 装载重放载体）
        version.ContentSnapshotJson = BuildRuleSetContentSnapshot(version);

        // 发布期键名漂移告警（只记日志，不阻断）——⑦块 SetupTransitionRules 的键名未冻结，拼错即静默当未配置。
        WarnIfUnknownSnapshotBlocks(version.ContentSnapshotJson, "规则集", ruleSetVersionId);

        version.Status = GovernanceVersionStatus.Published;
        version.PublishedAt = DateTime.Now;
        version.PublishedBy = publishedBy;

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _ruleSetVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志：记录发布操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Publish",
            EntityType = "RuleSetVersion",
            EntityId = ruleSetVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Published,
            UserId = actorUserId,
            UserCode = publishedBy,
            OccurredAt = DateTime.Now,
            Remark = string.IsNullOrWhiteSpace(changeReason) ? "规则集版本发布" : changeReason
        }, ct);
    }

    public async Task PublishParameterSetVersionAsync(long parameterSetVersionId, string? publishedBy, int actorUserId, CancellationToken ct = default, string? changeReason = null)
    {
        var version = await _parameterSetVersionRepository.GetByIdAsync(parameterSetVersionId, ct)
            ?? throw new InvalidOperationException($"参数集版本不存在：{parameterSetVersionId}");

        // P0-05：正式 Publish 强制发布前完整校验，无绕过路径（Validate → 有 Error 拒绝 → Publish）
        var validation = await ValidateParameterSetVersionForPublishAsync(parameterSetVersionId, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"参数集版本发布前校验失败：{validation.GetErrorMessage()}");
        }

        EnsurePublishable(version.Status, parameterSetVersionId);

        var beforeStatus = version.Status;

        // P0-02b：发布时聚合五子块（Lock/Supply/Procurement/SolverStrategy/CandidateGuardrail）→ ContentSnapshotJson（契约 §6.10.5）
        version.ContentSnapshotJson = BuildParameterSetContentSnapshot(version);

        // 发布期体积可见（2026-10-14 新增）：聚合后如实记下 `ContentSnapshotJson` 的真实字符数。
        // 此前本类无任何日志 ⇒ 发布因体积 OOM/超时挂掉时无迹可查。**仅记录，不阻断**（见 B-006：
        // 「发布数据质量校验不等于 Solver 对生产批量经济区间偏离的硬拒排」）。
        // 注：旧⑧块 BatchPolicy 注入（0号位 2026-10-07 裁决落码）已按 0号位 2026-10-15 终裁退出——
        // 停止发布时 JSON 全量注入归 3号位，与 2号位 按表查询读取切换**同批受控上线**（2/3号位 共同会签，不可单侧先投产）。
        if (_logger is not null)
        {
            var snapshotChars = version.ContentSnapshotJson?.Length ?? 0;
            _logger.LogInformation(
                "参数集版本发布快照体积：ParameterSetVersionId={VersionId}，ContentSnapshotJson={Chars} 字符（≈{Mb:F1} MB，UTF-16 单列）",
                parameterSetVersionId, snapshotChars, snapshotChars * 2.0 / 1048576.0);
        }

        // 发布期键名漂移告警（只记日志，不阻断）——⑧块键名未冻结，拼错即静默当未配置。
        WarnIfUnknownSnapshotBlocks(version.ContentSnapshotJson, "参数集", parameterSetVersionId);

        version.Status = GovernanceVersionStatus.Published;
        version.PublishedAt = DateTime.Now;
        version.PublishedBy = publishedBy;

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _parameterSetVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志：记录发布操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Publish",
            EntityType = "ParameterSetVersion",
            EntityId = parameterSetVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Published,
            UserId = actorUserId,
            UserCode = publishedBy,
            OccurredAt = DateTime.Now,
            Remark = string.IsNullOrWhiteSpace(changeReason) ? "参数集版本发布" : changeReason
        }, ct);
    }

    /// <summary>
    /// 校验状态是否可发布：仅 DRAFT/SUBMITTED/APPROVED 合法；
    /// PUBLISHED 拒绝（历史不可覆盖，R01）；DISABLED/ARCHIVED 拒绝（失效/归档不可发布）。
    /// </summary>
    private static void EnsurePublishable(string status, long versionId)
    {
        if (PublishableStatuses.Contains(status))
        {
            return;
        }

        throw status switch
        {
            GovernanceVersionStatus.Published => new InvalidOperationException($"版本已发布，历史不可覆盖：{versionId}"),
            _ => new InvalidOperationException($"版本状态不可发布（当前 {status}）：{versionId}"),
        };
    }

    /// <summary>
    /// 发布期**键名漂移**可见性告警（2026-10-14 新增）。
    ///
    /// **背景**：8 个子块里，①–⑥ 走 <c>FrozenStrategySnapshotProvider</c> 的强类型反序列化，
    /// 键名/结构不符会**抛异常**（fail-fast）；而 ⑦块（<c>SetupTransitionRules</c>）与 ⑧块（<c>BatchPolicy</c>）
    /// 的投影器是**单点 <c>TryGetProperty</c></c> —— 键名拼错 ⇒ **静默返回空列表**，
    /// 与「合法未配置」**在运行时不可区分**。**8 块键名一个都没有冻结**（见台账 §T-1014h）。
    ///
    /// **本方法只做一件事**：把「快照顶层出现了不在已知 8 键白名单里的键」记一条 <c>LogWarning</c>。
    /// **不阻断发布、不剔行、不改快照内容、不抛异常** —— 合 0号位 2026-10-12 §三.2 红线
    /// （不得在装载/发布层替对方补值或跳过异常）与 B-006（发布数据质量校验 ≠ 硬拒排）。
    ///
    /// **现状噪音为零**：2026-10-14 已对全库实测，RSV 4 份 ＋ PSV 4 份快照的顶层键**恰好**是白名单那 8 个，
    /// **零未知键**。故本告警只在**将来**有人写错键名时才会响。
    /// </summary>
    /// <param name="contentSnapshotJson">待检查的快照 JSON（可为 null/空 ⇒ 直接返回，不告警）</param>
    /// <param name="versionKind">版本种类（"规则集"/"参数集"），仅用于日志可读性</param>
    /// <param name="versionId">版本 Id，仅用于日志可读性</param>
    private void WarnIfUnknownSnapshotBlocks(string? contentSnapshotJson, string versionKind, long versionId)
    {
        if (_logger is null)
        {
            return;
        }

        var unknownKeys = TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames(contentSnapshotJson);
        if (unknownKeys.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "快照顶层出现未知子块键：{VersionKind}版本 {VersionId}，未知键=[{UnknownKeys}]。" +
            "已知 8 块为 DemandPriority/Lock/Supply/Procurement/SolverStrategy/CandidateGuardrail/SetupTransitionRules/BatchPolicy。" +
            "⚠ 若这是 ⑦/⑧ 块被写错键名，装载侧投影器会**静默当未配置**（不报错）——请核对键名逐字。本告警不阻断发布。",
            versionKind, versionId, string.Join(", ", unknownKeys));
    }

    /// <summary>
    /// 校验状态是否可停用（G2：Retired↔DISABLED 语义映射 §8.4）：
    /// SUBMITTED/APPROVED/PUBLISHED → DISABLED 合法（下线，新 Run 不再引用）；
    /// DRAFT 拒绝（草稿不产生引用，请直接编辑或删除）；
    /// DISABLED 拒绝（已停用，幂等保护）；ARCHIVED 拒绝（已归档）。
    /// </summary>
    private static void EnsureDisableable(string status, long versionId)
    {
        if (status is GovernanceVersionStatus.Submitted
            or GovernanceVersionStatus.Approved
            or GovernanceVersionStatus.Published)
        {
            return;
        }

        throw status switch
        {
            GovernanceVersionStatus.Draft => new InvalidOperationException($"草稿不可停用（请直接编辑或删除）：{versionId}"),
            GovernanceVersionStatus.Disabled => new InvalidOperationException($"版本已停用：{versionId}"),
            GovernanceVersionStatus.Archived => new InvalidOperationException($"版本已归档，不可停用：{versionId}"),
            _ => new InvalidOperationException($"版本状态不可停用（当前 {status}）：{versionId}"),
        };
    }

    /// <summary>
    /// C-5/D-2 引用保护（0号位 §4.2 方案a，2026-10-17）：停用前置预检——
    /// 被停用版本若仍被「合法可供新Run使用的已发布策略包版本」引用，阻断停用并返回明确引用冲突错误。
    /// 判定：引用方 Status='PUBLISHED' + 父 StrategyProfile IsActive=1（仓储过滤）+ 生效窗 EffectiveFrom≤now≤EffectiveTo（本层过滤，镜像 <see cref="SelectCurrentPublished"/>）。
    /// 由于实围墙停用经 D-2 建议方案a（尚未单独终裁），若 0号位 终裁为 b 本阻断改为仅告警，不预写失效引用静默建 Run 的豁免。
    /// </summary>
    private async Task EnsureNoLegitimateNewRunReferenceAsync(
        long? referencedRuleSetVersionId,
        long? referencedParameterSetVersionId,
        string displayName,
        CancellationToken ct)
    {
        var referencing = await _strategyProfileVersionRepository.GetReferencingPublishedVersionsAsync(
            referencedRuleSetVersionId, referencedParameterSetVersionId, ct);

        // D-2 检查范围校正（0号位 2026-10-17 统一审核 §5.2）：阻断范围须覆盖「未来有效期内仍可能合法用于新 Run」
        // 的已发布策略包版本——即使其 EffectiveFrom 尚未来临（如策略包今天发布、明天正式生效），今天停用其绑定的
        // 规则/参数版本，明天该策略包仍会失去合法排程资格。故仅以「未来仍生效」（EffectiveTo 未过或未限失效）为
        // 阻断判据，不再要求 EffectiveFrom ≤ now。EffectiveTo 已过去的失效引用则放行。
        var now = DateTime.Now;
        var inWindow = referencing
            .Where(v => !v.EffectiveTo.HasValue || v.EffectiveTo.Value >= now)
            .Select(v => v.Id)
            .ToList();

        if (inWindow.Count == 0)
            return;

        throw new InvalidOperationException(
            $"{displayName} 仍被 {inWindow.Count} 个当前或未来有效期内可新Run策略包版本引用（策略包版本Id: {string.Join(", ", inWindow)}），" +
            $"禁止直接停用（D-2 引用保护，须先经合法策略包切换消除引用再停用）");
    }

    public async Task DisableRuleSetVersionAsync(long ruleSetVersionId, string? operatedBy, int actorUserId, string? reason = null, CancellationToken ct = default)
    {
        var version = await _ruleSetVersionRepository.GetByIdAsync(ruleSetVersionId, ct)
            ?? throw new InvalidOperationException($"规则集版本不存在：{ruleSetVersionId}");

        EnsureDisableable(version.Status, ruleSetVersionId);

        // C-5/D-2 引用保护（0号位 §4.2 方案a）：与参数停用对称——被停用版本若仍被合法可新Run策略包引用，阻断停用。
        await EnsureNoLegitimateNewRunReferenceAsync(
            referencedRuleSetVersionId: ruleSetVersionId,
            referencedParameterSetVersionId: null,
            displayName: "规则集版本",
            ct);

        var beforeStatus = version.Status;
        version.Status = GovernanceVersionStatus.Disabled;

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _ruleSetVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志：记录停用操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Disable",
            EntityType = "RuleSetVersion",
            EntityId = ruleSetVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Disabled,
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = reason
        }, ct);
    }

    public async Task DisableParameterSetVersionAsync(long parameterSetVersionId, string? operatedBy, int actorUserId, string? reason = null, CancellationToken ct = default)
    {
        var version = await _parameterSetVersionRepository.GetByIdAsync(parameterSetVersionId, ct)
            ?? throw new InvalidOperationException($"参数集版本不存在：{parameterSetVersionId}");

        EnsureDisableable(version.Status, parameterSetVersionId);

        // C-5/D-2 引用保护（0号位 §4.2 方案a）：被停用版本若仍被合法可新Run的已发布策略包引用，阻断停用。
        await EnsureNoLegitimateNewRunReferenceAsync(
            referencedRuleSetVersionId: null,
            referencedParameterSetVersionId: parameterSetVersionId,
            displayName: "参数集版本",
            ct);

        var beforeStatus = version.Status;
        version.Status = GovernanceVersionStatus.Disabled;

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _parameterSetVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志：记录停用操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Disable",
            EntityType = "ParameterSetVersion",
            EntityId = parameterSetVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Disabled,
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = reason
        }, ct);
    }

    public async Task DisableStrategyProfileVersionAsync(long strategyProfileVersionId, string? operatedBy, int actorUserId, string? reason = null, CancellationToken ct = default)
    {
        var version = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        EnsureDisableable(version.Status, strategyProfileVersionId);

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        // IsDefault=1 停用：需清默认标志（避免 DISABLED 默认版本残留于 ResolveDefault 查询范围）
        if (version.IsDefault)
        {
            await _strategyProfileVersionRepository.ClearDefaultFlagAsync(version.StrategyProfileId, strategyProfileVersionId, ct);
        }

        var beforeStatus = version.Status;
        version.Status = GovernanceVersionStatus.Disabled;

        await _strategyProfileVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志：记录停用操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Disable",
            EntityType = "StrategyProfileVersion",
            EntityId = strategyProfileVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Disabled,
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = reason
        }, ct);
    }

    /// <summary>
    /// 设置策略包版本为同族默认（C-7 §9.2 显式治理操作，2026-10-17）。
    /// 默认标记 IsDefault 属治理元数据，不随 Create/Update 直写（C-7 已强制 Create IsDefault=false、
    /// Update 冻结既有值），只能经本显式操作置位。目标版本须为 PUBLISHED（D-3「默认版本=已发布版本」定义）；
    /// 置位前先清同 Profile 其它默认避免 UQ_StrategyProfileVersion_DefaultPublished 冲突（镜像 Disable 既有关联操作）。
    /// </summary>
    public async Task SetDefaultStrategyProfileVersionAsync(long strategyProfileVersionId, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var version = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        // D-3 语义：默认版本须为已发布版本（DRAFT/SUBMITTED/APPROVED 尚未生效，DISABLED/ARCHIVED 已失效，均不可设为同族解析默认）
        if (version.Status != GovernanceVersionStatus.Published)
        {
            throw new InvalidOperationException($"仅 PUBLISHED 版本可设为默认（当前状态：{version.Status}），版本 Id：{strategyProfileVersionId}");
        }

        // 已是默认则幂等早退（避免无谓清默认+置位与审计）
        if (version.IsDefault)
        {
            return;
        }

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        // 清同 Profile 其它默认，再置位；避免 UQ_StrategyProfileVersion_DefaultPublished 冲突
        await _strategyProfileVersionRepository.ClearDefaultFlagAsync(version.StrategyProfileId, strategyProfileVersionId, ct);
        version.IsDefault = true;
        await _strategyProfileVersionRepository.UpdateAsync(version, ct);

        // P1-04 审计日志：记录默认设置操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "SetDefault",
            EntityType = "StrategyProfileVersion",
            EntityId = strategyProfileVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = "false",
            NewValue = "true",
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = "策略包版本设为默认"
        }, ct);
    }

    /// <summary>开发者：3号位</summary>
    public async Task<VersionDiffResult> CompareRuleSetVersionsAsync(long sourceVersionId, long targetVersionId, CancellationToken ct = default)
    {
        var sourceVersion = await _ruleSetVersionRepository.GetByIdAsync(sourceVersionId, ct)
            ?? throw new InvalidOperationException($"源规则集版本不存在：{sourceVersionId}");

        var targetVersion = await _ruleSetVersionRepository.GetByIdAsync(targetVersionId, ct)
            ?? throw new InvalidOperationException($"目标规则集版本不存在：{targetVersionId}");

        // P1-02：Diff 基于 ContentSnapshotJson 真实子块（兼容仅传主题 JSON 的装配路径，EnsureNormalized 幂等）
        EnsureRuleSetNormalized(sourceVersion);
        EnsureRuleSetNormalized(targetVersion);

        var diffs = new List<FieldDiff>
        {
            CompareField("VersionCode", "版本编码", sourceVersion.VersionCode, targetVersion.VersionCode),
            CompareField("Status", "状态", sourceVersion.Status, targetVersion.Status),
            CompareField("DemandPriority", "需求优先级配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "DemandPriority"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "DemandPriority")),
            // S-6：Setup 换型规则子块对比（fail-open：缺失/为空 → 空规则集，对比仍可安全执行）
            CompareField("SetupTransitionRules", "换型规则配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, SetupTransitionRuleProjector.SetupTransitionRulesBlockName),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, SetupTransitionRuleProjector.SetupTransitionRulesBlockName)),
            CompareField("EffectiveFrom", "生效起始", sourceVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("EffectiveTo", "生效截止", sourceVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedAt", "发布时间", sourceVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedBy", "发布人", sourceVersion.PublishedBy, targetVersion.PublishedBy),
            CompareField("Remarks", "备注", sourceVersion.Remarks, targetVersion.Remarks)
        };

        return new VersionDiffResult
        {
            SourceVersionId = sourceVersionId,
            TargetVersionId = targetVersionId,
            SourceVersionCode = sourceVersion.VersionCode,
            TargetVersionCode = targetVersion.VersionCode,
            EntityType = "RuleSetVersion",
            FieldDiffs = diffs,
            ComparedAt = DateTime.Now
        };
    }

    /// <summary>开发者：3号位</summary>
    public async Task<VersionDiffResult> CompareParameterSetVersionsAsync(long sourceVersionId, long targetVersionId, CancellationToken ct = default)
    {
        var sourceVersion = await _parameterSetVersionRepository.GetByIdAsync(sourceVersionId, ct)
            ?? throw new InvalidOperationException($"源参数集版本不存在：{sourceVersionId}");

        var targetVersion = await _parameterSetVersionRepository.GetByIdAsync(targetVersionId, ct)
            ?? throw new InvalidOperationException($"目标参数集版本不存在：{targetVersionId}");

        // P1-02：Diff 基于 ContentSnapshotJson 真实子块（兼容仅传主题 JSON 的装配路径，EnsureNormalized 幂等）
        EnsureParameterSetNormalized(sourceVersion);
        EnsureParameterSetNormalized(targetVersion);

        var diffs = new List<FieldDiff>
        {
            CompareField("VersionCode", "版本编码", sourceVersion.VersionCode, targetVersion.VersionCode),
            CompareField("Status", "状态", sourceVersion.Status, targetVersion.Status),
            CompareField("Lock", "锁定配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "Lock"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "Lock")),
            CompareField("Supply", "供给配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "Supply"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "Supply")),
            CompareField("Procurement", "采购配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "Procurement"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "Procurement")),
            CompareField("SolverStrategy", "Solver策略配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "SolverStrategy"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "SolverStrategy")),
            CompareField("CandidateGuardrail", "Candidate技术Guardrail配置",
                ExtractBlockJson(sourceVersion.ContentSnapshotJson, "CandidateGuardrail"),
                ExtractBlockJson(targetVersion.ContentSnapshotJson, "CandidateGuardrail")),
            CompareField("EffectiveFrom", "生效起始", sourceVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("EffectiveTo", "生效截止", sourceVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedAt", "发布时间", sourceVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedBy", "发布人", sourceVersion.PublishedBy, targetVersion.PublishedBy),
            CompareField("Remarks", "备注", sourceVersion.Remarks, targetVersion.Remarks)
        };

        return new VersionDiffResult
        {
            SourceVersionId = sourceVersionId,
            TargetVersionId = targetVersionId,
            SourceVersionCode = sourceVersion.VersionCode,
            TargetVersionCode = targetVersion.VersionCode,
            EntityType = "ParameterSetVersion",
            FieldDiffs = diffs,
            ComparedAt = DateTime.Now
        };
    }

    /// <summary>
    /// 对比两个策略包版本的差异（G5/D3：3-4联调，4号位策略包对比页；V1 可后置）
    /// 策略包无 ContentSnapshotJson（不含 JSON 内容，仅引用 RuleSet/ParameterSet 版本），
    /// 对比治理字段 + 引用字段（RuleSetVersionId/ParameterSetVersionId/IsDefault）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    public async Task<VersionDiffResult> CompareStrategyProfileVersionsAsync(long sourceVersionId, long targetVersionId, CancellationToken ct = default)
    {
        var sourceVersion = await _strategyProfileVersionRepository.GetByIdAsync(sourceVersionId, ct)
            ?? throw new InvalidOperationException($"源策略包版本不存在：{sourceVersionId}");

        var targetVersion = await _strategyProfileVersionRepository.GetByIdAsync(targetVersionId, ct)
            ?? throw new InvalidOperationException($"目标策略包版本不存在：{targetVersionId}");

        var diffs = new List<FieldDiff>
        {
            CompareField("VersionCode", "版本编码", sourceVersion.VersionCode, targetVersion.VersionCode),
            CompareField("Status", "状态", sourceVersion.Status, targetVersion.Status),
            CompareField("RuleSetVersionId", "引用的规则集版本", sourceVersion.RuleSetVersionId.ToString(), targetVersion.RuleSetVersionId.ToString()),
            CompareField("ParameterSetVersionId", "引用的参数集版本", sourceVersion.ParameterSetVersionId.ToString(), targetVersion.ParameterSetVersionId.ToString()),
            CompareField("IsDefault", "默认版本", sourceVersion.IsDefault.ToString(), targetVersion.IsDefault.ToString()),
            CompareField("EffectiveFrom", "生效起始", sourceVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveFrom?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("EffectiveTo", "生效截止", sourceVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.EffectiveTo?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedAt", "发布时间", sourceVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss"), targetVersion.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss")),
            CompareField("PublishedBy", "发布人", sourceVersion.PublishedBy, targetVersion.PublishedBy)
        };

        // 注：策略包版本表无 Remarks 列（与 RuleSet/ParameterSet 同），故不含 Remarks 对比字段。
        return new VersionDiffResult
        {
            SourceVersionId = sourceVersionId,
            TargetVersionId = targetVersionId,
            SourceVersionCode = sourceVersion.VersionCode,
            TargetVersionCode = targetVersion.VersionCode,
            EntityType = "StrategyProfileVersion",
            FieldDiffs = diffs,
            ComparedAt = DateTime.Now
        };
    }

    /// <summary>字段对比辅助方法</summary>
    /// <remarks>开发者：3号位</remarks>
    private static FieldDiff CompareField(string fieldName, string displayName, string? sourceValue, string? targetValue)
    {
        return new FieldDiff
        {
            FieldName = fieldName,
            FieldDisplayName = displayName,
            SourceValue = sourceValue,
            TargetValue = targetValue,
            IsChanged = sourceValue != targetValue
        };
    }

    /// <summary>开发者：3号位</summary>
    public async Task<PublishValidationResult> ValidateRuleSetVersionForPublishAsync(long ruleSetVersionId, CancellationToken ct = default)
    {
        var result = new PublishValidationResult
        {
            ValidatedAt = DateTime.Now
        };

        var version = await _ruleSetVersionRepository.GetByIdAsync(ruleSetVersionId, ct);
        if (version == null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NOT_FOUND",
                Message = $"规则集版本不存在：{ruleSetVersionId}"
            });
            result.IsValid = false;
            return result;
        }

        // 校验状态是否可发布
        if (!PublishableStatuses.Contains(version.Status))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_STATUS",
                Message = $"版本状态不可发布（当前 {version.Status}）",
                FieldName = "Status"
            });
        }

        // 校验版本编码非空
        if (string.IsNullOrWhiteSpace(version.VersionCode))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_VERSION_CODE",
                Message = "版本编码不能为空",
                FieldName = "VersionCode"
            });
        }
        else
        {
            // 校验版本编码同规则集内唯一（4号位 2026-10-14 审计 R：同 RuleSet 下 VersionCode 重复即治理脏数据，
            // publish 前置 fail-closed 拦截；新增校验不改接口签名）
            var siblings = await _ruleSetVersionRepository.GetByRuleSetIdAsync(version.RuleSetId, ct) ?? [];
            var normalizedCode = version.VersionCode.Trim();
            if (siblings.Any(s => s.Id != version.Id && string.Equals(s.VersionCode?.Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase)))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DUPLICATE_VERSION_CODE",
                    Message = $"同一规则集内版本编码已存在：{normalizedCode}",
                    FieldName = "VersionCode"
                });
            }
        }

        // 校验生效时间范围
        if (version.EffectiveFrom.HasValue && version.EffectiveTo.HasValue)
        {
            if (version.EffectiveFrom.Value >= version.EffectiveTo.Value)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_DATE_RANGE",
                    Message = "生效起始时间必须早于截止时间",
                    FieldName = "EffectiveFrom,EffectiveTo",
                    Details = $"起始: {version.EffectiveFrom:yyyy-MM-dd}, 截止: {version.EffectiveTo:yyyy-MM-dd}"
                });
            }
        }

        // 校验 DemandPriority 子块（P0-01/P1-02：基于 ContentSnapshotJson 真实持久化内容——
        // 不再读临时主题 JSON；兼容仅传主题 JSON 的装配路径，EnsureRuleSetNormalized 自动归一化到快照）
        EnsureRuleSetNormalized(version);
        var demandPriorityJson = ExtractBlockJson(version.ContentSnapshotJson, "DemandPriority");
        if (string.IsNullOrWhiteSpace(demandPriorityJson))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_DEMAND_PRIORITY",
                Message = "DemandPriorityJson 不能为空（规则集版本须含完整排序配置）",
                FieldName = "DemandPriorityJson"
            });
        }
        else
        {
            try
            {
                var block = System.Text.Json.JsonSerializer.Deserialize<DemandPriorityBlock>(
                    demandPriorityJson,
                    JsonOptions);

                if (block == null)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "INVALID_JSON",
                        Message = "DemandPriorityJson 反序列化结果为空",
                        FieldName = "DemandPriorityJson"
                    });
                }
                else
                {
                    var dpResult = _demandPriorityValidator.Validate(block);
                    foreach (var err in dpResult.Errors)
                    {
                        result.Errors.Add(new ValidationError
                        {
                            Code = "INVALID_DEMAND_PRIORITY",
                            Message = err,
                            FieldName = "DemandPriorityJson"
                        });
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_JSON",
                    Message = "DemandPriorityJson 格式无效",
                    FieldName = "DemandPriorityJson",
                    Details = ex.Message
                });
            }
        }

        // S-6：Setup 换型规则子块发布前校验（fail-open：缺失/为空 → 通过，无规则 DEFAULT 兜底；
        // 存在但 JSON 损坏 → 阻断，不静默吞错——写入口严格、读出口宽容，与 SetupRuleService 合法写路径零冲突）
        var setupBlockJson = ExtractBlockJson(version.ContentSnapshotJson, SetupTransitionRuleProjector.SetupTransitionRulesBlockName);
        if (!string.IsNullOrWhiteSpace(setupBlockJson))
        {
            try
            {
                var setupRules = System.Text.Json.JsonSerializer.Deserialize<List<LPS.APS.Core.Entities.APS.SetupTransitionRule>>(
                    setupBlockJson, JsonOptions);
                if (setupRules == null)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "INVALID_SETUP_TRANSITION_RULES",
                        Message = "SetupTransitionRules 子块反序列化结果为空",
                        FieldName = SetupTransitionRuleProjector.SetupTransitionRulesBlockName
                    });
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_SETUP_TRANSITION_RULES",
                    Message = "SetupTransitionRules 子块格式无效",
                    FieldName = SetupTransitionRuleProjector.SetupTransitionRulesBlockName,
                    Details = ex.Message
                });
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    /// <summary>开发者：3号位</summary>
    public async Task<PublishValidationResult> ValidateParameterSetVersionForPublishAsync(long parameterSetVersionId, CancellationToken ct = default)
    {
        var result = new PublishValidationResult
        {
            ValidatedAt = DateTime.Now
        };

        var version = await _parameterSetVersionRepository.GetByIdAsync(parameterSetVersionId, ct);
        if (version == null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NOT_FOUND",
                Message = $"参数集版本不存在：{parameterSetVersionId}"
            });
            result.IsValid = false;
            return result;
        }

        // 校验状态是否可发布
        if (!PublishableStatuses.Contains(version.Status))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_STATUS",
                Message = $"版本状态不可发布（当前 {version.Status}）",
                FieldName = "Status"
            });
        }

        // 校验版本编码非空
        if (string.IsNullOrWhiteSpace(version.VersionCode))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_VERSION_CODE",
                Message = "版本编码不能为空",
                FieldName = "VersionCode"
            });
        }

        // 校验生效时间范围
        if (version.EffectiveFrom.HasValue && version.EffectiveTo.HasValue)
        {
            if (version.EffectiveFrom.Value >= version.EffectiveTo.Value)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_DATE_RANGE",
                    Message = "生效起始时间必须早于截止时间",
                    FieldName = "EffectiveFrom,EffectiveTo",
                    Details = $"起始: {version.EffectiveFrom:yyyy-MM-dd}, 截止: {version.EffectiveTo:yyyy-MM-dd}"
                });
            }
        }

        // 校验五子块（P0-01/P1-02：基于 ContentSnapshotJson 真实持久化内容，不再读临时主题 JSON；
        // 兼容仅传主题 JSON 的装配路径，EnsureParameterSetNormalized 自动归一化到快照）
        EnsureParameterSetNormalized(version);
        ValidateLockJson(ExtractBlockJson(version.ContentSnapshotJson, "Lock"), result);
        ValidateSupplyJson(ExtractBlockJson(version.ContentSnapshotJson, "Supply"), result);
        ValidateProcurementJson(ExtractBlockJson(version.ContentSnapshotJson, "Procurement"), result);

        // P0-02b：SolverStrategy / CandidateGuardrail 两 Validator 接入发布链（E-4，契约 §6.10.5 校验器接线）
        ValidateSolverStrategyJson(ExtractBlockJson(version.ContentSnapshotJson, "SolverStrategy"), result);
        ValidateCandidateGuardrailJson(ExtractBlockJson(version.ContentSnapshotJson, "CandidateGuardrail"), result);

        // 0号位 裁决5：无效仓库引用校验（INVALID_WAREHOUSE_REF）——使用现有 APS 权威仓库事实源只读校验
        await ValidateWarehouseReferencesAsync(version.ContentSnapshotJson, result);

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    /// <summary>
    /// 无效仓库引用校验（0号位 裁决5，INVALID_WAREHOUSE_REF）
    /// 校验参数集配置中的仓库编码（Supply.Inventory.WarehousePriority、Procurement.WarehousePriority、
    /// DefaultPurchaseLt[].WarehouseCode、ArrivalToUsableOffsets[].WarehouseCode）是否存在于现有
    /// APS 权威仓库事实源（仓库字典跨库包装视图 ext_MES_ProcessCode_View 的 DISTINCT ProcessCode）。
    /// 约定：事实源为空（系统尚未同步任何仓库事实）时跳过校验，避免对未就绪数据源误判；
    /// 权威读模型已确认为 ext_MES_ProcessCode_View（2026-09-07 用户确认）。
    /// </summary>
    private async Task ValidateWarehouseReferencesAsync(string? contentSnapshotJson, PublishValidationResult result)
    {
        if (_connectionManager is null || string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            return;
        }

        // 1. 采集配置中引用的仓库编码（去重，忽略空串）
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var supplyJson = ExtractBlockJson(contentSnapshotJson, "Supply");
            var procurementJson = ExtractBlockJson(contentSnapshotJson, "Procurement");
            var supply = string.IsNullOrWhiteSpace(supplyJson)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<SupplyBlock>(supplyJson, JsonOptions);
            var procurement = string.IsNullOrWhiteSpace(procurementJson)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<ProcurementBlock>(procurementJson, JsonOptions);

            if (supply?.Inventory?.WarehousePriority is { } supplyWh)
            {
                foreach (var code in supplyWh) { if (!string.IsNullOrWhiteSpace(code)) referenced.Add(code); }
            }
            if (procurement?.WarehousePriority is { } procWh)
            {
                foreach (var code in procWh) { if (!string.IsNullOrWhiteSpace(code)) referenced.Add(code); }
            }
            if (procurement?.DefaultPurchaseLt is { } lt)
            {
                foreach (var rule in lt) { if (!string.IsNullOrWhiteSpace(rule.WarehouseCode)) referenced.Add(rule.WarehouseCode); }
            }
            if (procurement?.ArrivalToUsableOffsets is { } offsets)
            {
                foreach (var rule in offsets) { if (!string.IsNullOrWhiteSpace(rule.WarehouseCode)) referenced.Add(rule.WarehouseCode); }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 内容损坏已由五子块校验报错，此处不重复
            return;
        }

        if (referenced.Count == 0)
        {
            return;
        }

        // 2. 读取权威仓库事实源（仓库字典跨库包装视图 ext_MES_ProcessCode_View，APS；IsActive=1 已由视图内置）
        var existing = await _connectionManager.QueryAsync<string>(
            @"SELECT DISTINCT ProcessCode FROM [ext_MES_ProcessCode_View]",
            null,
            db: DatabaseId.APS);

        var existingSet = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        if (existingSet.Count == 0)
        {
            // 事实源未就绪（系统尚未同步任何仓库事实）：跳过校验，避免对未就绪数据源误判
            return;
        }

        // 3. 引用不存在 → INVALID_WAREHOUSE_REF
        foreach (var code in referenced.Where(c => !existingSet.Contains(c)).OrderBy(c => c, StringComparer.Ordinal))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_WAREHOUSE_REF",
                Message = $"参数配置引用了不存在的仓库编码：{code}（权威仓库事实源：ext_MES_ProcessCode_View 仓库字典）",
                FieldName = "Procurement/Warehouse"
            });
        }
    }

    /// <summary>校验 SolverStrategyJson：缺失/损坏/业务约束（E-4 SolverStrategyValidator 接线，P0-02b）</summary>
    private void ValidateSolverStrategyJson(string? json, PublishValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_SOLVER_STRATEGY",
                Message = "SolverStrategyJson 不能为空（参数集版本须含 Solver 策略配置）",
                FieldName = "SolverStrategyJson"
            });
            return;
        }

        try
        {
            var block = System.Text.Json.JsonSerializer.Deserialize<SolverStrategyBlock>(json, JsonOptions);
            if (block == null)
            {
                result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "SolverStrategyJson 反序列化结果为空", FieldName = "SolverStrategyJson" });
                return;
            }

            var ssResult = _solverStrategyValidator.Validate(block);
            foreach (var err in ssResult.Errors)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_SOLVER_STRATEGY",
                    Message = err,
                    FieldName = "SolverStrategyJson"
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "SolverStrategyJson 格式无效", FieldName = "SolverStrategyJson", Details = ex.Message });
        }
    }

    /// <summary>校验 CandidateGuardrailJson：缺失/损坏/业务约束（E-4 CandidateGuardrailValidator 接线，P0-02b）</summary>
    private void ValidateCandidateGuardrailJson(string? json, PublishValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_CANDIDATE_GUARDRAIL",
                Message = "CandidateGuardrailJson 不能为空（参数集版本须含 Candidate 技术 Guardrail 配置）",
                FieldName = "CandidateGuardrailJson"
            });
            return;
        }

        try
        {
            var block = System.Text.Json.JsonSerializer.Deserialize<CandidateGuardrailBlock>(json, JsonOptions);
            if (block == null)
            {
                result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "CandidateGuardrailJson 反序列化结果为空", FieldName = "CandidateGuardrailJson" });
                return;
            }

            var cgResult = _candidateGuardrailValidator.Validate(block);
            foreach (var err in cgResult.Errors)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_CANDIDATE_GUARDRAIL",
                    Message = err,
                    FieldName = "CandidateGuardrailJson"
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "CandidateGuardrailJson 格式无效", FieldName = "CandidateGuardrailJson", Details = ex.Message });
        }
    }

    /// <summary>校验 LockJson：缺失/损坏/业务约束（P0-05）</summary>
    private void ValidateLockJson(string? json, PublishValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_PARAMETER",
                Message = "LockJson 不能为空（参数集版本须含完整参数配置）",
                FieldName = "LockJson"
            });
            return;
        }

        try
        {
            var block = System.Text.Json.JsonSerializer.Deserialize<LockBlock>(json, JsonOptions);
            if (block == null)
            {
                result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "LockJson 反序列化结果为空", FieldName = "LockJson" });
                return;
            }

            // 触发阈值：启用 RemainingTime 阈值时其值必须为正
            if (block.Trigger.UseRemainingTimeThreshold && block.Trigger.RemainingTimeThresholdHours <= 0)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_LOCK_TRIGGER",
                    Message = "LockJson 启用 RemainingTime 阈值但阈值非正（RemainingTimeThresholdHours > 0 必须）",
                    FieldName = "LockJson"
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "LockJson 格式无效", FieldName = "LockJson", Details = ex.Message });
        }
    }

    /// <summary>校验 SupplyJson：缺失/损坏/业务约束（P0-05）</summary>
    private void ValidateSupplyJson(string? json, PublishValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_PARAMETER",
                Message = "SupplyJson 不能为空（参数集版本须含完整参数配置）",
                FieldName = "SupplyJson"
            });
            return;
        }

        try
        {
            var block = System.Text.Json.JsonSerializer.Deserialize<SupplyBlock>(json, JsonOptions);
            if (block == null)
            {
                result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "SupplyJson 反序列化结果为空", FieldName = "SupplyJson" });
                return;
            }

            // Warehouse 优先级顺序隐含优先级，重复项会导致歧义
            if (block.Inventory.WarehousePriority.Count != block.Inventory.WarehousePriority.Distinct().Count())
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_WAREHOUSE_PRIORITY",
                    Message = "SupplyJson 的 WarehousePriority 存在重复 Warehouse，优先级歧义",
                    FieldName = "SupplyJson"
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "SupplyJson 格式无效", FieldName = "SupplyJson", Details = ex.Message });
        }
    }

    /// <summary>校验 ProcurementJson：缺失/损坏/业务约束（P0-05：Planning Yield / 采购 LT / Offset / OverdueMargin）</summary>
    private void ValidateProcurementJson(string? json, PublishValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_PARAMETER",
                Message = "ProcurementJson 不能为空（参数集版本须含完整参数配置）",
                FieldName = "ProcurementJson"
            });
            return;
        }

        try
        {
            var block = System.Text.Json.JsonSerializer.Deserialize<ProcurementBlock>(json, JsonOptions);
            if (block == null)
            {
                result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "ProcurementJson 反序列化结果为空", FieldName = "ProcurementJson" });
                return;
            }

            foreach (var rule in block.PlanningYields)
            {
                if (rule.YieldPercent <= 0 || rule.YieldPercent > 100)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "INVALID_YIELD",
                        Message = $"PlanningYield 越界（0 < YieldPercent <= 100）：物料 {rule.MaterialId} = {rule.YieldPercent}",
                        FieldName = "ProcurementJson"
                    });
                }
            }

            foreach (var rule in block.DefaultPurchaseLt)
            {
                if (rule.DefaultLtDays <= 0)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "INVALID_PURCHASE_LT",
                        Message = $"DefaultPurchaseLt 越界（DefaultLtDays > 0）：Warehouse {rule.WarehouseCode} = {rule.DefaultLtDays}",
                        FieldName = "ProcurementJson"
                    });
                }
            }

            foreach (var offset in block.ArrivalToUsableOffsets)
            {
                if (offset.OffsetHours < 0)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "INVALID_OFFSET",
                        Message = $"ArrivalToUsableOffsets 越界（OffsetHours >= 0）：Warehouse {offset.WarehouseCode} = {offset.OffsetHours}",
                        FieldName = "ProcurementJson"
                    });
                }
            }

            if (block.OverdueMargin.MarginPercent is < 0 or > 100)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_OVERDUE_MARGIN",
                    Message = $"OverdueMargin.MarginPercent 越界（0~100）：{block.OverdueMargin.MarginPercent}",
                    FieldName = "ProcurementJson"
                });
            }

            if (block.OverdueMargin.MinimumExtraDays < 0)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "INVALID_OVERDUE_MARGIN",
                    Message = $"OverdueMargin.MinimumExtraDays 越界（>= 0）：{block.OverdueMargin.MinimumExtraDays}",
                    FieldName = "ProcurementJson"
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Errors.Add(new ValidationError { Code = "INVALID_JSON", Message = "ProcurementJson 格式无效", FieldName = "ProcurementJson", Details = ex.Message });
        }
    }

    /// <summary>JSON 反序列化选项（大小写不敏感）</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 发布时取 RuleSet 侧 ContentSnapshotJson（契约 §6.10.5 内容归属：DemandPriority 子块）。
    /// P0-01：快照为唯一内容真相；兼容仅传主题 JSON 的装配路径（单测）——先归一化再取快照，幂等；
    /// 缺失时防御性抛出（发布链 Validate 已保证非空，不应走到）。
    /// </summary>
    private static string BuildRuleSetContentSnapshot(RuleSetVersion version)
    {
        EnsureRuleSetNormalized(version);
        return version.ContentSnapshotJson
            ?? throw new InvalidOperationException($"规则集版本 {version.Id} 无内容快照，无法聚合发布");
    }

    /// <summary>
    /// 发布时取 ParameterSet 侧 ContentSnapshotJson（契约 §6.10.5 内容归属：Lock/Supply/Procurement/SolverStrategy/CandidateGuardrail 五子块）。
    /// P0-01：快照为唯一内容真相（同 RuleSet 语义，幂等）。
    /// </summary>
    private static string BuildParameterSetContentSnapshot(ParameterSetVersion version)
    {
        EnsureParameterSetNormalized(version);
        return version.ContentSnapshotJson
            ?? throw new InvalidOperationException($"参数集版本 {version.Id} 无内容快照，无法聚合发布");
    }

    // ==================== P0-01：内容持久化链辅助（ContentSnapshotJson 唯一真相，方案 A2） ====================

    /// <summary>
    /// 归一化：把 RuleSet 主题 JSON（内存/API 字段）→ ContentSnapshotJson 子块（真实持久化载体）。
    /// 语义：任一主题 JSON 非空即全量重建快照（主题 JSON 为编辑态真相）；全部为空则保持现有快照（真实 DB 重读场景，避免覆盖已落库内容）。
    /// </summary>
    private static void EnsureRuleSetNormalized(RuleSetVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.DemandPriorityJson))
        {
            return;
        }

        var blocks = new Dictionary<string, object>
        {
            ["DemandPriority"] = System.Text.Json.JsonSerializer.Deserialize<DemandPriorityBlock>(version.DemandPriorityJson, JsonOptions)
                ?? throw new InvalidOperationException($"规则集版本 {version.Id} 的 DemandPriorityJson 反序列化失败，无法归一化内容快照")
        };

        // S-6：Setup 换型规则子块由 SetupRuleService 直写 ContentSnapshotJson（独立写路径，不走主题 JSON），
        // 全量重建快照时须保留既有 SetupTransitionRules 子块，避免被 DemandPriority 归一化覆盖（数据丢失）。
        var setupBlockJson = ExtractBlockJson(version.ContentSnapshotJson, SetupTransitionRuleProjector.SetupTransitionRulesBlockName);
        if (!string.IsNullOrWhiteSpace(setupBlockJson))
        {
            blocks[SetupTransitionRuleProjector.SetupTransitionRulesBlockName] =
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(setupBlockJson, JsonOptions);
        }

        version.ContentSnapshotJson = System.Text.Json.JsonSerializer.Serialize(blocks);
    }

    /// <summary>归一化：把 ParameterSet 五主题 JSON（内存/API 字段）→ ContentSnapshotJson 五子块；全部为空则保持现有快照。</summary>
    private static void EnsureParameterSetNormalized(ParameterSetVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.LockJson)
            && string.IsNullOrWhiteSpace(version.SupplyJson)
            && string.IsNullOrWhiteSpace(version.ProcurementJson)
            && string.IsNullOrWhiteSpace(version.SolverStrategyJson)
            && string.IsNullOrWhiteSpace(version.CandidateGuardrailJson))
        {
            return;
        }

        var blocks = new Dictionary<string, object>();
        TryAddBlock(blocks, "Lock", version.LockJson, version.Id);
        TryAddBlock(blocks, "Supply", version.SupplyJson, version.Id);
        TryAddBlock(blocks, "Procurement", version.ProcurementJson, version.Id);
        TryAddBlock(blocks, "SolverStrategy", version.SolverStrategyJson, version.Id);
        TryAddBlock(blocks, "CandidateGuardrail", version.CandidateGuardrailJson, version.Id);

        // 第⑧块 Batch Policy：由发布投影/治理写路径直写 ContentSnapshotJson（独立写路径，不走主题 JSON），
        // 全量重建快照时须保留既有 BatchPolicy 子块，避免被五主题归一化覆盖（数据丢失，与 RuleSet 保留 SetupTransitionRules 同轨）。
        var batchPolicyJson = ExtractBlockJson(version.ContentSnapshotJson, TaskSplitRuleConfigProjector.BatchPolicyBlockName);
        if (!string.IsNullOrWhiteSpace(batchPolicyJson))
        {
            blocks[TaskSplitRuleConfigProjector.BatchPolicyBlockName] =
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(batchPolicyJson, JsonOptions);
        }

        version.ContentSnapshotJson = System.Text.Json.JsonSerializer.Serialize(blocks);
    }

    /// <summary>归一化辅助：非空主题 JSON 反序列化为 JsonElement 后加入快照字典；空则跳过（该子块在快照中缺键，发布前校验将报 EMPTY）。</summary>
    private static void TryAddBlock(Dictionary<string, object> blocks, string blockName, string? json, long versionId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        blocks[blockName] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json, JsonOptions);
    }

    /// <summary>
    /// 从 ContentSnapshotJson 提取指定子块的原始 JSON 字符串；快照为空、子块缺失或为 null 返回 null。
    /// 损坏快照视为子块缺失（由发布前校验报 EMPTY/INVALID，不静默吞错）。
    /// </summary>
    private static string? ExtractBlockJson(string? contentSnapshotJson, string blockName)
    {
        if (string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(contentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(blockName, out var block))
            {
                return null;
            }

            return block.ValueKind == System.Text.Json.JsonValueKind.Null ? null : block.GetRawText();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 投影：ContentSnapshotJson 的 DemandPriority + SetupTransitionRules 子块 → 主题 JSON 内存字段（GET 详情前端兼容）。
    /// C-2 整改（2026-10-17）：补 SetupTransitionRules 投影，镜像 DemandPriority——镜像参数集五主题投影方式，
    /// 规则集详情/published-version 端点回放版本对应 Setup 主题内容（0号位 复核 7.3）。SetupTransitionRules 块名
    /// 常量取自 <see cref="SetupTransitionRuleProjector.SetupTransitionRulesBlockName"/>（避免字面量漂移）。
    /// </summary>
    private static void ProjectRuleSetContent(RuleSetVersion version)
    {
        version.DemandPriorityJson = ExtractBlockJson(version.ContentSnapshotJson, "DemandPriority");
        version.SetupTransitionRulesJson = ExtractBlockJson(
            version.ContentSnapshotJson, SetupTransitionRuleProjector.SetupTransitionRulesBlockName);
        // rule-set 同族（2026-10-15）：快照载体为内部可重放 JSON，投影已回填主题子块后置空，
        // 镜像参数集 ProjectParameterSetContent（R2 修复）——前端经详情/published-version 端点读主题子块，不消费载体列；
        // 本方法仅读路径调用（详情 GetRuleSetVersionAsync / published-version GetPublishedRuleSetVersionAsync），
        // 不影响发布装配（Write 走 ContentSnapshotJson 持久化）。
        version.ContentSnapshotJson = null;
    }

    /// <summary>P0-02：状态机约束——已发布不可原地修改；失效/归档不可改；仅 DRAFT/SUBMITTED/APPROVED 可编辑。</summary>
    private static void EnsureUpdatable(string status, long versionId)
    {
        if (status == GovernanceVersionStatus.Published)
        {
            throw new InvalidOperationException($"版本已发布，不可原地修改（历史不可变，须创建新版本）：{versionId}");
        }

        if (status is GovernanceVersionStatus.Disabled or GovernanceVersionStatus.Archived)
        {
            throw new InvalidOperationException($"版本状态不可修改（当前 {status}）：{versionId}");
        }
    }

    // ==================== P0-06：StrategyProfileVersion 治理完整闭环 ====================

    public async Task<PublishValidationResult> ValidateStrategyProfileVersionForPublishAsync(long strategyProfileVersionId, CancellationToken ct = default)
    {
        var result = new PublishValidationResult
        {
            ValidatedAt = DateTime.Now
        };

        var version = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct);
        if (version == null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "NOT_FOUND",
                Message = $"策略包版本不存在：{strategyProfileVersionId}"
            });
            result.IsValid = false;
            return result;
        }

        // 状态是否可发布
        if (!PublishableStatuses.Contains(version.Status))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_STATUS",
                Message = $"版本状态不可发布（当前 {version.Status}）",
                FieldName = "Status"
            });
        }

        // 版本编码非空
        if (string.IsNullOrWhiteSpace(version.VersionCode))
        {
            result.Errors.Add(new ValidationError
            {
                Code = "EMPTY_VERSION_CODE",
                Message = "版本编码不能为空",
                FieldName = "VersionCode"
            });
        }

        // 引用合法性：RuleSetVersion / ParameterSetVersion 存在且 PUBLISHED（P0-06 引用合法性检查）
        await ValidateReferencedVersionAsync(
            _ruleSetVersionRepository.GetByIdAsync(version.RuleSetVersionId, ct),
            "RuleSetVersion", version.RuleSetVersionId, "规则集版本",
            v => v.Status, result, ct);
        await ValidateReferencedVersionAsync(
            _parameterSetVersionRepository.GetByIdAsync(version.ParameterSetVersionId, ct),
            "ParameterSetVersion", version.ParameterSetVersionId, "参数集版本",
            v => v.Status, result, ct);

        // 生效窗口
        if (version.EffectiveFrom.HasValue && version.EffectiveTo.HasValue
            && version.EffectiveFrom.Value >= version.EffectiveTo.Value)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "INVALID_DATE_RANGE",
                Message = "生效起始时间必须早于截止时间",
                FieldName = "EffectiveFrom,EffectiveTo",
                Details = $"起始: {version.EffectiveFrom:yyyy-MM-dd}, 截止: {version.EffectiveTo:yyyy-MM-dd}"
            });
        }

        // 默认歧义：本版本 IsDefault=1 且同 Profile 已存在另一 PUBLISHED 默认 → 报配置错误
        if (version.IsDefault)
        {
            var existingDefault = await _strategyProfileVersionRepository.GetDefaultByStrategyProfileIdAsync(version.StrategyProfileId, ct);
            if (existingDefault != null && existingDefault.Id != version.Id)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "DEFAULT_CONFLICT",
                    Message = $"策略包 {version.StrategyProfileId} 已存在 PUBLISHED 默认版本 {existingDefault.Id}（{existingDefault.VersionCode}），发布将违反 UQ_StrategyProfileVersion_DefaultPublished",
                    FieldName = "IsDefault"
                });
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    /// <summary>引用版本合法性：存在且 PUBLISHED（P0-06；泛型兼容 RuleSet/ParameterSet 不同实体类型，statusGetter 提取状态）</summary>
    private async Task ValidateReferencedVersionAsync<T>(
        Task<T?> task, string entityType, long versionId, string displayName,
        Func<T, string> statusGetter, PublishValidationResult result, CancellationToken ct)
        where T : class
    {
        var referenced = await task;
        if (referenced == null)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "REF_NOT_FOUND",
                Message = $"引用的{displayName}不存在：{versionId}",
                FieldName = entityType
            });
        }
        else if (statusGetter(referenced) != GovernanceVersionStatus.Published)
        {
            result.Errors.Add(new ValidationError
            {
                Code = "REF_NOT_PUBLISHED",
                Message = $"引用的{displayName}未发布（当前 {statusGetter(referenced)}）：{versionId}",
                FieldName = entityType
            });
        }
    }

    public async Task PublishStrategyProfileVersionAsync(long strategyProfileVersionId, string? publishedBy, int actorUserId, CancellationToken ct = default, string? changeReason = null)
    {
        var version = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        // P0-06：正式 Publish 强制发布前完整校验，无绕过路径（与 P0-05 一致）
        var validation = await ValidateStrategyProfileVersionForPublishAsync(strategyProfileVersionId, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"策略包版本发布前校验失败：{validation.GetErrorMessage()}");
        }

        EnsurePublishable(version.Status, strategyProfileVersionId);

        // P1-03：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        // IsDefault=1：先清同 Profile 其他默认再置位，避免 UQ_StrategyProfileVersion_DefaultPublished 冲突
        if (version.IsDefault)
        {
            await _strategyProfileVersionRepository.ClearDefaultFlagAsync(version.StrategyProfileId, strategyProfileVersionId, ct);
        }

        var beforeStatus = version.Status;
        version.Status = GovernanceVersionStatus.Published;
        version.PublishedAt = DateTime.Now;
        version.PublishedBy = publishedBy;

        await _strategyProfileVersionRepository.UpdateAsync(version, ct);

        // A-7 审计日志
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Publish",
            EntityType = "StrategyProfileVersion",
            EntityId = strategyProfileVersionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeStatus,
            NewValue = GovernanceVersionStatus.Published,
            UserId = actorUserId,
            UserCode = publishedBy,
            OccurredAt = DateTime.Now,
            Remark = string.IsNullOrWhiteSpace(changeReason) ? "策略包版本发布" : changeReason
        }, ct);
    }

    public async Task<StrategyProfileVersion?> ResolveDefaultStrategyProfileVersionAsync(string? runType, DateTime? asOf = null, CancellationToken ct = default)
    {
        // P0-06 跨号位冻结语义：无显式 StrategyProfileVersionId 时，必须得到当前有效、无歧义的 PUBLISHED 策略包
        if (string.IsNullOrWhiteSpace(runType))
        {
            throw new InvalidOperationException("解析默认策略包需要 RunType（StrategyProfile.RunType 匹配）");
        }

        var candidates = await _strategyProfileVersionRepository.GetDefaultByRunTypeAsync(runType, ct);
        var effective = asOf ?? DateTime.Now;

        // 过滤生效窗口：EffectiveFrom <= now（有值才校验）、EffectiveTo >= now（有值才校验）
        var inWindow = candidates
            .Where(v => (!v.EffectiveFrom.HasValue || v.EffectiveFrom.Value <= effective)
                     && (!v.EffectiveTo.HasValue || v.EffectiveTo.Value >= effective))
            .ToList();

        return inWindow.Count switch
        {
            0 => null,
            1 => inWindow[0],
            _ => throw new InvalidOperationException(
                $"RunType={runType} 的默认 PUBLISHED 策略包存在歧义：{inWindow.Count} 个候选（Id: {string.Join(", ", inWindow.Select(v => v.Id))}），须收敛为 1 个再执行"),
        };
    }

    // ==================== G3：当前 Published 版本便捷查询（3-4联调；C2） ====================

    /// <summary>
    /// 规则集当前生效 PUBLISHED 版本直达（G3/C2）
    /// 语义：单 Set 内 Status==PUBLISHED + 生效窗口（EffectiveFrom≤now≤EffectiveTo，有值才校验）过滤；
    /// 0 个 → null；恰 1 个 → 返回（RuleSet 投影 DemandPriorityJson，前端与 A3 详情一致）；&gt;1 个 → 歧义报错不随机取。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    public async Task<RuleSetVersion?> GetPublishedRuleSetVersionAsync(long ruleSetId, CancellationToken ct = default)
    {
        var versions = await _ruleSetVersionRepository.GetByRuleSetIdAsync(ruleSetId, ct);
        var published = SelectCurrentPublished(
            versions,
            v => v.Status, v => v.Id,
            v => v.EffectiveFrom, v => v.EffectiveTo,
            $"规则集 {ruleSetId}");

        if (published != null)
        {
            ProjectRuleSetContent(published);
        }
        return published;
    }

    /// <summary>
    /// 参数集当前生效 PUBLISHED 版本直达（G3/C2；五主题 JSON 投影，与 A10 详情一致；多候选报错收敛）
    /// 方案 A（2026-10-16）：五主题由仓储 DB 侧 OPENJSON 按块提取，ContentSnapshotJson 不读回应用层——消除 39s 根因。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    public async Task<ParameterSetVersion?> GetPublishedParameterSetVersionAsync(long parameterSetId, CancellationToken ct = default)
    {
        var versions = await _parameterSetVersionRepository.GetDetailByParameterSetIdAsync(parameterSetId, ct);
        return SelectCurrentPublished(
            versions,
            v => v.Status, v => v.Id,
            v => v.EffectiveFrom, v => v.EffectiveTo,
            $"参数集 {parameterSetId}");
    }

    /// <summary>
    /// 策略包当前生效 PUBLISHED 版本直达（G3/C2；无内容快照，仅治理+引用字段；多候选报错收敛）
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    public async Task<StrategyProfileVersion?> GetPublishedStrategyProfileVersionAsync(long strategyProfileId, CancellationToken ct = default)
    {
        var versions = await _strategyProfileVersionRepository.GetByStrategyProfileIdAsync(strategyProfileId, ct);
        return SelectCurrentPublished(
            versions,
            v => v.Status, v => v.Id,
            v => v.EffectiveFrom, v => v.EffectiveTo,
            $"策略包 {strategyProfileId}");
    }

    /// <summary>
    /// 从版本列表中挑选当前生效 PUBLISHED 版本（G3 通用辅助）
    /// 红线 #4：返回列表而非单对象，禁止盲目 First()——0/1/多 三态判定，多候选报错收敛不随机取。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    private static T? SelectCurrentPublished<T>(
        IReadOnlyList<T> versions,
        Func<T, string> statusGetter,
        Func<T, long> idGetter,
        Func<T, DateTime?> effectiveFromGetter,
        Func<T, DateTime?> effectiveToGetter,
        string displayName)
        where T : class
    {
        var now = DateTime.Now;
        // 物化一次可空生效窗口，避免同一 getter 两次调用导致 CS8629 流分析告警
        var inWindow = versions
            .Where(v => statusGetter(v) == GovernanceVersionStatus.Published)
            .Select(v => (Version: v, From: effectiveFromGetter(v), To: effectiveToGetter(v)))
            .Where(t => (!t.From.HasValue || t.From.Value <= now)
                     && (!t.To.HasValue || t.To.Value >= now))
            .Select(t => t.Version)
            .ToList();

        return inWindow.Count switch
        {
            0 => null,
            1 => inWindow[0],
            _ => throw new InvalidOperationException(
                $"{displayName} 存在多个当前生效 PUBLISHED 版本（Id: {string.Join(", ", inWindow.Select(v => idGetter(v)))}），须收敛为 1 个再执行（不随机取）"),
        };
    }

    public async Task<RunStrategyProfileTrace> GetRunStrategyProfileTraceAsync(long strategyProfileVersionId, CancellationToken ct = default)
    {
        var version = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        var profile = await _strategyProfileRepository.GetByIdAsync(version.StrategyProfileId, ct);
        var ruleSet = await _ruleSetVersionRepository.GetByIdAsync(version.RuleSetVersionId, ct);
        var parameterSet = await _parameterSetVersionRepository.GetByIdAsync(version.ParameterSetVersionId, ct);

        return new RunStrategyProfileTrace
        {
            StrategyProfileVersionId = version.Id,
            VersionCode = version.VersionCode,
            StrategyProfileId = version.StrategyProfileId,
            StrategyProfileCode = profile?.StrategyProfileCode,
            RunType = profile?.RunType,
            RuleSetVersionId = version.RuleSetVersionId,
            RuleSetVersionCode = ruleSet?.VersionCode,
            ParameterSetVersionId = version.ParameterSetVersionId,
            ParameterSetVersionCode = parameterSet?.VersionCode,
            Status = version.Status,
            EffectiveFrom = version.EffectiveFrom,
            EffectiveTo = version.EffectiveTo,
            IsDefault = version.IsDefault,
            PublishedAt = version.PublishedAt,
            PublishedBy = version.PublishedBy,
            // 4号位 2026-10-14 R4 止血：V1 不真查 ScheduleRun 维，恒返空数组（前端防崩；真追溯归 Q1 待 0号位 裁决）
            ReferencingRuns = Array.Empty<RunReferenceItem>(),
        };
    }

    // ==================== P0-01/P0-02：CRUD 状态机不可绕过（ContentSnapshotJson 唯一真相） ====================

    /// <summary>
    /// 创建规则集版本（P0-02：强制 DRAFT——入参 Status 一律忽略覆盖，状态只能经 Submit/Approve/Publish 流转，Create 不可直提 PUBLISHED；
    /// 治理字段一律置空，由后续流转写入）。
    /// P0-01：DRAFT 编辑的 DemandPriorityJson（内存/API 字段）归一化写入 ContentSnapshotJson 后持久化。
    /// P1-04：Create 审计（ActionCode=Create）+ 审计前置预检（fail-before-write，跨库无 2PC）。
    /// </summary>
    public async Task<RuleSetVersion> CreateRuleSetVersionAsync(RuleSetVersion version, string? createdBy, int actorUserId, CancellationToken ct = default)
    {
        version.Status = GovernanceVersionStatus.Draft;
        version.CreatedAt = DateTime.Now;
        version.CreatedBy = createdBy;
        version.PublishedAt = null;
        version.PublishedBy = null;
        version.ApprovedAt = null;
        version.ApprovedBy = null;

        EnsureRuleSetNormalized(version);

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        var created = await _ruleSetVersionRepository.AddAsync(version, ct);

        // P1-04 审计日志：记录创建操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Create",
            EntityType = "RuleSetVersion",
            EntityId = created.Id.ToString(),
            VersionCode = created.VersionCode,
            OldValue = null,
            NewValue = created.VersionCode,
            UserId = actorUserId,
            UserCode = createdBy,
            OccurredAt = DateTime.Now,
            Remark = "规则集版本创建"
        }, ct);

        return created;
    }

    /// <summary>
    /// 更新规则集版本（P0-02：已发布不可原地修改、失效/归档不可改；入参 Status/治理字段/主键一律以现有记录为准，禁止越权改状态）。
    /// P0-01：DRAFT 编辑内容归一化到 ContentSnapshotJson 后持久化（任一主题 JSON 非空则重建，全空保持现有快照）。
    /// P1-04：Update 审计（ActionCode=Update）+ 审计前置预检（fail-before-write，跨库无 2PC）。
    /// </summary>
    public async Task UpdateRuleSetVersionAsync(long ruleSetVersionId, RuleSetVersion version, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _ruleSetVersionRepository.GetByIdAsync(ruleSetVersionId, ct)
            ?? throw new InvalidOperationException($"规则集版本不存在：{ruleSetVersionId}");

        EnsureUpdatable(existing.Status, ruleSetVersionId);

        version.Id = existing.Id;
        version.RuleSetId = existing.RuleSetId;
        version.Status = existing.Status;
        version.CreatedAt = existing.CreatedAt;
        version.CreatedBy = existing.CreatedBy;
        version.PublishedAt = existing.PublishedAt;
        version.PublishedBy = existing.PublishedBy;
        version.ApprovedAt = existing.ApprovedAt;
        version.ApprovedBy = existing.ApprovedBy;
        // Q2（4号位 2026-10-15）：快照载体为内部可重放 JSON，generic Update 永不合法写它（内容经主题 JSON
        // 归一化或 SetupRuleService 独立写路径）。回填 existing 防止读改写回传 null 时 EnsureXxxNormalized
        // 重建快照丢弃独立子块（SetupTransitionRules 仅存于该列）；置于归一化之前使子块保留提取读到既有快照。
        version.ContentSnapshotJson = existing.ContentSnapshotJson;

        EnsureRuleSetNormalized(version);

        var beforeVersionCode = existing.VersionCode;

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _ruleSetVersionRepository.UpdateAsync(version, ct);

        // P1-04 审计日志：记录更新操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Update",
            EntityType = "RuleSetVersion",
            EntityId = version.Id.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeVersionCode,
            NewValue = version.VersionCode,
            UserId = actorUserId,
            UserCode = existing.CreatedBy,
            OccurredAt = DateTime.Now,
            Remark = "规则集版本更新"
        }, ct);
    }

    /// <summary>获取规则集版本详情（P0-01：ContentSnapshotJson → DemandPriorityJson 投影，前端 API 兼容）</summary>
    public async Task<RuleSetVersion?> GetRuleSetVersionAsync(long ruleSetVersionId, CancellationToken ct = default)
    {
        var version = await _ruleSetVersionRepository.GetByIdAsync(ruleSetVersionId, ct);
        if (version != null)
        {
            ProjectRuleSetContent(version);
        }
        return version;
    }

    /// <summary>创建参数集版本（P0-02：强制 DRAFT；治理字段置空；P0-01：五主题 JSON 归一化到 ContentSnapshotJson 持久化；P1-04：Create 审计+预检）</summary>
    public async Task<ParameterSetVersion> CreateParameterSetVersionAsync(ParameterSetVersion version, string? createdBy, int actorUserId, CancellationToken ct = default)
    {
        version.Status = GovernanceVersionStatus.Draft;
        version.CreatedAt = DateTime.Now;
        version.CreatedBy = createdBy;
        version.PublishedAt = null;
        version.PublishedBy = null;
        version.ApprovedAt = null;
        version.ApprovedBy = null;

        EnsureParameterSetNormalized(version);

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        var created = await _parameterSetVersionRepository.AddAsync(version, ct);

        // P1-04 审计日志：记录创建操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Create",
            EntityType = "ParameterSetVersion",
            EntityId = created.Id.ToString(),
            VersionCode = created.VersionCode,
            OldValue = null,
            NewValue = created.VersionCode,
            UserId = actorUserId,
            UserCode = createdBy,
            OccurredAt = DateTime.Now,
            Remark = "参数集版本创建"
        }, ct);

        return created;
    }

    /// <summary>更新参数集版本（P0-02：已发布/失效/归档不可改；Status/治理字段冻结；P0-01：内容归一化到快照；P1-04：Update 审计+预检）</summary>
    public async Task UpdateParameterSetVersionAsync(long parameterSetVersionId, ParameterSetVersion version, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _parameterSetVersionRepository.GetByIdAsync(parameterSetVersionId, ct)
            ?? throw new InvalidOperationException($"参数集版本不存在：{parameterSetVersionId}");

        EnsureUpdatable(existing.Status, parameterSetVersionId);

        version.Id = existing.Id;
        version.ParameterSetId = existing.ParameterSetId;
        version.Status = existing.Status;
        version.CreatedAt = existing.CreatedAt;
        version.CreatedBy = existing.CreatedBy;
        version.PublishedAt = existing.PublishedAt;
        version.PublishedBy = existing.PublishedBy;
        version.ApprovedAt = existing.ApprovedAt;
        version.ApprovedBy = existing.ApprovedBy;
        // Q2（4号位 2026-10-15）：回填 existing，防止读改写回传 null 时 EnsureXxxNormalized 重建快照丢弃
        // BatchPolicy 子块（仅存于该列；参数集读路径 R2 已置空，此隐患当前即存在）。
        version.ContentSnapshotJson = existing.ContentSnapshotJson;

        EnsureParameterSetNormalized(version);

        var beforeVersionCode = existing.VersionCode;

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _parameterSetVersionRepository.UpdateAsync(version, ct);

        // P1-04 审计日志：记录更新操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Update",
            EntityType = "ParameterSetVersion",
            EntityId = version.Id.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeVersionCode,
            NewValue = version.VersionCode,
            UserId = actorUserId,
            UserCode = existing.CreatedBy,
            OccurredAt = DateTime.Now,
            Remark = "参数集版本更新"
        }, ct);
    }

    /// <summary>
    /// 获取参数集版本详情（P0-01：五主题 JSON 投影，前端 API 兼容；方案 A 落点 2026-10-16：
    /// 改调仓储 DB 侧按块提取——五主题 JSON + 治理字段，ContentSnapshotJson 不读回，消除 SELECT * 大 LOB 搬运）
    /// </summary>
    public async Task<ParameterSetVersion?> GetParameterSetVersionAsync(long parameterSetVersionId, CancellationToken ct = default)
    {
        return await _parameterSetVersionRepository.GetDetailByIdAsync(parameterSetVersionId, ct);
    }

    /// <summary>创建策略包版本（P0-02：强制 DRAFT；治理字段置空；P1-04：Create 审计+预检）</summary>
    public async Task<StrategyProfileVersion> CreateStrategyProfileVersionAsync(StrategyProfileVersion version, string? createdBy, int actorUserId, CancellationToken ct = default)
    {
        version.Status = GovernanceVersionStatus.Draft;
        version.CreatedAt = DateTime.Now;
        version.CreatedBy = createdBy;
        version.PublishedAt = null;
        version.PublishedBy = null;
        version.ApprovedAt = null;
        version.ApprovedBy = null;
        // C-7 越权防护（0号位 §9.2：默认标记「不应由当前用户直接控制」）：新建一律强制非默认，
        // 用户在创建请求中即使传 isDefault=true 也不得写入；默认标记只能经 SetDefault 显式治理操作置位。
        version.IsDefault = false;

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        var created = await _strategyProfileVersionRepository.AddAsync(version, ct);

        // P1-04 审计日志：记录创建操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Create",
            EntityType = "StrategyProfileVersion",
            EntityId = created.Id.ToString(),
            VersionCode = created.VersionCode,
            OldValue = null,
            NewValue = created.VersionCode,
            UserId = actorUserId,
            UserCode = createdBy,
            OccurredAt = DateTime.Now,
            Remark = "策略包版本创建"
        }, ct);

        return created;
    }

    /// <summary>更新策略包版本（P0-02：已发布/失效/归档不可改；Status/治理字段/引用字段冻结；P1-04：Update 审计+预检）</summary>
    public async Task UpdateStrategyProfileVersionAsync(long strategyProfileVersionId, StrategyProfileVersion version, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _strategyProfileVersionRepository.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        EnsureUpdatable(existing.Status, strategyProfileVersionId);

        version.Id = existing.Id;
        version.StrategyProfileId = existing.StrategyProfileId;
        version.RuleSetVersionId = existing.RuleSetVersionId;
        version.ParameterSetVersionId = existing.ParameterSetVersionId;
        version.IsDefault = existing.IsDefault;
        version.Status = existing.Status;
        version.CreatedAt = existing.CreatedAt;
        version.CreatedBy = existing.CreatedBy;
        version.PublishedAt = existing.PublishedAt;
        version.PublishedBy = existing.PublishedBy;
        version.ApprovedAt = existing.ApprovedAt;
        version.ApprovedBy = existing.ApprovedBy;

        var beforeVersionCode = existing.VersionCode;

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _strategyProfileVersionRepository.UpdateAsync(version, ct);

        // P1-04 审计日志：记录更新操作
        await _auditLogRepository.AddAsync(new Core.Entities.Auth.AuditLog
        {
            ActionCode = "Update",
            EntityType = "StrategyProfileVersion",
            EntityId = version.Id.ToString(),
            VersionCode = version.VersionCode,
            OldValue = beforeVersionCode,
            NewValue = version.VersionCode,
            UserId = actorUserId,
            UserCode = existing.CreatedBy,
            OccurredAt = DateTime.Now,
            Remark = "策略包版本更新"
        }, ct);
    }
}
