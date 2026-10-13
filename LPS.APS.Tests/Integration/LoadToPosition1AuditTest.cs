using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Fixtures;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **装载给 1号位 的整块载荷验收探针**（2026-10-09，用户口径：
/// 「就是整个装载给 1号位的，包括分桶的计算这些，我们需要保证不出问题」）。
///
/// 与 <see cref="StartOperationCodeProbeTest"/> 的区别：那个只跑 BFS 回路（到 voucher 为止，
/// **既不做分桶、也不装 Routing/部门/Stage 链**），所以它证明不了「交给 1号位 的东西是对的」。
/// 本探针走 <see cref="PeggingOrchestrator.RunPeggingOnlyAsync"/> —— 与生产
/// <c>ExecutePeggingWorkflowAsync</c> **共用同一段 <c>BuildSolveRequestAsync</c>**，
/// 差异只有一条：组出 <see cref="DomainSolveRequest"/> 就返回，**不调 Solver、不落库**。
///
/// 输出：`AUDIT ` 前缀的核对行（每条一个检查项 + 计数 + 违规样本）。
/// 判读原则：**计数行是事实，结论要人看**——探针不自动断言「通过」，因为多数项是
/// 「覆盖率/命中率」，阈值属业务口径（PM 裁决）而非代码常量。
/// </summary>
public class LoadToPosition1AuditTest
{
    private const int MaxSample = 5;

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_PROBE_PLAN_VERSION"), out var v) ? v : 2;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_PROBE_STRATEGY_VERSION"), out var v) ? v : 811L;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_AUDIT_TIMEOUT_MIN"), out var v) ? v : 90;

    /// <summary>
    /// **Candidate 模式通电开关**（2026-10-13 新增）。
    ///
    /// 【为什么要它】T-04 两条契约（`DomainSolveRequest.ExecutionConstraints` / `OrderProtections`）的
    ///   装载侧入口 `PeggingOrchestrator.BuildAnchorPayloadAsync` 有两条早退：
    ///   `!request.IsCandidate` 或 `!request.BasePlanVersionId.HasValue`。本探针原先硬写
    ///   `IsCandidate = false` ⇒ 该路径**恒早退**，永远打 `ExecutionConstraints=0 ⚠`，
    ///   **从没在真实数据上跑过**。要验「交给 1号位 的锚点对不对」，必须让它真的进去。
    ///
    /// 【怎么用】`APS_PROBE_CANDIDATE=1`（配合 `APS_PROBE_BASE_PV=&lt;PlanVersionId&gt;`）；
    ///   不给 `APS_PROBE_BASE_PV` 时 **Base 取本次 `APS_PROBE_PLAN_VERSION`**（同版自比，只为通电，
    ///   **不是**真实「上一 ACTIVE」语义——实库当前 0 个 ACTIVE，无真实跨版本基座可指）。
    ///
    /// 【性质】只改探针请求构造 ⇒ 不影响任何生产代码；仍走 `RunPeggingOnlyAsync`
    ///   （**不调 Solver、不落库**）。`CandidateContext` 由 `BuildCandidateContextAsync` 正常装配。
    /// </summary>
    private static bool CandidateMode =>
        string.Equals(Environment.GetEnvironmentVariable("APS_PROBE_CANDIDATE"), "1", StringComparison.Ordinal);

    /// <summary>`APS_PROBE_BASE_PV` ⇒ 指定 Base（上一 ACTIVE）PlanVersionId；缺省见 <see cref="CandidateMode"/>。</summary>
    private static int? BasePvOverride =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_PROBE_BASE_PV"), out var v) ? v : null;

    [Fact(DisplayName = "装载验收：交给 1号位 的 DomainSolveRequest 逐项核对（含连续性分桶）")]
    public async Task LoadToPosition1_PayloadAudit()
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
        // 生产 FrozenStrategySnapshotProvider 需要 3号位 治理仓储（StrategyProfileVersionRepository）才能装配冻结快照块
        services.AddGovernanceRepositories();
        // ⚠ **刻意不挂 Fixture**：`FrozenStrategySnapshotFixtureProvider` / `DemandPriorityFixtureProvider` 会让
        //   ⑦换型规则、⑧批量策略、⑤⑥护栏、策略参数全部走**写死的样例值**，
        //   于是「BatchPolicies=0 / SetupTransitionRules=0」这种结论到底是**真缺口**还是**Fixture 没给**就分不清。
        //   本探针要回答"交给 1号位 的东西对不对"，必须拿**生产 Provider**（读冻结快照）跑。
        // 默认不挂日志 Provider：探针自己出结论，不需要被日志 I/O 拖累（LoadSupplyPoolAsync 逐 PI 刷 Warning 是已知 I/O 杀手）。
        // `APS_PROBE_LOG=1` 时挂一个**只转发 Warning 及以上 + 我方埋点前缀**的极简 Console Sink（见下），
        // 用于拿各装载阶段的**分段耗时**——黑盒 160s 里哪一段占大头，光看总量看不出来。
        services.AddLogging(b =>
        {
            if (Environment.GetEnvironmentVariable("APS_PROBE_LOG") == "1")
                b.AddProvider(new ProbeConsoleLoggerProvider());
        });
        services.AddScoped<DatabaseConnectionManager>();
        // 2026-10-13 §T-1013x：仅用于**借用生产的资源/日历装载**（`LoadResourcesAndCalendarAsync`，已 internal）
        //   ⇒ 探针装给 1号位 的 `Resources`/`CalendarSlots` 与生产同源，不再恒 0。
        services.AddScoped<SchedulingOrchestrator>();

        var sp = services.BuildServiceProvider();
        var orchestrator = (PeggingOrchestrator)sp.GetRequiredService<IPeggingOrchestrator>();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();
        var schedulingOrchestrator = sp.GetRequiredService<SchedulingOrchestrator>();

        // 2026-10-13 §T-1013x 补：**连计划期一起取本 PV 自己的**（原来是写死的 `now..now+90d`）。
        //   生产建 `SchedulingContext` 用的是 `planVersion.PlanHorizonStart/End`（见 `LoadSchedulingContextAsync`），
        //   该值经 `BuildSolveRequestAsync` 流进 `DomainSolveRequest.PlanningStart/PlanningEnd` 交给 1号位。
        //   探针若自造 90 天窗，这行就与生产有差（PV2 实测差 5 天：生产 `2026-10-08..2027-01-06`）⇒ 不具代表性。
        var pv = await conn.QueryFirstOrDefaultAsync<PlanVersionHorizon>(
            "SELECT DomainKey, PlanHorizonStart, PlanHorizonEnd FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS);
        var domainKey = pv?.DomainKey;
        var orderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        Say($"Domain={domainKey} PlanVersionId={PlanVersionId} Orders={orderIds.Count} " +
            $"计划期={pv?.PlanHorizonStart:yyyy-MM-dd}..{pv?.PlanHorizonEnd:yyyy-MM-dd}（取自 PlanVersion，与生产同源）");

        var allOrderCount = orderIds.Count;   // 收窄前 = 本 PV 全量订单数（用于判定是否走了小样本）

        // ── 小样本定向跑（2026-10-09 用户口径：先从真实数据里挑「符合某条计算逻辑」的数据，
        //    跑起来定位问题，捋顺了再统一全量跑一次 —— 别每轮都全量）──
        //
        // 为什么必须做：全量 PV2 一轮 = `Orders=22,144` / 需求 6.4 万 / PI Position 11.8 万，
        //   装载耗时 **~200 秒**（见 §0 耗时行）。改一行代码跑一轮 200s 的迭代节奏没用。
        //
        // ⚠ 2026-10-09 实测更正：**订单收窄本身不会让装载变快**。初版以为「装载全按本批订单收窄」是想当然 ——
        //   实测小样本仍是 160s，因为两个最大头根本不看订单集：`LoadBomSnapshotAsync` 只收 `(planVersionId, ct)`，
        //   按整 PV 的 `ResolvedBOMNO` 装 ⇒ `MaterialUniverse` 是全 PV 的 ⇒ 5 个订单照样装出 118,151 条 PI 事实、
        //   5号位 PI Position 算掉 **105s**（占总 160s 的 65%）。分段耗时埋点见 `[Pegging][供给池阶段耗时]`。
        //   修法：`RunPeggingOnlyAsync(..., scopeBomToOrders: true)` ⇒ 见下方调用点的 `scopeBomToOrders`；
        //   BOM 宇宙随订单收窄后 160s → **46s**（PI 事实 118,151 → 25,748）。
        //   一句话：**只缩数据量、不改代码路径**这条性质，是修了 BOM 收窄之后才成立的，不是天生就有。
        // 用法：
        //   `APS_PROBE_ORDER_IDS=123,456,789`            —— 显式指定订单（最快、最可控，推荐）
        //   `APS_PROBE_SAMPLE=20`                        —— 自动挑 20 个「BOM 命中关注现象」的订单：
        //       子件在主档缺行 / 子件 SupplyMode=PURCHASE 且无 RoutingOperation
        //       ⇒ 恰好覆盖本轮修的两条路径。实测 6 秒内出样本。
        //   不设任何一个 ⇒ 全量（定稿验收用）。
        orderIds = await ScopeOrderIdsAsync(conn, orderIds);

        var now = DateTime.Now;

        // ── T-04 两条契约通电（2026-10-13）：Candidate 模式见 CandidateMode 注释 ──
        var candidate = CandidateMode;
        var basePv = candidate ? (BasePvOverride ?? PlanVersionId) : (int?)null;
        if (candidate)
        {
            Say($"⚠ **Candidate 模式通电**：IsCandidate=true, BasePlanVersionId={basePv}" +
                (BasePvOverride is null
                    ? "（未给 APS_PROBE_BASE_PV ⇒ 取本次 PlanVersionId 同版自比，**仅为通电**，不是真实「上一 ACTIVE」语义）"
                    : "（显式指定）") +
                " —— 走 PeggingOrchestrator.BuildAnchorPayloadAsync 全路径；仍不调 Solver、不落库");
        }

        // ── 2026-10-13 §T-1013x：把 `SchedulingContext` 的资源/日历轴**按生产同一份代码**填上 ──
        //   此前这里只赋 4 个字段 ⇒ `BuildResourceDefinitions`/`BuildResourceCalendarSlots` 首行判 Count==0 直接返空
        //   ⇒ 探针打出的 `Resources=0 CalendarSlots=0` 是**假象**（生产非 0），却一度被我登成缺陷。
        //   现改为调用 `SchedulingOrchestrator.LoadResourcesAndCalendarAsync`（同一份生产实现，不自己复刻 SQL）。
        //   ⚠ 仍与生产有别、**已核实不流入载荷**的一项：`ScheduleRunId` 探针=0（未建 Run）⇒ `LoadMESProgressAsync`
        //     不跑、`context.MESRemainingQty` 不填；而 `PeggingOrchestrator` 全文件**不读** `MESRemainingQty`
        //     /`Orders`/`InventorySupplies`（已 grep 核），故不影响载荷。
        var schedulingContext = new SchedulingContext
        {
            StrategyProfileVersionId = StrategyProfileVersionId,
            DataCutoffTime           = now,
            // 与生产一致：用本 PV 自己的计划期（`LoadSchedulingContextAsync` 即取 `planVersion.PlanHorizon*`）。
            PlanHorizonStart         = pv?.PlanHorizonStart ?? now,
            PlanHorizonEnd           = pv?.PlanHorizonEnd ?? now.AddDays(90)
        };
        // 超时用探针自己的 cts（原来传 `CancellationToken.None` ⇒ 这一步无超时保护，是我引入的疏漏）。
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));
        await schedulingOrchestrator.LoadResourcesAndCalendarAsync(schedulingContext, cts.Token);
        Say($"接通生产资源装载：Resources={schedulingContext.Resources.Count} " +
            $"带日历资源={schedulingContext.ResourceCalendars.Count}（来自 SchedulingOrchestrator.LoadResourcesAndCalendarAsync）");

        var request = new PeggingExecutionRequest
        {
            PlanVersionId      = PlanVersionId,
            DomainKey          = domainKey ?? string.Empty,
            OrderIds           = orderIds,
            SnapshotAt         = now,
            FrozenWindowStart  = now,
            FrozenWindowEnd    = now.AddHours(2),
            AllowCrossFactory  = false,
            DefaultStrategy    = PeggingStrategyType.FIFO,
            MaxBomDepth        = 10,
            TimeoutSeconds     = 900,
            ExecutionMode      = candidate ? "CANDIDATE" : "FULL_RUN",
            IsCandidate        = candidate,
            BasePlanVersionId  = basePv,
            SchedulingContext  = schedulingContext
        };

        // 小样本模式下把 BOM 宇宙一起收窄（否则 5 个订单仍要装整 PV 的 BOM ⇒ 118,151 条 PI 事实 ⇒ 光 5号位
        //   PI Position 计算就 105s，白等）。全量模式（未收窄订单）保持 scopeBomToOrders=false，与生产逐字一致。
        //   （`cts` 已上移至资源装载之前声明，此处复用同一条。）
        var payload = await orchestrator.RunPeggingOnlyAsync(
            request, cts.Token, scopeBomToOrders: orderIds.Count < allOrderCount);

        AuditPayload(payload);
        AuditGraph(payload.SolveRequest);
        AuditDepartmentsAndParams(payload.SolveRequest);

        // 需求物料的 PI 归属交叉核（DB 侧权威：PI 号 → 物料）
        var piMaterial = await LoadPiToMaterialMapAsync(conn);
        AuditPiOwnership(payload.SolveRequest.LogicalProductionDemands, piMaterial);

        // 分桶切不出的**逐条**证据（2026-10-09）：需求侧 PI 已能全中 PI Position，但 E 恒为 0 ⇒ 把命中的
        //   上下文原样 dump（E / Slices 明细 / Issue），别再从代码推。
        AuditContinuityContexts(payload);

        // §1.4(a) Stage 入口基线自由量（2026-10-12 新增；1号位 B-009 门禁后两项的唯一上游）
        AuditPiStageBaselines(payload);

        // §DG-05 DemandGoal（2026-10-12 新增；2026-10-13 取值链投产 ⇒ 本段改为**独立重算对账**）
        //   根订单口径需回查 [Order]（OrderNo / OrderType / CustomerSegment）——**只作独立重算，不是取值规则**。
        var orderMeta = (await conn.QueryAsync<OrderMetaRow>(
            @"SELECT Id, ISNULL(OrderNo, N'') AS OrderNo, ISNULL(OrderType, N'') AS OrderType,
                     ISNULL(CustomerSegment, N'') AS CustomerSegment
              FROM [Order] WHERE PlanVersionId=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS))
            .ToDictionary(r => r.Id);
        AuditDemandGoal(payload.SolveRequest.LogicalProductionDemands, orderMeta);

        // ── T-04 两条契约的**真实产物**（只有 Candidate 模式才有；FULL 侧恒早退）─────────────────────
        if (candidate && basePv.HasValue)
            await AuditAnchorsAsync(conn, payload, basePv.Value);
    }

    /// <summary>`PlanVersion` 的域键与**自身计划期**（探针据此建 `SchedulingContext`，与生产同源，不再自造 90 天窗）。</summary>
    private sealed class PlanVersionHorizon
    {
        public string DomainKey { get; set; } = string.Empty;
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }

    /// <summary>`[Order]` 行的 Goal 计量用元数据（仅 OrderType / CustomerSegment，不含金额等业务字段）。</summary>
    private sealed class OrderMetaRow
    {
        public long Id { get; set; }
        public string OrderNo { get; set; } = string.Empty;
        public string OrderType { get; set; } = string.Empty;
        public string CustomerSegment { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────
    // §1.4(a) Stage 入口基线自由量（`DomainSolveRequest.PiStageQuantityBaselines`）
    //
    // 判读：候选键 − 产行 = **未产行**数。未产行是**设计行为**（缺位置事实／该 Stage 自由量 ≤ 0），
    //   1号位 侧记「缺 Stage 合法自由量上下文」**显式未满足**，不是静默丢——但差额大时需要看一眼原因。
    //   四条不变量任一越界即真缺陷（②③④ 尤其：③ 越界 = 毛量超过权威总量根，等于静默放大可用量）。
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditPiStageBaselines(PeggingOrchestrator.PeggingPayload payload)
    {
        var req  = payload.SolveRequest;
        var rows = req.PiStageQuantityBaselines ?? Array.Empty<PiStageQuantityBaseline>();

        var piRemaining = req.PiRemainingFacts
            .GroupBy(f => (f.ProductionInstructionNo, f.MaterialId))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.PiRemainingQty));

        // 已验证 PI 宇宙（= 事实侧 B-01 业务身份闸门之后的 PI 集合；与装载层 `verifiedPiSet` 同源同口径）。
        var verifiedPiSet = req.PiRemainingFacts
            .Select(f => f.ProductionInstructionNo?.Trim() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        // 候选键：与装载层同谓词**独立重算**（C 桶 ＋ **PI 落在已验证宇宙内** ＋ 有 StartStageCode）
        //   ⚠ 2026-10-12 收紧：原谓词只查「PI 号非空」⇒ 把客户订单行（`OrderType='SALES_ORDER'`，挂着 PI 号
        //     但被 B-01 闸门挡在事实集外）也计进了分母（PV2 实测 21,787，真实 9,324）。
        //   ⚠ 原谓词里的 `!d.IsUnlocated` 已移除：需求侧该标志是装载层占位、恒 false ⇒ 空过滤（见本方法末尾知会）。
        var keys = req.LogicalProductionDemands
            .Where(d => !d.IsContinuation
                        && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
                        && verifiedPiSet.Contains(d.ProductionInstructionNo!.Trim())
                        && !string.IsNullOrWhiteSpace(d.StartStageCode))
            .Select(d => (Pi: d.ProductionInstructionNo!.Trim(), d.MaterialId, Stage: d.StartStageCode.Trim()))
            .Distinct()
            .ToList();

        // B-01 身份闸门挡下量：挂了 PI 号、但该 PI 不在权威事实集内（PV2 预期 ≈12,463）。
        //   这一行是 PM「12,463 为什么没有 PI Position」的现场答案（= 需求侧收了一个事实侧不认的号），别删。
        var identityGatedOut = req.LogicalProductionDemands
            .Where(d => !d.IsContinuation
                        && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
                        && !verifiedPiSet.Contains(d.ProductionInstructionNo!.Trim()))
            .ToList();

        var rowKeys = rows.Select(r => (r.ProductionInstructionNo, r.MaterialId, r.StageCode)).ToList();

        Say("──────── §1.4(a) Stage 入口基线自由量（1号位 B-009 门禁后两项）────────");
        Say($"候选键(C桶×PI在权威事实集×有Stage)={keys.Count} 产行={rows.Count} 未产行={keys.Count - rows.Count}");
        Say($"  └ B-01 身份闸门挡下(挂 PI 号但该 PI 不在事实集内，PV2 预期 ≈12,463)：{identityGatedOut.Count} 条需求" +
            (identityGatedOut.Count > 0
                ? $"；样本 {Join(identityGatedOut.Take(MaxSample).Select(d => d.ProductionInstructionNo))}"
                : ""));
        Say($"Σ毛量={rows.Sum(r => r.StageFreeEligibleQty)} Σ入口已占={rows.Sum(r => r.AlreadyCommittedQty)} " +
            $"已占>0 的行={rows.Count(r => r.AlreadyCommittedQty > 0m)}（>0 才证明 A/B 已执行量真装进来了）");

        // ── 归因（2026-10-13 §T-1013y）：Σ已占=0 是「装载漏装 A/B」还是「C桶基线与执行事实结构性不相交」？──
        //   两个互斥解释，**必须用数据分开**（旧台账把它一口断成「A/B 已执行量未装进来」，那只是**其中一种**可能）：
        //   ① 结构性不相交：凡 `PiPosition` 带执行上下文的 PI，其需求**必被分桶切成 Continuation** ⇒
        //      被装载层 `!d.IsContinuation` 过滤掉，**物理上不可能**出现在本基线行里 ⇒ Σ已占=0 是分工结果，非缺陷；
        //   ② 装载漏装：产行里确有「PI 带执行上下文」的键，却没算出 committed ⇒ **真缺陷**。
        var ctxPiSet = payload.PiPositions
            .Where(kv => (kv.Value.ExistingExecutionContexts?.Count ?? 0) > 0)
            .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var rowPiInCtx = rows.Select(r => r.ProductionInstructionNo)
            .Distinct(StringComparer.Ordinal).Count(ctxPiSet.Contains);
        Say($"   └ 归因①产行(去重PI)∩「带执行上下文的PI」={rowPiInCtx}" +
            (rows.Count == 0 ? "" : rowPiInCtx == 0
                ? " ⇒ **结构性不相交**（Σ已占=0 是 C桶/A/B 分工结果，非漏装）"
                : $" ⚠ ⇒ 有 {rowPiInCtx} 个 PI 既在基线上、又带执行上下文 ⇒ **装载漏装，真缺陷**"));
        //   反向对照：带执行上下文的 PI，其需求是否**清一色**落在 Continuation 侧（C 桶一条不剩）。
        var ctxPiAsCbucket = req.LogicalProductionDemands.Count(d =>
            !d.IsContinuation && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
            && ctxPiSet.Contains(d.ProductionInstructionNo!.Trim()));
        var ctxPiAsCont = req.LogicalProductionDemands.Count(d =>
            d.IsContinuation && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
            && ctxPiSet.Contains(d.ProductionInstructionNo!.Trim()));
        Say($"   └ 归因②「带执行上下文的PI」之需求分布：Continuation={ctxPiAsCont} 条 / C桶={ctxPiAsCbucket} 条" +
            (ctxPiAsCont > 0 && ctxPiAsCbucket == 0
                ? " ⇒ 全部被切成 Continuation，C 桶一条不剩（与归因①互为佐证）" : ""));
        //   └ 残留追问：C 桶里那几条「PI 带执行上下文」的需求，为何**也没**产行？（候选键外 / 自由量≤0，两种含义不同）
        if (ctxPiAsCbucket > 0)
        {
            var producedRowKeys = rows.Select(r => (r.ProductionInstructionNo, r.MaterialId, r.StageCode)).ToHashSet();
            var candidateKeySet = keys.ToHashSet();
            var ctxCbucket = req.LogicalProductionDemands.Where(d =>
                !d.IsContinuation && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
                && ctxPiSet.Contains(d.ProductionInstructionNo!.Trim())).ToList();
            Say($"     残留明细（{ctxCbucket.Count} 条）：" + Join(ctxCbucket.Take(MaxSample).Select(d =>
            {
                var k = (d.ProductionInstructionNo!.Trim(), d.MaterialId, (d.StartStageCode ?? string.Empty).Trim());
                var inKeys = candidateKeySet.Contains(k);
                var hasRow = producedRowKeys.Contains(k);
                return $"{k.Item1}/mat={d.MaterialId}/stage='{k.Item3}'/候选键={(inKeys ? "是" : "否")}/产行={(hasRow ? "是" : "否")}";
            })));
        }

        Say($"不变量①键唯一: 重复={rowKeys.Count - rowKeys.Distinct().Count()}" +
            (rowKeys.Count != rowKeys.Distinct().Count()
                ? " ⚠" + Join(rowKeys.GroupBy(k => k).Where(g => g.Count() > 1)
                    .Take(MaxSample).Select(g => $"{g.Key.ProductionInstructionNo}/{g.Key.MaterialId}/{g.Key.StageCode}×{g.Count()}"))
                : ""));

        var badCommitted = rows.Where(r => r.AlreadyCommittedQty < 0m || r.AlreadyCommittedQty > r.StageFreeEligibleQty).ToList();
        Say($"不变量②入口已占∈[0,毛量]: 越界={badCommitted.Count}" +
            (badCommitted.Count > 0 ? " ⚠" + Join(badCommitted.Take(MaxSample).Select(
                r => $"{r.ProductionInstructionNo}/{r.MaterialId}/{r.StageCode} 已占{r.AlreadyCommittedQty}>毛量{r.StageFreeEligibleQty}")) : ""));

        var overRemaining = rows.Where(r =>
            piRemaining.TryGetValue((r.ProductionInstructionNo, r.MaterialId), out var rem)
            && r.StageFreeEligibleQty > rem + 0.0001m).ToList();
        Say($"不变量③毛量≤PI剩余(权威总量根): 越界={overRemaining.Count}" +
            (overRemaining.Count > 0 ? " ⚠" + Join(overRemaining.Take(MaxSample).Select(
                r => $"{r.ProductionInstructionNo}/{r.StageCode} 毛量{r.StageFreeEligibleQty}>剩余{piRemaining[(r.ProductionInstructionNo, r.MaterialId)]}")) : ""));

        var noPiFact = rows.Where(r => !piRemaining.ContainsKey((r.ProductionInstructionNo, r.MaterialId))).ToList();
        Say($"不变量④行能对上 PI 权威事实: 对不上={noPiFact.Count}" +
            (noPiFact.Count > 0 ? " ⚠" + Join(noPiFact.Take(MaxSample).Select(r => $"{r.ProductionInstructionNo}/{r.MaterialId}")) : ""));

        Say($"样例(最多{MaxSample}): " + Join(rows.Take(MaxSample).Select(
            r => $"{r.ProductionInstructionNo}/{r.MaterialId}/{r.StageCode} 毛量{r.StageFreeEligibleQty} 已占{r.AlreadyCommittedQty}")));

        // ── §四.3 四项证明（0号位 2026-10-12 逐字：「验证两个操作数的源头关系、同PI/Stage身份、
        //    无MES工单时合法新PI的处理、A/B已占与C自由量不重复扣减；发现数据矛盾须显式记录，
        //    不能以取小值掩盖」）──────────────────────────────────────────────────────────────
        //   本段**独立重算**两个操作数与「未钳制的已占」，不复用产品侧已 clamp 的 DTO 值 —— 否则
        //   「已占 ≤ 毛量」恒真（钳制使然），证明不了任何东西。
        var materialCodeByKey = req.PiRemainingFacts
            .GroupBy(f => (f.ProductionInstructionNo, f.MaterialId))
            .ToDictionary(g => g.Key, g => g.First().MaterialCode);

        var operands = rows.Select(r =>
        {
            payload.PiPositions.TryGetValue(r.ProductionInstructionNo, out var pos);
            var posQty = pos?.Positions
                .Where(p => (p.PositionType == PositionType.FIRST_STAGE_PENDING
                             || p.PositionType == PositionType.STAGE_WAITING)
                            && string.Equals(p.StageCode, r.StageCode, StringComparison.Ordinal))
                .Sum(p => p.Quantity) ?? 0m;
            var nextQty = pos?.NextOperationContexts
                .Where(c => c.MaterialId == r.MaterialId
                            && string.Equals(c.StartStageCode, r.StageCode, StringComparison.Ordinal))
                .Sum(c => c.SliceQty) ?? 0m;

            var rawCommitted = 0m;
            if (materialCodeByKey.TryGetValue((r.ProductionInstructionNo, r.MaterialId), out var code)
                && pos is not null)
            {
                foreach (var execution in pos.ExistingExecutionContexts)
                {
                    if (!string.Equals(execution.MaterialCode, code, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (execution.Slices.Count > 0)
                    {
                        rawCommitted += execution.Slices
                            .Where(s => string.Equals(s.StartStageCode, r.StageCode, StringComparison.Ordinal))
                            .Sum(s => s.SliceQty);
                    }
                    else if (string.Equals(execution.StartStageCode, r.StageCode, StringComparison.Ordinal))
                    {
                        rawCommitted += execution.DerivedRemainingQty;
                    }
                }
            }

            return (Row: r, PosQty: posQty, NextQty: nextQty, RawCommitted: rawCommitted);
        }).ToList();

        // ① 源头关系：两个操作数必须读**同一个 PI 的结果对象**（一个 PI = 一个 ProductionInstructionPositionResult，
        //   Positions / NextOperationContexts / ExistingExecutionContexts 皆挂在该对象上 ⇒ PI 身份由容器保证）。
        var noPosObj = operands.Count(o => !payload.PiPositions.ContainsKey(o.Row.ProductionInstructionNo));
        Say($"§四.3① 两算子同源: 产行 {rows.Count} 条，其中取不到同一 PI 结果对象的 {noPosObj} 条" +
            "（0 ⇒ 位置量与起点量恒为同一 PI 同一对象的两视图）");

        // ② 同 PI/Stage 身份：PI 由容器保证；Stage 两侧都按同一个 stage 值过滤（位置侧 StageCode / 起点侧 StartStageCode）。
        //   ⚠ 物料身份**只在起点侧可用**（`NextOperationContextDto.MaterialId`）——`PositionSlice` 无物料列，
        //     ⇒ 若存在「一个 PI 对应多个物料」，① 会跨物料求和而 ② 按物料收窄。故必须把该情形**实测出来**：
        var multiMaterialPi = payload.PiPositions.Count(kv =>
            kv.Value.NextOperationContexts.Select(c => c.MaterialId).Distinct().Count() > 1);
        var multiMaterialPiInRows = operands.Select(o => o.Row.ProductionInstructionNo).Distinct()
            .Count(pi => payload.PiPositions.TryGetValue(pi, out var p)
                         && p.NextOperationContexts.Select(c => c.MaterialId).Distinct().Count() > 1);
        Say($"§四.3② 同PI/Stage身份: PI 由结果对象容器保证；Stage 两侧同一取值。" +
            $"⚠ 物料身份仅起点侧有 ⇒ 多物料 PI 全库={multiMaterialPi}（产行涉及={multiMaterialPiInRows}）" +
            "（0 ⇒ 该不对称无实例，不产生跨物料求和）");

        // ③「取 min」到底由谁绑定（证明是**真取小**，而非两个视图恒等 ⇒ min 形同虚设）。
        var posSmaller = operands.Count(o => o.PosQty < o.NextQty);
        var nextSmaller = operands.Count(o => o.NextQty < o.PosQty);
        var equalQty = operands.Count(o => o.PosQty == o.NextQty);
        Say($"§四.3③ min 绑定方(产行): 位置量更小={posSmaller} 起点量更小={nextSmaller} 相等={equalQty}；" +
            $"Σ位置量={operands.Sum(o => o.PosQty)} Σ起点量={operands.Sum(o => o.NextQty)} Σ毛量={rows.Sum(r => r.StageFreeEligibleQty)}");
        //   ③附：合法新 PI 无 MES 工单 ⇒ 量落在 FIRST_STAGE_PENDING（5号位 首 Stage 兜底），
        //   与 STAGE_WAITING（有 MES 在制）分开计数，用于回答「无 MES 工单时合法新 PI 怎么处理」。
        var producedPis = operands.Select(o => o.Row.ProductionInstructionNo).ToHashSet(StringComparer.Ordinal);
        var positionMix = payload.PiPositions.Where(kv => producedPis.Contains(kv.Key))
            .SelectMany(kv => kv.Value.Positions)
            .GroupBy(p => p.PositionType.ToString())
            .Select(g => $"{g.Key}={g.Count()}条/{g.Sum(p => p.Quantity)}")
            .ToList();
        Say("§四.3③附 产行 PI 的位置类型构成: " + Join(positionMix));

        // ④ A/B 已占 ⊄ 该 Stage 自由量 的矛盾（**未钳制**原值，钳制前后逐条对比才看得见）。
        var contradictions = operands
            .Where(o => o.RawCommitted < 0m || o.RawCommitted > o.Row.StageFreeEligibleQty)
            .ToList();
        Say($"§四.3④ 已占⊄毛量 矛盾(未钳制): {contradictions.Count} 条" +
            (contradictions.Count > 0
                ? " ⚠" + Join(contradictions.Take(MaxSample).Select(
                    o => $"{o.Row.ProductionInstructionNo}/{o.Row.MaterialId}/{o.Row.StageCode} 已占{o.RawCommitted} vs 毛量{o.Row.StageFreeEligibleQty}"))
                : "（0 ⇒ A/B 已执行量确在该 Stage 自由池视图内，取 min 不重复扣减；反之必须交 5号位 核重叠口径）"));

        // 未产行**原因**（独立重算，不依赖产品日志）：无位置事实／该 Stage 位置量为 0／起点量为 0。
        // 三者的业务含义完全不同：「无位置事实」= 5号位 缺交付；「位置量 0」= 该 Stage 上无待加工实物
        //（量在 XC／跨厂途中／UNLOCATED）——后者若占多数，说明公式里的位置量口径需要按业务再核。
        var rowKeySet = rowKeys.ToHashSet();
        var reasons = new List<(string Key, string Why, decimal PosQty, decimal NextQty)>();
        foreach (var k in keys.Where(k => !rowKeySet.Contains(k)))
        {
            if (!payload.PiPositions.TryGetValue(k.Pi, out var pos) || pos is null)
            {
                reasons.Add(($"{k.Pi}/{k.MaterialId}/{k.Stage}", "无位置事实", 0m, 0m));
                continue;
            }

            var posQty = pos.Positions
                .Where(p => (p.PositionType == PositionType.FIRST_STAGE_PENDING
                             || p.PositionType == PositionType.STAGE_WAITING)
                            && string.Equals(p.StageCode, k.Stage, StringComparison.Ordinal))
                .Sum(p => p.Quantity);
            var nextQty = pos.NextOperationContexts
                .Where(c => c.MaterialId == k.MaterialId
                            && string.Equals(c.StartStageCode, k.Stage, StringComparison.Ordinal))
                .Sum(c => c.SliceQty);

            reasons.Add(($"{k.Pi}/{k.MaterialId}/{k.Stage}",
                posQty <= 0m ? "该Stage位置量=0" : nextQty <= 0m ? "起点量=0" : "其它",
                posQty, nextQty));
        }
        Say($"未产行原因样本(最多{MaxSample}): " + Join(reasons.Take(MaxSample)
            .Select(r => $"{r.Key} [{r.Why}] 位={r.PosQty} 起点={r.NextQty}")));
        Say($"未产行原因分布: " + Join(reasons.GroupBy(r => r.Why).Select(g => $"{g.Key}={g.Count()}")));

        // 候选键 PI 的「量到底在哪」：位置类型分布（条数/数量）。用于判断「该Stage位置量=0」是
        //   真无实物、还是量被记在 XC／跨厂途中／UNLOCATED（后者说明位置量口径要按业务再核）。
        var demandPis = keys.Select(k => k.Pi).ToHashSet(StringComparer.Ordinal);
        Say("候选键 PI 的位置类型分布(条数/数量): " + Join(
            payload.PiPositions.Where(kv => demandPis.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Positions)
                .GroupBy(p => p.PositionType.ToString())
                .Select(g => $"{g.Key}={g.Count()}/{g.Sum(p => p.Quantity)}")));

        // ── 接线安全性：门禁一旦通电（1号位 换 request.PiRemainingFacts），缺失即 fail-closed ──
        //   ⚠ 判据必须**逐字复制** 1号位 EvaluatePiLegalQuantityGate 的两条早退，否则数字无意义：
        //     ① matched == 0 ⇒ PI_LEGAL_QTY_CONTRACT_PENDING（找不到 PI 权威行）
        //     ② matched >  1 ⇒ PI_LEGAL_QTY_CONTRACT_PENDING（来源不唯一）
        //     ③ stageContext is null ⇒ PI_LEGAL_QTY_CONTRACT_PENDING（缺 Stage 合法自由量上下文）
        //   ①② 归 §1.3（PiRemainingFacts）、③ 归 §1.4(a)（本件）。这两条**任一命中即整条需求不成批**。
        var demandRows = req.LogicalProductionDemands
            .Where(d => !d.IsContinuation
                        && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
                        && verifiedPiSet.Contains(d.ProductionInstructionNo!.Trim())
                        && !string.IsNullOrWhiteSpace(d.StartStageCode))
            .ToList();

        var unmatchedPi = 0; var ambiguousPi = 0; var missingStageCtx = 0;
        foreach (var d in demandRows)
        {
            var matched = req.PiRemainingFacts.Count(f =>
                string.Equals(f.ProductionInstructionNo, d.ProductionInstructionNo, StringComparison.Ordinal)
                && f.MaterialId == d.MaterialId && f.FactoryId == d.FactoryId);
            if (matched == 0) unmatchedPi++;
            else if (matched > 1) ambiguousPi++;

            if (!rowKeySet.Contains((d.ProductionInstructionNo!.Trim(), d.MaterialId, d.StartStageCode!.Trim())))
                missingStageCtx++;
        }
        var n = demandRows.Count;
        Say($"接线安全性（按**需求条数**计，非去重键；C 桶需求 {n} 条）：");
        Say($"  ① §1.3 PiRemainingFacts 缺行(matched=0) ⇒ 门禁 early-return：{unmatchedPi} 条（{(n == 0 ? 0 : unmatchedPi * 100.0 / n):F1}%）");
        Say($"  ② §1.3 PiRemainingFacts 来源不唯一(matched>1)：{ambiguousPi} 条");
        Say($"  ③ §1.4(a) 缺 Stage 上下文(stageContext=null)：{missingStageCtx} 条（{(n == 0 ? 0 : missingStageCtx * 100.0 / n):F1}%）");
        Say($"  ⇒ 三条任一命中即 PI_LEGAL_QTY_CONTRACT_PENDING（ExecutionBatchFormation.Conflict）＝该需求**不成批**。");
        Say($"  ⚠ 死条款知会：需求侧 `IsUnlocated` 恒 false（装载层 `IsUnlocated = false // INTEGRATION TODO` 占位）");
        Say($"     ⇒ 该标志**不能当「已排除 UNLOCATED」的自证**；UNLOCATED 的真实排除靠**公式内位置类型过滤**（非该标志）。");
        Say($"     ⇒ 2026-10-12 起：装载层谓词与本审计**均已移除** `!d.IsUnlocated`（原为空过滤），此标志**待接线**（未落债）。");
        Say($"     需求侧 IsUnlocated=true 条数（预期 0）：{req.LogicalProductionDemands.Count(d => d.IsUnlocated)}");

        // ── 2026-10-12 追加：**直接调用 1号位 真实门禁**，量出「逐条 Q ≤ 合法量」的**真实放行率** ──
        //   背景（用户 2026-10-12 令「你可以测试出来再发」）：发「门禁通电信号」前，把 §一 那个
        //   「97.7% Stage 上下文覆盖率」这条**上界**，换成通电后的**真实放行 / 拦截条数**。
        //
        //   ⚠ 本段**不再复制判据**（上面 ①②③ 是逐字复制 1号位 的两条早退，只够证明「哪些会被 early-return」），
        //     而是直接调用 `PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate` ——
        //     该类 `internal`、方法 `internal static`，`LPS.APS.Scheduling.csproj` 有 `InternalsVisibleTo(LPS.APS.Tests)`
        //     ⇒ **与生产路径同一份代码**，不存在口径漂移。
        //
        //   `configuredMax: null` ＝ **B-010 缺策略兜底口径**（B-010 逐字：兜底**不伪造** Min/Max/Preferred，
        //     ⇒ 不得把配置上限当兜底上限）。现行 ⑧块源块过期（4 条、MaterialId 全为 1）⇒ C 桶几乎全域
        //     无命中策略 ⇒ 通电后走的正是这条 B-010 路径，故本口径即**当前库的真实口径**。
        //   （有策略路径 [仅物料 1] 会再叠一层 `configuredMax = policy.MaxExecutionBatchQty`，本段不覆盖，条数可忽略。）
        var gateIndex = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(req.PiStageQuantityBaselines, out var ambiguousKeys);
        Say($"接线索引（1号位 BuildPiStageQuantityIndex 实调）：可键控={gateIndex.Count} 撞键摘除={ambiguousKeys.Count}" +
            (ambiguousKeys.Count > 0 ? " ⚠" + Join(ambiguousKeys.Take(MaxSample).Select(k => $"{k.PiNo}/{k.MaterialId}/{k.StageCode}")) : ""));

        int gPass = 0, gNoPiRow = 0, gAmbiguous = 0, gNoStageCtx = 0, gBadStage = 0, gOverLimit = 0, gOther = 0;
        decimal sumPassQ = 0m, sumOverQ = 0m, sumOverCap = 0m;
        var overSamples = new List<string>();

        foreach (var d in demandRows)
        {
            var ctx = PhaseTwoInitialScheduler.ResolvePiStageContext(
                gateIndex, d.ProductionInstructionNo, d.MaterialId, d.StartStageCode);

            var reason = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
                d.NetOutputQty, d.ProductionInstructionNo, d.MaterialId, d.FactoryId, d.StartStageCode,
                req.PiRemainingFacts, ctx, configuredMax: null);

            if (reason is null)
            {
                gPass++;
                sumPassQ += d.NetOutputQty;
                continue;
            }

            if (reason.Contains("找不到 PI 权威行", StringComparison.Ordinal)) gNoPiRow++;
            else if (reason.Contains("匹配到", StringComparison.Ordinal)) gAmbiguous++;
            else if (reason.Contains("缺 Stage 合法自由量上下文", StringComparison.Ordinal)) gNoStageCtx++;
            else if (reason.Contains("缺 StageCode", StringComparison.Ordinal)
                     || reason.Contains("目标 Stage 未知", StringComparison.Ordinal)
                     || reason.Contains("不是本需求目标 Stage", StringComparison.Ordinal)
                     || reason.Contains("与需求不匹配", StringComparison.Ordinal)) gBadStage++;
            else if (reason.Contains("PI_LEGAL_QTY_OVER_LIMIT", StringComparison.Ordinal))
            {
                gOverLimit++;
                sumOverQ += d.NetOutputQty;
                // ⚠ cap 由**1号位 自己写进原因文本**（`需求量 {Q} 超出合法可用量 {cap}`）⇒ 解析原文，
                //   不自行复算（复算＝复制判据，正是本段要避免的漂移源）。
                var capText = Between(reason, "超出合法可用量 ", "（");
                if (decimal.TryParse(capText, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var cap))
                {
                    sumOverCap += cap;
                }
                if (overSamples.Count < MaxSample) overSamples.Add($"{d.LogicalDemandKey} Q={d.NetOutputQty} 原因={reason}");
            }
            else gOther++;
        }

        Say("【通电后真实放行率｜实调 1号位 EvaluatePiLegalQuantityGate，configuredMax=null（B-010 口径）】");
        Say($"  需求条数（C 桶 × PI 在权威事实集 × 有 Stage）={n}");
        Say($"  ✅ 放行（reason=null）={gPass} 条（{(n == 0 ? 0 : gPass * 100.0 / n):F1}%）ΣQ={sumPassQ}");
        Say($"  ⛔ 拦截合计={n - gPass} 条（{(n == 0 ? 0 : (n - gPass) * 100.0 / n):F1}%）");
        Say($"     ├ ② 找不到 PI 权威行(matched=0)：{gNoPiRow} 条");
        Say($"     ├ ③ 来源不唯一(matched>1)：{gAmbiguous} 条");
        Say($"     ├ ④ 缺 Stage 合法自由量上下文：{gNoStageCtx} 条");
        Say($"     ├ ⑤ Stage 身份不匹配/缺 StageCode/目标 Stage 未知：{gBadStage} 条");
        Say($"     ├ ⑥ **超合法量**（`PI_LEGAL_QTY_OVER_LIMIT`）：{gOverLimit} 条" +
            $"（ΣQ={sumOverQ} vs Σcap={sumOverCap}，缺口={sumOverQ - sumOverCap}）");
        Say($"     └ ⑦ 其它：{gOther} 条");
        Say($"  超量样例(最多{MaxSample})：" + (overSamples.Count > 0 ? Join(overSamples) : "（无）"));
        Say("  ⇒ ✅ 行 ＝ 通电后**仍能成批**的 C 桶需求；⛔ 行 ＝ 通电后**会被如实拦下**的" +
            "（拦下时 1号位 出口 Reason 会落 `BATCH_POLICY_CONFLICT`，即其 §七 自报的出口归因缺陷）。");
        Say("     ⚠ 本段为**逐需求独立判定**（未带 `PiStageCommitLedger` 运行累计账）⇒ 是**上界**：" +
            "真实运行时同 PI 同 Stage 的后续需求会因前序批已占而进一步收紧。");
    }

    /// <summary>取 <paramref name="text"/> 中 <paramref name="after"/> 之后、<paramref name="before"/> 之前的子串；取不到 ⇒ 空串。</summary>
    private static string Between(string text, string after, string before)
    {
        var i = text.IndexOf(after, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        i += after.Length;
        var j = text.IndexOf(before, i, StringComparison.Ordinal);
        return j < 0 ? text[i..] : text[i..j];
    }

    // ─────────────────────────────────────────────────────────────────────
    // §DG-05 `DemandGoal`（0号位 2026-10-12 裁决《DemandGoal与SH整单及关联问题正式裁决回执 v1.0》§一 DG-05）
    //
    // ⚠ 2026-10-13 起**取值链已投产**（`PeggingOrchestrator.FillDemandGoalsAsync`）⇒ 本段从
    //   「载体缺口计量」改为**独立重算对账**：按 `[Order]` 的 `OrderNo` 前缀 ＋ `CustomerSegment`
    //   在本探针内**独立复算**一遍 Goal，与 2号位 送出的 `demand.DemandGoal` **逐条比对**。
    //
    //   为什么必须独立重算（而不是只数「有值/缺值」）：常绿≠有效——只数覆盖率的话，即便取值逻辑
    //   整个判反（O 行给成补库），本段仍会显示「100% 有值、全部通过」。故本段的**唯一结论**
    //   是「不一致条数」，该数不为 0 即报缺陷。
    //
    //   ⚠ 本段的 OrderType 列**不是**取值规则（DG-02/DG-04 明令不得按 OrderType 硬映射）；
    //     它只用于**反证**：若按 OrderType 判 Goal 会与按 OrderNo 前缀判产生多少条分歧。
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditDemandGoal(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyDictionary<long, OrderMetaRow> orderMeta)
    {
        var n = demands.Count;

        Say("──────── §DG-05 DemandGoal 取值对账（0号位 裁决 §一 DG-05／§2.1–§2.4；2026-10-13 取值链投产）────────");

        // ① 独立重算（判据：OrderNo 前缀 ＋ CustomerSegment；跨厂 SH 按 DG-05 §2.4 **期望留空**）。
        string? ExpectedGoal(LogicalProductionDemand d, out string cls)
        {
            cls = "(根订单不在本 PV)";
            if (d.OrderId is not { } id || !orderMeta.TryGetValue(id, out var m)
                || string.IsNullOrWhiteSpace(m.OrderNo))
                return null;   // 判例 8：血缘无法唯一确定 ⇒ 期望留空

            var isPi = m.OrderNo[0] == 'P' || m.OrderNo[0] == 'p';
            var isSh = string.Equals(m.CustomerSegment, "跨厂", StringComparison.Ordinal);

            if (isSh)          { cls = "跨厂SH";   return null; }   // 未落地项：期望留空 + 诊断
            if (isPi)          { cls = "P行(PI)";  return DemandGoalValues.InventoryReplenishment; }
            cls = "O行(客户)";  return DemandGoalValues.CustomerCommitment;
        }

        var mismatch = 0;
        var nullGoal = 0;
        var shRows = 0;
        var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var goalCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var mismatchSample = new List<string>(5);

        foreach (var d in demands)
        {
            var expected = ExpectedGoal(d, out var cls);
            byClass[cls] = byClass.GetValueOrDefault(cls) + 1;
            if (cls == "跨厂SH") shRows++;

            var actual = d.DemandGoal;
            var key = string.IsNullOrEmpty(actual) ? "(空)" : actual!;
            goalCount[key] = goalCount.GetValueOrDefault(key) + 1;
            if (string.IsNullOrEmpty(actual)) nullGoal++;

            // 双向核：期望有值而实空、期望空而实有值、两边都有但不相等 —— 三种都算不一致。
            if (!string.Equals(actual ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal))
            {
                mismatch++;
                if (mismatchSample.Count < MaxSample)
                    mismatchSample.Add($"应={expected ?? "空"} 实={actual ?? "空"}" +
                                       $"/rootOrder={d.OrderId}/key={d.LogicalDemandKey}");
            }
        }

        Say($"需求切片={n} 条；Goal 取值分布：" + Join(goalCount.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}={kv.Value}")));
        Say("  └ 根订单类别剖面（按 OrderNo 前缀 ＋ CustomerSegment **独立复算**）：" +
            Join(byClass.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Say($"  └ **对账结论：与独立重算不一致 = {mismatch} 条**（≠0 即缺陷）；其中送出空值 = {nullGoal} 条" +
            $"（1号位 将按 `DEMAND_GOAL_ABSENT` 登记缺口，不静默）");
        Say($"  └ 跨厂 SH 行 = {shRows} 条 —— ⚠ **未落地项**：SH-02 要的「被真实上层需求承接」信号" +
            $"区分不出「SH 供给在目标厂自扣自己那张订单」（实测 PV2 四个 SH 订单 Allocation=4 而需求=0）" +
            $"⇒ 按 DG-05 §2.4 期望留空；本探针**不断言**其 Goal 具体值。");
        if (mismatch > 0)
            Say($"  ⚠ 不一致样例(最多{MaxSample}): " + Join(mismatchSample));

        // ② 反证：若按 OrderType 硬映射 Goal 会分歧多少条（DG-02 明令禁止的写法，此处只作**危害计量**）
        string? GoalByOrderType(LogicalProductionDemand d)
            => d.OrderId is { } id && orderMeta.TryGetValue(id, out var m)
                ? (m.OrderType == "PRODUCTION_INSTRUCTION"
                    ? DemandGoalValues.InventoryReplenishment
                    : DemandGoalValues.CustomerCommitment)
                : null;
        var byTypeDiff = demands.Count(d =>
            GoalByOrderType(d) is { } t && !string.IsNullOrEmpty(d.DemandGoal)
            && !string.Equals(t, d.DemandGoal, StringComparison.Ordinal));
        Say($"  └ 反证：若按 `OrderType` 硬映射，将有 **{byTypeDiff} 条**与按 OrderNo 前缀的判定分歧" +
            $"（DG-02/DG-04 明令禁止按 OrderType 硬映射；此数即该写法的误判规模）");

        // ③ 留空样例（逐条带根订单身份）
        var ordered = demands
            .Where(d => string.IsNullOrEmpty(d.DemandGoal))
            .OrderBy(d => d.OrderId).ThenBy(d => d.LogicalDemandKey)
            .Take(MaxSample)
            .Select(d =>
            {
                var mem = d.OrderId is { } id && orderMeta.TryGetValue(id, out var m) ? m : null;
                return $"{d.LogicalDemandKey}/mat={d.MaterialId}/PI={d.ProductionInstructionNo ?? "-"}" +
                       $"/rootOrder={d.OrderId}/OrderNo={mem?.OrderNo ?? "?"}/类型={mem?.OrderType ?? "?"}" +
                       $"/跨厂={(string.Equals(mem?.CustomerSegment, "跨厂", StringComparison.Ordinal) ? "是" : "否")}" +
                       $"/NeedBy={d.RequiredAvailableTime:yyyy-MM-dd}/Q={d.NetOutputQty}";
            });
        Say($"留空样例(最多{MaxSample}): " + Join(ordered));
    }

    /// <summary>
    /// 把订单集收窄成「小样本」（见调用点注释）。不设环境变量时原样返回全量。
    /// </summary>
    private static async Task<List<long>> ScopeOrderIdsAsync(
        DatabaseConnectionManager conn, List<long> allOrderIds)
    {
        var explicitIds = Environment.GetEnvironmentVariable("APS_PROBE_ORDER_IDS");
        if (!string.IsNullOrWhiteSpace(explicitIds))
        {
            var wanted = explicitIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var x) ? x : -1L)
                .Where(x => x > 0)
                .ToHashSet();
            var hit = allOrderIds.Where(wanted.Contains).ToList();
            Say($"⚠ 小样本定向跑：APS_PROBE_ORDER_IDS 指定 {wanted.Count} 个订单，" +
                $"本 PV 命中 {hit.Count} 个（未命中的 {wanted.Count - hit.Count} 个不在 PlanVersionId={PlanVersionId} 内）");
            return hit.Count > 0 ? hit : allOrderIds;
        }

        // ── 定向挑「在制」订单（2026-10-09）──
        // 背景：分桶=0 的合法输入只有「PI 在 MES 处于 IN_PROGRESS」的订单 —— PV2 实测 **284/22,144（1.3%）**。
        // 按「BOM 主档缺行」挑样本命中率 15.9%，n=5 期望命中 0.8 个 ⇒ **根本打不到分桶那条逻辑**（我踩过）。
        // 用法：`APS_PROBE_PICK=INPROGRESS`（可配 `APS_PROBE_SAMPLE=N` 限条数，默认 20）。
        if (string.Equals(Environment.GetEnvironmentVariable("APS_PROBE_PICK"), "INPROGRESS",
                          StringComparison.OrdinalIgnoreCase))
        {
            var pickN = int.TryParse(Environment.GetEnvironmentVariable("APS_PROBE_SAMPLE"), out var pn) && pn > 0
                ? pn : 20;
            var inProgress = (await conn.QueryAsync<long>(
                @"SELECT DISTINCT TOP (@n) o.Id
                  FROM [Order] o
                  INNER JOIN MESWorkOrderSnapshot m
                          ON m.ProductionInstructionNo = o.MTS_InstructionNo
                         AND m.WorkOrderStatus = 'IN_PROGRESS'
                  WHERE o.PlanVersionId = @pv
                  ORDER BY o.Id",
                new { n = pickN, pv = PlanVersionId },
                db: DatabaseId.APS, commandTimeout: 120)).ToList();
            Say($"⚠ 小样本定向跑：APS_PROBE_PICK=INPROGRESS 挑中 {inProgress.Count} 个「PI 在制」订单" +
                $"（口径：该订单 MTS_InstructionNo 在 MESWorkOrderSnapshot 有 IN_PROGRESS 工单；全 PV 仅 284 个）");
            return inProgress.Count > 0 ? inProgress : allOrderIds;
        }

        var sampleRaw = Environment.GetEnvironmentVariable("APS_PROBE_SAMPLE");
        if (!int.TryParse(sampleRaw, out var n) || n <= 0)
            return allOrderIds;

        // 关注现象（本轮修的两条路径）：
        //   ① BOM 子件在 `Material` 主档缺行（→ 旧实现在装载层被 ISNULL(...,0) 静默改成 MaterialId=0）；**稀有**
        //   ② BOM 子件 `SupplyMode='PURCHASE'` 且**无** `RoutingOperation`（→ 旧实现当自制件 ⇒ 凭空产生产需求）；
        //      「且无 Routing」是这一条的要害，不能省：采购件本就该没有工艺，有工艺的反而不是本路径的样本。
        //
        // ⚠ 2026-10-09 踩坑留痕：第一版把两条并成一个查询、`SupplyMode` 用**相关子查询 `EXISTS`**
        //   （1.8M 行 MSC × 84 万 BOM 行），30s 默认命令超时直接炸（`SqlException 执行超时已过期`）。
        //   改法：MSC 采购码集**先物化成临时表**（`SELECT DISTINCT … INTO #pc`），BOM 侧只做**等值 JOIN**；
        //   并且两阶段**分开跑、按需**（阶段②只在阶段①凑不满 N 时才跑）——阶段① 1.5s / 阶段② 6s。
        //   另：显式给 `commandTimeout: 120`，别再让默认 30s 把「查询重」伪装成「偶发失败」。
        const string sqlBatchNo =
            @"SELECT TOP 1 r.BatchNo
              FROM OrderBomRequestLink r
              INNER JOIN [Order] o ON o.Id = r.OrderId
              WHERE o.PlanVersionId = @pv
              ORDER BY r.SyncedAt DESC";

        var batchNo = await conn.QueryFirstOrDefaultAsync<string>(
            sqlBatchNo, new { pv = PlanVersionId }, db: DatabaseId.APS, commandTimeout: 120);

        if (string.IsNullOrWhiteSpace(batchNo))
        {
            Say($"⚠ 小样本定向跑：APS_PROBE_SAMPLE={n} 失效 —— 本 PV 无 OrderBomRequestLink.BatchNo，退回全量");
            return allOrderIds;
        }

        // 阶段①：主档缺行（稀有，优先取）。
        var picked = (await conn.QueryAsync<long>(
            @"SELECT DISTINCT TOP (@n) o.Id
              FROM [Order] o
              INNER JOIN OrderBomRequestLink r ON r.OrderId = o.Id
              INNER JOIN APS_BOM_RAW b ON b.BOMNO = r.ResolvedBOMNO AND b.BatchNo = @bn
              LEFT JOIN Material m ON m.MaterialCode = b.ChildMaterialCode
              WHERE o.PlanVersionId = @pv AND m.Id IS NULL
              ORDER BY o.Id",
            new { n, pv = PlanVersionId, bn = batchNo },
            db: DatabaseId.APS, commandTimeout: 120)).ToList();
        var fromStage1 = picked.Count;

        // 阶段②：采购件无工艺（补齐到 N）。
        if (picked.Count < n)
        {
            var topUp = (await conn.QueryAsync<long>(
                @"SELECT DISTINCT TOP (@n) o.Id
                  FROM [Order] o
                  INNER JOIN OrderBomRequestLink r ON r.OrderId = o.Id
                  INNER JOIN APS_BOM_RAW b ON b.BOMNO = r.ResolvedBOMNO AND b.BatchNo = @bn
                  INNER JOIN Material m ON m.MaterialCode = b.ChildMaterialCode
                  INNER JOIN (SELECT DISTINCT MaterialCode FROM MaterialSupplyContext
                              WHERE IsCurrent = 1 AND SupplyMode = 'PURCHASE') p
                          ON p.MaterialCode = b.ChildMaterialCode
                  LEFT JOIN RoutingOperation ro ON ro.MaterialId = m.Id
                  WHERE o.PlanVersionId = @pv AND ro.Id IS NULL
                  ORDER BY o.Id",
                new { n, pv = PlanVersionId, bn = batchNo },
                db: DatabaseId.APS, commandTimeout: 120)).ToList();

            var already = picked.ToHashSet();
            picked.AddRange(topUp.Where(id => !already.Contains(id)));
        }

        var sample = picked.Take(n).ToList();

        Say($"⚠ 小样本定向跑：APS_PROBE_SAMPLE={n} 自动挑中 {sample.Count} 个订单" +
            $"（阶段① 子件主档缺行={fromStage1} 个；阶段② 采购件子件无 Routing 补齐；BatchNo={batchNo}）");
        return sample.Count > 0 ? sample : allOrderIds;
    }

    // ─────────────────────────────────────────────────────────────────────
    // §T-04 两条契约（Candidate 模式）：真实产物 + 连接轴对照 + PI 基数
    //
    // 目的（用户口径：「装载给 1号位 的时候数据正确」）：
    //   ① 让 `BuildAnchorPayloadAsync` **真的跑进去**（此前硬写 `IsCandidate=false` ⇒ 恒早退），
    //      把 `DomainSolveRequest.ExecutionConstraints` / `OrderProtections` 的**真实产物**打出来；
    //   ② **独立重算**资格与命中数（不复制产品侧投影器），与产物条数比对 —— 探针必须能证伪自己；
    //   ③ 量出 item 1（连接轴改「PI + 工序」，`PM回复0908.md` §十）的**唯一未决前置**：
    //      一个 PI 在本轮需求集里到底对应几条 `LogicalProductionDemand`。
    // ─────────────────────────────────────────────────────────────────────
    private static async Task AuditAnchorsAsync(
        DatabaseConnectionManager conn,
        PeggingOrchestrator.PeggingPayload payload,
        int basePv)
    {
        var req = payload.SolveRequest;
        var demands = req.LogicalProductionDemands;

        Say("──────── §T-04 两条契约（Candidate 模式通电）────────");
        Say($"CandidateContext={(req.CandidateContext is null ? "null ⚠（早退了）" : "非空")} " +
            $"RunScope={(req.RunScope is null ? "null" : "非空")} BasePlanVersionId={basePv}");

        // ── 输入侧：上一 Base 的 `[Task]` 行 ──
        var rows = (await conn.QueryAsync<BaseTaskRow>(
            @"SELECT t.Id AS TaskId, t.OrderId, t.MaterialId, t.StageCode, t.OperationCode,
                     t.ResourceId, t.PlannedStartTime, t.PlannedEndTime,
                     t.Quantity, t.PlannedProcessQty, t.TaskNo,
                     t.MESWorkOrderNo, t.MTS_InstructionNo, t.Status,
                     o.ProductFamilyId, o.FactoryId
              FROM [Task] t
              LEFT JOIN [Order] o ON o.Id = t.OrderId
              WHERE t.PlanVersionId = @BasePlanVersionId",
            new { BasePlanVersionId = basePv }, db: DatabaseId.APS)).ToList();

        Say($"Base `[Task]` 行={rows.Count}" +
            (rows.Count == 0 ? " ⚠（BuildAnchorPayloadAsync 在此早退 ⇒ 两条契约均为空）" : ""));
        if (rows.Count > 0)
            Say("  └ Status 分布: " + Join(rows.GroupBy(r => r.Status ?? "<null>").Select(g => $"{g.Key}={g.Count()}")) +
                $"; MESWorkOrderNo 非空={rows.Count(r => !string.IsNullOrWhiteSpace(r.MESWorkOrderNo))}" +
                $"; MTS_InstructionNo 非空={rows.Count(r => !string.IsNullOrWhiteSpace(r.MTS_InstructionNo))}");

        // Fence 参数（窗口判定的唯一来源；归 3号位 治理）
        var fenceRows = (await conn.QueryAsync<FenceConfigRow>(
            @"SELECT Id, ProductFamilyId, FactoryId, ProcessType, FrozenDays, FirmDays, EffectiveFrom, EffectiveTo
              FROM FenceConfig", null, db: DatabaseId.APS)).ToList();
        // `DomainSolveRequest.DataCutoffTime` 是可空 `DateTime?` ⇒ 取值域要先剥空再判 default。
        var fenceBasis = req.DataCutoffTime is { } cutoff && cutoff != default ? cutoff : DateTime.Now;
        Say($"FenceConfig 行={fenceRows.Count}" +
            (fenceRows.Count == 0
                ? $" ⇒ **无窗口参数**（基准={fenceBasis:yyyy-MM-dd HH:mm}）⇒ 窗口分支一律 Movable，**不自造天数**"
                : $"，基准={fenceBasis:yyyy-MM-dd HH:mm}"));

        // ── 独立重算：资格 + 两条候选连接轴的命中数（见方法头注释②）──
        var demandByFour = demands
            .GroupBy(d => (d.OrderId, d.MaterialId, Norm(d.StartStageCode), Norm(d.StartOperationCode)))
            .ToDictionary(g => g.Key, g => g.ToList());
        var demandByPi = demands
            .Where(d => !string.IsNullOrWhiteSpace(d.ProductionInstructionNo))
            .GroupBy(d => (Pi: d.ProductionInstructionNo!.Trim(), Norm(d.StartStageCode), Norm(d.StartOperationCode)))
            .ToDictionary(g => g.Key, g => g.ToList());

        int expected = 0, incomplete = 0, noOrderId = 0;
        int fourHit = 0, fourUnmatched = 0, fourAmbiguous = 0;
        int piHit = 0, piUnmatched = 0, piAmbiguous = 0;
        var seen = new HashSet<(string Draft, string Stage, string Op)>();
        var consSample = new List<string>();

        foreach (var r in rows)
        {
            var stg = Norm(r.StageCode);
            var op = Norm(r.OperationCode);
            if (stg.Length == 0 || op.Length == 0 || r.ResourceId is null
                || r.PlannedStartTime is null || r.PlannedEndTime is null)
            {
                incomplete++;
                continue;
            }

            FenceResolution res;
            if (r.OrderId is null)
            {
                noOrderId++;
                res = new FenceResolution(null, FenceConfigResolver.StatusScopeNotFound, "Task 无 OrderId");
            }
            else
            {
                res = FenceConfigResolver.Resolve(fenceRows, fenceBasis, r.ProductFamilyId ?? 0, r.FactoryId);
            }

            var band = V1FenceCalculator.Classify(r.PlannedStartTime.Value, res.Parameters);
            // 消费纯函数（非复制逻辑）—— 判据一变，这里跟着变，不会漂移
            var qualified = V1AnchorQualifier.IsHardAnchor(r.MESWorkOrderNo, r.Status, band, out var ctype, out _);

            var four = demandByFour.TryGetValue((r.OrderId, r.MaterialId, stg, op), out var f) ? f : null;
            switch (four?.Count ?? 0)
            {
                case 0: fourUnmatched++; break;
                case 1: fourHit++; break;
                default: fourAmbiguous++; break;
            }

            var pi = r.MTS_InstructionNo?.Trim() ?? string.Empty;
            if (pi.Length == 0)
            {
                piUnmatched++;
            }
            else
            {
                var hits = demandByPi.TryGetValue((pi, stg, op), out var p) ? p : null;
                switch (hits?.Count ?? 0)
                {
                    case 0: piUnmatched++; break;
                    case 1: piHit++; break;
                    default: piAmbiguous++; break;
                }
            }

            if (!qualified || (four?.Count ?? 0) != 1)
                continue;

            var d0 = four![0];
            if (!seen.Add((d0.LogicalDemandKey, stg, op)))
                continue;

            expected++;
            if (consSample.Count < MaxSample)
                consSample.Add($"{d0.LogicalDemandKey} {stg}/{op} res={r.ResourceId} " +
                               $"{r.PlannedStartTime:MM-dd HH:mm}→{r.PlannedEndTime:MM-dd HH:mm} " +
                               $"qty={r.Quantity} type={ctype}");
        }

        Say("── 独立重算（不复用产品侧投影器）──");
        Say($"  复合键齐备行={rows.Count - incomplete}（缺 Stage/Op/Resource/时间={incomplete}；无 OrderId={noOrderId}）");
        Say($"  **本应产契约① 锚点={expected}**（与下方实际产物条数比对，不等即缺陷）");
        Say($"  四元组轴 (OrderId,MaterialId,Stage,Op)：命中1={fourHit} 未命中={fourUnmatched} 歧义={fourAmbiguous}");
        Say($"  PI轴 (MTS_InstructionNo,Stage,Op)：命中1={piHit} 未命中={piUnmatched} 歧义={piAmbiguous}" +
            "（Base 侧 PI 全空 ⇒ 该轴天然 0 命中 ⇒ **PI 回写前改轴会全丢锚点**）");

        // ── 实际产物 ──
        var cons = req.ExecutionConstraints;
        var prots = req.OrderProtections;
        Say("── 实际产物 ──");
        Say($"**契约① ExecutionConstraints={cons.Count}**（独立重算预期={expected}）" +
            (cons.Count == expected ? " ✅一致" : " ⚠**不一致**"));
        foreach (var c in cons.Take(MaxSample))
            Say($"    └DraftId={c.DraftId} {c.StageCode}/{c.OperationCode} res={c.ResourceId} " +
                $"{c.LockedStart:MM-dd HH:mm}→{c.LockedEnd:MM-dd HH:mm} qty={c.LockedQuantity} " +
                $"type={c.ConstraintType} TaskKey={c.TaskKey ?? "<null>"}");
        foreach (var s in consSample) Say($"  ·独立样本 {s}");
        Say($"**契约② OrderProtections={prots.Count}**" +
            (prots.Count == 0 ? "（本域无可投保护：无 Firm/Frozen 带、无执行事实）" : ""));
        foreach (var p in prots.Take(MaxSample))
            Say($"    └OrderId={p.OrderId} type={p.ProtectionType} reason={p.ProtectionReason}");

        // ── item 1 前置：PI → 本轮需求 的基数（`PM回复0908.md` §十 改轴的唯一未决项）──
        Say("── item 1 前置：PI → 本轮需求 基数 ──");
        var withPi = demands.Count(d => !string.IsNullOrWhiteSpace(d.ProductionInstructionNo));
        var byPi = demands.Where(d => !string.IsNullOrWhiteSpace(d.ProductionInstructionNo))
            .GroupBy(d => d.ProductionInstructionNo!.Trim(), StringComparer.Ordinal)
            .Select(g => new { Pi = g.Key, N = g.Count(), Keys = g.Select(x => x.LogicalDemandKey).Distinct().ToList() })
            .ToList();
        var multi = byPi.Where(x => x.N > 1).ToList();

        Say($"  需求侧：带 PI 需求={withPi} distinct PI={byPi.Count}");
        Say($"  **一个 PI 对应 >1 条需求的 PI 数={multi.Count}**" +
            (multi.Count == 0
                ? "（0 ⇒ PI 在本轮需求集内唯一 ⇒ 改 PI 轴不会撞 1号位 的 DraftId 唯一性）"
                : $" ⚠ max={multi.Max(x => x.N)} ⇒ 改轴前必须先定「同 PI 多需求时锚点挂哪条」"));
        foreach (var m in multi.OrderByDescending(x => x.N).Take(MaxSample))
            Say($"    └PI={m.Pi} 需求{m.N}条 key={Join(m.Keys.Take(MaxSample))}");

        var basePi = rows.Select(r => r.MTS_InstructionNo?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Say($"  Base 侧：distinct PI={basePi.Count}" +
            $"；与需求侧 PI 交集={basePi.Count(p => byPi.Any(x => x.Pi == p))}");
    }

    /// <summary>`[Task]` 行（本探针独立重算用；只读列）。</summary>
    private sealed class BaseTaskRow
    {
        public long TaskId { get; set; }
        public long? OrderId { get; set; }
        public int MaterialId { get; set; }
        public string? StageCode { get; set; }
        public string? OperationCode { get; set; }
        public int? ResourceId { get; set; }
        public DateTime? PlannedStartTime { get; set; }
        public DateTime? PlannedEndTime { get; set; }
        public decimal Quantity { get; set; }
        public decimal? PlannedProcessQty { get; set; }
        public string? TaskNo { get; set; }
        public string? MESWorkOrderNo { get; set; }
        public string? MTS_InstructionNo { get; set; }
        public string? Status { get; set; }
        public int? ProductFamilyId { get; set; }
        public int? FactoryId { get; set; }
    }

    private static string Norm(string? s) => (s ?? string.Empty).Trim();

    // ─────────────────────────────────────────────────────────────────────
    // §1 载荷规模 + 分桶守恒 + E 重复计
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditPayload(PeggingOrchestrator.PeggingPayload payload)
    {
        var req = payload.SolveRequest;
        var pre = payload.PreBucketingDemands;
        var post = req.LogicalProductionDemands;

        Say("──────── §0 规模 ────────");
        Say($"红线: 错误={payload.RedLineErrors.Count} {Join(payload.RedLineErrors)}");
        Say($"耗时: Pegging={payload.PeggingMs}ms 总(到载荷为止)={payload.TotalMs}ms");
        Say($"需求: 分桶前={pre.Count} 分桶后={post.Count} 差={post.Count - pre.Count}");
        Say($"需求量: ΣQ前={pre.Sum(d => d.NetOutputQty)} ΣQ后={post.Sum(d => d.NetOutputQty)} " +
            $"Σ工艺投入前={pre.Sum(d => d.PlannedProcessQty)} Σ后={post.Sum(d => d.PlannedProcessQty)}");
        Say($"供给分配: Allocation={req.AllocationLineage.Count} 物料需求链接={req.MaterialRequirementLinks.Count}");

        // ── 分桶守恒：源 key → 片 ──
        // 源 key 反解：Continuation 片 = key + "/WO:xxx"；Free 片 = key + "/FREE"；未切分 = 原 key。
        string SourceKeyOf(string sliceKey)
        {
            var i = sliceKey.IndexOf("/WO:", StringComparison.Ordinal);
            if (i >= 0) return sliceKey.Substring(0, i);
            if (sliceKey.EndsWith("/FREE", StringComparison.Ordinal))
                return sliceKey.Substring(0, sliceKey.Length - "/FREE".Length);
            return sliceKey;
        }

        var preByKey = pre.GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty), StringComparer.Ordinal);
        var postByKey = post.GroupBy(d => SourceKeyOf(d.LogicalDemandKey), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty), StringComparer.Ordinal);

        var lost = preByKey.Keys.Where(k => !postByKey.ContainsKey(k)).ToList();
        var added = postByKey.Keys.Where(k => !preByKey.ContainsKey(k)).ToList();
        var inflated = new List<string>();
        var shrunk = new List<string>();
        foreach (var kv in preByKey)
        {
            if (!postByKey.TryGetValue(kv.Key, out var after)) continue;
            if (after - kv.Value > 0.001m) inflated.Add($"{kv.Key}(源{kv.Value}→片{after})");
            else if (kv.Value - after > 0.001m) shrunk.Add($"{kv.Key}(源{kv.Value}→片{after})");
        }

        Say("──────── §1 分桶守恒（一进多出：Σ片Q 应 == 源Q，除 E>Q 超量与 Q=0 缺口）────────");
        Say($"源需求键={preByKey.Count} 片侧源键={postByKey.Count} 守恒={preByKey.Count - inflated.Count - shrunk.Count} " +
            $"放大={inflated.Count} 缩水={shrunk.Count} 丢失={lost.Count} 凭空多出={added.Count}");
        if (inflated.Count > 0) Say($"  ⚠放大样本: {Join(inflated.Take(MaxSample))}");
        if (shrunk.Count > 0) Say($"  ⚠缩水样本: {Join(shrunk.Take(MaxSample))}");
        if (lost.Count > 0) Say($"  ⚠丢失样本: {Join(lost.Take(MaxSample))}");
        if (added.Count > 0) Say($"  ⚠凭空多出样本: {Join(added.Take(MaxSample))}");

        var cont = post.Where(d => d.IsContinuation).ToList();
        var free = post.Where(d => !d.IsContinuation && d.LogicalDemandKey.EndsWith("/FREE", StringComparison.Ordinal)).ToList();
        Say($"切片: Continuation={cont.Count} 片 ΣE={cont.Sum(d => d.NetOutputQty)}；" +
            $"Free={free.Count} 片 ΣQ={free.Sum(d => d.NetOutputQty)}；" +
            $"未切分={post.Count - cont.Count - free.Count}");
        Say($"超量事件: {payload.OverCommits.Count} 条 " +
            $"(EXECUTION_OVER_COMMIT={payload.OverCommits.Count(e => e.Kind == "EXECUTION_OVER_COMMIT")}, " +
            $"DEMAND_MISMATCH={payload.OverCommits.Count(e => e.Kind == "DEMAND_MISMATCH")})");
        foreach (var g in payload.OverCommits.GroupBy(e => e.Kind)
                     .SelectMany(g => g.Take(MaxSample).Select(e => $"[{e.Kind}] {e.Message}")))
            Say($"  ⚠{g}");

        // ── E 重复计：同一 PI 被多个源需求携带时，各需求独立切「该 PI 的全部上下文」 ⇒ E 会被重复放大 ──
        var activeEByPi = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var ctxCountByPi = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (pi, pos) in payload.PiPositions)
        {
            var act = (pos.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
                .Where(c => c.DerivedRemainingQty > 0m).ToList();
            if (act.Count == 0) continue;
            activeEByPi[pi] = act.Sum(c => c.DerivedRemainingQty);
            ctxCountByPi[pi] = act.Count;
        }
        var eOfPiFromSlices = cont.Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
            .GroupBy(d => d.ProductionInstructionNo!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty), StringComparer.Ordinal);
        var dupPi = eOfPiFromSlices
            .Where(kv => activeEByPi.TryGetValue(kv.Key, out var real) && kv.Value - real > 0.001m)
            .Select(kv => $"PI={kv.Key} 片ΣE={kv.Value} 真实ΣE={activeEByPi[kv.Key]} 上下文数={ctxCountByPi[kv.Key]}")
            .ToList();
        var piWithMultiDemand = pre.Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
            .GroupBy(d => d.ProductionInstructionNo!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).ToList();

        Say("──────── §1b E 重复计（同 PI 被多需求携带 ⇒ 逐需求各切一遍全部上下文）────────");
        Say($"切出 Continuation 的 PI={eOfPiFromSlices.Count} 其中 片ΣE>真实ΣE 的 PI={dupPi.Count}；" +
            $"带 PI 的源需求按 PI 分组后 >1 条的 PI={piWithMultiDemand.Count}");
        foreach (var s in dupPi.Take(MaxSample)) Say($"  ⚠{s}");

        // ── 分桶为什么没切（Continuation=0 时必查）：需求侧的 PI 是否真的落在「有既存执行上下文」的 PI 上 ──
        var posWithCtx = payload.PiPositions
            .Where(kv => (kv.Value.ExistingExecutionContexts?.Count ?? 0) > 0)
            .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var posWithActiveCtx = payload.PiPositions
            .Where(kv => (kv.Value.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
                .Any(c => c.DerivedRemainingQty > 0m))
            .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var demandPiSet = pre.Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
            .Select(d => d.ProductionInstructionNo!).ToHashSet(StringComparer.Ordinal);
        Say($"分桶前置: PI Position={payload.PiPositions.Count} 其中带执行上下文={posWithCtx.Count} 带**活跃**(E>0)上下文={posWithActiveCtx.Count}");
        Say($"分桶前置: 源需求侧 PI={demandPiSet.Count}；与「带上下文 PI」交集={demandPiSet.Count(p => posWithCtx.Contains(p))} " +
            $"与「活跃上下文 PI」交集={demandPiSet.Count(p => posWithActiveCtx.Contains(p))}" +
            (cont.Count == 0 ? " ⚠（Continuation 为 0 的根因看这几个交集：分母非 0 而交集为 0 ⇒ 需求 PI 与 MES 工单 PI 不同源）" : ""));

        // ── Continuation 片 1号位 硬 Fail Closed 字段 ──
        var missCk = cont.Count(d => string.IsNullOrEmpty(d.ContinuationKey));
        var missRoute = cont.Count(d => string.IsNullOrEmpty(d.RouteCode));
        var missPath = cont.Count(d => d.PathId is null);
        var missStartOp = cont.Count(d => string.IsNullOrEmpty(d.StartOperationCode));
        var missNoSplit = cont.Count(d => !d.NoSplitMerge);
        Say("──────── §1c Continuation 片身份字段（缺任一 ⇒ 1号位 记 Unscheduled 且不产 Task）────────");
        Say($"片数={cont.Count} 缺ContinuationKey={missCk} 缺RouteCode={missRoute} 缺PathId={missPath} " +
            $"缺StartOperationCode={missStartOp} NoSplitMerge 未置={missNoSplit}");

        // ── 身份/数量自洽 ──
        say_identity();
        void say_identity()
        {
            var dupKey = post.GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
            Say("──────── §2 需求身份自洽 ────────");
            Say($"LogicalDemandKey 重复={dupKey.Count} {Join(dupKey.Take(MaxSample).Select(g => $"×{g.Count()}"))}");
            Say($"MaterialId<=0={post.Count(d => d.MaterialId <= 0)} FactoryId<=0={post.Count(d => d.FactoryId <= 0)} " +
                $"样本: {Join(post.Where(d => d.MaterialId <= 0).Take(MaxSample).Select(d => $"mat={d.MaterialId} key={d.LogicalDemandKey} Q={d.NetOutputQty}"))} | " +
                $"NetOutputQty<=0={post.Count(d => d.NetOutputQty <= 0m)} PlannedProcessQty<=0={post.Count(d => d.PlannedProcessQty <= 0m)}");
            Say($"OrderId 空={post.Count(d => d.OrderId is null)} DomainKey 空={post.Count(d => string.IsNullOrEmpty(d.DomainKey))} " +
                $"StartStageCode 空={post.Count(d => string.IsNullOrEmpty(d.StartStageCode))} RequiredStageCode 空={post.Count(d => string.IsNullOrEmpty(d.RequiredStageCode))} " +
                $"StartOperationCode 空={post.Count(d => string.IsNullOrEmpty(d.StartOperationCode))} UOM 空={post.Count(d => string.IsNullOrEmpty(d.UOM))}");
            Say($"AllocationSequence<=0={post.Count(d => d.AllocationSequence <= 0)} DemandKey 空={post.Count(d => string.IsNullOrEmpty(d.DemandKey))}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // §4 工艺图（RoutingOperations / RoutingDependencies / Eligibility）
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditGraph(DomainSolveRequest req)
    {
        Say("──────── §4 工艺图（交给 1号位 的节点/边/资源能力）────────");
        var ops = req.RoutingOperations;
        var deps = req.RoutingDependencies;
        var elig = req.OperationResourceEligibility;
        Say($"节点={ops.Count} 覆盖物料={ops.Select(o => o.MaterialId).Distinct().Count()} " +
            $"边={deps.Count} 覆盖物料={deps.Select(d => d.MaterialId).Distinct().Count()} 资源能力={elig.Count}");

        var dupNode = ops.GroupBy(o => (o.MaterialId, o.RouteCode, o.PathId, o.OperationCode))
            .Count(g => g.Count() > 1);
        Say($"节点键(Material,Route,Path,OpCode) 重复组={dupNode}" + (dupNode > 0 ? " ⚠" : ""));
        Say($"RouteCode 分布: {Join(ops.GroupBy(o => o.RouteCode).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}={g.Count()}"))}");

        // 逐 (Material,Route,Path) 建图：悬空边 / 无源结点 / 成环
        var nodeSet = ops.GroupBy(o => (o.MaterialId, o.RouteCode, o.PathId))
            .ToDictionary(g => g.Key, g => g.Select(o => o.OperationCode)
                .ToHashSet(StringComparer.Ordinal));
        var depByGraph = deps.GroupBy(d => (d.MaterialId, d.RouteCode, d.PathId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var dangling = 0;
        var danglingSample = new List<string>();
        foreach (var kv in depByGraph)
        {
            nodeSet.TryGetValue(kv.Key, out var nodes);
            foreach (var d in kv.Value)
            {
                var ok = nodes != null && nodes.Contains(d.FromOperationCode) && nodes.Contains(d.ToOperationCode);
                if (!ok)
                {
                    dangling++;
                    if (danglingSample.Count < MaxSample)
                        danglingSample.Add($"{kv.Key.MaterialId}/{kv.Key.RouteCode}/{kv.Key.PathId} {d.FromOperationCode}->{d.ToOperationCode}");
                }
            }
        }
        var noSource = 0;
        var cyclic = 0;
        var cyclicSample = new List<string>();
        var noSourceSample = new List<string>();
        foreach (var kv in nodeSet)
        {
            depByGraph.TryGetValue(kv.Key, out var edges);
            edges ??= new List<Core.Entities.APS.RoutingDependency>();
            var indeg = kv.Value.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            var adj = kv.Value.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var e in edges)
            {
                if (!indeg.ContainsKey(e.ToOperationCode) || !adj.ContainsKey(e.FromOperationCode)) continue;
                adj[e.FromOperationCode].Add(e.ToOperationCode);
                indeg[e.ToOperationCode]++;
            }
            if (!indeg.Values.Any(v => v == 0)) { noSource++; if (noSourceSample.Count < MaxSample) noSourceSample.Add($"{kv.Key.MaterialId}/{kv.Key.RouteCode}/{kv.Key.PathId}"); }

            var queue = new Queue<string>(indeg.Where(x => x.Value == 0).Select(x => x.Key));
            var seen = 0;
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue(); seen++;
                foreach (var nx in adj[cur]) if (--indeg[nx] == 0) queue.Enqueue(nx);
            }
            if (seen != kv.Value.Count)
            {
                cyclic++;
                if (cyclicSample.Count < MaxSample) cyclicSample.Add($"{kv.Key.MaterialId}/{kv.Key.RouteCode}/{kv.Key.PathId} 图节点={kv.Value.Count} 可达={seen}");
            }
        }
        Say($"悬空边(端点不在同图节点集内)={dangling}" + (dangling > 0 ? " ⚠" : "") + $" {Join(danglingSample)}");
        Say($"无入边源结点的图={noSource}" + (noSource > 0 ? " ⚠（该图无起点，1号位 建不出可达前沿）" : "") + $" {Join(noSourceSample)}");
        Say($"成环图={cyclic}" + (cyclic > 0 ? " ⚠（Phase5 硬校验会整域全丢）" : "") + $" {Join(cyclicSample)}");

        // 工序无任何可用资源 ⇒ 有限产能下排不下
        var eligKeys = elig.Select(e => (e.MaterialId, e.RouteCode, e.PathId, e.OperationCode)).ToHashSet();
        var noRes = ops.Where(o => !eligKeys.Contains((o.MaterialId, o.RouteCode, o.PathId, o.OperationCode))).ToList();
        Say($"无资源能力记录的工序={noRes.Count}/{ops.Count}" +
            $" {Join(noRes.Take(MaxSample).Select(o => $"{o.MaterialId}/{o.OperationCode}"))}");

        // ── 这些"坏图"是否真会砸到需求上（成环 = Phase5 硬校验整域全丢）──
        var demandMaterials = req.LogicalProductionDemands.Select(d => d.MaterialId).ToHashSet();
        var cyclicMaterials = cyclicSample
            .Select(s => s.Split(' ').Skip(1).FirstOrDefault() ?? string.Empty)
            .Where(s => s.Contains('/'))
            .Select(s => int.TryParse(s.Split('/')[0], out var m) ? m : 0)
            .Where(m => m > 0).ToHashSet();
        var opsMaterials = ops.Select(o => o.MaterialId).ToHashSet();
        var noRoutingMaterials = demandMaterials.Where(m => !opsMaterials.Contains(m)).ToList();
        Say($"需求物料={demandMaterials.Count}；**其中无任何 Routing 节点={noRoutingMaterials.Count}**" +
            $" {Join(noRoutingMaterials.Take(MaxSample).Select(m => m.ToString()))}" +
            (noRoutingMaterials.Count > 0 ? " ⚠（该物料的新增需求 1号位 建不出工序 ⇒ Unscheduled）" : ""));
        Say($"成环图涉及物料={Join(cyclicMaterials.Select(m => m.ToString()))}；" +
            $"是否落在需求物料集内={Join(cyclicMaterials.Select(m => $"{m}:{(demandMaterials.Contains(m) ? "是⚠" : "否")}"))}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // §5 部门上下文 / Stage 链 / 提前期 + §6 求解参数
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditDepartmentsAndParams(DomainSolveRequest req)
    {
        var demands = req.LogicalProductionDemands;
        var msc = req.MaterialStageDepartmentContexts;
        var chains = req.StageSequenceChains;

        Say("──────── §5 部门上下文 / Stage 链 / 提前期 ────────");
        Say($"MaterialStageDeptContext 行={msc.Count} 覆盖(物料,Stage)={msc.Select(c => (c.MaterialId, c.StageCode)).Distinct().Count()} " +
            $"覆盖物料={msc.Select(c => c.MaterialId).Distinct().Count()}");
        Say($"Stage 链={chains.Count} 覆盖物料={chains.Select(c => c.MaterialId).Distinct().Count()} " +
            $"每物料多条的物料数={chains.GroupBy(c => c.MaterialId).Count(g => g.Count() > 1)}");
        Say($"StageLeadTime 行={req.StageLeadTimes.Count} 覆盖(物料,Stage)={req.StageLeadTimes.Select(l => (l.MaterialId, l.StageCode)).Distinct().Count()}");

        var chainByMat = chains.GroupBy(c => c.MaterialId).ToDictionary(g => g.Key, g => g.First().Stages);
        var dupStageInChain = 0;
        var unsortedChain = 0;
        foreach (var kv in chainByMat)
        {
            if (kv.Value.Select(s => s.StageCode).Distinct().Count() != kv.Value.Count) dupStageInChain++;
            for (var i = 1; i < kv.Value.Count; i++)
                if (kv.Value[i].StageSeq < kv.Value[i - 1].StageSeq) { unsortedChain++; break; }
        }
        Say($"链内 Stage 重复的物料={dupStageInChain} StageSeq 非升序的物料={unsortedChain}");

        var mscKeys = msc.Select(c => (c.MaterialId, c.StageCode)).ToHashSet();
        var startMiss = demands.Count(d => !string.IsNullOrEmpty(d.StartStageCode) && !mscKeys.Contains((d.MaterialId, d.StartStageCode)));
        var reqMiss = demands.Count(d => !string.IsNullOrEmpty(d.RequiredStageCode) && !mscKeys.Contains((d.MaterialId, d.RequiredStageCode!)));
        Say($"StartStageCode 无部门映射的需求={startMiss}/{demands.Count}" +
            $" RequiredStageCode 无部门映射的需求={reqMiss}" + (startMiss > 0 || reqMiss > 0 ? " ⚠（1号位 记 MISSING_PRODUCTION_DEPARTMENT_CONTEXT 并置 Unscheduled）" : ""));

        // ⚠ 两个数字必须分开报、不可相除：分子是**需求条数**、分母是**物料数**（2026-10-10 修正）——
        // 旧写法 `无 Stage 链的需求物料=5661；需求物料数=12513` 会被读成 5661/12513=45%，
        // 实际物料级缺口只有 132/12513≈1%，132 个料身上压了 5661 条需求。
        var demandMats = demands.Select(d => d.MaterialId).Distinct().ToList();
        var noChainMats = demandMats.Count(m => !chainByMat.ContainsKey(m));
        var noChainDemands = demands.Count(d => !chainByMat.ContainsKey(d.MaterialId));
        Say($"无 Stage 链：物料={noChainMats}/{demandMats.Count}（{noChainMats * 100.0 / Math.Max(demandMats.Count, 1):F1}%）" +
            $" 需求条数={noChainDemands}/{demands.Count}（{noChainDemands * 100.0 / Math.Max(demands.Count, 1):F1}%）" +
            (noChainMats > 0 ? " ⚠" : ""));

        Say("──────── §6 求解参数 / 冻结块投影 ────────");
        var p = req.StrategySnapshot.Parameters;
        Say($"Parameters: AllowSplit={p.AllowSplit} AllowMerge={p.AllowMerge} Direction={p.SchedulingDirection} " +
            $"ImpactedTaskWarningPercent={p.ImpactedTaskWarningPercent} MaxPropagationRounds={p.MaxPropagationRounds} " +
            $"SplitAlternatives={p.SplitAlternatives} MinBatchQty={p.MinBatchQty}");
        Say($"Snapshot: StrategyProfileVersionId={req.StrategySnapshot.StrategyProfileVersionId} " +
            $"ParameterSetVersionId={req.StrategySnapshot.ParameterSetVersionId} " +
            $"SolverStrategy.Mode={req.StrategySnapshot.SolverStrategy?.Mode.ToString() ?? "(null)"} " +
            $"BatchPolicies={req.StrategySnapshot.BatchPolicies?.Count ?? 0} " +
            $"SetupTransitionRules={req.StrategySnapshot.SetupTransitionRules?.Count ?? 0}");
        // ⚠ 三个 0 里的前两个**曾经是探针假象**，2026-10-13 §T-1013x 已修（勿再当缺陷登）：
        //   Resources/CalendarSlots 由装载层从 `request.SchedulingContext` 投影（`PeggingOrchestrator.BuildResourceDefinitions`
        //     /`BuildResourceCalendarSlots`，首行即 `context.Resources.Count == 0 ⇒ 返回空`）；
        //   旧探针构造 `SchedulingContext` 时**只赋** StrategyProfileVersionId/DataCutoffTime/PlanHorizon* ⇒ **恒 0**，
        //     生产则由 `SchedulingOrchestrator.LoadResourcesAndCalendarAsync` 填 ⇒ 非 0 ⇒ **探针当时在资源轴上不具代表性**。
        //   现探针**直接调该生产方法**（已 internal；不自己复刻 SQL，免造漂移源）⇒ 本行不再恒 0，与生产同源。
        //   ResourceEligibility 是**另一回事、且非缺陷**：该 DTO 字段**已被 1号位 弃用**（`PhaseOneConstraintBuilder` 只留一行
        //     注释「不再使用旧的 ResourceEligibility」，全 `Scheduling` 零可执行引用），活的是下一行的 OperationResourceEligibility。
        //   ⚠ **仍与生产有别的一项**：`ScheduleRunId` 探针=0（未建 Run）⇒ 装载侧 `LoadMESProgressAsync` 不跑、`context.MESRemainingQty` 不填。
        //     但 `PeggingOrchestrator` 只从 context 取 StrategyProfileVersionId/ScheduleRunId/PlanHorizon*/Resources/ResourceCalendars
        //     （已逐点 grep 核过），**不读 MESRemainingQty** ⇒ 该差异**不流入本载荷**。（注：日志里 `ⓘ`/`⚠` 会显示成 `?`，是控制台编码，非内容丢失。）
        Say($"Resources={req.Resources.Count} CalendarSlots={req.CalendarSlots.Count} ResourceEligibility={req.ResourceEligibility.Count}" +
            " [注] 前两者来自生产同源装载；ResourceEligibility 为**弃用字段**（1号位 只读 OperationResourceEligibility）");
        Say($"ExecutionConstraints={req.ExecutionConstraints.Count}" +
            (req.ExecutionConstraints.Count == 0
                ? " [注] **已接线**（2026-10-13 落码）；为 0 是因 Base 无燃料：无 ACTIVE、[Task] 全 PLANNED、FenceConfig 0 行 —— 见 §T-1013w"
                : ""));
        Say($"CandidateContext={(req.CandidateContext is null ? "null(FULL)" : "非空")} RunScope={(req.RunScope is null ? "null(FULL)" : "非空")} " +
            $"UpstreamDomainResourceBlocks={req.UpstreamDomainResourceBlocks.Count}");
        Say($"窗口: PlanningStart={req.PlanningStart:yyyy-MM-dd HH:mm} PlanningEnd={req.PlanningEnd:yyyy-MM-dd HH:mm} DataCutoffTime={req.DataCutoffTime:yyyy-MM-dd HH:mm}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // §3 PI 归属（串味核验）：载荷里带 PI 的需求，其 PI 是否真属于该物料
    // ─────────────────────────────────────────────────────────────────────
    private static void AuditPiOwnership(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyDictionary<string, int> piMaterial)
    {
        Say("──────── §3 PI 归属（跨物料串味核验）────────");
        var withPi = demands.Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo)).ToList();
        var multiMatInPayload = withPi.GroupBy(d => d.ProductionInstructionNo!, StringComparer.Ordinal)
            .Select(g => new { Pi = g.Key, Mats = g.Select(d => d.MaterialId).Distinct().Count() })
            .Where(x => x.Mats > 1).ToList();
        Say($"带 PI 的片={withPi.Count} 覆盖 PI={withPi.Select(d => d.ProductionInstructionNo).Distinct().Count()}；" +
            $"载荷内 同一 PI 挂多个 MaterialId 的 PI={multiMatInPayload.Count} " +
            $"{Join(multiMatInPayload.Take(MaxSample).Select(x => $"{x.Pi}×{x.Mats}"))}");

        var checkedCnt = 0;
        var mismatch = new List<string>();
        foreach (var d in withPi)
        {
            if (!piMaterial.TryGetValue(d.ProductionInstructionNo!, out var owner)) continue;
            checkedCnt++;
            if (owner != d.MaterialId)
                mismatch.Add($"PI={d.ProductionInstructionNo} 权威物料={owner} 载荷物料={d.MaterialId} key={d.LogicalDemandKey}");
        }
        Say($"可与 DB 权威(PI→物料)对上的片={checkedCnt}/{withPi.Count}；" +
            $"**物料不符={mismatch.Count}**" + (mismatch.Count > 0 ? " ⚠" : ""));
        foreach (var s in mismatch.Take(MaxSample)) Say($"  ⚠{s}");
    }

    /// <summary>DB 侧 PI → MaterialId 权威映射（与 LoadPiRemainingFactsAsync 同源同闸门口径）。</summary>
    private static async Task<IReadOnlyDictionary<string, int>> LoadPiToMaterialMapAsync(DatabaseConnectionManager conn)
    {
        var rows = await conn.QueryAsync<PiMatRow>(
            @"SELECT MTS_InstructionNo AS PI, MaterialId FROM [Order]
              WHERE PlanVersionId=@p AND MTS_InstructionNo IS NOT NULL AND MTS_InstructionNo <> ''
              GROUP BY MTS_InstructionNo, MaterialId",
            new { p = PlanVersionId }, db: DatabaseId.APS);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (!map.ContainsKey(r.PI)) map[r.PI] = r.MaterialId;   // 一号多物料时取首个，并由 §3 的「一PI多物料」计数暴露
        return map;
    }

    private sealed class PiMatRow
    {
        public string PI { get; set; } = string.Empty;
        public int MaterialId { get; set; }
    }

    private static void Say(string line) => Console.WriteLine("AUDIT " + line);

    /// <summary>
    /// §1d 分桶切不出的逐条证据：把「需求侧 PI 命中的 PI Position 上下文」原样 dump。
    /// 用途：需求侧 PI 已能全中 PI Position（交集=命中数），但 `DerivedRemainingQty` 恒 0 ⇒ 到底
    /// 是 5号位 算出来就是 0、还是 Slices 空、还是我读错字段，**看值不看代码**。
    /// </summary>
    private static void AuditContinuityContexts(PeggingOrchestrator.PeggingPayload payload)
    {
        var demandPis = payload.SolveRequest.LogicalProductionDemands
            .Select(d => d.ProductionInstructionNo)
            .Where(pi => !string.IsNullOrWhiteSpace(pi))
            .Select(pi => pi!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var matched = demandPis.Where(pi => payload.PiPositions.ContainsKey(pi)).ToList();

        Say("──────── §1d 分桶为何切不出：需求侧 PI 命中的上下文逐条 ────────");
        Say($"需求侧 PI={demandPis.Count} 命中 PI Position={matched.Count}");

        foreach (var pi in matched.Take(MaxSample))
        {
            var pos = payload.PiPositions[pi];
            var ctxs = pos.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>();
            Say($"PI={pi} 上下文数={ctxs.Count}");
            foreach (var c in ctxs.Take(3))
            {
                var slices = c.Slices ?? Array.Empty<ExistingExecutionSliceDto>();
                Say($"   工单={c.MESWorkOrderNo} 状态={c.WorkOrderStatus} E={c.DerivedRemainingQty} " +
                    $"Slices={slices.Count} ΣSliceQty={slices.Sum(s => s.SliceQty)} " +
                    $"起点Stage={c.StartStageCode ?? "<null>"} 起点工序={c.StartOperationCode ?? "<null>"} " +
                    $"Issue={c.IssueDescription ?? "-"}");
                foreach (var s in slices.Take(3))
                    Say($"      └Slice Stage={s.StartStageCode} 工序={s.StartOperationCode ?? "<null>"} " +
                        $"量={s.SliceQty} Issue={s.IssueCode ?? "-"}");
            }
        }

        var allCtx = matched
            .SelectMany(pi => payload.PiPositions[pi].ExistingExecutionContexts
                              ?? Array.Empty<ExistingExecutionContextDto>())
            .ToList();
        Say($"命中上下文合计={allCtx.Count}；其中 E>0={allCtx.Count(c => c.DerivedRemainingQty > 0)}；" +
            $"Slices 非空={allCtx.Count(c => (c.Slices?.Count ?? 0) > 0)}；ΣE={allCtx.Sum(c => c.DerivedRemainingQty)}");
        Say("命中上下文 工单状态分布: " + string.Join(" | ",
            allCtx.GroupBy(c => c.WorkOrderStatus ?? "<null>")
                  .OrderByDescending(g => g.Count())
                  .Select(g => $"{g.Key}={g.Count()}")));
        Say("命中上下文 起点Stage 分布: " + string.Join(" | ",
            allCtx.GroupBy(c => c.StartStageCode ?? "<null>")
                  .OrderByDescending(g => g.Count())
                  .Take(8)
                  .Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>
    /// 极简 Console 日志 Sink（`APS_PROBE_LOG=1` 时挂）——只为取**分段耗时**。
    /// 只放行 Warning+ 与含 <c>[Pegging]</c> 的埋点，其余（尤其逐 PI 的 Debug/Info）扔掉，
    /// 否则 I/O 反而污染计时。不引 `Microsoft.Extensions.Logging.Console` 包，手写最省事。
    /// </summary>
    private sealed class ProbeConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ProbeConsoleLogger(categoryName);
        public void Dispose() { }

        private sealed class ProbeConsoleLogger : ILogger
        {
            private readonly string _category;
            public ProbeConsoleLogger(string category) => _category = category;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel level) => true;

            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
                                    Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, ex);
                // `APS_PROBE_LOG=1`（默认）：只留 `[Pegging][` 埋点行（阶段/供给池分段耗时、埋点计数）。
                //   上一版连 Warning 一起放行 ⇒ 逐 PI 的 Warning 刷了 **36,767 行**，日志 I/O 自己成了瓶颈。
                // `APS_PROBE_LOG=2`：再加上一切 Warning 及以上（查数据问题时才用）。
                var wantWarnings = Environment.GetEnvironmentVariable("APS_PROBE_LOG") == "2";
                var keep = msg.Contains("[Pegging][", StringComparison.Ordinal)
                           || (wantWarnings && level >= LogLevel.Warning);
                if (!keep) return;
                Console.WriteLine($"LOGV {DateTime.Now:HH:mm:ss.fff} [{level}] {_category.Split('.').Last()}: {msg}");
            }
        }
    }

    private static string Join(IEnumerable<string?> xs) => string.Join(" | ", xs);
}
