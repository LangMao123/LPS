using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// T-04 契约①（硬锚点装载）单元测试 —— 2号位 供给端。
///
/// 【为什么必须单测】改造前 `DomainSolveRequest.ExecutionConstraints` **恒为空**
///   （`PeggingOrchestrator` 里硬写 `Array.Empty&lt;ExecutionConstraint&gt;()`），**供给端零用例**。
///   接上后若投影错了，不会编译报错，而是：
///     · `DraftId` 挂错需求 ⇒ 1号位 `PhaseTwoInitialScheduler` 查不到 demand ⇒ **TechnicalFailure**（整解失败）；
///     · `DraftId` 挂成旧 `TaskNo` ⇒ 跨 Run 恒不命中 `FinalDraftId` ⇒ 锚点**静默失效**（比报错更坏）；
///     · `(DraftId, Stage/Op)` 撞键 ⇒ 1号位 `PhaseOneConstraintBuilder.BuildLockedTasks` 抛
///       `SolverInputContractException` ⇒ Fail Closed（整解不可用）。
///   ⇒ 本组覆盖 连接/歧义/撞键/不完整/量映射 五类判据，全部为**纯函数**用例（无 IO、无 DB）。
///
/// `PM回复1010.md` §T-04 逐字要求：「用**隔离 fixture 验证消费链**；**没有真实ACTIVE样本也不应阻止开发和单测**。」
/// 本组即该要求的「隔离 fixture」部分（消费链一侧由既有 `PhaseFourAnchorProtectionTests` 等覆盖）。
/// </summary>
public class ExecutionConstraintProjectorTests
{
    // ─────────────────────────── helpers ───────────────────────────

    private static LogicalProductionDemand Demand(
        string key, long? orderId, int materialId, string stage, string? op)
        => new()
        {
            LogicalDemandKey   = key,
            OrderId            = orderId,
            MaterialId         = materialId,
            StartStageCode     = stage,
            StartOperationCode = op
        };

    private static BasePlanAnchorRow Row(
        long taskId, long? orderId, int materialId, string? stage, string? op,
        int? resourceId = 7, DateTime? start = null, DateTime? end = null,
        decimal qty = 100m, decimal? processQty = null, string? taskNo = "T-OLD",
        string constraintType = ExecutionConstraintTypes.Frozen)
        => new()
        {
            TaskId            = taskId,
            OrderId           = orderId,
            MaterialId        = materialId,
            StageCode         = stage,
            OperationCode     = op,
            ResourceId        = resourceId,
            PlannedStartTime  = start ?? new DateTime(2026, 10, 13, 8, 0, 0),
            PlannedEndTime    = end   ?? new DateTime(2026, 10, 13, 10, 0, 0),
            Quantity          = qty,
            PlannedProcessQty = processQty,
            TaskNo            = taskNo,
            ConstraintType    = constraintType
        };

    // ─────────────────────────── 核心：跨版本 DraftId 重算 ───────────────────────────

    [Fact]
    public void 命中唯一需求时_DraftId_取本次Run的LogicalDemandKey而非旧TaskNo()
    {
        // 需求 A/B 桶（带 /WO: 后缀）与自由桶（/FREE）——DraftId 必须原样取本次的键。
        var demands = new[]
        {
            Demand("ORD-1|MAT-9|CN_MACH|OP10/WO:WO123", 1001, 9, "CN_MACH", "OP10"),
            Demand("ORD-1|MAT-9|CN_MACH|OP20/FREE",     1001, 9, "CN_MACH", "OP20")
        };
        var rows = new[]
        {
            Row(11, 1001, 9, "CN_MACH", "OP10"),
            Row(12, 1001, 9, "CN_MACH", "OP20")
        };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().HaveCount(2);
        p.Constraints.Select(c => c.DraftId).Should().BeEquivalentTo(
            new[] { "ORD-1|MAT-9|CN_MACH|OP10/WO:WO123", "ORD-1|MAT-9|CN_MACH|OP20/FREE" },
            o => o.WithoutStrictOrdering());
        // 关键反例：绝不能把旧 TaskNo 当 DraftId。
        p.Constraints.Select(c => c.DraftId).Should().NotContain("T-OLD");
        p.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void 其余字段按基座行直接映射()
    {
        var start = new DateTime(2026, 10, 13, 8, 30, 0);
        var end   = new DateTime(2026, 10, 13, 11, 45, 0);
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10",
                                  resourceId: 42, start: start, end: end,
                                  qty: 250m, processQty: 275m) };

