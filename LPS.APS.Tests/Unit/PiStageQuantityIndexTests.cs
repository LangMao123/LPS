using System;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **接线就绪单测**（2026-10-12）：`PiStageQuantityBaseline`（Core 入参）→ `PiStageQuantityContext`
/// （1号位 消费形状）的**投影段** —— <c>BuildPiStageQuantityIndex</c> / <c>ResolvePiStageContext</c>。
///
/// 【为什么先落这段】生产入口当前 `piFacts` / `piStageContext` **传 null（未通电）**（见
///   `PhaseTwoInitialScheduler` 中 `FormExecutionBatches` 调用点就地注释）。若等到「事实覆盖率上来」
///   那天才首次写这段，那天就是**首次集成调试** ⇒ 被动（用户 2026-10-12 指出）。
///   ⇒ 投影段**先落并在此锁住**，激活时只剩「加两个实参」。
///
/// 【判别性重点】同 `(PI, 物料, Stage)` 多行 ⇒ **不得取首条**（0号位 裁决 F-02 判死的缺陷形态：
///   「多行匹配时**静默选定一条**」）⇒ 本实现**摘除该键** ⇒ 门禁按「缺 Stage 上下文」Fail Closed。
///
/// 全部纯内存，不触库（用户红线：`LPS.APS.Tests/Integration/` 直连生产库，只跑 Unit）。
/// </summary>
public class PiStageQuantityIndexTests
{
    private const int MaterialId = 1;
    private const int FactoryId = 1;
    private const string PiNo = "PI-1";
    private const string Stage = "STAGE1";

    // ══════════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Core 侧 baseline 行（2号位 装载 → 1号位 门禁）。</summary>
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

    private static PiRemainingFact Fact(
        string piNo = PiNo, int materialId = MaterialId,
        decimal original = 100m, decimal remaining = 100m, int factoryId = FactoryId)
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

    private static LogicalProductionDemand Demand(decimal netQty = 40m)
        => new()
        {
            LogicalDemandKey = "D1",
            ProductionInstructionNo = PiNo,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = "D1",
            MaterialId = MaterialId,
            FactoryId = FactoryId,
            StartStageCode = Stage,
            NetOutputQty = netQty,
            PlannedProcessQty = netQty,
            RequiredAvailableTime = new DateTime(2026, 9, 21, 0, 0, 0),
            DemandSequence = 1
        };

