using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
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
/// 依据：06 集成接口 **v1.36 <c>:17</c>**（G2-T01 补正）／规则清单 **v1.7 <c>B-013</c>**／
/// 0号位 2026-10-13《未命名的Markdown文件3》<c>§2</c>＋《未命名的Markdown文件4》<c>§3.2／§3.3</c>／
/// 1号位 2026-10-14《BatchPolicy三态载体…回执》<c>§三</c>（<c>MatchSource</c> 9 值封闭值域）
/// ＋<c>§一.3</c>（装载期一致性护栏「乙」，粒度逐键）。
/// </summary>
public class BatchPolicyStateCarrierTests
{
    private static readonly DateTime RunAt = new(2026, 10, 13, 15, 0, 0, DateTimeKind.Local);

    /// <summary>合规规则行。<paramref name="preferred"/> 默认给合法值 —— **必须**，
    /// 否则装载期一致性护栏会把本键判成 <c>Unavailable</c>（见 ⑦ 组用例）。</summary>
    private static BatchPolicyRuleSnapshot Rule(
        int materialId,
        int? deptId,
        DateTime? from = null,
        DateTime? to = null,
        decimal? preferred = 100m)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = deptId,
            EffectiveFrom = from,
            EffectiveTo = to,
            PreferredBatchQty = preferred,
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
        s.MatchSource.Should().Be(BatchPolicyMatchSource.SnapshotHit,
            "Valid 的唯一合法来源值；1号位 C-3 将按本值域对 Valid 条目做 Fail Closed 校验");
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
        s.MatchSource.Should().Be(BatchPolicyMatchSource.OutOfWindow);
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
        s.MatchSource.Should().Be(BatchPolicyMatchSource.OtherDeptOnly,
            "06 v1.36 :17「本部门无命中而别的部门存在记录属于MISSING」且来源须可辨");
    }

    [Fact]
    public void BuildStates_本键与物料均无规则_判Missing()
    {
        var rules = new[] { Rule(999, 900) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Missing);
        s.MatchSource.Should().Be(BatchPolicyMatchSource.NoRuleForMaterial);
    }

    [Fact]
    public void BuildStates_八块整体为空_全部Missing且来源为完全缺配()
    {
        var states = TaskSplitRuleConfigProjector.BuildStates([], [(100, (int?)900), (200, (int?)901)], RunAt);

        states.Should().HaveCount(2);
        states.Should().OnlyContain(s => s.State == BatchPolicyState.Missing);
        states.Should().OnlyContain(s => s.MatchSource == BatchPolicyMatchSource.NoRuleForMaterial);
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
        s.MatchSource.Should().Be(BatchPolicyMatchSource.NoDept);
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
        states.Should().OnlyContain(s => s.MatchSource.StartsWith(BatchPolicyMatchSource.LoadError),
            "来源值须以值域常量锚定：明细可随行带出（只有调用方知道、从条目不可复原），但不得脱离值域");
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

    // ── ⑥ 装载期：生效窗口**随行下发** → BuildStates 按 Run 时点重判 ─────────────────────
    // （发布侧已退出⑧投影，装载改由 2号位 按 TaskSplitRuleConfig 表直读；此处直接构造快照行测装载判定。）

    [Fact]
    public void BuildStates_到Run时点已失效的行_判Invalid()
    {
        // 快照行携带生效窗口；装载期按 Run 时点重判 ⇒ Invalid。
        var publishAt = RunAt.AddDays(-5);
        var snapshot = new BatchPolicyRuleSnapshot
        {
            MaterialId = 100,
            ProductionDepartmentId = 900,
            EffectiveFrom = publishAt.AddDays(-1),
            EffectiveTo = publishAt.AddDays(1),
            PreferredBatchQty = 100m,   // 合规行：否则装载期一致性护栏会把本键判成 Unavailable，测不到 Invalid
        };

        var states = TaskSplitRuleConfigProjector.BuildStates([snapshot], [(100, (int?)900)], RunAt);
        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Invalid, "Run 时点已出窗口");
    }

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

    // ── ⑧ 装载期一致性护栏（1号位 2026-10-14 回执 §一.3 择定「乙」；粒度 = **逐键**）──────────
    //    版本内容里**本键**存在不合格行 ⇒ 本键判 Unavailable（消费侧 → Unknown → Fail Closed → 不取 Q_C）。
    //    护栏与发布闸共用同一份判据 IsSnapshotEligible ⇒ 结构上不可能「发布闸说合格、装载期说不合格」。

    [Fact]
    public void BuildStates_本键存在不合格行_判Unavailable而非Valid()
    {
        // Preferred 缺失 = 不合格。合规发布路径上发布闸已阻断，此处模拟 seed／直写版本**绕过发布闸**后的载荷
        // —— 那正是 1号位 回执 §一.3 甲 行承认的那条缝，也是本护栏存在的唯一理由。
        var rules = new[] { Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1), preferred: null) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        var s = Single(states, 100, 900);
        s.State.Should().Be(BatchPolicyState.Unavailable, "版本内容不合格 ⇒ 该键规则真相不可信 ⇒ Fail Closed");
        s.State.Should().NotBe(BatchPolicyState.Valid, "不合格行不得被当作有效经济策略消费");
        s.State.Should().NotBe(BatchPolicyState.Missing, "技术失败不得按业务无效（Missing）处置 —— 0号位 文件3 §2 所禁");
        s.MatchSource.Should().StartWith(BatchPolicyMatchSource.LoadError);
    }

    [Fact]
    public void BuildStates_Min大于Max_判Unavailable()
    {
        var rules = new[]
        {
            new BatchPolicyRuleSnapshot
            {
                MaterialId = 100,
                ProductionDepartmentId = 900,
                MinExecutionBatchQty = 50m,
                MaxExecutionBatchQty = 10m,   // Min > Max
                PreferredBatchQty = 20m,
            },
        };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Unavailable);
    }

    [Fact]
    public void BuildStates_护栏只打本键_他键不受牵连()
    {
        // 「逐键」是 1号位 回执 §一.3 的明文精化：**不**因版本里存在一条他键的不合格行，
        // 就把整域键全判 Unavailable（那会把无关键一并打成未排 = 过度阻断）。
        var rules = new[]
        {
            Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1), preferred: null),   // 不合格
            Rule(200, 901, RunAt.AddDays(-1), RunAt.AddDays(1)),                    // 合规
        };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900), (200, (int?)901)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Unavailable);
        Single(states, 200, 901).State.Should().Be(BatchPolicyState.Valid, "他键不得被不合格行牵连");
    }

    [Fact]
    public void BuildStates_同一键混合合格与不合格行_整键判Unavailable()
    {
        // 同键两行：只要有一行不合格，就无法判定哪一行才是权威 ⇒ 该键整体 Fail Closed，
        // **不得**挑「看起来合格的那条」当 Valid（那正是把不合格行当有效策略用）。
        var rules = new[]
        {
            Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1)),                    // 合规且在窗口内
            Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1), preferred: -1m),    // 不合格
        };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, [(100, (int?)900)], RunAt);

        Single(states, 100, 900).State.Should().Be(BatchPolicyState.Unavailable);
    }

    [Fact]
    public void IsSnapshotEligible_发布闸与装载护栏同判据()
    {
        // 判据本体原先只存在于 TaskSplitRuleConfigGovernanceService（发布闸）内；
        // 2026-10-14 提到 TaskSplitRuleConfigProjector 按三个数量参数表达，两条链共用一份。
        TaskSplitRuleConfigProjector.IsSnapshotEligible(10m, 100m, 50m).Should().BeTrue();
        TaskSplitRuleConfigProjector.IsSnapshotEligible(null, null, 50m).Should().BeTrue("Min/Max 可空，Preferred 必填");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(null, null, null).Should().BeFalse("Preferred 必填");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(null, null, 0m).Should().BeFalse("Preferred 须 > 0");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(-1m, null, 50m).Should().BeFalse("Min 非负");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(null, -1m, 50m).Should().BeFalse("Max 非负");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(50m, 10m, 20m).Should().BeFalse("Min ≤ Max");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(50m, 100m, 10m).Should().BeFalse("Preferred ≥ Min");
        TaskSplitRuleConfigProjector.IsSnapshotEligible(10m, 100m, 200m).Should().BeFalse("Preferred ≤ Max");
    }

    // ── ⑨ MatchSource **值域封闭**（1号位 回执 §三 定稿 9 值）────────────────────────

    [Fact]
    public void BuildStates_所有产出的来源值_均落在封闭值域内()
    {
        var rules = new[]
        {
            Rule(100, 900, RunAt.AddDays(-1), RunAt.AddDays(1)),    // Valid
            Rule(200, 901, RunAt.AddDays(-5), RunAt.AddDays(-1)),   // Invalid（出窗口）
            Rule(300, 902, RunAt.AddDays(-1), RunAt.AddDays(1)),    // 别的部门有行 → 本键 Missing
            Rule(400, null),                                        // NULL 部门行（不参与正式命中）
        };
        var keys = new List<(int, int?)> { (100, 900), (200, 901), (300, 903), (500, null), (600, 904) };

        var states = TaskSplitRuleConfigProjector.BuildStates(rules, keys, RunAt);

        var domain = new[]
        {
            BatchPolicyMatchSource.SnapshotHit, BatchPolicyMatchSource.NoRuleForMaterial,
            BatchPolicyMatchSource.OtherDeptOnly, BatchPolicyMatchSource.Inactive,
            BatchPolicyMatchSource.NoDept, BatchPolicyMatchSource.OutOfWindow,
            BatchPolicyMatchSource.PreferredNull, BatchPolicyMatchSource.WindowConflict,
            BatchPolicyMatchSource.LoadError,
        };

        states.Should().HaveCount(keys.Count);
        states.Should().OnlyContain(s => domain.Contains(s.MatchSource),
            "MatchSource 是 B-013 要求的「真实匹配来源」，必须是**可判定的域值**而非自由文本 —— "
            + "否则 1号位 C-3 的值域校验无从落地");

        // 逐键来源可辨（与上表一一对应）
        Single(states, 100, 900).MatchSource.Should().Be(BatchPolicyMatchSource.SnapshotHit);
        Single(states, 200, 901).MatchSource.Should().Be(BatchPolicyMatchSource.OutOfWindow);
        Single(states, 300, 903).MatchSource.Should().Be(BatchPolicyMatchSource.OtherDeptOnly);
        Single(states, 500, null).MatchSource.Should().Be(BatchPolicyMatchSource.NoDept);
        Single(states, 600, 904).MatchSource.Should().Be(BatchPolicyMatchSource.NoRuleForMaterial);
    }
}
