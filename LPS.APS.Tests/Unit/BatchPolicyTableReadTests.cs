using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// ⑧ 批量策略**装载侧表直读**（2026-10-17 切换）单元测试。
///
/// 钉死三件容易退化的性质：
///   1. <see cref="TaskSplitRuleConfigProjector.ProjectFromTableRows"/> **不按生效窗过滤** ——
///      窗口判定必须留给 <see cref="TaskSplitRuleConfigProjector.BuildStates"/> 在 `DomainBatchEvaluationTime` 上做，
///      装载层提前剔行会把本该判 `Invalid` 的键退化成 `Missing`（PM 2026-10-15 §T-1038 第 2 条）。
///   2. 过滤轴 = `MaterialId` 单轴：同物料**其它部门**的规则必须留在集合里，
///      它是 `Missing(OTHER_DEPT_ONLY)` 的唯一证据。
///   3. 装载技术失败 ⇒ 全部业务键 `Unavailable`，**不降级为 `MISSING`**。
/// </summary>
public class BatchPolicyTableReadTests
{
    private static readonly DateTime AsOf = new(2026, 10, 17, 10, 0, 0);

    private static TaskSplitRuleConfig Rule(
        int id = 1,
        int materialId = 100,
        int? deptId = 10,
        bool isActive = true,
        DateTime? effectiveFrom = null,
        DateTime? effectiveTo = null,
        decimal? min = 1m,
        decimal? max = 100m,
        decimal? preferred = 10m,
        bool allowSplit = true,
        bool allowMerge = false)
        => new()
        {
            Id = id,
            MaterialId = materialId,
            ProductionDepartmentId = deptId,
            IsActive = isActive,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            PreferredBatchQty = preferred,
            AllowSplit = allowSplit,
            AllowMerge = allowMerge,
            // 四列历史兼容值：即便表里有值，也**不得**投给 1号位
            MaxOptimizationSplitCount = 9,
            MaxBatchCandidates = 9,
            BottleneckSplitStrategy = "SHOULD_NOT_LEAK",
            NonBottleneckStrategy = "SHOULD_NOT_LEAK"
        };

    // ===================== ProjectFromTableRows =====================

    [Fact]
    public void ProjectFromTableRows_不按生效窗过滤_窗外行原样保留()
    {
        var rows = new[]
        {
            Rule(id: 1, effectiveFrom: AsOf.AddDays(-10), effectiveTo: AsOf.AddDays(-1)), // 已失效
            Rule(id: 2, effectiveFrom: AsOf.AddDays(1), effectiveTo: null),               // 未生效
            Rule(id: 3, effectiveFrom: AsOf.AddDays(-1), effectiveTo: AsOf.AddDays(1))    // 窗内
        };

        var rules = TaskSplitRuleConfigProjector.ProjectFromTableRows(rows);

        rules.Should().HaveCount(3);
        rules.Select(r => r.EffectiveFrom).Should().OnlyContain(d => d.HasValue);
        rules.Select(r => r.EffectiveTo).Should().ContainInOrder(
            AsOf.AddDays(-1), (DateTime?)null, AsOf.AddDays(1));
    }

    [Fact]
    public void ProjectFromTableRows_非活跃行与空部门行剔除()
    {
        var rows = new[]
        {
            Rule(id: 1, deptId: 10, isActive: true),
            Rule(id: 2, deptId: 10, isActive: false),  // 软删 = IsActive=0
            Rule(id: 3, deptId: null, isActive: true)  // NULL 部门行不默认为所有部门生效
        };

        var rules = TaskSplitRuleConfigProjector.ProjectFromTableRows(rows);

        rules.Should().ContainSingle();
        rules[0].ProductionDepartmentId.Should().Be(10);
    }

    [Fact]
    public void ProjectFromTableRows_业务字段原样投影_四列历史兼容值恒不投()
    {
        var rows = new[]
        {
            Rule(id: 1, materialId: 777, deptId: 55,
                effectiveFrom: AsOf.AddDays(-1), effectiveTo: AsOf.AddDays(1),
                min: 2m, max: 80m, preferred: 20m, allowSplit: false, allowMerge: true)
        };

        var r = TaskSplitRuleConfigProjector.ProjectFromTableRows(rows).Should().ContainSingle().Subject;

        r.MaterialId.Should().Be(777);
        r.ProductionDepartmentId.Should().Be(55);
        r.MinExecutionBatchQty.Should().Be(2m);
        r.MaxExecutionBatchQty.Should().Be(80m);
        r.PreferredBatchQty.Should().Be(20m);
        r.AllowSplit.Should().BeFalse();
        r.AllowMerge.Should().BeTrue();

        r.MaxOptimizationSplitCount.Should().BeNull("1号位 Solver 技术预算不投（v5.1.10 收口①）");
        r.MaxBatchCandidates.Should().BeNull("同上");
        r.BottleneckSplitStrategy.Should().BeNull("V1 主链不得消费拆/合批倾向（v5.1.10 收口②）");
        r.NonBottleneckStrategy.Should().BeNull("同上");
    }