    private static BatchPolicyRuleSnapshot Policy(decimal? max = null)
        => new()
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = 100,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = max,
            AllowSplit = true,
            AllowMerge = false
        };

    // ══════════════════════════════════════════════════════════════════
    //  ① 建索引
    // ══════════════════════════════════════════════════════════════════

    /// <summary>null / 空行集 ⇒ 空索引（不抛、不造行）。</summary>
    [Fact]
    public void 建索引_null或空_返回空索引()
    {
        var fromNull = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(null, out var ambiguousNull);
        Assert.Empty(fromNull);
        Assert.Empty(ambiguousNull);

        var fromEmpty = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            Array.Empty<PiStageQuantityBaseline>(), out var ambiguousEmpty);
        Assert.Empty(fromEmpty);
        Assert.Empty(ambiguousEmpty);
    }

    /// <summary>字段**逐字搬运、无换算**（与 `PiStageQuantityContext` 1:1）。</summary>
    [Fact]
    public void 建索引_字段逐字搬运_无换算()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 77.5m, committed: 12.25m) }, out var ambiguous);

        Assert.Empty(ambiguous);
        var ctx = Assert.Single(index).Value;
        Assert.Equal(PiNo, ctx.ProductionInstructionNo);
        Assert.Equal(MaterialId, ctx.MaterialId);
        Assert.Equal(Stage, ctx.StageCode);
        Assert.Equal(77.5m, ctx.StageFreeEligibleQty);
        Assert.Equal(12.25m, ctx.AlreadyCommittedQty);
    }

    /// <summary>
    /// **判别性**：同 `(PI, 物料, Stage)` 两行（自由量 100 / 999）⇒ **不取首条也不取末条** ——
    ///   该键被**摘除**（索引为空）且撞键留痕 ⇒ 门禁按「缺 Stage 上下文」显式未满足。
    ///   旧式 `ToDictionary` 在此**抛异常**；「静默取首条」则会把 100 当成可用量（0号位 裁决 F-02 判死的形态）。
    /// </summary>
    [Fact]
    public void 建索引_同三元组多行_摘除该键且留痕_不取首条()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 100m), Row(stageFree: 999m) }, out var ambiguous);

        Assert.Empty(index);
        var key = Assert.Single(ambiguous);
        Assert.Equal((PiNo, MaterialId, Stage), key);
        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(index, PiNo, MaterialId, Stage));
    }

    /// <summary>撞键只留痕一次（3 行同键 ⇒ 索引空、留痕 1 条，不重复记）。</summary>
    [Fact]
    public void 建索引_同三元组三行_留痕只一条()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 1m), Row(stageFree: 2m), Row(stageFree: 3m) }, out var ambiguous);

        Assert.Empty(index);
        Assert.Single(ambiguous);
    }

    /// <summary>空 PI 号 / 空 StageCode 的行**不可键控** ⇒ 跳过（这类行本就不该产出）。</summary>
    [Fact]
    public void 建索引_空PI号或空Stage的行_跳过()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[]
            {
                Row(piNo: string.Empty),
                Row(piNo: "   "),
                Row(stage: string.Empty),
                Row(stage: "   "),
                Row(piNo: "PI-2", stage: "STAGE2")
            },
            out var ambiguous);

        Assert.Empty(ambiguous);
        var only = Assert.Single(index);
        Assert.Equal(("PI-2", MaterialId, "STAGE2"), only.Key);
    }

    /// <summary>不同三元组各自独立，互不干扰。</summary>
    [Fact]
    public void 建索引_不同三元组_各自独立()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[]
            {
                Row(piNo: "PI-1", stage: "STAGE1", stageFree: 10m),
                Row(piNo: "PI-1", stage: "STAGE2", stageFree: 20m),
                Row(piNo: "PI-2", stage: "STAGE1", stageFree: 30m),
                Row(piNo: "PI-1", materialId: 9, stage: "STAGE1", stageFree: 40m)
            },
            out var ambiguous);

        Assert.Empty(ambiguous);
        Assert.Equal(4, index.Count);
        Assert.Equal(10m, index[("PI-1", MaterialId, "STAGE1")].StageFreeEligibleQty);
        Assert.Equal(20m, index[("PI-1", MaterialId, "STAGE2")].StageFreeEligibleQty);
        Assert.Equal(30m, index[("PI-2", MaterialId, "STAGE1")].StageFreeEligibleQty);
        Assert.Equal(40m, index[("PI-1", 9, "STAGE1")].StageFreeEligibleQty);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ② 按需求三元组取上下文
    // ══════════════════════════════════════════════════════════════════

    /// <summary>精确命中 ⇒ 返回该上下文。</summary>
    [Fact]
    public void 取上下文_精确命中_返回上下文()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 55m) }, out _);

        var ctx = PhaseTwoInitialScheduler.ResolvePiStageContext(index, PiNo, MaterialId, Stage);

        Assert.NotNull(ctx);
        Assert.Equal(55m, ctx!.StageFreeEligibleQty);
    }

    /// <summary>Stage 不匹配 ⇒ `null`（门禁随后按「错 Stage」显式未满足）。</summary>
    [Fact]
    public void 取上下文_Stage不匹配_返回null()
        => Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(
            PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(new[] { Row() }, out _),
            PiNo, MaterialId, "STAGE-OTHER"));

    /// <summary>物料不匹配 ⇒ `null`。</summary>
    [Fact]
    public void 取上下文_物料不匹配_返回null()
        => Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(
            PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(new[] { Row() }, out _),
            PiNo, 999, Stage));

    /// <summary>PI 不匹配 ⇒ `null`。</summary>
    [Fact]
    public void 取上下文_PI不匹配_返回null()
        => Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(
            PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(new[] { Row() }, out _),
            "PI-OTHER", MaterialId, Stage));

    /// <summary>索引为 null / 空，或三元组任一为空 ⇒ `null`（Fail Closed，不猜）。</summary>
    [Fact]
    public void 取上下文_无索引或三元组为空_返回null()
    {
        var empty = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            Array.Empty<PiStageQuantityBaseline>(), out _);

        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(null, PiNo, MaterialId, Stage));
        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(empty, PiNo, MaterialId, Stage));
        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(
            PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(new[] { Row() }, out _), null, MaterialId, Stage));
        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(
            PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(new[] { Row() }, out _), PiNo, MaterialId, null));
    }

    // ══════════════════════════════════════════════════════════════════
    //  ④ 防串用反例（0号位 交付清单 ③：S1-02「消费索引和账本防串用」）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// **防串用（跨工厂）** —— S1-02 缺口的**消费侧表现**：
    /// `PiStageQuantityBaseline` 无 `FactoryId`（0号位《正式回复 v2.0》S1-02；已核 `Core/Dto/DomainSolveRequest.cs`）
    /// ⇒ **两个工厂同 `(PI, 物料, Stage)`** 的两条真实行，在本索引里**键完全相同** ⇒ 走**撞键摘除**
    /// ⇒ 取上下文 `null` ⇒ 门禁 **Fail Closed**（显式未满足），**绝不静默用任一工厂的额度放行**。
    /// ⚠ 这是**缺口在我方消费侧的兜底**：不是「已支持两工厂」，而是「两工厂载荷下**拒绝串用**」。
    ///   补 `FactoryId` 归 2号位（Core）；补上后本用例期望应从「摘除」改为「各自独立可键控」。
    /// </summary>
    [Fact]
    public void 防串用_两工厂同PI同物料同Stage_无FactoryId则撞键摘除_不静默串用()
    {
        // 两个工厂各一行；工厂身份在 Core DTO 里**无处可放** ⇒ 两行在索引键上退化为同一三元组。
        var factoryA = Row(stageFree: 100m);
        var factoryB = Row(stageFree: 7m);

        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { factoryA, factoryB }, out var ambiguous);

        Assert.Empty(index);                                   // 摘除，不取首条（否则会把 100 当成唯一额度）
        Assert.Equal((PiNo, MaterialId, Stage), Assert.Single(ambiguous));
        Assert.Null(PhaseTwoInitialScheduler.ResolvePiStageContext(index, PiNo, MaterialId, Stage));
    }

    /// <summary>
    /// **防串用（跨 PI）**：同物料 / 同 Stage、**不同 PI 号** ⇒ 各自独立键控，
    /// 按需求 PI 取上下文时**只取本 PI 的额度**，**绝不互借**。
    /// 比单行 null 断言更强：索引里**确有**另一 PI 的同 Stage 行（若实现按「物料+Stage」而非三元组取 ⇒ 会取到 30）。
    /// </summary>
    [Fact]
    public void 防串用_同物料同Stage不同PI_只取本PI额度_不互借()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[]
            {
                Row(piNo: "PI-1", stageFree: 10m),
                Row(piNo: "PI-2", stageFree: 30m)
            },
            out _);

        Assert.Equal(10m, PhaseTwoInitialScheduler.ResolvePiStageContext(index, "PI-1", MaterialId, Stage)!.StageFreeEligibleQty);
        Assert.Equal(30m, PhaseTwoInitialScheduler.ResolvePiStageContext(index, "PI-2", MaterialId, Stage)!.StageFreeEligibleQty);
    }

    /// <summary>
    /// **防串用（跨物料）**：同 PI / 同 Stage、**不同物料** ⇒ 各自独立键控，**绝不互借**。
    /// （索引里确有另一物料的同 Stage 行。）
    /// </summary>
    [Fact]
    public void 防串用_同PI同Stage不同物料_只取本物料额度_不互借()
    {
        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[]
            {
                Row(materialId: 1, stageFree: 10m),
                Row(materialId: 2, stageFree: 30m)
            },
            out _);

        Assert.Equal(10m, PhaseTwoInitialScheduler.ResolvePiStageContext(index, PiNo, 1, Stage)!.StageFreeEligibleQty);
        Assert.Equal(30m, PhaseTwoInitialScheduler.ResolvePiStageContext(index, PiNo, 2, Stage)!.StageFreeEligibleQty);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ③ 端到端：投影段接上门禁后，生产形状的载荷能**放行**（今天从未执行过的那条路径）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 端到端**正证**：`Baselines`（真实形状）→ 索引 → 按需求三元组取上下文 → 过门禁 ⇒
    ///   `IsLegal=true`、批照常产出、`Blocked` / `Unevaluated` 计数均为 0。
    ///   `StageFree=40` 恰等于需求总量 40（最紧项）⇒ 合法。
    /// </summary>
    [Fact]
    public void 端到端_投影后过门禁_产出合法批()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 40m) }, out _);
        var demand = Demand(netQty: 40m);

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, Policy(max: 100m),
            piFacts: new[] { Fact(original: 100m, remaining: 100m) },
            piStageContext: PhaseTwoInitialScheduler.ResolvePiStageContext(
                index, demand.ProductionInstructionNo, demand.MaterialId, demand.StartStageCode));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var single = Assert.Single(formation.Batches);
        Assert.Equal(40m, single.NetOutputQty);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateUnevaluated);
    }

    /// <summary>
    /// 端到端**反证（撞键）**：同三元组多行 ⇒ 索引摘除该键 ⇒ 取上下文为 `null` ⇒ 门禁**显式未满足**
    ///   （不是静默用某一行放行）⇒ `IsLegal=false`、0 批、原因带「缺 Stage 合法自由量上下文」、`Blocked` +1。
    /// </summary>
    [Fact]
    public void 端到端_撞键_门禁显式未满足_不成批()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            new[] { Row(stageFree: 100m), Row(stageFree: 999m) }, out _);
        var demand = Demand(netQty: 40m);

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, Policy(max: 100m),
            piFacts: new[] { Fact() },
            piStageContext: PhaseTwoInitialScheduler.ResolvePiStageContext(
                index, demand.ProductionInstructionNo, demand.MaterialId, demand.StartStageCode));

        Assert.False(formation.IsLegal);
        Assert.Empty(formation.Batches);
        Assert.Contains("缺 Stage 合法自由量上下文", formation.ConflictReason);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGateBlocked);
    }

    /// <summary>
    /// 端到端**反证（无行）**：`Baselines` 为空（= 2号位 实测的 99.9% 情形）⇒ 取上下文为 `null` ⇒
    ///   门禁显式未满足 ⇒ `IsLegal=false`、0 批。
    ///   ⚠ 本用例**同时如实固定**「覆盖率未达标时通电 = 不成批」这一后果（即当前不通电的依据）。
    /// </summary>
    [Fact]
    public void 端到端_无baseline行_门禁显式未满足_不成批()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var index = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(
            Array.Empty<PiStageQuantityBaseline>(), out _);
        var demand = Demand(netQty: 40m);

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, Policy(max: 100m),
            piFacts: new[] { Fact() },
            piStageContext: PhaseTwoInitialScheduler.ResolvePiStageContext(
                index, demand.ProductionInstructionNo, demand.MaterialId, demand.StartStageCode));

        Assert.False(formation.IsLegal);
        Assert.Empty(formation.Batches);
        Assert.Contains("缺 Stage 合法自由量上下文", formation.ConflictReason);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGateBlocked);
    }

    /// <summary>
    /// 端到端**对照（未通电的今天）**：两个实参都传 `null` ⇒ 门禁**不可评估**、**不拦截**
    ///   ⇒ 批照常产出 + `Unevaluated` +1（与 `PiLegalQuantityGateTests` ⑭ 同口径）。
    ///   与上两例对照，说明「通电与否」的差别**只在实参**，代码路径是同一条。
    /// </summary>
    [Fact]
    public void 端到端_未通电_不拦截但登记缺口()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 40m), Policy(max: 100m), piFacts: null, piStageContext: null);

        Assert.True(formation.IsLegal, formation.ConflictReason);
        Assert.NotEmpty(formation.Batches);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGateUnevaluated);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);
    }
}
