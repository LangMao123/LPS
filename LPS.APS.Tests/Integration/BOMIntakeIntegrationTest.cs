using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 独立 BOM 接货入口（2号位）：
/// 与 NightlyBatchOrchestrator 解耦，单独触发「找最近 READY 批次 → 接货」，供白天补接货 / 联调。
/// 接货落库到 APS_BOM_RAW + APS_BOM_STAGE_PATH_RAW（StagePath 事实源，跨版本连续性 E>0 的关键上游）。
/// </summary>
public class BOMIntakeIntegrationTest
{
    private const int PlanVersionId = 328; // FAMILY_X（OrderBomRequestLink 映射用，单 Domain 唯一命中）

    [Fact(DisplayName = "独立接货：拉取最近 READY BOM 批次并落库（APS_BOM_RAW + APS_BOM_STAGE_PATH_RAW）")]
    public async Task IntakeLatestReadyBatchAsync()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new ConsoleOutLoggerProvider()));

        var sp = services.BuildServiceProvider();
        var puller = sp.GetRequiredService<IBOMResultPullService>();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();

        Console.WriteLine($"触发独立接货 IntakeLatestReadyBatchAsync([{PlanVersionId}]) ...");
        var result = await puller.IntakeLatestReadyBatchAsync(new[] { PlanVersionId }, CancellationToken.None);
        Console.WriteLine(
            $"[接货] IntakePerformed={result.IntakePerformed}, BatchNo={result.BatchNo ?? "<无READY批次>"}, PulledCount={result.PulledCount}");

        Assert.True(result.IntakePerformed, "未找到 READY 状态的 BOM 批次，接货未执行");

        // 落库硬证据：接货后两表的当批行数 + StagePath 覆盖的物料数
        var bomRowCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM APS_BOM_RAW WHERE BatchNo=@b",
            new { b = result.BatchNo }, db: DatabaseId.APS);
        var stageDetailRowCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM APS_BOM_STAGE_PATH_RAW WHERE BatchNo=@b",
            new { b = result.BatchNo }, db: DatabaseId.APS);
        var stageMaterialCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(DISTINCT ChildMaterialCode) FROM APS_BOM_STAGE_PATH_RAW WHERE BatchNo=@b",
            new { b = result.BatchNo }, db: DatabaseId.APS);

        Console.WriteLine(
            $"[接货后] APS_BOM_RAW={bomRowCount}行, APS_BOM_STAGE_PATH_RAW={stageDetailRowCount}行, " +
            $"StagePath覆盖物料={stageMaterialCount}个");
    }
}