        var c = ExecutionConstraintProjector.Project(demands, rows).Constraints.Single();

        c.ResourceId.Should().Be(42);
        c.LockedStart.Should().Be(start);
        c.LockedEnd.Should().Be(end);
        c.StageCode.Should().Be("CN_MACH");
        c.OperationCode.Should().Be("OP10");
        c.LockedQuantity.Should().Be(250m);
        c.LockedNetOutputQty.Should().Be(250m);
        c.LockedPlannedProcessQty.Should().Be(275m);
        c.ConstraintType.Should().Be(ExecutionConstraintTypes.Frozen);
    }

    [Fact]
    public void TaskKey恒为null_因为跨Run不可能命中FinalDraftId()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10", taskNo: "T-OLD-999") };

        var c = ExecutionConstraintProjector.Project(demands, rows).Constraints.Single();

        c.TaskKey.Should().BeNull();
    }

    [Fact]
    public void PlannedProcessQty为空时退化为Quantity()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10", qty: 120m, processQty: null) };

        var c = ExecutionConstraintProjector.Project(demands, rows).Constraints.Single();

        c.LockedPlannedProcessQty.Should().Be(120m);
    }

    // ─────────────────────────── 连接判定 ───────────────────────────

    [Fact]
    public void 连接是严格等值_前缀相同不得误命中()
    {
        // 需求 OP1，基座行 OP10 —— 严格等值 ⇒ 不命中（若误用子集匹配就会挂错需求）。
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP1") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10") };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.UnmatchedCount.Should().Be(1);
    }

    [Fact]
    public void 命中0条需求_记为UNMATCHED不产锚点()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1002, 9, "CN_MACH", "OP10") };   // OrderId 不同

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.UnmatchedCount.Should().Be(1);
        p.Diagnostics.Single().Reason.Should().Be("UNMATCHED");
    }

    [Fact]
    public void 空需求集_全部记为UNMATCHED()
    {
        var rows = new[] { Row(11, 1001, 9, "CN_MACH", "OP10") };

        var p = ExecutionConstraintProjector.Project(Array.Empty<LogicalProductionDemand>(), rows);

        p.Constraints.Should().BeEmpty();
        p.UnmatchedCount.Should().Be(1);
    }

    [Fact]
    public void 命中多条需求_歧义_不猜不产锚点()
    {
        // 同 (OrderId, MaterialId, Stage, Op) 出现两条需求（理论上不该有）⇒ 禁止猜。
        var demands = new[]
        {
            Demand("K1", 1001, 9, "CN_MACH", "OP10"),
            Demand("K2", 1001, 9, "CN_MACH", "OP10")
        };
        var rows = new[] { Row(11, 1001, 9, "CN_MACH", "OP10") };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.AmbiguousCount.Should().Be(1);
        p.Diagnostics.Single().Reason.Should().Be("AMBIGUOUS");
    }

    // ─────────────────────────── 不完整行 ───────────────────────────

    [Fact]
    public void StageCode为空_不产锚点()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1001, 9, null, "OP10") };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.IncompleteRowCount.Should().Be(1);
        p.Diagnostics.Single().Reason.Should().Be("INCOMPLETE");
    }

    [Fact]
    public void ResourceId为空_不产锚点()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10", resourceId: null) };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.IncompleteRowCount.Should().Be(1);
    }

    [Fact]
    public void 计划时间为空_不产锚点()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        // 显式造「有 Start、无 End」的行（helper 的默认值会补 End，此处不能走 helper）。
        var rows = new[]
        {
            new BasePlanAnchorRow
            {
                TaskId           = 11,
                OrderId          = 1001,
                MaterialId       = 9,
                StageCode        = "CN_MACH",
                OperationCode    = "OP10",
                ResourceId       = 7,
                PlannedStartTime = new DateTime(2026, 10, 13, 8, 0, 0),
                PlannedEndTime   = null,
                Quantity         = 100m
            }
        };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().BeEmpty();
        p.IncompleteRowCount.Should().Be(1);
    }

    // ─────────────────────────── 撞键（1号位 Fail Closed 诱因）───────────────────────────

    [Fact]
    public void 同DraftId加StageOp撞键_保留首条并记DUPLICATE_KEY()
    {
        var demands = new[] { Demand("K1", 1001, 9, "CN_MACH", "OP10") };
        var rows = new[]
        {
            Row(11, 1001, 9, "CN_MACH", "OP10", resourceId: 7),
            Row(12, 1001, 9, "CN_MACH", "OP10", resourceId: 8)
        };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().HaveCount(1);
        p.Constraints.Single().ResourceId.Should().Be(7);   // 首条
        p.DuplicateKeyCount.Should().Be(1);
        p.Diagnostics.Single().Reason.Should().Be("DUPLICATE_KEY");
    }

    [Fact]
    public void 复合键是DraftId加Stage加Op_不同Stage不撞键()
    {
        var demands = new[]
        {
            Demand("K1", 1001, 9, "CN_MACH", "OP10"),
            Demand("K1b", 1001, 9, "CN_SURF", "OP10")
        };
        var rows = new[]
        {
            Row(11, 1001, 9, "CN_MACH", "OP10"),
            Row(12, 1001, 9, "CN_SURF", "OP10")
        };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().HaveCount(2);
        p.DuplicateKeyCount.Should().Be(0);
    }

    // ─────────────────────────── 归一化 ───────────────────────────

    [Fact]
    public void 两侧前后空白被裁剪后仍能命中()
    {
        var demands = new[] { Demand("K1", 1001, 9, " CN_MACH ", " OP10 ") };
        var rows    = new[] { Row(11, 1001, 9, "CN_MACH", "OP10") };

        var p = ExecutionConstraintProjector.Project(demands, rows);

        p.Constraints.Should().HaveCount(1);
        p.Constraints.Single().StageCode.Should().Be("CN_MACH");
    }
}

