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
/// S5-01 整改冒烟（2026-10-14，5号位）：人工能力槽 Department Scope 二次校验 + 列表授权过滤。
/// 纯单测全 mock（假连接串），零真实库；断言核心=越界抛 ScopeViolationException 且写方法零调用。
/// Scope 走 EnsureInScopeAsync(Department, DeptCode)（照 RoutingOperation/Setup 治理先例）。
/// </summary>
public class ManualCapacityServiceScopeTests
{
    private const int Actor = 7;

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

    private ManualCapacityService CreateService(Mock<DatabaseConnectionManager> conn)
        => new(conn.Object, _dataScope.Object);

    /// <summary>部门码解析回带（EnsureDepartmentScopeAsync 第一跳）。</summary>
    private static void SetupDeptCode(Mock<DatabaseConnectionManager> conn, string? code)
    {
        conn.Setup(c => c.QueryFirstOrDefaultAsync<string?>(
                It.Is<string>(s => s.Contains("DeptCode FROM dbo.ProductionDepartment")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(code);
    }

    /// <summary>槽→部门解析回带（SoftDelete/BulkCreate/GetCalendar/DeleteCalendar 第一跳）。</summary>
    private static void SetupSlotDept(Mock<DatabaseConnectionManager> conn, int? deptId)
    {
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int?>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(deptId);
    }

    /// <summary>授权判定：DEPT-B 越权抛 ScopeViolationException，其余默认放行（loose mock）。</summary>
    private void SetupScopeThrowOnDeptB()
    {
        _dataScope.Setup(d => d.EnsureInScopeAsync(Actor, DataScopeTypes.Department, "DEPT-B", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ScopeViolationException(DataScopeTypes.Department, "DEPT-B"));
    }

    private static ManualCapacitySlotDto BuildSlot(int deptId) => new()
    {
        ProductionDepartmentId = deptId,
        OperationName = "印4",
        SlotCode = "印4_SLOT_99",
    };

    // ---------- CreateSlot ----------

    [Fact]
    public async Task CreateSlot_部门越界_抛ScopeViolation且零查重零INSERT()
    {
        var conn = CreateConnectionMock();
        SetupDeptCode(conn, "DEPT-B");
        SetupScopeThrowOnDeptB();

        Func<Task> act = () => CreateService(conn).CreateSlotAsync(BuildSlot(20), Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.QueryFirstOrDefaultAsync<long?>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        conn.Verify(c => c.QueryFirstOrDefaultAsync<int>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task CreateSlot_部门不存在_抛KeyNotFound且不判Scope()
    {
        var conn = CreateConnectionMock();
        SetupDeptCode(conn, null);

        Func<Task> act = () => CreateService(conn).CreateSlotAsync(BuildSlot(20), Actor);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _dataScope.Verify(d => d.EnsureInScopeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateSlot_授权内_正常落库()
    {
        var conn = CreateConnectionMock();
        SetupDeptCode(conn, "DEPT-A");
        conn.Setup(c => c.QueryFirstOrDefaultAsync<long?>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((long?)null); // 无重复
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(77);

        var created = await CreateService(conn).CreateSlotAsync(BuildSlot(10), Actor);

        created.ManualSlotId.Should().Be(77);
        _dataScope.Verify(d => d.EnsureInScopeAsync(Actor, DataScopeTypes.Department, "DEPT-A", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- GetSlots（列表授权过滤） ----------

    [Fact]
    public async Task GetSlots_非Global无部门授权_返回空页且完全不查库()
    {
        var conn = CreateConnectionMock();
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Factory, "BJ") })); // 只有 Factory 维，无 Department

        var page = await CreateService(conn).GetSlotsAsync(null, null, false, 1, 20, Actor);

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
        conn.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSlots_Global_正常分页查询()
    {
        var conn = CreateConnectionMock();
        _dataScope.Setup(d => d.ResolveScopeAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(DataScopeContext.Global);
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("COUNT(*)")),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(3);
        conn.Setup(c => c.QueryAsync<ManualCapacitySlotDto>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<ManualCapacitySlotDto> { new() { ManualSlotId = 1 } });

        var page = await CreateService(conn).GetSlotsAsync(null, null, false, 1, 20, Actor);

        page.Total.Should().Be(3);
        page.Items.Should().ContainSingle();
    }

    // ---------- SoftDelete / BulkCreateCalendar / GetCalendar / DeleteCalendar ----------

    [Fact]
    public async Task SoftDelete_越界_抛ScopeViolation且零UPDATE()
    {
        var conn = CreateConnectionMock();
        SetupSlotDept(conn, 20);
        SetupDeptCode(conn, "DEPT-B");
        SetupScopeThrowOnDeptB();

        Func<Task> act = () => CreateService(conn).SoftDeleteSlotAsync(1, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task SoftDelete_槽不存在_返回false不判Scope()
    {
        var conn = CreateConnectionMock();
        SetupSlotDept(conn, null);

        var ok = await CreateService(conn).SoftDeleteSlotAsync(1, Actor);

        ok.Should().BeFalse();
        _dataScope.Verify(d => d.EnsureInScopeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BulkCreateCalendar_越界_抛ScopeViolation且零写库()
    {
        var conn = CreateConnectionMock();
        SetupSlotDept(conn, 20);
        SetupDeptCode(conn, "DEPT-B");
        SetupScopeThrowOnDeptB();
        var req = new ManualSlotCalendarBulkRequest
        {
            ManualSlotId = 1,
            StartDate = new DateTime(2026, 11, 1),
            Days = 1,
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(17, 0, 0),
            AvailableFlag = true,
        };

        Func<Task> act = () => CreateService(conn).BulkCreateCalendarAsync(req, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task GetCalendarBySlot_越界_抛ScopeViolation且不查窗口()
    {
        var conn = CreateConnectionMock();
        SetupSlotDept(conn, 20);
        SetupDeptCode(conn, "DEPT-B");
        SetupScopeThrowOnDeptB();

        Func<Task> act = () => CreateService(conn).GetCalendarBySlotAsync(1, null, null, 1, 20, Actor);

        await act.Should().ThrowAsync<ScopeViolationException>();
        conn.Verify(c => c.QueryAsync<ManualSlotCalendarDto>(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task DeleteCalendar_窗口不存在_返回false不判Scope()
    {
        var conn = CreateConnectionMock();
        SetupSlotDept(conn, null);

        var ok = await CreateService(conn).DeleteCalendarAsync(900L, Actor);

        ok.Should().BeFalse();
        _dataScope.Verify(d => d.EnsureInScopeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}