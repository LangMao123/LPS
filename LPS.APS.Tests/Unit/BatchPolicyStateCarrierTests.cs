using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// ⑧块 **三态＋不可用最小载体** 单测（2号位，2026-10-13 落；1号位 明示「最小形态由 2号位 择定」）。
///
/// 本文件存在的**唯一理由**：把「载体凭什么判某个状态」钉死在纯函数上，使
///   · 1号位 `BatchPolicyMatchState.Missing` 分支（**已落码但今日不可达** —— 其注释逐字
///     「<c>Missing</c> 分支保留（**载体到位后由 2号位 可信状态激活**）—— 今日不可达」）**有可信来源**；
///   · `Invalid` 从「结构性不可达」变成**可判**（唯一成因 = 发布时有效、Run 时已失效）；
///   · `Unknown`（0号位《未命名的Markdown文件4.md》§3.3）不被误升级为 `Missing`。
///
/// 依据：06 集成接口 v1.37 <c>:25</c>（G2-T01 补正）／规则清单 v1.8 <c>B-013</c>／
/// 0号位 2026-10-13《未命名的Markdown文件3》<c>§2</c>＋《未命名的Markdown文件4》<c>§3.2／§3.3</c>。
/// </summary>
public class BatchPolicyStateCarrierTests
{
    private static readonly DateTime RunAt = new(2026, 10, 13, 15, 0, 0, DateTimeKind.Local);

