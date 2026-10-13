using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 三级日历继承解析（工厂 → 部门 → 资源/人工槽）语义钉死测试。
///
/// 【为什么必须有这组测试】
///   ⚠ 2026-10-13 更正：写这组测试时（同日早些时候）`ProductionWorkCalendarSlot` 实库为 **0 行**，
///   三级合成分支在真实数据下**不可观测**；但当日 13:48 起 5号位 已落入 47 行 TEST 种子，
///   **该分支现已在真实载荷上生效**（详见 `SchedulingOrchestrator.LoadResourcesAndCalendarAsync` 处注释）。
///   本组测试因此从「唯一可观测性来源」变为「回归护栏」—— 仍需逐条钉住，
///   因为真实数据只覆盖「周一至周五 WORK / 周六日 OFF + 少量按日 fixture」这一条窄路径。
///
/// 口径逐条对应：规则清单 v1.7 RC-001/RC-002/RC-006/RC-007；日历专项 v1.4 §2/§4/§5。
/// </summary>
public class ResourceCalendarResolverTests
{
    /// <summary>2026-10-13 是**周二**（ISO 星期 = 2）。</summary>
    private static readonly DateTime D = new(2026, 10, 13);
    private static readonly DateTime D1 = D.AddDays(1);      // 周三，ISO = 3
    private static readonly DateTime D7 = D.AddDays(7);      // 下周二，ISO = 2

    private const int FactoryId = 3;
    private const int DeptId = 30;

    // ── 构造器 ──────────────────────────────────────────────────────────

    private static ResourceCalendarResolver.LevelWindow Work(DateTime s, DateTime e)
        => new() { Start = s, End = e, IsAvailable = true };

    private static ResourceCalendarResolver.LevelWindow Off(DateTime s, DateTime e)
        => new() { Start = s, End = e, IsAvailable = false };

    private static ResourceCalendarResolver.ResourceCalendarInput Res(
        string key, int? factory, int? dept,
        params ResourceCalendarResolver.LevelWindow[] own)
        => new() { Key = key, FactoryId = factory, DepartmentId = dept, OwnWindows = own };

    private static ProductionWorkCalendarSlot Upper(
        string scopeType, int factoryId, int? deptId, byte? weekdayIso, DateTime? workDate,
        string state, TimeSpan? startAt, TimeSpan? endAt,
        bool allowLowerOverride = false, bool isAuthorizedOverride = false)
        => new()
        {
            ScopeType = scopeType,
            FactoryId = factoryId,
            ProductionDepartmentId = deptId,
            WeekdayIso = weekdayIso,
            WorkDate = workDate,
            WorkState = state,
            StartAt = startAt,
            EndAt = endAt,
            AllowLowerOverride = allowLowerOverride,
            IsAuthorizedOverride = isAuthorizedOverride,
            EffectiveFrom = new DateTime(2020, 1, 1),
            EffectiveTo = null,
            IsActive = true
        };

    private static ProductionWorkCalendarSlot FactoryWork(byte weekdayIso, int fromHour, int toHour)
        => Upper("FACTORY", FactoryId, null, weekdayIso, null, "WORK",
                 TimeSpan.FromHours(fromHour), TimeSpan.FromHours(toHour));

    private static ProductionWorkCalendarSlot FactoryWholeDayOff(
        byte weekdayIso, bool allowLowerOverride = false)
        => Upper("FACTORY", FactoryId, null, weekdayIso, null, "OFF", null, null, allowLowerOverride);

    private static ProductionWorkCalendarSlot DeptWork(byte weekdayIso, int fromHour, int toHour,
        bool isAuthorizedOverride = false)
        => Upper("DEPARTMENT", FactoryId, DeptId, weekdayIso, null, "WORK",
                 TimeSpan.FromHours(fromHour), TimeSpan.FromHours(toHour), false, isAuthorizedOverride);

    /// <summary>默认计划期 = **单日**（2026-10-13）。周重复规则在 60 天计划期上会展开成 ~9 片，断言会失真。</summary>
    private static ResourceCalendarResolver.ResolutionResult Resolve(
        IReadOnlyList<ProductionWorkCalendarSlot> upper,
        IReadOnlyList<ResourceCalendarResolver.ResourceCalendarInput> resources)
        => ResolveOver(upper, resources, D);

    private static ResourceCalendarResolver.ResolutionResult ResolveOver(
        IReadOnlyList<ProductionWorkCalendarSlot> upper,
        IReadOnlyList<ResourceCalendarResolver.ResourceCalendarInput> resources,
        DateTime horizonEnd)
        => ResourceCalendarResolver.Resolve(upper, resources, D, horizonEnd);

    private static readonly ProductionWorkCalendarSlot[] NoUpper =
        Array.Empty<ProductionWorkCalendarSlot>();

