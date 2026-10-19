using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 0号位 第十七轮 J17-04 的落库前资源身份判定（纯函数，不碰数据库）。
///
/// 钉死四件事：
///   1. 非正 `ResourceId` 一律非法（既不是 `Resource` 主数据、也不是人工槽合成键）。
///   2. `ResourceId == null` **不再一律合法** —— 只有 `UNCONSTRAINED`／`WAIT_ONLY` 非资源工序可以无资源
///      （0号位 2026-09-22 裁决；1号位 `PhaseOneConstraintBuilder:256-264`）。
///   3. 未提供非资源工序键集（null）时按**非法**处理 —— 保守方向，宁可少落库也不落 `ResourceId=NULL`。
///   4. 键是**完整工序身份**（Material + Stage + Route + Path + OperationCode），不是
///      `(MaterialId, OperationCode)` 短键 —— 0号位 第十八轮 `KEY_SCOPE_RISK`：`OperationCode` 不具全局唯一性，
///      短键会把非资源工序的放行资格错套到同物料同工序码、但不同 Path/Stage 的有限资源工序上。
/// </summary>
public class Round17PersistenceGuardTests
{
    private static FinalTaskDraft Task(
        int materialId, string opCode, int? resourceId,
        string stageCode = "S10", string? routeCode = "DEFAULT", long? pathId = 1) => new()
    {
        FinalDraftId = $"D{materialId}-{opCode}-{resourceId?.ToString() ?? "NULL"}",
        MaterialId = materialId,
        OperationCode = opCode,
        StageCode = stageCode,
        RouteCode = routeCode,
        PathId = pathId,
        ResourceId = resourceId
    };

    private static (int MaterialId, string StageCode, string RouteCode, long PathId, string OperationCode) Key(
        int materialId, string opCode,
        string stageCode = "S10", string routeCode = "DEFAULT", long pathId = 1)
        => (materialId, stageCode, routeCode, pathId, opCode);

