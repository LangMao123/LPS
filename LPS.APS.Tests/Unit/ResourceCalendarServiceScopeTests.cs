using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// S5-01 整改冒烟（2026-10-14，5号位）：设备资源日历 Scope 二次校验。
/// 纯单测全 mock（假 localhost 连接串，照 RoutingOperationGovernanceServiceTests 先例），零真实库连接。
/// 覆盖：越界写/删/读 → ScopeViolationException 且**写库方法零调用**（审计放行证据"403 且数据库无副作用"单测形态）；
/// Global/授权内 → 正常落库。
/// </summary>
public class ResourceCalendarServiceScopeTests
{
    private const int Actor = 7;
    private const int ResourceId = 6444;

    private readonly Mock<IDataScopeService> _dataScope = new();

    private static Mock<DatabaseConnectionManager> CreateConnectionMock()
    {
        var options = new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Mock;" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=MES_Integration_Mock;" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Auth_Mock;" },
        };
        return new Mock<DatabaseConnectionManager>(Microsoft.Extensions.Options.Options.Create(options));
    }

    private ResourceCalendarService CreateService(Mock<DatabaseConnectionManager> conn)
        => new(conn.Object, _dataScope.Object);

    /// <summary>解析查询回带：resource（r.Id）或窗口反查（rc.Id）→ (FactoryCode, DeptCode)。</summary>
    private static void SetupResolution(Mock<DatabaseConnectionManager> conn, string? factoryCode, string? deptCode, bool byWindow = false)
    {
        conn.Setup(c => c.QueryFirstOrDefaultAsync<(string? FactoryCode, string? DeptCode)?>(
                It.Is<string>(s => byWindow
                    ? s.Contains("rc.Id = @Id")
                    : s.Contains("r.Id = @Id") && !s.Contains("rc.")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(factoryCode is null ? null : (factoryCode, deptCode));
    }

    private void SetupScope(params (string Type, string Value)[] grants)
        => _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(grants));

    private static ResourceCalendarBulkRequest BuildBulk(int days = 1) => new()
    {
        ResourceId = ResourceId,
        StartDate = new DateTime(2026, 11, 1),
        Days = days,
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(17, 0, 0),
        AvailableFlag = true,
    };

    // ---------- 写：BulkCreate ----------

    [Fact]
    public async Task BulkCreate_工厂越界_抛ScopeViolation且零写库()
    {
        var conn = CreateConnectionMock();
        SetupResolution(conn, "CN", "DEPT-B");
        SetupScope((DataScopeTypes.Factory, "BJ"), (DataScopeTypes.Department, "DEPT-B"));

        Func<Task> act = () => CreateService(conn).BulkCreateAsync(BuildBulk(), Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task BulkCreate_部门越界_抛ScopeViolation且零写库()
    {
        // 厂在授权内、部门不在 → 跨维 AND 仍拒绝（StageLeadTimeParam P0-03 语义）
        var conn = CreateConnectionMock();
        SetupResolution(conn, "BJ", "DEPT-A");
        SetupScope((DataScopeTypes.Factory, "BJ"), (DataScopeTypes.Department, "DEPT-B"));

        Func<Task> act = () => CreateService(conn).BulkCreateAsync(BuildBulk(), Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task BulkCreate_Global_放行并落库()
    {
        var conn = CreateConnectionMock();
        SetupResolution(conn, "BJ", "DEPT-A");
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(DataScopeContext.Global);
        conn.Setup(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(1);

        var inserted = await CreateService(conn).BulkCreateAsync(BuildBulk(days: 1), Actor);

        inserted.Should().Be(1);
        // 覆盖式：1 次 DELETE + Days 次 INSERT
        conn.Verify(c => c.ExecuteAsync(It.Is<string>(s => s.TrimStart().StartsWith("DELETE")), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
        conn.Verify(c => c.ExecuteAsync(It.Is<string>(s => s.TrimStart().StartsWith("INSERT")), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }

    // ---------- 读：GetByResource ----------

    [Fact]
    public async Task GetByResource_越界_抛ScopeViolation且不查窗口()
    {
        var conn = CreateConnectionMock();
        SetupResolution(conn, "CN", "DEPT-A");
        SetupScope((DataScopeTypes.Factory, "BJ"));

        Func<Task> act = () => CreateService(conn).GetByResourceAsync(ResourceId, null, null, 1, 20, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.QueryAsync<LPS.APS.Application.Services.Dto.ResourceCalendarEntryDto>(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    // ---------- 删：Delete ----------

    [Fact]
    public async Task Delete_越界_抛ScopeViolation且零DELETE()
    {
        var conn = CreateConnectionMock();
        SetupResolution(conn, "CN", "DEPT-A", byWindow: true);
        SetupScope((DataScopeTypes.Factory, "BJ"));

        Func<Task> act = () => CreateService(conn).DeleteAsync(900L, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task Delete_窗口不存在_返回false且不做Scope查询()
    {
        var conn = CreateConnectionMock();
        SetupResolution(conn, null, null, byWindow: true);

        var ok = await CreateService(conn).DeleteAsync(900L, Actor);

        ok.Should().BeFalse();
        _dataScope.Verify(d => d.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- 参数守卫回归（Scope 校验不吞参数校验） ----------

    [Fact]
    public async Task BulkCreate_Days越界_先抛参数异常不碰库()
    {
        var conn = CreateConnectionMock();
        var req = BuildBulk(days: 371);

        Func<Task> act = () => CreateService(conn).BulkCreateAsync(req, Actor);

        await act.Should().ThrowAsync<ArgumentException>();
        conn.VerifyNoOtherCalls();
    }
}