using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// `StageLeadTimeParam` 值域等价匹配的回归测试（2026-10-15 修复「三级命中率恒为 0」）。
///
/// 【修的是什么】参数表 282 行的 `StageCode` 只有阶段大类（`MACH`），工厂另落 `FactoryCode` 列（`BJ`）；
///   而消费侧传入的 `stageCode` 取自 `APS_BOM_STAGE_PATH_RAW`，是**带工厂前缀**的权威形态（`BJ_MACH`）。
///   旧实现只做 `StringComparison.Ordinal` 字面比较 ⇒ 两者永不相等 ⇒ 三级全不中 ⇒
///   该域**每一个** (物料,阶段) 都静默走 3 天兜底（`STAGE_LEADTIME_MISSING`），参数表形同未生效。
///
/// 【为什么断言这几条】不是「函数能跑通」，而是四条**可证伪**的行为：
///   ① 值域不一致时**能命中**（本次修复的目标）；
///   ② 命中层级仍按「从细到粗」正确归属（`DEPT_EXACT` / `FACTORY_STAGE_DEFAULT`）；
///   ③ 同层多行仍取 `Priority` 最小（既有语义未被等价分支改变）；
///   ④ **不误命中** —— 跨厂、脏 `FactoryCode`、StagePath 中不存在的 Stage 一律不中。
///
/// 【口径边界】本测试只断言 `ResolveStageLeadTime` 这一层。`FactoryCode` 脏字符（`CN6` + U+8BFE）与
///   `BJ|DRAW` 死行属**数据侧**，装载层**不得**替参数 Owner 猜（终态归 3号位，S-004 / 07 §366）。
/// </summary>
public class StageLeadTimeValueDomainMatchTests
{
    private static PeggingOrchestrator.StageLeadTimeParamRow Row(
        string factoryCode, string stageCode,
        string? deptCode = null, decimal days = 5m, int priority = 100)
        => new()
        {
            FactoryCode = factoryCode,
            StageCode = stageCode,
            ProductionDeptCode = deptCode,
            LeadTimeDays = days,
            Priority = priority,
        };

    // ① 核心回归：参数行不带前缀（`MACH`）+ 传入带前缀（`BJ_MACH`）⇒ 必须命中。
    //    修复前本用例返回 null（静默走 3 天兜底）。
    [Fact]
    public void 值域不一致_参数行不带前缀而传入带前缀_必须命中工厂级默认()
    {
        var rows = new[] { Row("BJ", "MACH") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.NotNull(hit);
        Assert.Equal("FACTORY_STAGE_DEFAULT", hit!.MatchLevel);
        Assert.Equal(5m, hit.LeadTimeDays);
    }

    // ② 部门码齐备时仍归最细一级 —— 等价分支不得让层级「降级」。
    [Fact]
    public void 值域不一致_且部门码齐备_归部门精确级()
    {
        var rows = new[] { Row("BJ", "MACH", deptCode: "D-MACH") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: "D-MACH", factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.NotNull(hit);
        Assert.Equal("DEPT_EXACT", hit!.MatchLevel);
    }

    // ③ 字面相等仍优先命中 —— 对「3号位 今后按 §366 统一成带前缀」的数据同样成立，
    //    证明本次改动不会成为将来统一值域的障碍。
    [Fact]
    public void 参数行已带前缀_字面形态仍命中()
    {
        var rows = new[] { Row("BJ", "BJ_MACH") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.NotNull(hit);
        Assert.Equal("FACTORY_STAGE_DEFAULT", hit!.MatchLevel);
    }

    // ③b 同层多行取 Priority 最小 —— 等价分支不改变既有的「命中即停、同层优先」语义。
    [Fact]
    public void 值域不一致_同层多行仍取Priority最小()
    {
        var rows = new[]
        {
            Row("BJ", "MACH", days: 5m, priority: 200),
            Row("BJ", "MACH", days: 7m, priority: 100),
        };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.NotNull(hit);
        Assert.Equal(100, hit!.Priority);
        Assert.Equal(7m, hit.LeadTimeDays);
    }

    // ④a 跨厂不误命中：BJ 的参数行不得被 CN 的请求捞走。
    [Fact]
    public void 值域不一致_跨厂不误命中()
    {
        var rows = new[] { Row("BJ", "MACH") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "CN", stageCode: "CN_MACH");

        Assert.Null(hit);
    }

    // ④b 脏 FactoryCode（实测 11 行 `CN6` + U+8BFE）⇒ 拼出来是 `CN6课_ASSY`，与 `CN6_ASSY` 不等。
    //     装载层**刻意不猜**：这是数据侧问题，须由参数 Owner 修（不得靠模糊匹配掩盖）。
    [Fact]
    public void 脏FactoryCode_装载层不猜_未命中()
    {
        var rows = new[] { Row("CN6课", "ASSY") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "CN6", stageCode: "CN6_ASSY");

        Assert.Null(hit);
    }

    // ④c StagePath 中不存在的 Stage（实测 1 行 `BJ|DRAW`，本库无任何 `*_DRAW`）⇒ 同样只是普通未命中。
    [Fact]
    public void 参数行Stage在StagePath不存在_未命中()
    {
        var rows = new[] { Row("BJ", "DRAW") };

        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            rows, deptCode: null, factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.Null(hit);
    }

    // ④d 参数表完全空 ⇒ 返回 null（调用方据此走 STAGE_LEADTIME_MISSING + 3 天兜底）。
    [Fact]
    public void 无任何参数行_未命中()
    {
        var hit = PeggingOrchestrator.ResolveStageLeadTime(
            Array.Empty<PeggingOrchestrator.StageLeadTimeParamRow>(),
            deptCode: null, factoryCode: "BJ", stageCode: "BJ_MACH");

        Assert.Null(hit);
    }
}
