using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// P1-02 item1（Setup换型 v1.2 收口版）骨架回归测试（纯内存）。
/// 对齐《冻结文档修改指导 v1.2》§二十二验收场景 + 0号位 20260917 裁决（Q1 生产日窗口 / Q4 初始状态与规则缺失分开）：
///   S01/S04 EXACT 命中 + 方向性；S03 同设备不同当前工序分别命中；
///   S05 DEFAULT 回退；S06 无规则=0+缺失解释；S08 同产品默认0/显式覆盖；
///   S18 初始设备状态=0（与规则缺失严格分开）；Q1 生产日窗口切分。
/// 说明：S02（上一 Task 工序不参与键）由签名保证——ResolveSetup 不存在 fromOperation 参数，
/// 规则命中只取决于当前 Task 自身 OperationCode。
/// </summary>
public class SetupTransitionRuleTests
{
    private readonly SetupOptimizer _optimizer = new();

    private static Dictionary<SetupOptimizer.SetupExactKey, decimal> Exact(params (string Op, int Res, int From, int To, decimal Min)[] rules)
        => rules.ToDictionary(r => new SetupOptimizer.SetupExactKey(r.Op, r.Res, r.From, r.To), r => r.Min);

    private static Dictionary<SetupOptimizer.SetupDefaultKey, decimal> Default(params (string Op, int Res, decimal Min)[] rules)
        => rules.ToDictionary(r => new SetupOptimizer.SetupDefaultKey(r.Op, r.Res), r => r.Min);

    // ── S01/S04：EXACT 命中 + 方向性 ──

    [Fact]
    public void S01_S04_Exact命中且方向不同值()
    {
        var exact = Exact(("OP20", 1, 100, 200, 20m), ("OP20", 1, 200, 100, 60m));
        var def = Default();

        var ab = _optimizer.ResolveSetup("OP20", 1, 100, 200, exact, def);
        var ba = _optimizer.ResolveSetup("OP20", 1, 200, 100, exact, def);

        Assert.Equal(20m, ab.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.ExactHit, ab.Outcome);
        Assert.Equal(60m, ba.SetupMinutes);   // A→B 与 B→A 必须不同（方向性）
        Assert.NotEqual(ab.SetupMinutes, ba.SetupMinutes);
    }

    // ── S03：同设备不同当前工序分别命中 ──

    [Fact]
    public void S03_同设备不同当前工序_分别命中()
    {
        var exact = Exact(("OP10", 1, 100, 200, 15m), ("OP20", 1, 100, 200, 40m));
        var def = Default();

        Assert.Equal(15m, _optimizer.ResolveSetup("OP10", 1, 100, 200, exact, def).SetupMinutes);
        Assert.Equal(40m, _optimizer.ResolveSetup("OP20", 1, 100, 200, exact, def).SetupMinutes);
    }

    // ── S05：DEFAULT 回退 ──

    [Fact]
    public void S05_无明确产品对_命中默认规则()
    {
        var exact = Exact();
        var def = Default(("OP20", 1, 30m));

        var r = _optimizer.ResolveSetup("OP20", 1, 100, 200, exact, def);

        Assert.Equal(30m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.DefaultHit, r.Outcome);
        // 0号位 正式回复 §5.2：DEFAULT 回退属正常 fallback，写 INFO 轻量追踪。
        Assert.Equal("INFO", r.TraceLevel);
        Assert.Contains("默认换型时间", r.TraceMessage);
    }

    [Fact]
    public void EXACT优先于DEFAULT()
    {
        var exact = Exact(("OP20", 1, 100, 200, 45m));
        var def = Default(("OP20", 1, 30m));

        var r = _optimizer.ResolveSetup("OP20", 1, 100, 200, exact, def);

        Assert.Equal(45m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.ExactHit, r.Outcome);
    }

    // ── S06：无规则 = 0 + 缺失解释（规则缺失降级，非初始状态） ──

    [Fact]
    public void S06_明确与默认均无_零分钟并记缺失解释()
    {
        var r = _optimizer.ResolveSetup("OP20", 1, 100, 200, Exact(), Default());

        Assert.Equal(0m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.RuleMissing, r.Outcome);
        Assert.False(string.IsNullOrEmpty(r.TraceMessage));
        Assert.Contains("未维护Setup规则", r.TraceMessage);
        // 0号位 正式回复 §5.3：WARNING + SETUP_RULE_MISSING_ZERO_FALLBACK（进 4号位 数据质量查询）。
        Assert.Equal("WARNING", r.TraceLevel);
        Assert.Equal("SETUP_RULE_MISSING_ZERO_FALLBACK", r.ExplanationType);
        Assert.Equal(SetupOptimizer.RuleMissingZeroFallbackType, r.ExplanationType);
    }

