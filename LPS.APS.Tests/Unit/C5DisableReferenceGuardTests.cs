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
/// C-5/D-2 停用引用保护测试（2026-10-17，3号位）：
/// 0号位 统一复核裁决《未命名(2)(2)》§9.1（参数停用补引用关系检查）+ §4.2（D-2 方案a 阻断）：
/// 被停用版本若仍被「合法可供新Run使用的已发布策略包版本」引用（Status=PUBLISHED + 父 Profile IsActive=1 + 生效窗内），
/// 停用必须被阻断并抛引用冲突错误；无合法引用时照常停用。
/// 本文件零 DDL、零接口签名变更（仅新增仓储查询方法与停用预检）。
/// </summary>
public class C5DisableReferenceGuardTests
{
    private const int ActorUserId = 7;

    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _paramRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileRepo = new();
    private readonly GovernanceVersionService _service;

    public C5DisableReferenceGuardTests()
    {
        _service = new GovernanceVersionService(
            _ruleSetRepo.Object,
            _paramRepo.Object,
            Mock.Of<IStrategyProfileRepository>(),
            _strategyProfileRepo.Object,
            _auditRepo.Object);
    }

    // ==================== 参数停用引用保护 ====================

    [Fact]
    public async Task DisableParameter_被合法可新Run策略包引用_阻断停用抛引用冲突()
    {
        // Arrange
        var existing = new ParameterSetVersion { Id = 5, ParameterSetId = 50, VersionCode = "P-V1", Status = GovernanceVersionStatus.Published };
        _paramRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(null, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new StrategyProfileVersion { Id = 100, VersionCode = "SP-V1", RuleSetVersionId = 1, ParameterSetVersionId = 5,
                    Status = GovernanceVersionStatus.Published, EffectiveFrom = DateTime.Now.AddDays(-1), EffectiveTo = DateTime.Now.AddDays(1) }
            });

        // Act
        var act = () => _service.DisableParameterSetVersionAsync(5, "admin", ActorUserId);

        // Assert —— 引用保护阻断
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*禁止直接停用*")
            .WithMessage("*策略包版本Id: 100*");
    }

    [Fact]
    public async Task DisableParameter_引用策略包未来生效窗_仍阻断停用()
    {
        // Arrange —— 0号位 2026-10-17 §5.2 校正：引用方 PUBLISHED+父 IsActive 但 EffectiveFrom 尚未来临
        // （策略包今天发布、明天正式生效），今天停用其绑定参数版本 → 明天仍失去排程资格 → 阻断
        var existing = new ParameterSetVersion { Id = 5, ParameterSetId = 50, VersionCode = "P-V1", Status = GovernanceVersionStatus.Published };
        _paramRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(null, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new StrategyProfileVersion { Id = 300, VersionCode = "SP-V3", RuleSetVersionId = 1, ParameterSetVersionId = 5,
                    Status = GovernanceVersionStatus.Published, EffectiveFrom = DateTime.Now.AddDays(1), EffectiveTo = DateTime.Now.AddDays(30) }
            });

        // Act
        var act = () => _service.DisableParameterSetVersionAsync(5, "admin", ActorUserId);

        // Assert —— 未来生效窗引用仍阻断（D-2 检查范围校正）
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*禁止直接停用*")
            .WithMessage("*策略包版本Id: 300*");
    }

    [Fact]
    public async Task DisableParameter_引用策略包生效窗外_放行停用()
    {
        // Arrange —— 引用方 PUBLISHED+父 IsActive 但生效窗已过（EffectiveTo < now）→ 不再具新Run资格 → 放行
        var existing = new ParameterSetVersion { Id = 5, ParameterSetId = 50, VersionCode = "P-V1", Status = GovernanceVersionStatus.Published };
        _paramRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(null, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new StrategyProfileVersion { Id = 100, VersionCode = "SP-V1", RuleSetVersionId = 1, ParameterSetVersionId = 5,
                    Status = GovernanceVersionStatus.Published, EffectiveFrom = DateTime.Now.AddDays(-5), EffectiveTo = DateTime.Now.AddDays(-1) }
            });

        // Act
        var act = () => _service.DisableParameterSetVersionAsync(5, "admin", ActorUserId);

        // Assert —— 生效窗外引用不再阻断，停用照常（状态改 DISABLED）
        await act.Should().NotThrowAsync();
        existing.Status.Should().Be(GovernanceVersionStatus.Disabled);
    }

    [Fact]
    public async Task DisableParameter_无引用_放行停用()
    {
        // Arrange
        var existing = new ParameterSetVersion { Id = 5, ParameterSetId = 50, VersionCode = "P-V1", Status = GovernanceVersionStatus.Published };
        _paramRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(null, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StrategyProfileVersion>());

        // Act
        var act = () => _service.DisableParameterSetVersionAsync(5, "admin", ActorUserId);

        // Assert
        await act.Should().NotThrowAsync();
        existing.Status.Should().Be(GovernanceVersionStatus.Disabled);
    }

    // ==================== 规则停用引用保护（D-2 对称） ====================

    [Fact]
    public async Task DisableRule_被合法可新Run策略包引用_阻断停用抛引用冲突()
    {
        // Arrange
        var existing = new RuleSetVersion { Id = 3, RuleSetId = 30, VersionCode = "R-V1", Status = GovernanceVersionStatus.Published };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(3, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new StrategyProfileVersion { Id = 200, VersionCode = "SP-V2", RuleSetVersionId = 3, ParameterSetVersionId = 5,
                    Status = GovernanceVersionStatus.Published, EffectiveFrom = DateTime.Now.AddDays(-1), EffectiveTo = DateTime.Now.AddDays(1) }
            });

        // Act
        var act = () => _service.DisableRuleSetVersionAsync(3, "admin", ActorUserId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*禁止直接停用*")
            .WithMessage("*策略包版本Id: 200*");
    }

    [Fact]
    public async Task DisableRule_无引用_放行停用()
    {
        // Arrange
        var existing = new RuleSetVersion { Id = 3, RuleSetId = 30, VersionCode = "R-V1", Status = GovernanceVersionStatus.Published };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _strategyProfileRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(3, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StrategyProfileVersion>());

        // Act
        var act = () => _service.DisableRuleSetVersionAsync(3, "admin", ActorUserId);

        // Assert
        await act.Should().NotThrowAsync();
        existing.Status.Should().Be(GovernanceVersionStatus.Disabled);
    }
}