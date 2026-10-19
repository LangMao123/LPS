using FluentAssertions;
using LPS.APS.Application.Services;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// C-7 可写字段白名单 + 防越权 + /strategy-profiles 序列化核验测试（2026-10-17，3号位）：
/// 0号位 统一复核裁决《未命名(2)(2)》§9.2（创建/编辑字段白名单）：必须证明用户不能通过普通创建或编辑接口
/// 任意写入 Status、PublishedAt/PublishedBy、ApprovedAt/ApprovedBy、默认标记 IsDefault、其它正式治理元数据；
/// 后端不能只依赖前端隐藏。§9.3：GET /strategy-profiles 若保留主表实体响应，须确保无治理元数据外泄。
/// 本文件零 DDL、零接口签名变更（Create/Update 为既有方法内部白名单语义）。
/// </summary>
public class C7FieldWhitelistAuthzTests
{
    private const int ActorUserId = 7;

    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _paramRepo = new();
    private readonly Mock<IStrategyProfileRepository> _strategyProfileRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly GovernanceVersionService _service;

    public C7FieldWhitelistAuthzTests()
    {
        _service = new GovernanceVersionService(
            _ruleSetRepo.Object,
            _paramRepo.Object,
            _strategyProfileRepo.Object,
            _strategyProfileVersionRepo.Object,
            _auditRepo.Object);
    }

    // ==================== §9.2 Create 白名单：入参治理元数据一律强制覆盖，不得越权写入 ====================

    [Fact]
    public async Task CreateRuleSet_伪造Status与治理时间戳_强制DRAFT并置空()
    {
        var request = new RuleSetVersion
        {
            RuleSetId = 1,
            VersionCode = "R-V1",
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            PublishedBy = "attacker",
            ApprovedAt = DateTime.Now,
            ApprovedBy = "attacker"
        };
        _ruleSetRepo.Setup(r => r.AddAsync(It.IsAny<RuleSetVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RuleSetVersion v, CancellationToken _) => v);

        var created = await _service.CreateRuleSetVersionAsync(request, "creator", ActorUserId);

        created.Status.Should().Be(GovernanceVersionStatus.Draft);
        created.PublishedAt.Should().BeNull();
        created.PublishedBy.Should().BeNull();
        created.ApprovedAt.Should().BeNull();
        created.ApprovedBy.Should().BeNull();
        created.CreatedBy.Should().Be("creator");
    }

    [Fact]
    public async Task CreateParameterSet_伪造Status与治理时间戳_强制DRAFT并置空()
    {
        var request = new ParameterSetVersion
        {
            ParameterSetId = 1,
            VersionCode = "P-V1",
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            PublishedBy = "attacker",
            ApprovedAt = DateTime.Now,
            ApprovedBy = "attacker"
        };
        _paramRepo.Setup(r => r.AddAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ParameterSetVersion v, CancellationToken _) => v);

        var created = await _service.CreateParameterSetVersionAsync(request, "creator", ActorUserId);

