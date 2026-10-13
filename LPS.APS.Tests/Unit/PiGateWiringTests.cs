using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **2026-10-12 门禁通电的「接线守卫」**（走**正式** `FiniteCapacitySolver.SolveAsync` 生产入口，
///   不是只测 Helper）。
///
/// 【为什么必须有本文件】
///   接线前，生产入口 `PhaseTwoInitialScheduler.Schedule` 对 `FormExecutionBatches` 只传
///   `piStageLedger`，`piFacts` / `piStageContext` **走默认 null** ⇒ `ApplyPiLegalQuantityGate` 命中
///   `piFacts is null` 早退（只 `CountPiLegalQuantityGateUnevaluated`，**不拦截**）。
///   「接线」= 那两个实参换成 `request.PiRemainingFacts` + `ResolvePiStageContext(piStageIndex, …)`。
///   这类改动**编译期发现不了**，只能靠**端到端 + 计数器**守：接线掉了，下面 ② 会重新变成「有 Task」而**红**。
///   ⚠ **实测更正（2026-10-12）**：`DomainSolveRequest.PiRemainingFacts` 的 Core 默认值是
///   `Array.Empty&lt;PiRemainingFact&gt;()`（**空列表，非 null**）⇒ 接线后**既有单测里的 PI 自由需求会真被评估**
///   （`ContinuityShareTests.C02` 一度因此红，其夹具已补投影）。所以本文件不是「唯一能发现」的守卫，
///   而是**专测门禁档位**（放行 / 拦截 / 未评估）的判别性夹具 —— 这一点比原以为的更强，不是更弱。
///
/// 依据：2号位《APS_V1_20261012_门禁通电信号_2号位致1号位_v1.0》§一（覆盖率达标信号）/
///   §二之二（放行 9,091 / 9,324）＋ 1号位 答复件 §一（「覆盖率达标 ⇒ 我方一个提交内接线」）。
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class PiGateWiringTests
{
    private static readonly DateTime PlanningStart = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new(2026, 10, 31, 0, 0, 0);

    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const string Route = "RTA";
    private const int Path = 1;
    private const string PiNo = "PI-1";
    private const string Stage = "STAGE1";

    private readonly FiniteCapacitySolver _solver = new();

    // ══════════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════════

    private static PiRemainingFact Fact(
        string piNo = PiNo, int materialId = MaterialId,
        decimal original = 100m, decimal remaining = 100m, int factoryId = 1)
        => new()
        {
            ProductionInstructionNo = piNo,
            MaterialId = materialId,
            FactoryId = factoryId,
            OrderType = string.Empty,
            PiQuantity = original,
            PiReceivedQty = original - remaining,
            PiRemainingQty = remaining
        };

    private static PiStageQuantityBaseline Row(
        string piNo = PiNo, int materialId = MaterialId, string stage = Stage,
        decimal stageFree = 100m, decimal committed = 0m)
        => new()
        {
            ProductionInstructionNo = piNo,
            MaterialId = materialId,
            StageCode = stage,
            StageFreeEligibleQty = stageFree,
            AlreadyCommittedQty = committed
        };

    /// <summary>
    /// 单 Stage、单资源、单路径的 **C 桶**夹具（需求不声明 Route/Path ⇒ 走候选内择优）；
    ///   `BatchPolicies` 用宽松策略（`AllowSplit=false`、`Max=null` ⇒ 恒 1 批 = 需求量）——
    ///   目的是让**门禁**成为唯一变量，而不是让拆批规则参与。
    /// </summary>
    private static DomainSolveRequest BuildRequest(
        decimal qty = 10m,
        IReadOnlyList<PiRemainingFact>? piFacts = null,
        IReadOnlyList<PiStageQuantityBaseline>? baselines = null)
    {
        var windowStart = PlanningStart.AddHours(8);
        var windowEnd = windowStart.AddHours(11);

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1",
                    MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = Stage,
                    ProductionInstructionNo = PiNo,
                    NetOutputQty = qty, PlannedProcessQty = qty,
                    RequiredAvailableTime = PlanningStart.AddDays(20), DemandSequence = 1
                    // RouteCode / PathId 故意留空 ⇒ C 桶。
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = Route, PathId = Path,
                    OperationCode = "OP10", StageCode = Stage,
                    StandardDuration = 60m, OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            RoutingDependencies = Array.Empty<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = Route, PathId = Path,
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = windowStart, End = windowEnd, IsAvailable = true }
            },
            PiRemainingFacts = piFacts!,
            PiStageQuantityBaselines = baselines ?? Array.Empty<PiStageQuantityBaseline>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  ① 对照：未投影（= 接线前形态）⇒ 门禁不可评估、不拦截
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ① `PiRemainingFacts` 未投影（`null`）⇒ `ApplyPiLegalQuantityGate` 命中早退：**只登记缺口、不拦截**
    ///   （0号位 §8「不要求重新冻结业务」；凭空发明「每条 PI 需求都拦下」的失败模式是不允许的）。
    ///   本用例是 ② 的**判别性对照**：同一夹具、同一数量，唯一差别是事实是否投影。
    ///
    ///   ⚠ **口径提醒（2026-10-12 实测）**：`null` 只能由**显式**赋 null 造出
    ///   （`DomainSolveRequest.PiRemainingFacts` 的 Core 默认是**空列表**）；生产唯一构造点
    ///   `PeggingOrchestrator:358` 恒投影 ⇒ **「未评估」档在生产线路上恒为 0**，
    ///   而「整体未投影」会表现为**全量拦截**而非未评估。本用例守的是**判据本身**（null 分支不误伤），
    ///   不是「生产会出现这一档」。
    /// </summary>
    [Fact]
    public async Task 未投影_门禁不可评估_只登记缺口不拦截()
    {
        var request = BuildRequest(qty: 10m, piFacts: null, baselines: null);

        using var scope = SolverDiagnostics.BeginScope();
        var result = await _solver.SolveAsync(request);

        Assert.NotEmpty(result.FinalTasks);                                  // 需求照常排下
        Assert.True(scope.Counters.PiLegalQuantityGateUnevaluated >= 1, "未投影必须登记缺口（不静默）");
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlockedViaB010Fallback);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGatePassed);           // 早退 ≠ 放行
    }

    // ══════════════════════════════════════════════════════════════════
    //  ② 接线守卫：已投影 ⇒ 门禁强制（超 Stage 合法自由量 ⇒ 零 Task）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ② **接线守卫（本文件的核心）**：事实已投影、且 Stage 合法自由量 **5 &lt; 需求 10** ⇒ 门禁必须**强制**：
    ///   · `PiLegalQuantityGateUnevaluated` **= 0** —— 这一条**只能**由「生产入口真把 `piFacts` 传进来了」解释
    ///     （接线掉了 ⇒ 该早退重新命中 ⇒ 本断言红）；
    ///   · `PiLegalQuantityGateBlocked` **≥ 1** —— 门禁确实判了越限；
    ///   · **零 FinalTask** —— 拦截真生效（不是只计数）。
    ///
    /// 说明：本夹具 `BatchPolicies` 为宽松策略 ⇒ 走**有策略路径**（`ApplyPiLegalQuantityGate`）而非 B-010 兜底，
    ///   故断言的是 `PiLegalQuantityGateBlocked`；B-010 路径的计数由 `PiLegalQuantityGateTests` ⑮/⑮b 覆盖。
    /// </summary>
    [Fact]
    public async Task 已投影_门禁强制_超Stage合法自由量则零Task且登记拦截()
    {
        var request = BuildRequest(
            qty: 10m,
            piFacts: new[] { Fact(original: 100m, remaining: 100m) },
            baselines: new[] { Row(stageFree: 5m) });      // Stage 合法自由 5 < 需求 10

        using var scope = SolverDiagnostics.BeginScope();
        var result = await _solver.SolveAsync(request);

        Assert.Empty(result.FinalTasks);                                    // 门禁真生效
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateUnevaluated);     // ← 接线守卫：必须为 0
        Assert.True(scope.Counters.PiLegalQuantityGateBlocked >= 1,
            "已投影且超量 ⇒ 门禁必须拦截并计数（接线掉了本断言会红）");
        Assert.Equal(0, scope.Counters.PiLegalQuantityGatePassed);          // 拦截 ⇒ 不计放行（两档互斥）
    }

    // ══════════════════════════════════════════════════════════════════
    //  ③ 接线不误伤：已投影且量限满足 ⇒ 照常排下
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ③ **接线不误伤**：同一夹具、事实已投影，但 Stage 合法自由量 **充足（100 ≥ 需求 10）** ⇒
    ///   门禁评估后**放行**，需求照常产出 FinalTask。与 ② 构成**同夹具判别对**（唯一变量 = Stage 自由量）。
    /// </summary>
    [Fact]
    public async Task 已投影_量限充足_门禁放行且照常排下()
    {
        var request = BuildRequest(
            qty: 10m,
            piFacts: new[] { Fact(original: 100m, remaining: 100m) },
            baselines: new[] { Row(stageFree: 100m) });

        using var scope = SolverDiagnostics.BeginScope();
        var result = await _solver.SolveAsync(request);

        Assert.NotEmpty(result.FinalTasks);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateUnevaluated);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGatePassed);          // ← 放行档（有策略路径）
    }
}