    private static IReadOnlySet<(int MaterialId, string StageCode, string RouteCode, long PathId, string OperationCode)>
        NonResource(params (int MaterialId, string StageCode, string RouteCode, long PathId, string OperationCode)[] keys)
        => keys.ToHashSet();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void 非正资源Id一律非法(int resourceId)
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", resourceId), NonResource())
            .Should().BeTrue();
    }

    [Fact]
    public void 正资源Id合法_即便不在非资源工序集里()
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", 6444), NonResource())
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("UNCONSTRAINED")]
    [InlineData("WAIT_ONLY")]
    public void 非资源工序的空资源合法(string opCode)
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, opCode, null), NonResource(Key(100, opCode)))
            .Should().BeFalse();
    }

    [Fact]
    public void 有限资源工序的空资源非法()
    {
        // FINITE_RESOURCE 的工序不在非资源集合里 ⇒ 空资源落库等于「宣称已分配却没有资源」
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", null), NonResource(Key(100, "OP20")))
            .Should().BeTrue();
    }

    [Fact]
    public void 键按物料加区分_同工序码不同物料不串用()
    {
        // 物料 200 的 OP10 是非资源工序，不代表物料 100 的 OP10 也是
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", null), NonResource(Key(200, "OP10")))
            .Should().BeTrue();
    }

    [Fact]
    public void 未提供非资源工序集时空资源按非法处理()
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(Task(100, "UNCONSTRAINED", null), null)
            .Should().BeTrue("保守方向：不能证明是非资源工序就不放行空资源");
    }

    // ── 第十八轮 KEY_SCOPE_RISK：短键够不到的三种「同物料同工序码、身份不同」情形 ──

    [Fact]
    public void 同物料同工序码_不同Path_不得套用非资源放行()
    {
        // Path 1 的 OP10 是 UNCONSTRAINED；Path 2 的 OP10 是 FINITE_RESOURCE ⇒ Path 2 的空资源必须非法。
        // 短键 (MaterialId, OperationCode) 会把这两条混为一谈。
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", null, pathId: 2),
                NonResource(Key(100, "OP10", pathId: 1)))
            .Should().BeTrue("Path 不同的同名工序不得共用规划模式");
    }

    [Fact]
    public void 同物料同工序码_不同Stage_不得套用非资源放行()
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", null, stageCode: "S20"),
                NonResource(Key(100, "OP10", stageCode: "S10")))
            .Should().BeTrue("Stage 不同的同名工序不得共用规划模式");
    }

    [Fact]
    public void 同物料同工序码_不同Route_不得套用非资源放行()
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "OP10", null, routeCode: "ASSY"),
                NonResource(Key(100, "OP10", routeCode: "DEFAULT")))
            .Should().BeTrue("Route 不同的同名工序不得共用规划模式");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Task缺RouteCode时空资源按非法处理(string? routeCode)
    {
        // 身份不足 ⇒ 不能证明非资源；Fail Closed，**不反向猜 DEFAULT**。
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "UNCONSTRAINED", null, routeCode: routeCode),
                NonResource(Key(100, "UNCONSTRAINED")))
            .Should().BeTrue();
    }

    [Fact]
    public void Task缺PathId时空资源按非法处理()
    {
        PeggingOrchestrator.IsResourceIdentityIllegal(
                Task(100, "UNCONSTRAINED", null, pathId: null),
                NonResource(Key(100, "UNCONSTRAINED")))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("UNCONSTRAINED", true)]
    [InlineData("WAIT_ONLY", true)]
    [InlineData("FINITE_RESOURCE", false)]
    [InlineData("unconstrained", false)]   // 大小写敏感：与装载侧 Ordinal 比较一致
    [InlineData(null, false)]
    public void 非资源模式判定(string? mode, bool expected)
    {
        PeggingOrchestrator.IsNonResourcePlanningMode(mode).Should().Be(expected);
    }

    // ── J17-01：Routing 拒绝集（0号位 第十八轮要求「无合法降级依据则最小拦截」）──
    //
    //   上下文：首选 scope 无链 = 本批 StagePath **有**该物料的行，只是不在本需求的 BOMNO／角色下。
    //   它不属于 `NoRows`（本批根本没有该物料行）的「无事实兼容」情形，不能沿用「缺键即不过滤」；
    //   若该物料本次没有任何链 ⇒ 剔除其 Routing 三件套（不产 Task，按未排真相计入 U）。

    private static IReadOnlyDictionary<int, HashSet<string>> Chains(params (int MaterialId, string Stage)[] chains)
        => chains
            .GroupBy(c => c.MaterialId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Stage).ToHashSet());

    [Fact]
    public void 首选scope无链且该物料无任何链_并入拒绝集()
    {
        PeggingOrchestrator.ComputeRejectedMaterials(
                Array.Empty<int>(), Array.Empty<int>(), new[] { 300 }, Chains())
            .Should().Equal(300);
    }

    [Fact]
    public void 首选scope无链但该物料另有需求出链_不并入拒绝集()
    {
        // 同一物料既作子件（无链）又作顶层需求（有链）⇒ Task 是物料级、共用那条链，全拒会杀掉合法需求
        PeggingOrchestrator.ComputeRejectedMaterials(
                Array.Empty<int>(), Array.Empty<int>(), new[] { 300 }, Chains((300, "S10")))
            .Should().BeEmpty();
    }

    [Fact]
    public void 无StagePath行的物料不在拒绝集()
    {
        // `NoRows`（采购件/数据缺口）根本不会进入 missingPreferredScope 集合；此处钉死空集输入 ⇒ 空集输出
        PeggingOrchestrator.ComputeRejectedMaterials(
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Chains())
            .Should().BeEmpty();
    }

    [Fact]
    public void 角色冲突与身份歧义无条件拒绝_即便该物料有链()
    {
        PeggingOrchestrator.ComputeRejectedMaterials(
                new[] { 100 }, new[] { 200 }, Array.Empty<int>(), Chains((100, "S10"), (200, "S20")))
            .Should().BeEquivalentTo(new[] { 100, 200 });
    }

    [Fact]
    public void 拒绝集去重_多类来源同一物料只出现一次()
    {
        PeggingOrchestrator.ComputeRejectedMaterials(
                new[] { 100 }, new[] { 100 }, new[] { 100 }, Chains())
            .Should().Equal(100);
    }
}
