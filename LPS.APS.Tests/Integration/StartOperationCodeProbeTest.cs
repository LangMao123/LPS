using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Application.Services.Fixtures;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Repositories.Governance;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 只读探针：核验 5号位 计算器出口的 StartOperationCode 是否落在 1号位 的匹配空间
/// （RoutingOperation.OperationCode）内。不落库、不 Solver。
///
/// 背景：1号位 PhaseOneConstraintBuilder.BuildReachableStages 用 OperationCode 建节点表并按
/// demand.StartOperationCode 查；PhaseTwoInitialScheduler.GetOperationsFromStage 按 k.OperationCode 匹配，
/// 非空但查不中时无兜底 → 需求判 S26 Unscheduled。
///
/// 日志只放行含「[Pegging][红线]」的行，规避 LoadSupplyPoolAsync 逐PI刷 Warning 拖死 I/O 的已知问题。
/// </summary>
public class StartOperationCodeProbeTest
{
    private const int PlanVersionId = 633;
    private const long StrategyProfileVersionId = 811L;

    [Fact(DisplayName = "只读探针：续排起点 StartOperationCode 出口值 vs 1号位 OperationCode 匹配空间")]
    public async Task StartOperationCode_ExitSpace()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        services.AddScoped<IDemandPriorityConfigProvider, DemandPriorityFixtureProvider>();
        services.AddScoped<IFrozenStrategySnapshotProvider, FrozenStrategySnapshotFixtureProvider>();
        // 过滤口径（2026-09-30 修）：原 `[EECTX` 前缀会把逐工单的 [EECTX]/[EECTX-STARTSTAGE] Debug 行
        // 全放进来（真实批次数百条），先吃满 MaxPrint 上限 ⇒ 末尾的 `[Pegging][红线]` 汇总行被挤掉、
        // 探针等于没输出。现只放两类：
        //   `[Pegging][红线]` —— 汇总 2~3 行，探针的主结论；
        //   `[EECTX]`        —— 逐工单明细（含 startStage / opsCnt / remainingCnt / inputOpsTotal），
        //                       用于判「E>0 是走上序前沿还是走 AllocateStageRemaining 比例分摊」。
        // ⚠️ `[EECTX]` 是字面匹配，**不会**命中 `[EECTX-STARTSTAGE]` 行（中间隔着 `-`）；若要核
        //    5号位 DetermineEffectiveStartStage 是否把 startStage 错推，需把 `[EECTX-STARTSTAGE]`
        //    一并加进数组（本次未加）。
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new MarkerLoggerProvider(new[] { "[Pegging][红线]", "[EECTX]" })));
        services.AddScoped<DatabaseConnectionManager>();

        var sp = services.BuildServiceProvider();
        var orchestrator = sp.GetRequiredService<IPeggingOrchestrator>();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();

        var domainKey = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT DomainKey FROM PlanVersion WHERE Id=@p", new { p = PlanVersionId }, db: DatabaseId.APS);
        var orderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        Console.WriteLine($"PROBE Domain={domainKey} Orders={orderIds.Count}");

        var now = DateTime.Now;
        var request = new PeggingExecutionRequest
        {
            PlanVersionId     = PlanVersionId,
            DomainKey         = domainKey ?? string.Empty,
            OrderIds          = orderIds,
            SnapshotAt        = now,
            FrozenWindowStart = now,
            FrozenWindowEnd   = now.AddHours(2),
            AllowCrossFactory = false,
            DefaultStrategy   = PeggingStrategyType.FIFO,
            MaxBomDepth       = 10,
            TimeoutSeconds    = 900,
            ExecutionMode     = "FULL_RUN",
            IsCandidate       = false,
            SchedulingContext = new SchedulingContext
            {
                StrategyProfileVersionId = StrategyProfileVersionId,
                DataCutoffTime           = now,
                PlanHorizonStart         = now,
                PlanHorizonEnd           = now.AddDays(90)
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var report = await orchestrator.RunBfsOnlyAsync(request, cts.Token);
        Console.WriteLine($"PROBE DONE Lpd={report.LpdCount} Orders={report.OrderCount} RedLinePass={report.RedLinePass}");

        // 自诊断：若没打出 [红线] 定位行，说明 PI Position 装载整段没跑到（典型：该 PV 的
        // SourceScheduleRunId 指向的快照批次里 StageProgress/OperationProgress 为空）——此时
        // 「StartOperationCode 全为空」不是结论，而是根本没测到，必须显式说明而不是默默通过。
        // ⚠️ 快照为空通常只是「夜间全量同步还没跑」（ScheduleRunId 只在夜间同步时产生，快照要等
        //    00:40/45/50 三个 MES 快照任务落），属运维节奏、**不是数据缺口**——先去做同步，
        //    别把「表是空的」升级成缺陷或裁决项。
        if (!MarkerLoggerProvider.SawRedline)
            Console.WriteLine(
                "PROBE-结果：未打出 [Pegging][红线] 定位行 ⇒ PI Position 装载未触达（wipStageRows 为空）。" +
                "本次不能作为「续排起点全为空」的证据。常见原因=该 PV 的快照批次尚未由夜间全量同步灌入（非缺陷）；" +
                "先跑夜间同步，或临时借用已灌批次（如 run 477）观察、跑完还原。");
        else
            Console.WriteLine("PROBE-结果：已触达续排起点装载，结论取上面的 [红线] 两行。");
    }

    /// <summary>只放行消息里含指定标记的日志行（其余整条丢弃），并对高频行限量。</summary>
    private sealed class MarkerLoggerProvider : ILoggerProvider
    {
        private readonly string[] _markers;
        private static int _printed;
        private const int MaxPrint = 400;

        /// <summary>是否打出过 [Pegging][红线] 定位行（= PI Position 装载真的跑到了）。</summary>
        internal static volatile bool SawRedline;

        public MarkerLoggerProvider(string[] markers) => _markers = markers;
        public ILogger CreateLogger(string categoryName) => new MarkerLogger(_markers, this);
        public void Dispose() { }

        private sealed class MarkerLogger : ILogger
        {
            private readonly string[] _markers;
            private readonly MarkerLoggerProvider _owner;
            public MarkerLogger(string[] markers, MarkerLoggerProvider owner) { _markers = markers; _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                var hit = false;
                foreach (var m in _markers)
                    if (msg.Contains(m, StringComparison.Ordinal)) { hit = true; break; }
                if (!hit) return;
                if (msg.Contains("[Pegging][红线]", StringComparison.Ordinal)) SawRedline = true;

                var n = System.Threading.Interlocked.Increment(ref _printed);
                if (n <= MaxPrint) Console.WriteLine("PROBE-LOG " + msg);
                else if (n == MaxPrint + 1) Console.WriteLine($"PROBE-LOG ...(后续同类日志已折叠，总计 {n - 1}+ 条)");
            }
        }
    }
}
