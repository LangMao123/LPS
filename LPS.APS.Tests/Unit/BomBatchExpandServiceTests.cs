using System.Data;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// BOM 批展开驱动服务单元测试（2号位）。
///
/// 覆盖本服务**自己声明的目的**（不是「跑起来不报错」）：
///   ① 批号只来自「本次夜间唯一的推送成功记录」（`APS_ETL_Log` / `Step=BomRequestPush` / `Status=SUCCESS`），
///      **不再**扫「最新 PENDING」猜选（0号位 2026-10-17 §二.2／§三.B）；
///   ② ODS 状态闸门：只有 `PENDING` 才展开；`PROCESSING/READY/CONSUMED/FAILED/不存在` 一律不动；
///   ③ 以该批号调 `sp_ExpandBOMBatch_vNext`、StoredProcedure、走 ODS、超时 1800；
///   ④ 无记录 / 多条记录 ⇒ 不调 SP、不抛、记 SKIPPED + Issue；
///   ⑤ SP 抛异常 ⇒ 不抛（转 Issue），SP 返回但状态非 READY ⇒ 判失败；
///   ⑥ 明细为空 ⇒ **不调 SP**（该 SP 先清空四表）；
///   ⑦ 成功后写 `APS_ETL_Log`；ETL 写失败不影响结果。
///
/// 说明：DatabaseConnectionManager 是**具体类**，查询方法为 virtual，可拦截。
/// </summary>
public class BomBatchExpandServiceTests
{
    private const string SpName = "sp_ExpandBOMBatch_vNext";
    private const string NightBatch = "REQ_20261017_7090c0407f1442d";

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

    private static BomBatchExpandService CreateService(Mock<DatabaseConnectionManager> connection)
        => new(connection.Object, Mock.Of<ILogger<BomBatchExpandService>>());

    /// <summary>本次夜间的推送成功记录（返回 PushRecord 集合的那个 QueryAsync 重载）。</summary>
    private static void SetupPushRecords(
        Mock<DatabaseConnectionManager> connection,
        params (string BatchNo, DateTime CreatedAt)[] records)
        => connection.Setup(c => c.QueryAsync<BomNightBatchResolver.PushRecord>(
                It.Is<string>(s => s.Contains("APS_ETL_Log")),
                It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.APS, It.IsAny<int?>()))
            .ReturnsAsync(records.Select(r => new BomNightBatchResolver.PushRecord(r.BatchNo, r.CreatedAt)));