    // ═══════════════════════════════════════════════════════════════════
    // 一、等价快路径：上级表 0 行 ⇒ 今日行为逐字不变
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 上级表为空_资源级窗口原样下传_来源标资源级()
    {
        var r = Resolve(NoUpper, new[] { Res("1", FactoryId, DeptId, Work(D.AddHours(8), D.AddHours(17))) });

        r.UpperLevelFactsPresent.Should().BeFalse();
        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(8));
        r.Calendars["1"][0].End.Should().Be(D.AddHours(17));
        r.Calendars["1"][0].Source.Should().Be(ResourceCalendarResolver.CalendarLevel.Resource);
    }

    [Fact]
    public void 上级表为空_无本级窗口的资源不进日历字典_并计入无窗口诊断()
    {
        var r = Resolve(NoUpper, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
        r.ResourcesWithoutAnyWindow.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 二、继承：本级 MISSING ⇒ 向上取（RC-002：资源级无记录 ≠ 不可排）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 资源级无行_继承部门工作日历()
    {
        var r = Resolve(new[] { DeptWork(2, 8, 17) }, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(8));
        r.Calendars["1"][0].End.Should().Be(D.AddHours(17));
        r.Calendars["1"][0].Source.Should().Be(ResourceCalendarResolver.CalendarLevel.Department);
        r.ResourcesWithInheritedWindow.Should().Be(1);
    }

    [Fact]
    public void 资源级与部门级都无行_继承工厂工作日历()
    {
        var r = Resolve(new[] { FactoryWork(2, 8, 17) }, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Source.Should().Be(ResourceCalendarResolver.CalendarLevel.Factory);
        r.WindowCountFromFactory.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 三、绝不相加：跨层只做交集（专项 v1.4 §2「不能把上级时段相加制造产能」）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 工厂与资源级窗口_取交集而非相加()
    {
        var r = Resolve(
            new[] { FactoryWork(2, 8, 12) },
            new[] { Res("1", FactoryId, DeptId, Work(D.AddHours(9), D.AddHours(17))) });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(9));   // 不是 08:00
        r.Calendars["1"][0].End.Should().Be(D.AddHours(12));    // 不是 17:00
    }

    // ═══════════════════════════════════════════════════════════════════
    // 四、OFF 不得被复活（RC-006 / 专项 §2）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 工厂整日OFF_下级部门WORK不得复活_该日无窗口()
    {
        var r = Resolve(
            new[] { FactoryWholeDayOff(2), DeptWork(2, 9, 12) },
            new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
        r.ResourcesWithoutAnyWindow.Should().Be(1);
    }

    [Fact]
    public void 工厂整日OFF_但双向授权齐备_下级授权加班可越权()
    {
        var r = Resolve(
            new[] { FactoryWholeDayOff(2, allowLowerOverride: true), DeptWork(2, 9, 12, isAuthorizedOverride: true) },
            new[] { Res("1", FactoryId, DeptId) });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(9));
        r.Calendars["1"][0].End.Should().Be(D.AddHours(12));
    }

    [Fact]
    public void 工厂整日OFF_只有下级授权但工厂未授权_仍不得复活()
    {
        var r = Resolve(
            new[] { FactoryWholeDayOff(2, allowLowerOverride: false), DeptWork(2, 9, 12, isAuthorizedOverride: true) },
            new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
    }

    [Fact]
    public void 资源级OFF是硬排除_从上级窗口中挖掉()
    {
        var r = Resolve(
            new[] { FactoryWork(2, 8, 17) },
            new[] { Res("1", FactoryId, DeptId, Off(D.AddHours(12), D.AddHours(13))) });

        r.Calendars["1"].Should().HaveCount(2);
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(8));
        r.Calendars["1"][0].End.Should().Be(D.AddHours(12));
        r.Calendars["1"][1].Start.Should().Be(D.AddHours(13));
        r.Calendars["1"][1].End.Should().Be(D.AddHours(17));
    }

    [Fact]
    public void 资源级只写OFF无WORK_该资源不可排()
    {
        var r = Resolve(
            new[] { FactoryWork(2, 8, 17) },
            new[] { Res("1", FactoryId, DeptId, Off(D.AddHours(8), D.AddHours(17))) });

        r.Calendars.Should().NotContainKey("1");
        r.ResourcesWithoutAnyWindow.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 五、不兜底：三级都拿不出 WORK ⇒ 无窗口（RC-007：禁止补 8h / 7×24）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 该日三级都无事实_无窗口_绝不补8小时或7x24()
    {
        // 上级只有**周三**的部门 WORK；查的是**周二**
        var r = Resolve(new[] { DeptWork(3, 8, 17) }, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
        r.ResourcesWithoutAnyWindow.Should().Be(1);
    }

    [Fact]
    public void 该日三级都无事实_但次日有事实_次日照常开工()
    {
        // 同一条数据：无事实的日不兜底、有事实的日照常 —— 证明「不兜底」不等于「整条资源哑掉」
        var r = ResolveOver(new[] { DeptWork(3, 8, 17) }, new[] { Res("1", FactoryId, DeptId) }, D1);

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D1.AddHours(8));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 六、日期例外优先于周重复（RC-006 / 专项 §5）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 日期例外优先于周重复_只影响该日()
    {
        var upper = new[]
        {
            FactoryWork(2, 8, 17),
            Upper("FACTORY", FactoryId, null, null, D, "OFF", null, null)   // 仅 2026-10-13 全天停
        };

        var r = ResolveOver(upper, new[] { Res("1", FactoryId, DeptId) }, D7);

        r.Calendars["1"].Should().ContainSingle();                       // 只有下周二那片
        r.Calendars["1"].Should().NotContain(w => w.Start.Date == D);    // 例外日被关掉
        r.Calendars["1"][0].Start.Date.Should().Be(D7);                  // 例外不得外溢到同 ISO 星期
    }

    [Fact]
    public void 生效期之外的行不生效_退回MISSING()
    {
        var expired = Upper("FACTORY", FactoryId, null, 2, null, "WORK",
            TimeSpan.FromHours(8), TimeSpan.FromHours(17));
        expired.EffectiveTo = D.AddDays(-1);

        var r = Resolve(new[] { expired }, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 七、同层矛盾 ⇒ 整日拒绝（RC-006「相同级别重叠矛盾拒绝发布」；不猜）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 同层WORK部分重叠_整日拒绝并计数()
    {
        var upper = new[]
        {
            FactoryWork(2, 8, 12),
            FactoryWork(2, 10, 17)      // 与上一行部分重叠
        };

        var r = Resolve(upper, new[] { Res("1", FactoryId, DeptId) });

        r.Calendars.Should().NotContainKey("1");
        r.ConflictDayCount.Should().Be(1);
    }

    [Fact]
    public void 同层WORK完全重复_不算矛盾_去重后只出一片()
    {
        var upper = new[] { FactoryWork(2, 8, 17), FactoryWork(2, 8, 17) };

        var r = Resolve(upper, new[] { Res("1", FactoryId, DeptId) });

        r.ConflictDayCount.Should().Be(0);
        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].End.Should().Be(D.AddHours(17));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 八、跨午夜拆片：无遗漏、无重叠（RC-006）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 资源级窗口跨午夜_按自然日拆片且被上级裁齐()
    {
        var upper = new[]
        {
            FactoryWork(2, 0, 23),      // 周二 00:00–23:00
            FactoryWork(3, 0, 23)       // 周三 00:00–23:00
        };
        // 资源级：周二 22:00 → 周三 02:00（跨午夜）
        var own = Work(D.AddHours(22), D1.AddHours(2));

        var r = ResolveOver(upper, new[] { Res("1", FactoryId, DeptId, own) }, D1);

        r.Calendars["1"].Should().HaveCount(2);
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(22));   // 周二片，切在午夜
        r.Calendars["1"][0].End.Should().Be(D.AddHours(23));     // 被周二上级 23:00 收口
        r.Calendars["1"][1].Start.Should().Be(D1);               // 周三片从午夜起
        r.Calendars["1"][1].End.Should().Be(D1.AddHours(2));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 九、归属不可靠 ⇒ 只按本级、并留诊断（专项 v1.4 §1）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 归属不可靠_不解析上级_仍按本级出窗口并计入诊断()
    {
        var r = Resolve(
            new[] { FactoryWork(2, 8, 17) },
            new[] { Res("1", factory: null, dept: null, Work(D.AddHours(8), D.AddHours(17))) });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Source.Should().Be(ResourceCalendarResolver.CalendarLevel.Resource);
        r.ResourcesWithUnreliableOwnership.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 十、多资源：同一工厂的共享日计划被缓存，结果互不串味
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void 同工厂多资源_各自本级窗口不同_互不污染()
    {
        var r = Resolve(
            new[] { FactoryWork(2, 8, 17) },
            new[]
            {
                Res("1", FactoryId, DeptId),                                            // 全继承
                Res("2", FactoryId, DeptId, Work(D.AddHours(9), D.AddHours(10))),       // 本级收窄
            });

        r.Calendars["1"].Should().ContainSingle();
        r.Calendars["1"][0].Start.Should().Be(D.AddHours(8));
        r.Calendars["1"][0].End.Should().Be(D.AddHours(17));
        r.Calendars["2"].Should().ContainSingle();
        r.Calendars["2"][0].Start.Should().Be(D.AddHours(9));
        r.Calendars["2"][0].End.Should().Be(D.AddHours(10));
    }
}