    // ===================== 表行 → 三态（INVALID 与 MISSING 分离） =====================

    [Fact]
    public void BuildStates_表行_窗内判Valid_窗外判Invalid_不退化为Missing()
    {
        var inWindow = TaskSplitRuleConfigProjector.ProjectFromTableRows(new[]
        {
            Rule(id: 1, materialId: 100, deptId: 10,
                effectiveFrom: AsOf.AddDays(-1), effectiveTo: AsOf.AddDays(1))
        });
        var expired = TaskSplitRuleConfigProjector.ProjectFromTableRows(new[]
        {
            Rule(id: 2, materialId: 200, deptId: 10,
                effectiveFrom: AsOf.AddDays(-10), effectiveTo: AsOf.AddDays(-1))
        });

        var states = TaskSplitRuleConfigProjector.BuildStates(
            inWindow.Concat(expired).ToList(),
            new[] { (100, (int?)10), (200, (int?)10) },
            AsOf);

        var m100 = states.Single(s => s.MaterialId == 100);
        m100.State.Should().Be(BatchPolicyState.Valid);
        m100.MatchSource.Should().Be(BatchPolicyMatchSource.SnapshotHit);

        var m200 = states.Single(s => s.MaterialId == 200);
        m200.State.Should().Be(BatchPolicyState.Invalid, "有规则但不在生效窗内 ⇒ INVALID");
        m200.MatchSource.Should().Be(BatchPolicyMatchSource.OutOfWindow);
    }

    [Fact]
    public void BuildStates_表行_本部门无命中而他部门有记录_判Missing且来源可区分()
    {
        var rules = TaskSplitRuleConfigProjector.ProjectFromTableRows(new[]
        {
            Rule(id: 1, materialId: 100, deptId: 20,
                effectiveFrom: AsOf.AddDays(-1), effectiveTo: AsOf.AddDays(1))
        });

        var states = TaskSplitRuleConfigProjector.BuildStates(
            rules, new[] { (100, (int?)10) }, AsOf);

        var s = states.Should().ContainSingle().Subject;
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Be(BatchPolicyMatchSource.OtherDeptOnly);
    }

    [Fact]
    public void BuildStates_表行_物料无任何规则_判Missing来源NoRuleForMaterial()
    {
        var states = TaskSplitRuleConfigProjector.BuildStates(
            Array.Empty<BatchPolicyRuleSnapshot>(), new[] { (999, (int?)10) }, AsOf);

        var s = states.Should().ContainSingle().Subject;
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Be(BatchPolicyMatchSource.NoRuleForMaterial);
    }

    [Fact]
    public void BuildStates_装载技术失败_全部业务键判Unavailable不得降级为Missing()
    {
        var states = TaskSplitRuleConfigProjector.BuildStates(
            Array.Empty<BatchPolicyRuleSnapshot>(),
            new[] { (100, (int?)10), (200, (int?)null) },
            AsOf,
            unavailableSource: "表直读失败：连接超时");

        states.Should().HaveCount(2);
        states.Should().OnlyContain(s => s.State == BatchPolicyState.Unavailable);
        states.Should().OnlyContain(s => s.MatchSource.StartsWith(BatchPolicyMatchSource.LoadError));
        states.Should().OnlyContain(s => s.MatchSource.Contains("连接超时"));
    }

    // ===================== 裁剪轴 =====================

    [Fact]
    public void CropToDomain_MaterialId单轴_保留同物料其它部门变体()
    {
        var rules = TaskSplitRuleConfigProjector.ProjectFromTableRows(new[]
        {
            Rule(id: 1, materialId: 100, deptId: 10),
            Rule(id: 2, materialId: 100, deptId: 20),
            Rule(id: 3, materialId: 300, deptId: 10)
        });

        var cropped = TaskSplitRuleConfigProjector.CropToDomain(rules, new[] { 100 });

        cropped.Should().HaveCount(2);
        cropped.Select(r => r.ProductionDepartmentId).Should().BeEquivalentTo(new int?[] { 10, 20 });
    }
}
