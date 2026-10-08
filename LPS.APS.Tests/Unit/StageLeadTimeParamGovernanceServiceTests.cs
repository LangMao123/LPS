using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 阶段提前期参数治理服务单元测试（StageLeadTimeParam，3号位 规则参数体系）。
/// 覆盖：构造空值守卫、ListAsync 透传与过滤条件拼接、Create/Update/Deactivate 的输入校验红线
/// （FactoryCode/StageCode 非空、提前期非负且不同时为 0、Priority 非负、EffectiveTo ≥ EffectiveFrom）。
/// 说明：Create/Update/Deactivate 的 DB 写库 happy path 依赖 DatabaseConnectionManager 的非 virtual
/// QueryFirstOrDefaultAsync/ExecuteAsync，须由真实库集成测试覆盖，本单测不企及（见类注释边界）。
/// </summary>
public class StageLeadTimeParamGovernanceServiceTests
{
    private readonly Mock<IAuditLogRepository> _auditRepo = new();

    private static Mock<DatabaseConnectionManager> CreateConnectionMock()
    {
        var options = new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Production;" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=MES_Integration;" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Auth;" },
        };
        return new Mock<DatabaseConnectionManager>(Options.Create(options));
    }

    private StageLeadTimeParamGovernanceService CreateService(Mock<DatabaseConnectionManager> connection)
        => new(connection.Object, _auditRepo.Object, Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

    private static SaveStageLeadTimeParamRequest Request(
        string factoryCode = "BJ",
        string stageCode = "BJ_ASSY",
        decimal leadTimeDays = 1,
        decimal leadTimeHours = 0,
        int priority = 0,
        DateTime? effectiveFrom = null,
        DateTime? effectiveTo = null)
        => new()
        {
            FactoryCode = factoryCode,
            StageCode = stageCode,
            LeadTimeDays = leadTimeDays,
            LeadTimeHours = leadTimeHours,
            Priority = priority,
            EffectiveFrom = effectiveFrom ?? new DateTime(2026, 1, 1),
            EffectiveTo = effectiveTo,
        };

    // ---------- 构造守卫 ----------

    [Fact]
    public void 构造_connectionManager为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            null!, _auditRepo.Object, Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("connectionManager");
    }

    [Fact]
    public void 构造_auditLogRepository为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            CreateConnectionMock().Object, null!, Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("auditLogRepository");
    }

    [Fact]
    public void 构造_logger为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            CreateConnectionMock().Object, _auditRepo.Object, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ---------- ListAsync ----------

    [Fact]
    public async Task ListAsync_返回行()
    {
        var rows = new List<StageLeadTimeParamDto>
        {
            new() { Id = 1, FactoryCode = "BJ", StageCode = "BJ_ASSY", IsActive = true },
            new() { Id = 2, FactoryCode = "BJ", StageCode = "BJ_FINAL", IsActive = true },
        };
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(rows);

        var result = await CreateService(conn).ListAsync(null, null, null, null);

        result.Should().HaveCount(2);
        result[0].StageCode.Should().Be("BJ_ASSY");
        result[1].StageCode.Should().Be("BJ_FINAL");
    }

    [Fact]
    public async Task ListAsync_带过滤_拼入对应条件()
    {
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<StageLeadTimeParamDto>());

        await CreateService(conn).ListAsync("BJ", "BJ_ASSY", "DEPT-1", true);

        conn.Verify(c => c.QueryAsync<StageLeadTimeParamDto>(
            It.Is<string>(s => s.Contains("AND FactoryCode = @FactoryCode")
                            && s.Contains("AND StageCode = @StageCode")
                            && s.Contains("AND ProductionDeptCode = @ProductionDeptCode")
                            && s.Contains("AND IsActive = @IsActive")),
            It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }

    // ---------- Create 校验红线 ----------

    [Fact]
    public async Task CreateAsync_FactoryCode为空_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(factoryCode: ""), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("FactoryCode");
    }

    [Fact]
    public async Task CreateAsync_StageCode为空_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(stageCode: "  "), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("StageCode");
    }

    [Fact]
    public async Task CreateAsync_LeadTimeDays为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeDays: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_LeadTimeHours为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeHours: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_提前期同时为0_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeDays: 0, leadTimeHours: 0), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_Priority为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(priority: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_EffectiveTo早于EffectiveFrom_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(
            Request(effectiveFrom: new DateTime(2026, 1, 2), effectiveTo: new DateTime(2026, 1, 1)),
            1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    // ---------- Update / Deactivate 守卫 ----------

    [Fact]
    public async Task UpdateAsync_Id非正_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.UpdateAsync(0, Request(), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public async Task DeactivateAsync_Id非正_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.DeactivateAsync(0, 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("id");
    }
}