    // ── S08/§六：同产品连续默认 0，显式 A→A 规则可覆盖 ──

    [Fact]
    public void S08_同产品无显式规则_零分钟()
    {
        var r = _optimizer.ResolveSetup("OP20", 1, 100, 100, Exact(), Default(("OP20", 1, 30m)));

        Assert.Equal(0m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.SameProductZero, r.Outcome);  // 同产品不走 DEFAULT
    }

    [Fact]
    public void 同产品显式规则覆盖默认零()
    {
        var exact = Exact(("OP20", 1, 100, 100, 10m));

        var r = _optimizer.ResolveSetup("OP20", 1, 100, 100, exact, Default());

        Assert.Equal(10m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.ExactHit, r.Outcome);
    }

    // ── S18/Q4：初始设备状态（无上一产品）→ 0 + INITIAL_SETUP_STATE 追踪，与规则缺失分开 ──

    [Fact]
    public void S18_无上一产品_初始设备状态零分钟()
    {
        // 即使存在 EXACT/DEFAULT 规则，无上一产品也不形成转换（不构造虚拟前产品）。
        var r = _optimizer.ResolveSetup("OP20", 1, null, 200,
            Exact(("OP20", 1, 100, 200, 45m)), Default(("OP20", 1, 30m)));

        Assert.Equal(0m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.InitialState, r.Outcome);
        // 0号位 正式回复 §5.1：INFO + INITIAL_SETUP_STATE + 标准文案（非 ReasonCode）。
        Assert.Equal("INFO", r.TraceLevel);
        Assert.Equal(SetupOptimizer.InitialSetupStateType, r.ExplanationType);
        Assert.Equal("当前资源无可追溯上一产品，按初始设备状态处理，Setup=0。", r.TraceMessage);
    }

    [Fact]
    public void Q4_初始状态与规则缺失是不同分支()
    {
        var initial = _optimizer.ResolveSetup("OP20", 1, null, 200, Exact(), Default());
        var missing = _optimizer.ResolveSetup("OP20", 1, 100, 200, Exact(), Default());

        Assert.Equal(SetupOptimizer.SetupOutcome.InitialState, initial.Outcome);
        Assert.Equal(SetupOptimizer.SetupOutcome.RuleMissing, missing.Outcome);
        Assert.NotEqual(initial.Outcome, missing.Outcome);   // 0号位裁决：不得合并成同一分支
        // 追踪级别也必须分开：初始状态=INFO（正常），规则缺失=WARNING（数据质量）。
        Assert.Equal("INFO", initial.TraceLevel);
        Assert.Equal("WARNING", missing.TraceLevel);
        Assert.NotEqual(initial.ExplanationType, missing.ExplanationType);
    }

    // ── Q1：生产日 = Resource Calendar 连续可用生产窗口 ──

    [Fact]
    public void Q1_相邻无间断可用段合并为一个窗口_跨零点不断开()
    {
        // 0号位示例：9/17 20:00 ～ 9/18 08:00 连续可用 → 同一窗口，不因午夜拆两天。
        var slots = new (DateTime, DateTime, bool)[]
        {
            (new DateTime(2026, 9, 17, 20, 0, 0), new DateTime(2026, 9, 18, 0, 0, 0), true),
            (new DateTime(2026, 9, 18, 0, 0, 0), new DateTime(2026, 9, 18, 8, 0, 0), true)
        };

        var windows = SetupOptimizer.BuildProductionWindows(slots);

        Assert.Single(windows);
        Assert.Equal(new DateTime(2026, 9, 17, 20, 0, 0), windows[0].Start);
        Assert.Equal(new DateTime(2026, 9, 18, 8, 0, 0), windows[0].End);
    }

    [Fact]
    public void Q1_班次断点切成两个窗口()
    {
        // 0号位示例：08:00～12:00 可用、12:00～13:00 不可用、13:00～17:00 可用 → 两个生产窗口。
        var slots = new (DateTime, DateTime, bool)[]
        {
            (new DateTime(2026, 9, 17, 8, 0, 0), new DateTime(2026, 9, 17, 12, 0, 0), true),
            (new DateTime(2026, 9, 17, 12, 0, 0), new DateTime(2026, 9, 17, 13, 0, 0), false),
            (new DateTime(2026, 9, 17, 13, 0, 0), new DateTime(2026, 9, 17, 17, 0, 0), true)
        };

        var windows = SetupOptimizer.BuildProductionWindows(slots);

        Assert.Equal(2, windows.Count);
        Assert.Equal(new DateTime(2026, 9, 17, 8, 0, 0), windows[0].Start);
        Assert.Equal(new DateTime(2026, 9, 17, 12, 0, 0), windows[0].End);
        Assert.Equal(new DateTime(2026, 9, 17, 13, 0, 0), windows[1].Start);
        Assert.Equal(new DateTime(2026, 9, 17, 17, 0, 0), windows[1].End);
    }