    private static BatchPolicyRuleSnapshot Rule(
        int materialId,
        int? deptId,
        DateTime? from = null,
        DateTime? to = null)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = deptId,
            EffectiveFrom = from,
            EffectiveTo = to,
            AllowSplit = false,
            AllowMerge = false,
        };

    private static BatchPolicyStateSnapshot Single(
        IEnumerable<BatchPolicyStateSnapshot> states, int materialId, int? deptId)
        => states.Single(s => s.MaterialId == materialId && s.ProductionDepartmentId == deptId);

    // ── ① Valid：精确命中且在本 Run 生效时点的窗口内 ──────────────────────────────────

    [Fact]
    public void BuildStates_精确命中且在生效窗口内_判Valid()
    {
        var rules = new[] { Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1)) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Valid);
        s.MatchSource.Should().Contain("精确命中");
    }

    [Fact]
    public void BuildStates_旧快照无窗口字段_判Valid_向后兼容()
    {
        // PSV 807 这类**旧快照**没有 EffectiveFrom/EffectiveTo（2026-10-13 才新增）⇒ 反序列化后恒 null
        // ⇒ 无界 ⇒ 不得因此把有效规则判成 Invalid。本断言钉死「新增字段不改变旧快照语义」。
        var rules = new[] { Rule(100, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Valid);
    }

    // ── ② Invalid：本键有规则但不在窗口内 —— 本链上 INVALID 的**唯一可达成因** ──────────

    [Fact]
    public void BuildStates_本键有规则但Run时已失效_判Invalid_不得判Missing()
    {
        // 发布时（窗口内）有效、本 Run 生效时点已过期。发布侧 Project 已按发布时刻筛过，
        // 故这一行会**原样进入快照**；若装载期不按 Run 时点重判，它就会被当成有效规则下发。
        var rules = new[] { Rule(100, 900, RunAt.AddDays(-10), RunAt.AddDays(-1)) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Invalid,
            "本键有规则但不在生效窗口 ⇒ 06 §281「不得假装 MISSING 兜底，应暴露 Issue 等待治理」");
        s.State.Should().NotBe(BatchPolicyState.Missing);
        s.MatchSource.Should().Contain("生效窗口");
    }

    [Fact]
    public void BuildStates_本键有规则但尚未生效_判Invalid()
    {
        var rules = new[] { Rule(100, 900, RunAt.AddDays(1), null) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Invalid);
    }

    [Fact]
    public void BuildStates_半开区间相切_边界不重叠()
    {
        // 与 Project / FindOverlappingWindowIds / EnsureUniqueEffectiveWindowAsync 同口径：
        // [from, to) 半开 —— 窗口终点**等于** Run 时点 ⇒ 已失效（严格 <）。
        var rules = new[] { Rule(100, 900, RunAt.AddDays(-1), RunAt) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Invalid);
    }

    // ── ③ Missing：经完整核对确无匹配规则（两个成因来源可区分）────────────────────────

    [Fact]
    public void BuildStates_本部门无命中而别的部门有记录_判Missing且来源可辨()
    {
        var rules = new[] { Rule(100, 901) }; // 同物料，别的部门

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Contain("别的部门", "06 :25「本部门无命中而别的部门存在记录属于MISSING」且来源须可辨");
    }

    [Fact]
    public void BuildStates_本键与物料均无规则_判Missing()
    {
        var rules = new[] { Rule(999, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Contain("精确业务键无命中");
    }

    [Fact]
    public void BuildStates_八块整体为空_全部Missing且来源为完全缺配()
    {
        var states = TaskSplitRuleConfigProjector.BuildStates([], [(100, (int?)900), (200, (int?)901)], RunAt);

        states.Should().HaveCount(2);
        states.Should().OnlyContain(s => s.State == BatchPolicyState.Missing);
        states.Should().OnlyContain(s => s.MatchSource.Contains("完全缺配"));
    }

    [Fact]
    public void BuildStates_NULL部门规则_不参与正式命中()
    {
        // B-001 / v5.1.10 收口④ / AUD-1-004：NULL 部门行仅历史兼容，**不得**变成 Material 级默认。
        var rules = new[] { Rule(100, null) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Missing);
    }

    [Fact]
    public void BuildStates_业务键不完整_判Missing而非未传递()
    {
        // 域内需求物料本 Run 未解析到部门 ⇒ 显式出 (MaterialId, null) 键，避免消费侧
        // 把「键缺失」误读成「接口未传递状态」（后者属 Unknown / 技术失败）。
        var rules = new[] { Rule(100, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, null)], RunAt);

        var s = Single(states, 100, null);
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Contain("业务键不完整");
    }

    // ── ④ Unavailable（不可用／技术失败）：不得静默降级为空集合 ──────────────────────

    [Fact]
    public void BuildStates_不可用通道_全部Unavailable_不得判Missing()
    {
        var rules = new[] { Rule(100, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(
            rules, [(100, (int?)900), (200, (int?)901)], RunAt, unavailableSource: "规则数据装载失败：JSON 损坏");

        states.Should().HaveCount(2);
        states.Should().OnlyContain(s => s.State == BatchPolicyState.Unavailable);
        states.Should().NotContain(s => s.State == BatchPolicyState.Missing,
            "0号位 文件3 §2 逐字：不得直接视为 MISSING（否则 1号位 会误启 Q_C）");
        states.Should().OnlyContain(s => s.MatchSource.Contains("装载失败"));
    }

    // ── ⑤ 载体完整性：每键必有条目、状态取值封闭 ────────────────────────────────────

    [Fact]
    public void BuildStates_每个业务键都有条目_且不重复()
    {
        var rules = new[] { Rule(100, 900), Rule(200, 901, RunAt.AddDays(-5), RunAt.AddDays(-1)) };
        var keys = new List<(int, int?)> { (100, 900), (200, 901), (300, 902), (400, null) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, keys, RunAt);

        states.Should().HaveCount(keys.Count);
        foreach (var key in keys)
        {
            states.Should().ContainSingle(
                s => s.MaterialId == key.Item1 && s.ProductionDepartmentId == key.Item2);
        }
        states.Select(s => s.State).Should().OnlyContain(
            v => v == BatchPolicyState.Valid || v == BatchPolicyState.Missing
                 || v == BatchPolicyState.Invalid || v == BatchPolicyState.Unavailable);
    }

    [Fact]
    public void BuildStates_重复业务键_去重后每键一条()
    {
        var rules = new[] { Rule(100, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900), (100, (int?)900)], RunAt);

        states.Should().ContainSingle();
    }

    // ── ⑥ 发布侧：生效窗口**随行下发**（此前被 Project 丢弃）──────────────────────────

    [Fact]
    public void Project_携带生效窗口_不再丢弃()
    {
        var from = RunAt.AddDays(-1);
        var to = RunAt.AddDays(1);
        var entity = new TaskSplitRuleConfig
        {
            Id = 1,
            MaterialId = 100,
            ProductionDepartmentId = 900,
            IsActive = true,
            EffectiveFrom = from,
            EffectiveTo = to,
        };

        var projected = TaskSplitRuleConfigProjector.Project([entity], asOf: RunAt);

        var row = projected.Should().ContainSingle().Subject;
        row.EffectiveFrom.Should().Be(from);
        row.EffectiveTo.Should().Be(to);
    }

    [Fact]
    public void Project_到Run时点已失效的行_仍在快照内_由装载期判Invalid()
    {
        // 发布时刻在窗口内 ⇒ Project 收入快照；装载期按 Run 时点重判 ⇒ Invalid。
        // 若 Project 丢弃窗口，这一行在装载期就**无从**被判 Invalid（正是此前载体缺口的成因）。
        var publishAt = RunAt.AddDays(-5);
        var entity = RuleEntityEffectiveAround(publishAt);

        var projected = TaskSplitRuleConfigProjector.Project([entity], asOf: publishAt);
        projected.Should().ContainSingle("发布时刻在窗口内");

        var states = TaskSplitRuleConfigProjector.BuildStates(projected, [(100, (int?)900)], RunAt);
        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Invalid, "Run 时点已出窗口");
    }

    /// <summary>生效窗口以 <paramref name="publishAt"/> 为中心的物理表行（发布时刻在窗口内、Run 时点已出窗口）。</summary>
    private static TaskSplitRuleConfig RuleEntityEffectiveAround(DateTime publishAt) => new()
    {
        Id = 1,
        MaterialId = 100,
        ProductionDepartmentId = 900,
        IsActive = true,
        EffectiveFrom = publishAt.AddDays(-1),
        EffectiveTo = publishAt.AddDays(1),
    };

    // ── ⑦ 与域裁剪的关系：载体键集 ⊆ 域内、参数行仍走 BatchPolicies ──────────────────

    [Fact]
    public void CropToDomain与BuildStates_同源物料_裁剪只减参数行不丢状态()
    {
        var rules = new[] { Rule(100, 900), Rule(999, 901) };

        var cropped = TaskSplitRuleConfigProjector.CropToDomain(rules, [100]);
        cropped.Should().ContainSingle().Which.MaterialId.Should().Be(100);

        // 域外物料 999 的规则被裁掉 —— 但若域内真有 999 的需求键，载体仍须给出状态（Missing）。
        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900), (999, (int?)901)], RunAt);
        states.Should().HaveCount(2);
        Single(states, 999, 901).State.Should().Be(BatchPolicyState.Valid);
    }
}