/// <summary>
/// V1 硬锚点资格判定（<see cref="V1AnchorQualifier"/>）单元测试。
///
/// **口径 = Frozen 窗口 ∪ 执行事实**（`PM回复0908` §六/§十八、建议稿 §7.2/§14/§15/§20），
/// **Firm 不在其中**（`PM回复0923-1` §三.2「Firm 不是锁」）。
/// 本组最关键的一条是 <see cref="Firm窗或可移动的纯计划_不产锚点"/> ——
/// 它把「Firm 走契约②、Frozen 走契约①」这条边界钉成可执行断言。
/// </summary>
public class V1AnchorQualifierTests
{
    [Fact]
    public void MES工单号非空_判为已下发锚点()
    {
        var ok = V1AnchorQualifier.IsHardAnchor(
            "WO-MES-9988", "PLANNED", FenceBand.Movable, out var type, out var reason);

        ok.Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.DispatchedImmovable);
        reason.Should().Be(OrderProtectionReason.MES_DISPATCHED);
    }

    [Fact]
    public void MES工单号为空白_等同未下发()
    {
        V1AnchorQualifier.IsHardAnchor("   ", "PLANNED", FenceBand.Movable, out _, out _).Should().BeFalse();
        V1AnchorQualifier.IsHardAnchor("", "PLANNED", FenceBand.Movable, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("COMPLETED")]
    public void 已开工或已完成_判为已执行锚点(string status)
    {
        var ok = V1AnchorQualifier.IsHardAnchor(null, status, FenceBand.Movable, out var type, out var reason);

        ok.Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.Executed);
        reason.Should().Be(OrderProtectionReason.IN_EXECUTION);
    }

    [Fact]
    public void 已下达RELEASED_判为已下发锚点()
    {
        var ok = V1AnchorQualifier.IsHardAnchor(null, "RELEASED", FenceBand.Movable, out var type, out _);

        ok.Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.DispatchedImmovable);
    }

    [Fact]
    public void 状态值不区分大小写且忽略首尾空白()
    {
        V1AnchorQualifier.IsHardAnchor(null, " in_progress ", FenceBand.Movable, out var type, out _).Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.Executed);
    }

    [Fact]
    public void 既已开工又有MES工单_种类取已执行()
    {
        var ok = V1AnchorQualifier.IsHardAnchor(
            "WO-MES-9988", "IN_PROGRESS", FenceBand.Movable, out var type, out var reason);

        ok.Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.Executed);
        // 执行事实**覆盖**窗口判定（§20 Execution > Frozen > Firm > Movable）。
        reason.Should().Be(OrderProtectionReason.IN_EXECUTION);
    }

    /// <summary>
    /// 🔴 2026-10-13 二次更正的核心断言：**Frozen 窗口内的纯计划（未下发、未开工）必须产锚点。**
    ///
    /// 依据 `PM回复0908.md` §六：T101 9/9 开始、基准 9/7 ⇒「距离排程基准2天，因此进入 Frozen」——
    /// 该例里 T101 **既未下发也未开工**，纯由**时间窗**判成 Frozen；同件 §十八「Frozen → 固定Task物理位置」。
    /// 建议稿 §15 亦要求「必须先建立 Execution Anchors + **Frozen Anchors** 作为**硬约束**」。
    /// </summary>
    [Fact]
    public void Frozen窗内的纯计划_产Frozen锚点()
    {
        var ok = V1AnchorQualifier.IsHardAnchor(
            null, "PLANNED", FenceBand.Frozen, out var type, out var reason);

        ok.Should().BeTrue();
        type.Should().Be(ExecutionConstraintTypes.Frozen);
        reason.Should().Be(OrderProtectionReason.FROZEN_WINDOW);
    }

    /// <summary>
    /// 🔴 本组最关键的一条：**Firm 窗内 / 可移动的纯计划不产契约① 锚点。**
    ///
    /// `PM回复0923-1.md`（2026-09-23，更晚且专答此问的统一裁决回复）§三.2 逐字
    /// 「**Firm 不是锁。Firm 不是不可移动。**」⇒ Firm 走**契约②**（`OrderProtection`），
    /// 不把"业务判断提前固化"（同件 §八）。
    /// </summary>
    [Theory]
    [InlineData(FenceBand.Firm)]
    [InlineData(FenceBand.Movable)]
    public void Firm窗或可移动的纯计划_不产锚点(FenceBand band)
    {
        V1AnchorQualifier.IsHardAnchor(null, "PLANNED", band, out _, out _).Should().BeFalse();
        V1AnchorQualifier.IsHardAnchor(null, null, band, out _, out _).Should().BeFalse();
        V1AnchorQualifier.IsHardAnchor("", "PLANNED", band, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void 已取消CANCELLED_不产锚点()
    {
        V1AnchorQualifier.IsHardAnchor(null, "CANCELLED", FenceBand.Frozen, out _, out _).Should().BeFalse();
    }
}

/// <summary>
/// Fence 时间带判定（<see cref="V1FenceCalculator"/>）单元测试。
///
/// **算例直接取自文档**：
///   · `PM回复0908.md` §六：基准 9/7，FrozenDays=3 ⇒ T101(9/9) Frozen、T102(9/12) 尚未 Frozen；
///   · 建议稿 §7.2：基准 `2026-09-08 02:00`、FrozenDays=3、FirmDays=2
///     ⇒ Frozen `[09/08 02:00, 09/11 02:00)`、Firm `[09/11 02:00, 09/13 02:00)`、Movable `>= 09/13 02:00`。
/// </summary>
public class V1FenceCalculatorTests
{
    private static readonly DateTime Basis = new(2026, 9, 8, 2, 0, 0);

    [Theory]
    // 建议稿 §7.2 的三带边界（左闭右开）
    [InlineData(0, FenceBand.Frozen)]      // 恰在基准
    [InlineData(2, FenceBand.Frozen)]      // 09/10 02:00
    [InlineData(3, FenceBand.Firm)]        // 恰在 FrozenEnd —— 归 Firm（左闭）
    [InlineData(4, FenceBand.Firm)]        // 09/12 02:00
    [InlineData(5, FenceBand.Movable)]     // 恰在 FirmEnd —— 归 Movable（左闭）
    [InlineData(10, FenceBand.Movable)]
    public void 三带边界_左闭右开(int daysFromBasis, FenceBand expected)
        => V1FenceCalculator.Classify(Basis.AddDays(daysFromBasis), Basis, 3, 2)
            .Should().Be(expected);

    [Fact]
    public void PM0908算例_基准9月7日_T101九日九日Frozen_T102九日十二日未Frozen()
    {
        var basis = new DateTime(2026, 9, 7, 0, 0, 0);

        V1FenceCalculator.Classify(new DateTime(2026, 9, 9), basis, 3, 2)
            .Should().Be(FenceBand.Frozen, "PM回复0908 §六：距离排程基准2天，因此进入Frozen");

        V1FenceCalculator.Classify(new DateTime(2026, 9, 12), basis, 3, 2)
            .Should().Be(FenceBand.Movable, "PM回复0908 §六：T102 9/12 尚未进入Frozen");
    }

    [Fact]
    public void 基准之前_取Frozen_文档未覆盖处的2号位判断()
    {
        // §7.2 把 PlannedStartTime >= FenceBasisTime 写作前提，未定义基准之前；
        // 2号位 取 Frozen（已到期/越过基准仍在上一 ACTIVE ⇒ 不可随手搬走）。判据集中在此，改口径只改这里。
        V1FenceCalculator.Classify(Basis.AddHours(-1), Basis, 3, 2).Should().Be(FenceBand.Frozen);
        V1FenceCalculator.Classify(Basis.AddDays(-30), Basis, 3, 2).Should().Be(FenceBand.Frozen);
    }

    [Fact]
    public void 无窗口参数_一律Movable_不自造天数()
    {
        V1FenceCalculator.Classify(Basis, null).Should().Be(FenceBand.Movable);
        V1FenceCalculator.Classify(Basis.AddDays(-30), null).Should().Be(FenceBand.Movable);
    }

    [Fact]
    public void FirmEnd从FrozenEnd起算_不是从基准起算()
    {
        var p = new FenceParameters(Basis, 3, 2);

        p.FrozenEnd.Should().Be(Basis.AddDays(3));
        p.FirmEnd.Should().Be(Basis.AddDays(5), "§7.2：FirmEnd = FrozenEnd + FirmDays（连续不重叠）");
    }
}

/// <summary>
/// FenceConfig 作用域解析（<see cref="FenceConfigResolver"/>）单元测试。
///
/// 重点覆盖**不猜**：无生效行 / 无作用域行 / 同作用域参数分裂（ProcessType 维度 2号位 无映射）⇒ 一律 null + 明确状态。
/// 以及 **`EffectiveTo` 为 NULL 的显式处理**（`EffectiveTo >= x` 遇 NULL 得 UNKNOWN ⇒ 会把"永久生效"行整批静默滤掉）。
/// </summary>
public class FenceConfigResolverTests
{
    private static readonly DateTime Basis = new(2026, 9, 8, 2, 0, 0);

    private static FenceConfigRow Row(
        int id, int pf, int? factory, string processType, int frozenDays, int firmDays,
        string from = "2026-01-01", string? to = null)
        => new()
        {
            Id = id,
            ProductFamilyId = pf,
            FactoryId = factory,
            ProcessType = processType,
            FrozenDays = frozenDays,
            FirmDays = firmDays,
            EffectiveFrom = DateTime.Parse(from),
            EffectiveTo = to is null ? null : DateTime.Parse(to)
        };

    [Fact]
    public void 空表_报FENCE_CONFIG_EMPTY_不给默认天数()
    {
        var r = FenceConfigResolver.Resolve(Array.Empty<FenceConfigRow>(), Basis, 1, 2);

        r.Parameters.Should().BeNull("§7.1/§15：2号位 只读快照，不得自己重判天数");
        r.Status.Should().Be(FenceConfigResolver.StatusNoEffectiveRow);
    }

    [Fact]
    public void EffectiveTo为NULL_永久生效行不得被静默滤掉()
    {
        var rows = new[] { Row(1, 1, 2, "ASSEMBLY", 3, 2, to: null) };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, 2);

        r.Status.Should().Be(FenceConfigResolver.StatusOk);
        r.Parameters!.FrozenDays.Should().Be(3);
        r.Parameters.FirmDays.Should().Be(2);
        r.Parameters.BasisTime.Should().Be(Basis);
    }

    [Fact]
    public void 生效期已过_不算生效()
    {
        var rows = new[] { Row(1, 1, 2, "ASSEMBLY", 3, 2, from: "2026-01-01", to: "2026-08-31") };

        FenceConfigResolver.Resolve(rows, Basis, 1, 2).Status
            .Should().Be(FenceConfigResolver.StatusNoEffectiveRow);
    }

    [Fact]
    public void 工厂精确优先_压过通配行()
    {
        var rows = new[]
        {
            Row(1, 1, null, "ASSEMBLY", 9, 9),   // 通配
            Row(2, 1, 2, "ASSEMBLY", 3, 2)       // 精确
        };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, 2);

        r.Status.Should().Be(FenceConfigResolver.StatusOk);
        r.Parameters!.FrozenDays.Should().Be(3, "精确 (ProductFamilyId=1, FactoryId=2) 优先于通配");
    }

    [Fact]
    public void 无精确行_退通配行()
    {
        var rows = new[] { Row(1, 1, null, "ASSEMBLY", 4, 5) };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, 2);

        r.Status.Should().Be(FenceConfigResolver.StatusOk);
        r.Parameters!.FrozenDays.Should().Be(4);
    }

    [Fact]
    public void 产品族不匹配_报SCOPE_NOT_FOUND()
    {
        var rows = new[] { Row(1, 99, 2, "ASSEMBLY", 3, 2) };

        FenceConfigResolver.Resolve(rows, Basis, 1, 2).Status
            .Should().Be(FenceConfigResolver.StatusScopeNotFound);
    }

    /// <summary>
    /// 🔴 本组最关键的一条：**同作用域下 ProcessType 分裂且参数不一致 ⇒ 不猜。**
    ///
    /// 2号位 侧没有 stage→`FenceConfig.ProcessType` 值域的映射（实库 `RoutingOperation.ProcessType` 是中文，
    /// 对不上 `ASSEMBLY`/`MACHINING`/…）⇒ 带一个猜出来的 ProcessType 去查，比不查更糟。
    /// </summary>
    [Fact]
    public void 同作用域参数分裂_不猜_报AMBIGUOUS()
    {
        var rows = new[]
        {
            Row(1, 1, 2, "ASSEMBLY", 3, 2),
            Row(2, 1, 2, "MACHINING", 5, 4)
        };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, 2);

        r.Parameters.Should().BeNull();
        r.Status.Should().Be(FenceConfigResolver.StatusAmbiguous);
        r.Detail.Should().Contain("ASSEMBLY").And.Contain("MACHINING");
    }

    [Fact]
    public void 同作用域参数一致_不算歧义_取EffectiveFrom最新()
    {
        var rows = new[]
        {
            Row(1, 1, 2, "ASSEMBLY",  3, 2, from: "2026-01-01"),
            Row(2, 1, 2, "MACHINING", 3, 2, from: "2026-06-01")
        };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, 2);

        r.Status.Should().Be(FenceConfigResolver.StatusOk, "参数一致 ⇒ 无需按 ProcessType 消歧");
        r.Detail.Should().Contain("Id=2", "取 EffectiveFrom 最新的一行");
    }

    [Fact]
    public void 工厂未知_只认通配行()
    {
        var rows = new[]
        {
            Row(1, 1, 2, "ASSEMBLY", 3, 2),
            Row(2, 1, null, "ASSEMBLY", 7, 8)
        };

        var r = FenceConfigResolver.Resolve(rows, Basis, 1, factoryId: null);

        r.Status.Should().Be(FenceConfigResolver.StatusOk);
        r.Parameters!.FrozenDays.Should().Be(7);
    }
}
