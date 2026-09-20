using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Interfaces;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// SetupTransitionRuleService 单元测试（阶段 E-4 扩展）。
/// 使用手写 Fake（仓储 + 审计）替代 Moq 动态代理，规避 5号位 曾遇的 A 类 Moq 失败。
/// 覆盖：Create/Update/Delete/List/Get、§十 唯一键冲突（EXACT/DEFAULT）、audit fail-closed。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class SetupTransitionRuleServiceTests
{
    private readonly FakeRepository _repo = new();
    private readonly FakeAuditRepository _audit = new();
    private readonly SetupTransitionRuleService _sut;

    public SetupTransitionRuleServiceTests()
    {
        _sut = new SetupTransitionRuleService(_repo, _audit);
    }

    // ---------- Create ----------

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_无冲突_落库并落Create审计()
    {
        // Arrange
        var rule = Exact(minutes: 25m);

        // Act
        var id = await _sut.CreateAsync(rule, actorUserId: 7, actorUserCode: "u7");

        // Assert
        id.Should().BeGreaterThan(0);
        _repo.Rows.Should().ContainSingle().Which.SetupMinutes.Should().Be(25m);

        var log = _audit.Logs.Should().ContainSingle().Subject;
        log.ActionCode.Should().Be("Create");
        log.EntityType.Should().Be(SetupTransitionRuleService.EntityTypeSetupTransitionRule);
        log.EntityId.Should().Be(id.ToString());
        log.VersionCode.Should().Be(rule.RuleSetVersionId.ToString());
        log.UserId.Should().Be(7);
        log.UserCode.Should().Be("u7");
        log.OldValue.Should().BeNull();
        log.NewValue.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_EXACT唯一键冲突_抛异常_不落库不落审计()
    {
        // Arrange
        await _repo.AddAsync(Exact(fromMat: 1000, toMat: 2000));
        var duplicate = Exact(fromMat: 1000, toMat: 2000, minutes: 99m);

        // Act
        var act = async () => await _sut.CreateAsync(duplicate, 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*唯一键冲突*");
        _repo.Rows.Should().ContainSingle();
        _audit.Logs.Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_DEFAULT唯一键冲突_抛异常()
    {
        // Arrange
        await _repo.AddAsync(Default(op: "OP10", resource: 100));
        var duplicate = Default(op: "OP10", resource: 100, minutes: 88m);

        // Act
        var act = async () => await _sut.CreateAsync(duplicate, 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*唯一键冲突*");
        _repo.Rows.Should().ContainSingle();
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_审计不可写_抛异常_且不落库()
    {
        // Arrange
        _audit.FailEnsureWritable = true;

        // Act
        var act = async () => await _sut.CreateAsync(Exact(), 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _repo.Rows.Should().BeEmpty();
    }

    // ---------- Update ----------

    [Fact]
    public async System.Threading.Tasks.Task UpdateAsync_更新成功_审计含变更前后快照()
    {
        // Arrange
        var id = await _repo.AddAsync(Exact(minutes: 30m));
        var updated = Exact(minutes: 55m);
        updated.Id = id;

        // Act
        await _sut.UpdateAsync(updated, 7, "u7");

        // Assert
        _repo.FindById(id)!.SetupMinutes.Should().Be(55m);

        var log = _audit.Logs.Should().ContainSingle().Subject;
        log.ActionCode.Should().Be("Update");
        log.EntityId.Should().Be(id.ToString());
        log.OldValue.Should().NotBeNullOrEmpty();
        log.NewValue.Should().NotBeNullOrEmpty();
        log.OldValue.Should().NotBe(log.NewValue);
    }

    [Fact]
    public async System.Threading.Tasks.Task UpdateAsync_目标不存在_抛异常()
    {
        // Arrange
        var ghost = Exact();
        ghost.Id = 999;

        // Act
        var act = async () => await _sut.UpdateAsync(ghost, 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
        _audit.Logs.Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task UpdateAsync_改为冲突键_抛异常_保持原值()
    {
        // Arrange
        var idA = await _repo.AddAsync(Exact(fromMat: 100, toMat: 200, minutes: 10m));
        await _repo.AddAsync(Exact(fromMat: 300, toMat: 400, minutes: 20m));

        var updated = Exact(fromMat: 300, toMat: 400, minutes: 99m);
        updated.Id = idA;

        // Act
        var act = async () => await _sut.UpdateAsync(updated, 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _repo.FindById(idA)!.FromMaterialId.Should().Be(100);
        _repo.FindById(idA)!.SetupMinutes.Should().Be(10m);
        _audit.Logs.Should().BeEmpty();
    }

    // ---------- Delete ----------

    [Fact]
    public async System.Threading.Tasks.Task DeleteAsync_删除成功_审计含删除前快照_新值为空()
    {
        // Arrange
        var id = await _repo.AddAsync(Exact(minutes: 40m));

        // Act
        await _sut.DeleteAsync(id, 7, "u7");

        // Assert
        _repo.Rows.Should().BeEmpty();

        var log = _audit.Logs.Should().ContainSingle().Subject;
        log.ActionCode.Should().Be("Delete");
        log.EntityId.Should().Be(id.ToString());
        log.OldValue.Should().NotBeNullOrEmpty();
        log.NewValue.Should().BeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteAsync_目标不存在_抛异常()
    {
        // Act
        var act = async () => await _sut.DeleteAsync(999, 7, "u7");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
        _audit.Logs.Should().BeEmpty();
    }

    // ---------- List / Get ----------

    [Fact]
    public async System.Threading.Tasks.Task ListAsync与GetByIdAsync_按版本与主键正确返回()
    {
        // Arrange
        var id = await _repo.AddAsync(Exact(version: 1));
        await _repo.AddAsync(Exact(version: 2, op: "OP20"));
        await _repo.AddAsync(Default(version: 1, op: "OP30"));

        // Act
        var list = await _sut.ListAsync(ruleSetVersionId: 1);
        var got = await _sut.GetByIdAsync(id);

        // Assert
        list.Should().HaveCount(2);
        got.Should().NotBeNull();
        got!.Id.Should().Be(id);
    }

    // ---------- 工厂与 Fake ----------

    private static SetupTransitionRule Exact(
        decimal minutes = 30m,
        int dept = 1,
        string stage = "STG",
        string op = "OP10",
        int resource = 100,
        int fromMat = 1000,
        int toMat = 2000,
        long version = 1) => new()
    {
        RuleSetVersionId = version,
        ProductionDepartmentId = dept,
        StageCode = stage,
        OperationCode = op,
        ResourceId = resource,
        FromMaterialId = fromMat,
        ToMaterialId = toMat,
        RuleType = SetupTransitionRuleType.Exact,
        SetupMinutes = minutes,
        IsActive = true,
    };

    private static SetupTransitionRule Default(
        decimal minutes = 45m,
        int dept = 1,
        string stage = "STG",
        string op = "OP10",
        int resource = 100,
        long version = 1) => new()
    {
        RuleSetVersionId = version,
        ProductionDepartmentId = dept,
        StageCode = stage,
        OperationCode = op,
        ResourceId = resource,
        FromMaterialId = null,
        ToMaterialId = null,
        RuleType = SetupTransitionRuleType.Default,
        SetupMinutes = minutes,
        IsActive = true,
    };

    private sealed class FakeRepository : ISetupTransitionRuleRepository
    {
        public readonly List<SetupTransitionRule> Rows = new();
        private long _nextId = 1;

        public SetupTransitionRule? FindById(long id) => Rows.FirstOrDefault(r => r.Id == id);

        public Task<IReadOnlyList<SetupTransitionRule>> GetByRuleSetVersionAsync(long ruleSetVersionId, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<SetupTransitionRule>>(
                Rows.Where(r => r.RuleSetVersionId == ruleSetVersionId).ToList());

        public Task<SetupTransitionRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(FindById(id));

        public Task<long> AddAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default)
        {
            var clone = Clone(rule);
            clone.Id = _nextId++;
            Rows.Add(clone);
            return System.Threading.Tasks.Task.FromResult(clone.Id);
        }

        public System.Threading.Tasks.Task UpdateAsync(SetupTransitionRule rule, CancellationToken cancellationToken = default)
        {
            var index = Rows.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
            {
                Rows[index] = Clone(rule);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(r => r.Id == id);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static SetupTransitionRule Clone(SetupTransitionRule source) => new()
        {
            Id = source.Id,
            RuleSetVersionId = source.RuleSetVersionId,
            ProductionDepartmentId = source.ProductionDepartmentId,
            StageCode = source.StageCode,
            OperationCode = source.OperationCode,
            ResourceId = source.ResourceId,
            FromMaterialId = source.FromMaterialId,
            ToMaterialId = source.ToMaterialId,
            RuleType = source.RuleType,
            SetupMinutes = source.SetupMinutes,
            IsActive = source.IsActive,
            CreatedAt = source.CreatedAt,
            CreatedBy = source.CreatedBy,
            UpdatedAt = source.UpdatedAt,
            UpdatedBy = source.UpdatedBy,
        };
    }

    private sealed class FakeAuditRepository : IAuditLogRepository
    {
        public readonly List<AuditLog> Logs = new();
        public bool FailEnsureWritable { get; set; }

        public System.Threading.Tasks.Task EnsureWritableAsync(CancellationToken cancellationToken = default)
        {
            if (FailEnsureWritable)
            {
                throw new InvalidOperationException("审计库不可写（模拟）");
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public Task<AuditLog> AddAsync(AuditLog entity, CancellationToken cancellationToken = default)
        {
            Logs.Add(entity);
            return System.Threading.Tasks.Task.FromResult(entity);
        }

        public Task<AuditLog?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<AuditLog?>(null);

        public Task<IEnumerable<AuditLog>> GetLogsByUserAsync(int userId, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IEnumerable<AuditLog>>(Array.Empty<AuditLog>());

        public Task<IEnumerable<AuditLog>> GetLogsByDateRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IEnumerable<AuditLog>>(Array.Empty<AuditLog>());

        public Task<IReadOnlyList<AuditLog>> GetByEntityAsync(string entityType, string entityId, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<AuditLog>>(Array.Empty<AuditLog>());

        public Task<IReadOnlyList<AuditLog>> QueryAsync(string? entityType = null, string? entityId = null, DateTime? from = null, DateTime? to = null, int? take = null, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<AuditLog>>(Array.Empty<AuditLog>());

        public Task<IReadOnlyList<AuditLog>> QueryPagedAsync(int? userId = null, string? action = null, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<AuditLog>>(Array.Empty<AuditLog>());
    }
}