    /// <summary>PENDING 积压计数 SQL（返回 int 的那个重载）。</summary>
    private static void SetupPendingBacklog(Mock<DatabaseConnectionManager> connection, int count)
        => connection.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("COUNT(*)") && s.Contains("Status = 'PENDING'")),
                It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(count);

    /// <summary>批明细行数 SQL（返回 int 的那个重载）。</summary>
    private static void SetupDetailCount(Mock<DatabaseConnectionManager> connection, int count)
        => connection.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("FROM MES_API_BOM_Request_Detail")),
                It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(count);

    /// <summary>ODS 批次状态（同一 SQL 最多调两次：展开前查一次、展开后复核一次）。</summary>
    private static void SetupOdsStatus(
        Mock<DatabaseConnectionManager> connection, string? first, string? second = null)
        => connection.SetupSequence(c => c.QueryFirstOrDefaultAsync<string>(
                It.Is<string>(s => s.Contains("SELECT Status FROM MES_API_BOM_Request")),
                It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(first)
            .ReturnsAsync(second);

    private static DateTime TodayAt(int hour) => DateTime.Now.Date.AddHours(hour);

    // ---------- ① / ② 本次唯一推送记录 + PENDING：以该批号调 SP ----------

    [Fact]
    public async Task 本次唯一推送记录且PENDING_以该批号调SP且走ODS()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 4);
        SetupOdsStatus(connection, "PENDING", "READY");
        SetupDetailCount(connection, 179549);

        object? capturedParams = null;
        connection.Setup(c => c.ExecuteAsync(
                SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, It.IsAny<int?>()))
            .Callback<string, object, CommandType, DatabaseId, int?>((_, p, _, _, _) => capturedParams = p)
            .ReturnsAsync(1);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, 1800), Times.Once);

        capturedParams.Should().NotBeNull();
        capturedParams!.GetType().GetProperty("BatchNo")!.GetValue(capturedParams)
            .Should().Be(NightBatch, "必须显式传「本次夜间唯一推送记录」的批号");

        result.BatchNo.Should().Be(NightBatch);
        result.Expanded.Should().BeTrue();
        result.PendingBacklogCount.Should().Be(4);
        result.Issues.Should().BeEmpty();
    }

    // ---------- ① 取批口径：读推送成功日志，而非扫 PENDING ----------

    [Fact]
    public async Task 取批SQL_只认推送成功日志_不再扫最新PENDING()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING", "READY");
        SetupDetailCount(connection, 100);

        await CreateService(connection).ExpandNightBatchAsync();

        // 必须认：APS_ETL_Log + Step=BomRequestPush + Status=SUCCESS + 排除 RT:
        connection.Verify(c => c.QueryAsync<BomNightBatchResolver.PushRecord>(
            It.Is<string>(s => s.Contains("APS_ETL_Log")
                               && s.Contains("Step = @Step")
                               && s.Contains("Status = 'SUCCESS'")
                               && s.Contains("NOT LIKE 'RT:%'")),
            It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.APS, It.IsAny<int?>()), Times.Once);

        // 不得再出现「TOP 1 … Status='PENDING' … ORDER BY CreatedAt DESC」这种猜选
        connection.Verify(c => c.QueryFirstOrDefaultAsync<string>(
            It.Is<string>(s => s.Contains("TOP 1") && s.Contains("Status = 'PENDING'")),
            It.IsAny<object>(), It.IsAny<CommandType>(), DatabaseId.ODS, It.IsAny<int?>()), Times.Never);
    }

    // ---------- ③ 无推送记录 ----------

    [Fact]
    public async Task 本次无推送成功记录_不调SP且不抛出()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection);
        SetupPendingBacklog(connection, 3);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        result.Expanded.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Should().Contain("无 BomRequestPush");
    }

    // ---------- ③' 多条推送记录：无法唯一识别 ⇒ 停止猜测 ----------

    [Fact]
    public async Task 本次多条推送成功记录_停止猜测_不调SP()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection,
            ("REQ_20261017_aaaaaaaaaaaaaaaa", TodayAt(9)),
            ("REQ_20261017_bbbbbbbbbbbbbbbb", TodayAt(0)));
        SetupPendingBacklog(connection, 3);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        result.Expanded.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Should().Contain("无法唯一识别");
    }

    // ---------- ② ODS 状态闸门 ----------

    [Theory]
    [InlineData("PROCESSING")]
    [InlineData("READY")]
    [InlineData("CONSUMED")]
    [InlineData("FAILED")]
    public async Task 本次批次状态非PENDING_不调SP(string status)
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, status);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        result.Expanded.Should().BeFalse();
        result.BatchNo.Should().Be(NightBatch);
    }

    [Fact]
    public async Task 推送记录指向的批次在ODS不存在_不调SP()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, null);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        result.Expanded.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Should().Contain("ODS 不存在");
    }

    // ---------- ⑥ 明细为空：拒绝调用（否则清空四表） ----------

    [Fact]
    public async Task 批明细为空_不调SP且记SKIPPED()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING");
        SetupDetailCount(connection, 0);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            SpName, It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
        result.Expanded.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Should().Contain("明细为空");
    }

    // ---------- ⑤ SP 抛异常：不硬断 ----------

    [Fact]
    public async Task SP抛异常_方法不抛出且结果含Issue()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING");
        SetupDetailCount(connection, 100);

        connection.Setup(c => c.ExecuteAsync(
                SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        BomBatchExpandResult? result = null;
        var act = async () => { result = await CreateService(connection).ExpandNightBatchAsync(); };

        await act.Should().NotThrowAsync();
        result!.Expanded.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Should().Contain("boom");
    }

    // ---------- ②' SP 返回但状态非 READY：不得判成功 ----------

    [Fact]
    public async Task SP返回但实际状态非READY_判失败()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING", "PROCESSING");
        SetupDetailCount(connection, 100);

        connection.Setup(c => c.ExecuteAsync(
                SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(1);

        var result = await CreateService(connection).ExpandNightBatchAsync();

        result.Expanded.Should().BeFalse("调用返回成功不等于 ODS 已 READY");
        result.Issues.Should().ContainSingle().Which.Should().Contain("非 READY");
    }

    // ---------- ⑦ ETL 日志 ----------

    [Fact]
    public async Task 成功后写ETL日志到APS()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING", "READY");
        SetupDetailCount(connection, 100);

        connection.Setup(c => c.ExecuteAsync(
                SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(1);

        await CreateService(connection).ExpandNightBatchAsync();

        connection.Verify(c => c.ExecuteAsync(
            It.Is<string>(s => s.Contains("INSERT INTO APS_ETL_Log")),
            It.Is<object>(p => p.ToString()!.Contains(NightBatch)),
            It.IsAny<CommandType>(), DatabaseId.APS, It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task ETL写失败_不影响展开结果()
    {
        var connection = CreateConnectionMock();
        SetupPushRecords(connection, (NightBatch, TodayAt(0)));
        SetupPendingBacklog(connection, 1);
        SetupOdsStatus(connection, "PENDING", "READY");
        SetupDetailCount(connection, 100);

        connection.Setup(c => c.ExecuteAsync(
                SpName, It.IsAny<object>(), CommandType.StoredProcedure, DatabaseId.ODS, It.IsAny<int?>()))
            .ReturnsAsync(1);
        connection.Setup(c => c.ExecuteAsync(
                It.Is<string>(s => s.Contains("INSERT INTO APS_ETL_Log")),
                It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("log down"));

        var result = await CreateService(connection).ExpandNightBatchAsync();

        result.Expanded.Should().BeTrue();
        result.BatchNo.Should().Be(NightBatch);
    }
}
