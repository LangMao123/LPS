using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// S5-01 整改冒烟（2026-10-14，5号位）：工厂/部门上级工作日历 Scope 二次校验 + 读授权过滤。
/// 纯单测全 mock（假连接串），零真实库。校验语义（照 StageLeadTimeParam P0-03 先例）：
/// 非 Global：FACTORY 行须 Factory.Code ∈ 授权；DEPARTMENT 行另须 DeptCode ∈ 授权（不同维度 AND）；
/// 越界抛 ScopeViolationException 且任何写方法零调用；行级操作（close/delete）Scope 先于业务校验。
/// </summary>
public class ProductionWorkCalendarServiceScopeTests
{
    private const int Actor = 7;
    private const int FactoryInScope = 2;   // Code="CN"
    private const int DeptUnderTest = 21;   // DeptCode="DEPT-A"，归属工厂 Id=7

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

    private ProductionWorkCalendarService CreateService(Mock<DatabaseConnectionManager> conn)
        => new(conn.Object, _dataScope.Object);

    private static void SetupFactoryCode(Mock<DatabaseConnectionManager> conn, string? code)
    {
        conn.Setup(c => c.QueryFirstOrDefaultAsync<string?>(
                It.Is<string>(s => s.Contains("Code FROM dbo.Factory")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(code);
    }

    /// <summary>DEPARTMENT 行：部门(归属工厂 Id, DeptCode)回带。</summary>
    private static void SetupDeptRow(Mock<DatabaseConnectionManager> conn, int ownerFactoryId, string deptCode)
    {
        conn.Setup(c => c.QueryFirstOrDefaultAsync<(int FactoryId, string DeptCode)?>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((ownerFactoryId, deptCode));
    }

    private void SetupScope(params (string Type, string Value)[] grants)
    {
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(grants));
    }

    private static WeeklyCalendarUpsertRequest BuildWeekly(string scopeType, int factoryId, int? deptId) => new()
    {
        ScopeType = scopeType,
        FactoryId = factoryId,
        ProductionDepartmentId = deptId,
        EffectiveFrom = new DateTime(2026, 11, 1),
        Entries = new List<WeeklyCalendarEntry>
        {
            new() { WeekdayIso = 1, WorkState = "WORK", StartAt = new TimeSpan(8, 0, 0), EndAt = new TimeSpan(17, 0, 0) },
        },
    };

    // ---------- 写路径：FACTORY / DEPARTMENT 行 ----------

    [Fact]
    public async Task UpsertWeekly_工厂行越界_抛ScopeViolation且零写库()
    {
        var conn = CreateConnectionMock();
        SetupFactoryCode(conn, "CN");
        SetupScope((DataScopeTypes.Factory, "BJ"), (DataScopeTypes.Department, "DEPT-A"));

        Func<Task> act = () => CreateService(conn).UpsertWeeklyAsync(BuildWeekly("FACTORY", FactoryInScope, null), Actor, "u");

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        conn.Verify(c => c.QueryFirstOrDefaultAsync<long>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task UpsertWeekly_部门行厂授权但部门越界_抛且零写库()
    {
        // 跨维 AND：Factory=CN 授权内、Department 只授 DEPT-B，目标 DEPT-A → 仍拒
        var conn = CreateConnectionMock();
        SetupFactoryCode(conn, "CN");
        SetupDeptRow(conn, FactoryInScope, "DEPT-A");
        SetupScope((DataScopeTypes.Factory, "CN"), (DataScopeTypes.Department, "DEPT-B"));

        Func<Task> act = () => CreateService(conn).UpsertWeeklyAsync(BuildWeekly("DEPARTMENT", FactoryInScope, DeptUnderTest), Actor, "u");

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task UpsertWeekly_Global_放行并落库()
    {
        var conn = CreateConnectionMock();
        SetupFactoryCode(conn, "CN");
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(DataScopeContext.Global);
        conn.Setup(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(1);
        conn.Setup(c => c.QueryFirstOrDefaultAsync<long>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(11L);

        var inserted = await CreateService(conn).UpsertWeeklyAsync(BuildWeekly("FACTORY", FactoryInScope, null), Actor, "u");

        inserted.Should().Be(1);
        // 每星期组：1 DELETE + 1 闭区间 UPDATE + 1 条 INSERT
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Exactly(2));
        conn.Verify(c => c.QueryFirstOrDefaultAsync<long>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }

    // ---------- 行级操作：Scope 先于业务校验 ----------

    [Fact]
    public async Task CloseRow_越界_抛ScopeViolation且零UPDATE()
    {
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryFirstOrDefaultAsync<(long Id, DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive, string? FactoryCode, string? DeptCode)?>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((5L, new DateTime(2026, 10, 1), (DateTime?)null, true, "CN", (string?)null));
        SetupScope((DataScopeTypes.Factory, "BJ"));

        Func<Task> act = () => CreateService(conn).CloseRowAsync(5L, new DateTime(2026, 10, 31), Actor, "u");

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRow_越界_抛ScopeViolation且零DELETE()
    {
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryFirstOrDefaultAsync<(DateTime EffectiveFrom, string? FactoryCode, string? DeptCode)?>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((new DateTime(2026, 11, 10), "CN", (string?)null)); // 未生效行（可删窗口期内）但厂越界
        SetupScope((DataScopeTypes.Factory, "BJ"));

        Func<Task> act = () => CreateService(conn).DeleteRowAsync(5L, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    // ---------- 读路径：授权过滤 ----------

    [Fact]
    public async Task Query_非Global无工厂授权_返回空页且完全不查库()
    {
        var conn = CreateConnectionMock();
        SetupScope((DataScopeTypes.Department, "DEPT-A")); // 只有部门维，无 Factory 维 → fail-closed

        var page = await CreateService(conn).QueryAsync(actorUserId: Actor);

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
        conn.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Query_Global_正常分页查询()
    {
        var conn = CreateConnectionMock();
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(DataScopeContext.Global);
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("COUNT(*)")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(2);
        conn.Setup(c => c.QueryAsync<ProductionWorkCalendarDto>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<ProductionWorkCalendarDto> { new() { Id = 1 }, new() { Id = 2 } });

        var page = await CreateService(conn).QueryAsync(actorUserId: Actor);

        page.Total.Should().Be(2);
        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_非Global有授权_查询SQL带Scope过滤且部门维缺失时只见FACTORY行()
    {
        var conn = CreateConnectionMock();
        SetupScope((DataScopeTypes.Factory, "CN")); // 有厂无部门授权 → 应仅 FACTORY 行可见
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("COUNT(*)")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(0);
        conn.Setup(c => c.QueryAsync<ProductionWorkCalendarDto>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<ProductionWorkCalendarDto>());

        await CreateService(conn).QueryAsync(actorUserId: Actor);

        conn.Verify(c => c.QueryAsync<ProductionWorkCalendarDto>(
            It.Is<string>(s => s.Contains("@ScopeFacs") && s.Contains("p.ProductionDepartmentId IS NULL") && !s.Contains("@ScopeDepts")),
            It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }
}