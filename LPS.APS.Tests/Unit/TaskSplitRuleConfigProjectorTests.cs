using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// TaskSplitRuleConfigProjector 单元测试（Batch Policy 投影/编解码，0号位 2026-10-07 裁决本轮落码）。
/// 无状态纯函数，直接钉死：IsActive/生效区间筛除、去审计字段投影、ContentSnapshotJson 子块编解码 + fail-fast。
/// （注：本类注释原写 "fail-open"，与实现不符 —— ⑧块投影器自 §T-1014b 起已改为 **fail-fast**：JSON 损坏抛异常而非静默返回空。2026-10-14 订正。）
/// </summary>
public class TaskSplitRuleConfigProjectorTests
{
    private static TaskSplitRuleConfig Rule(
        int id = 1, int materialId = 100, int? deptId = null, bool isActive = true,
        DateTime? effectiveFrom = null, DateTime? effectiveTo = null,
        decimal? min = null, decimal? max = null, bool allowSplit = true)
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
            AllowSplit = allowSplit,
        };

    [Fact]
    public void ExtractRules_B010三态_缺失与空配置返回空列表_损坏抛红线异常()
    {
        // B-010 三态（3-NEW-P0-04，0号位 再审核 2026-10-10）：
        // ① 缺子块/null/空串 → 空列表（合法未配置，非装载失败）；② 显式空配置 "BatchPolicy":[] → 空列表（明确空配置）；
        // ③ JSON 结构损坏 → 显式失败（抛 SetupRuleDataRedLineException，不再静默空列表——「完全无匹配」≠「已有配置但损坏」）。
        // 冻结 §十五 fail-closed 属匹配终端 ④（1号位 消费侧 Strategy Snapshot 校验），装载端不承载信号位。
        TaskSplitRuleConfigProjector.ExtractRules(null).Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules(string.Empty).Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules("{\"Lock\":{}}").Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules("{\"BatchPolicy\":null}").Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules("{\"BatchPolicy\":[]}").Should().BeEmpty();
        Action act = () => TaskSplitRuleConfigProjector.ExtractRules("{{{bad json");
        act.Should().Throw<SetupRuleDataRedLineException>();
    }

    [Fact]
    public void ExtractRules_有效子块_返回规则列表()
    {
        TaskSplitRuleConfigProjector.ExtractRules("{\"BatchPolicy\":[{\"MaterialId\":100,\"AllowSplit\":true}]}")
            .Should().ContainSingle().Which.MaterialId.Should().Be(100);
    }

    // ---------- R02（0号位 再审核反证 2026-10-10）生效窗口重叠判定（纯函数，零 DB） ----------

    [Fact]
    public void FindOverlappingWindowIds_同键重叠窗口_列出双方Id()
    {
        // 同 (MaterialId, Dept) 生效窗口重叠（半开区间、NULL 无界）：[2026-01-01, 2026-12-31) 与 [2026-06-01, NULL)。
        var a = Rule(id: 1, materialId: 100, deptId: 10, effectiveFrom: new DateTime(2026, 1, 1), effectiveTo: new DateTime(2026, 12, 31));
        var b = Rule(id: 2, materialId: 100, deptId: 10, effectiveFrom: new DateTime(2026, 6, 1), effectiveTo: null);

        TaskSplitRuleConfigProjector.FindOverlappingWindowIds(new[] { a, b }).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void FindOverlappingWindowIds_相切不重叠_返回空()
    {
        // 半开区间 [10:00,11:00) 与 [11:00,12:00) 相切不重叠（严格 <，与写路径唯一有效期同口径）。
        var a = Rule(id: 1, materialId: 100, deptId: 10, effectiveFrom: new DateTime(2026, 1, 1, 10, 0, 0), effectiveTo: new DateTime(2026, 1, 1, 11, 0, 0));
        var b = Rule(id: 2, materialId: 100, deptId: 10, effectiveFrom: new DateTime(2026, 1, 1, 11, 0, 0), effectiveTo: new DateTime(2026, 1, 1, 12, 0, 0));

        TaskSplitRuleConfigProjector.FindOverlappingWindowIds(new[] { a, b }).Should().BeEmpty();
    }

    [Fact]
    public void FindOverlappingWindowIds_不同部门或物料_不视为重叠()
    {
        var a = Rule(id: 1, materialId: 100, deptId: 10, effectiveFrom: new DateTime(2026, 1, 1), effectiveTo: null);
        var b = Rule(id: 2, materialId: 100, deptId: 20, effectiveFrom: new DateTime(2026, 1, 1), effectiveTo: null);
        var c = Rule(id: 3, materialId: 200, deptId: 10, effectiveFrom: new DateTime(2026, 1, 1), effectiveTo: null);

        TaskSplitRuleConfigProjector.FindOverlappingWindowIds(new[] { a, b, c }).Should().BeEmpty();
    }

    // ── B-006 已确认异常 `1/1/1`（2026-10-14 落）────────────────────────────────────────────────
    // 规则清单 B-006（**生效**）逐字：「…**发布数据质量校验不等于Solver对生产批量经济区间偏离的硬拒排**；
    // 历史NULL物理兼容，**已确认异常`1/1/1`经核实后治理防重入**」。
    // 下面三条**必须一起看**才完整 —— 只钉前两条会诱导后人把它塞进发布闸：
    //   ① `1/1/1` 必须**可检出**（否则 B-006 的「经核实后治理」无从谈起）；
    //   ② 检出**不得误伤**同类（缺 Preferred 属另一类，全等但非 1 不属本类）；
    //   ③ `1/1/1` **不得**并入 IsSnapshotEligible（它是**经济区间偏离**、本身自洽；并入会让发布闸对
    //      全量存量行一次性 fail-closed，与「不等于硬拒排」直接冲突）。

    private static TaskSplitRuleConfig RuleWithQty(
        int id, decimal? min, decimal? max, decimal? preferred, int materialId = 100, int? deptId = 900)
        => new()
        {
            Id = id,
            MaterialId = materialId,
            ProductionDepartmentId = deptId,
            IsActive = true,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            PreferredBatchQty = preferred,
            AllowSplit = true,
            AllowMerge = true,
        };

    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(1, 1, null, false)]   // 缺 Preferred ⇒ 属 IsSnapshotEligible 的阻断类，不是本类
    [InlineData(1, 1, 2, false)]
    [InlineData(null, 1, 1, false)]
    [InlineData(1, 2, 1, false)]
    [InlineData(2, 2, 2, false)]      // 全等但非 1 ⇒ 不是 B-006 点名的那个组合
    public void IsConfirmedDegenerateOneOneOne_只认三者全显式为1(int? min, int? max, int? preferred, bool expected)
    {
        TaskSplitRuleConfigProjector
            .IsConfirmedDegenerateOneOneOne(min, max, preferred)
            .Should().Be(expected);
    }

    [Fact]
    public void FindConfirmedDegenerateOneOneOneIds_只报全1行_不误伤他行()
    {
        var rows = new[]
        {
            RuleWithQty(11, 1, 1, 1),      // 命中
            RuleWithQty(12, 10, 100, 50),  // 正常规则
            RuleWithQty(13, 1, 1, null),   // 缺 Preferred ⇒ 另一类（发布闸已阻断），不属本类
            RuleWithQty(14, 2, 2, 2),      // 全等但非 1
            RuleWithQty(15, 1, 1, 1),      // 命中
        };

        TaskSplitRuleConfigProjector.FindConfirmedDegenerateOneOneOneIds(rows)
            .Should().Equal(11, 15);
    }

    [Fact]
    public void IsSnapshotEligible_全1行_判合格_不得被并进阻断闸()
    {
        // 反证：1/1/1 本身自洽（Min ≤ Max、Preferred ∈ [Min, Max]）⇒ 在发布闸看来是"合格"的。
        // B-006 明令「发布数据质量校验不等于…硬拒排」⇒ 这个 true 是**正确行为**、不是缺陷。
        // 1/1/1 的可见性由 FindConfirmedDegenerateOneOneOneIds（发布告警）承担，**不**由本闸承担。
        TaskSplitRuleConfigProjector.IsSnapshotEligible(1, 1, 1).Should().BeTrue();
    }

    // ── 快照顶层键名漂移检测（2026-10-14 落）──────────────────────────────────────────────────────
    // 为什么需要它：8 块里 ①–⑥ 走强类型反序列化（键名不符 ⇒ 抛异常，fail-fast），
    // 而 ⑦块 SetupTransitionRules／⑧块 BatchPolicy 的投影器是**单点 TryGetProperty** ——
    // 键名拼错 ⇒ **静默返回空列表**，与「合法未配置」在运行时**不可区分**。8 块键名**一个都没冻结**。
    // 本检测器只负责把"出现了不该有的键"暴露成日志，**不阻断发布、不改快照**。

    [Fact]
    public void FindUnknownSnapshotBlockNames_已知8键_全不报()
    {
        // 逐字取自 FrozenStrategySnapshotProvider（①–⑥）＋ GovernanceVersionService（⑦⑧）。
        const string json = """
            {
              "DemandPriority": {}, "Lock": {}, "Supply": {}, "Procurement": {},
              "SolverStrategy": {}, "CandidateGuardrail": {},
              "SetupTransitionRules": [], "BatchPolicy": []
            }
            """;

        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames(json).Should().BeEmpty();
    }

    [Fact]
    public void FindUnknownSnapshotBlockNames_未知键与拼错键_报出且去重()
    {
        // 关键用例：`SetupTransitionRule`（少个 s）就是 ⑦块最典型的手滑 —— 它**不报错**、静默当未配置。
        const string json = """
            {
              "DemandPriority": {},
              "SetupTransitionRule": [],
              "Batchpolicy": [],
              "SetupTransitionRule": []
            }
            """;

        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames(json)
            .Should().BeEquivalentTo(new[] { "SetupTransitionRule", "Batchpolicy" });
    }

    [Fact]
    public void FindUnknownSnapshotBlockNames_空快照_不报不抛()
    {
        // 空/未配置的快照是**合法状态**（新版本尚未注入任何子块），不得被当成"键名漂移"。
        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames(null).Should().BeEmpty();
        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames("").Should().BeEmpty();
        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames("   ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]")]                       // 根是数组
    [InlineData("123")]                      // 根是标量
    [InlineData("null")]                     // 根是 null
    [InlineData("{ this is not json ")]      // 整体损坏
    public void FindUnknownSnapshotBlockNames_非对象根或损坏JSON_不抛_返回空(string json)
    {
        // 根不是对象 ⇒ 不是「8 块」形态，不承载"未知键"这个信号；
        // JSON 整体损坏 ⇒ 由各子块投影器按既有 fail-fast 语义处置（见 ExtractRules），
        // 本函数**不重复承载**该信号（否则同一损坏被报两次、第二次语义还不同）。
        TaskSplitRuleConfigProjector.FindUnknownSnapshotBlockNames(json).Should().BeEmpty();
    }
}