    [Fact]
    public void Q1_乱序输入按时间排序后合并()
    {
        var slots = new (DateTime, DateTime, bool)[]
        {
            (new DateTime(2026, 9, 17, 13, 0, 0), new DateTime(2026, 9, 17, 17, 0, 0), true),
            (new DateTime(2026, 9, 17, 8, 0, 0), new DateTime(2026, 9, 17, 12, 0, 0), true),
            (new DateTime(2026, 9, 17, 12, 0, 0), new DateTime(2026, 9, 17, 13, 0, 0), true)
        };

        var windows = SetupOptimizer.BuildProductionWindows(slots);

        Assert.Single(windows);
        Assert.Equal(new DateTime(2026, 9, 17, 8, 0, 0), windows[0].Start);
        Assert.Equal(new DateTime(2026, 9, 17, 17, 0, 0), windows[0].End);
    }

    [Fact]
    public void Q1_全不可用或空输入_无窗口()
    {
        Assert.Empty(SetupOptimizer.BuildProductionWindows(Array.Empty<(DateTime, DateTime, bool)>()));

        var slots = new (DateTime, DateTime, bool)[]
        {
            (new DateTime(2026, 9, 17, 8, 0, 0), new DateTime(2026, 9, 17, 12, 0, 0), false)
        };
        Assert.Empty(SetupOptimizer.BuildProductionWindows(slots));
    }

    // ── BuildRuleLookups：SetupTransitionRuleSnapshot（r13376 第⑦块）→ 运行时字典适配 ──

    [Fact]
    public void 快照适配_EXACT与DEFAULT分流()
    {
        var snapshots = new List<SetupTransitionRuleSnapshot>
        {
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = 100, ToMaterialId = 200, RuleType = "EXACT", SetupMinutes = 45m },
            new() { OperationCode = "OP20", ResourceId = 1, RuleType = "DEFAULT", SetupMinutes = 30m },
        };

        var (exact, def) = SetupOptimizer.BuildRuleLookups(snapshots);

        Assert.Equal(45m, exact[new SetupOptimizer.SetupExactKey("OP20", 1, 100, 200)]);
        Assert.Equal(30m, def[new SetupOptimizer.SetupDefaultKey("OP20", 1)]);

        // 适配后直接喂 ResolveSetup：EXACT 优先命中。
        var r = _optimizer.ResolveSetup("OP20", 1, 100, 200, exact, def);
        Assert.Equal(45m, r.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.ExactHit, r.Outcome);
    }

    [Fact]
    public void 快照适配_无效行防御性跳过()
    {
        var snapshots = new List<SetupTransitionRuleSnapshot>
        {
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = null, ToMaterialId = 200, RuleType = "EXACT", SetupMinutes = 45m },  // EXACT 缺 From → 跳过
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = 100, ToMaterialId = null, RuleType = "EXACT", SetupMinutes = 45m },  // EXACT 缺 To → 跳过
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = 100, ToMaterialId = 200, RuleType = "UNKNOWN", SetupMinutes = 99m }, // 未知类型 → 忽略
        };

        var (exact, def) = SetupOptimizer.BuildRuleLookups(snapshots);

        Assert.Empty(exact);
        Assert.Empty(def);
    }

    [Fact]
    public void 快照适配_同键重复取第一条_不随机选规则()
    {
        var snapshots = new List<SetupTransitionRuleSnapshot>
        {
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = 100, ToMaterialId = 200, RuleType = "EXACT", SetupMinutes = 20m },
            new() { OperationCode = "OP20", ResourceId = 1, FromMaterialId = 100, ToMaterialId = 200, RuleType = "EXACT", SetupMinutes = 60m },  // 冲突行（发布前校验应拦，Solver 兜底取第一条）
        };

        var (exact, _) = SetupOptimizer.BuildRuleLookups(snapshots);

        Assert.Single(exact);
        Assert.Equal(20m, exact[new SetupOptimizer.SetupExactKey("OP20", 1, 100, 200)]);
    }

    [Fact]
    public void 快照适配_null输入返回空字典()
    {
        var (exact, def) = SetupOptimizer.BuildRuleLookups(null);

        Assert.Empty(exact);
        Assert.Empty(def);
    }
}