        created.Status.Should().Be(GovernanceVersionStatus.Draft);
        created.PublishedAt.Should().BeNull();
        created.PublishedBy.Should().BeNull();
        created.ApprovedAt.Should().BeNull();
        created.ApprovedBy.Should().BeNull();
    }

    [Fact]
    public async Task CreateStrategyProfile_伪造Status治理时间戳与IsDefault_强制DRAFT非默认并置空()
    {
        // C-7 §9.2 新增：默认标记 IsDefault 同样为「不应由当前用户直接控制」的治理元数据，
        // Create 时必须强制 false（即使用户在创建请求里伪造 isDefault=true 也不得写入）。
        var request = new StrategyProfileVersion
        {
            StrategyProfileId = 1,
            VersionCode = "SP-V1",
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            PublishedBy = "attacker",
            ApprovedAt = DateTime.Now,
            ApprovedBy = "attacker",
            IsDefault = true   // 伪造默认标记越权写入
        };
        _strategyProfileVersionRepo.Setup(r => r.AddAsync(It.IsAny<StrategyProfileVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StrategyProfileVersion v, CancellationToken _) => v);

        var created = await _service.CreateStrategyProfileVersionAsync(request, "creator", ActorUserId);

        created.Status.Should().Be(GovernanceVersionStatus.Draft);
        created.PublishedAt.Should().BeNull();
        created.PublishedBy.Should().BeNull();
        created.ApprovedAt.Should().BeNull();
        created.ApprovedBy.Should().BeNull();
        created.IsDefault.Should().BeFalse();   // 默认标记不得由当前用户经 Create 置位
    }

    // ==================== §9.2 Update 白名单：入参治理元数据不得改变既有记录 ====================

    [Fact]
    public async Task UpdateRuleSet_草稿版本_入参伪造治理字段被冻结为既有值()
    {
        // DRAFT 为可更新状态（EnsureUpdatable 通过）；但治理元数据仍须冻结——
        // 用户即使在 Update 请求中伪造 Status=Published / PublishedBy / ApprovedBy，也被强制回填 existing 值。
        var existing = new RuleSetVersion
        {
            Id = 5,
            RuleSetId = 1,
            VersionCode = "R-V1",
            Status = GovernanceVersionStatus.Draft,
            CreatedAt = DateTime.Now.AddDays(-2),
            CreatedBy = "owner"
        };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var request = new RuleSetVersion
        {
            Id = 999,
            RuleSetId = 999,
            VersionCode = "R-V1",
            Status = GovernanceVersionStatus.Published,   // 越权伪造状态
            PublishedAt = DateTime.Now, PublishedBy = "attacker",
            ApprovedAt = DateTime.Now, ApprovedBy = "attacker"
        };

        await _service.UpdateRuleSetVersionAsync(5, request, ActorUserId);

        request.Status.Should().Be(GovernanceVersionStatus.Draft);   // 冻结为既有
        request.CreatedBy.Should().Be("owner");
        request.PublishedAt.Should().BeNull();
        request.PublishedBy.Should().BeNull();
        request.ApprovedAt.Should().BeNull();
        request.ApprovedBy.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStrategyProfile_草稿版本_入参伪IsDefaulttrue与引用字段被冻结为既有值()
    {
        var existing = new StrategyProfileVersion
        {
            Id = 5,
            StrategyProfileId = 1,
            VersionCode = "SP-V1",
            Status = GovernanceVersionStatus.Draft,
            RuleSetVersionId = 10,
            ParameterSetVersionId = 20,
            IsDefault = false
        };
        _strategyProfileVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var request = new StrategyProfileVersion
        {
            Id = 999,
            StrategyProfileId = 999,
            RuleSetVersionId = 999,
            ParameterSetVersionId = 999,
            VersionCode = "SP-V1",
            IsDefault = true,   // 越权伪造默认标记
            Status = GovernanceVersionStatus.Published
        };

        await _service.UpdateStrategyProfileVersionAsync(5, request, ActorUserId);

        request.IsDefault.Should().BeFalse();          // 默认标记冻结为既有
        request.RuleSetVersionId.Should().Be(10);       // 引用字段冻结
        request.ParameterSetVersionId.Should().Be(20);
        request.StrategyProfileId.Should().Be(1);
        request.Status.Should().Be(GovernanceVersionStatus.Draft);   // 状态冻结
    }

    // ==================== C-7 §9.2 显式 SetDefault 治理操作（默认标记唯一合法置位路径） ====================

    [Fact]
    public async Task SetDefault_DRAFT版本_拒绝置默认()
    {
        // 默认标记仅 PUBLISHED 版本可为同族解析默认；DRAFT 尚未生效，拒绝置位
        var draft = new StrategyProfileVersion { Id = 5, StrategyProfileId = 1, VersionCode = "SP-V1", Status = GovernanceVersionStatus.Draft };
        _strategyProfileVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(draft);

        var act = () => _service.SetDefaultStrategyProfileVersionAsync(5, "operator", ActorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*仅 PUBLISHED 版本可设为默认*");
    }

    [Fact]
    public async Task SetDefault_已发布非默认_清同族其它默认并置位_审计SetDefault()
    {
        var published = new StrategyProfileVersion { Id = 5, StrategyProfileId = 1, VersionCode = "SP-V1", Status = GovernanceVersionStatus.Published, IsDefault = false };
        _strategyProfileVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(published);

        var act = _service.SetDefaultStrategyProfileVersionAsync(5, "operator", ActorUserId);

        await act;
        _strategyProfileVersionRepo.Verify(r => r.ClearDefaultFlagAsync(1, 5, It.IsAny<CancellationToken>()), Times.Once);   // 清同族其它默认
        _strategyProfileVersionRepo.Verify(r => r.UpdateAsync(published, It.IsAny<CancellationToken>()), Times.Once);        // 置位
        published.IsDefault.Should().BeTrue();
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<LPS.APS.Core.Entities.Auth.AuditLog>(), It.IsAny<CancellationToken>()), Times.Once);   // 审计
    }

    [Fact]
    public async Task SetDefault_已发布且已是默认_幂等早退()
    {
        var alreadyDefault = new StrategyProfileVersion { Id = 5, StrategyProfileId = 1, VersionCode = "SP-V1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _strategyProfileVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(alreadyDefault);

        await _service.SetDefaultStrategyProfileVersionAsync(5, "operator", ActorUserId);

        // 已是默认 → 不执行清冲突/置位/审计
        _strategyProfileVersionRepo.Verify(r => r.ClearDefaultFlagAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _strategyProfileVersionRepo.Verify(r => r.UpdateAsync(It.IsAny<StrategyProfileVersion>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<LPS.APS.Core.Entities.Auth.AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== §9.3 /strategy-profiles 主表实体序列化安全性 ====================

    [Fact]
    public void StrategyProfileMainEntity_无治理版本字段_可公开为主表契约()
    {
        // §9.3：GET /strategy-profiles 返回 StrategyProfile 主表实体（非版本实体）。
        // 治理元数据（Status/PublishedAt/PublishedBy/ApprovedAt/ApprovedBy/IsDefault）全部承载于
        // StrategyProfileVersion 版本表，主表实体无这些成员——序列化时天然不外泄版本治理字段。
        var profile = typeof(LPS.APS.Core.Entities.APS.StrategyProfile);

        // 主表仅保留可公开的主数据字段
        Assert.Null(profile.GetProperty("Status"));
        Assert.Null(profile.GetProperty("PublishedAt"));
        Assert.Null(profile.GetProperty("PublishedBy"));
        Assert.Null(profile.GetProperty("ApprovedAt"));
        Assert.Null(profile.GetProperty("ApprovedBy"));
        Assert.Null(profile.GetProperty("IsDefault"));

        // 主表公开字段即契约字段
        Assert.NotNull(profile.GetProperty("StrategyProfileCode"));
        Assert.NotNull(profile.GetProperty("StrategyProfileName"));
        Assert.NotNull(profile.GetProperty("RunType"));
        Assert.NotNull(profile.GetProperty("IsActive"));
    }
}