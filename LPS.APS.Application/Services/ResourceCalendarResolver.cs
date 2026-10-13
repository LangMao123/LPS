using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Application.Services;

/// <summary>
/// 资源日历**三级继承**解析（工厂 → 生产部门 → 设备 / 人工能力槽）—— 纯内存，不碰数据库、不写库。
///
/// 【为什么需要它】
///   2026-10-13 之前，装载层只读**资源级** `ResourceCalendarSlot` 与**人工级** `ManualCapacitySlotCalendar`，
///   工厂/部门级 `ProductionWorkCalendarSlot` **在 .cs 里零引用**。后果是：
///   5号位 一旦按冻结要求铺上工厂节假日 / 停机 OFF，装载层会**整条无视** ⇒ 该停的照排 ⇒ 交给 1号位 的载荷是错的。
///
/// 【口径来源（逐条对齐，不得自行放宽）】
///   · 规则清单 v1.7 RC-001：工厂→部门→设备/人工槽**逐时间片继承与显式覆盖**；未配置可继承、**明确关闭不可由上级复活**、
///     **不能把上级时段相加制造产能**；2号位 合成合法能力窗口、1号位 只消费。
///   · 规则清单 v1.7 RC-002：**资源级日历无记录并不自动不可排**，须按三级权威继承结果判定。
///   · 规则清单 v1.7 RC-006：合法 Calendar 区分**未配置 / 明确工作 / 明确不工作**；日期例外及授权覆盖按逐片规则执行；
///     **维修/停机/OFF 不得被上级复活**；跨午夜拆片须无遗漏与重叠。
///   · 规则清单 v1.7 RC-007：三级**无合法权威来源时无可排 Slot**；**禁止统一补每日 8 小时或 7×24**。
///   · 日历专项 v1.4 §2：MISSING 允许继承上级；OFF 不得被上级 WORK 复活；**跨层按时间片覆盖、交集/例外裁剪，绝不时段相加**。
///   · 日历专项 v1.4 §5：**相同级别重叠矛盾拒绝发布**；日期例外优先于周重复；**资源状态=停用/维修时，即使下级未写 OFF 亦不得排程**。
///   · 06 接口设计 §18（2→1 Calendar）：逐片继承与显式覆盖，提供合法时间槽及关闭/加班/来源的最低追溯信息；
///     **1号位 无权从 NULL Calendar 推导 8h 或 7×24**。
///
/// 【本实现的三条硬红线】
///   ① **不合成**：只做「交集 / 例外裁剪」，任何分支都不产生「上级时段 + 下级时段」的相加。
///   ② **不兜底**：三级都拿不出合法 WORK ⇒ 该资源该日**无窗口**（= 不可排），绝不补 8h / 7×24。
///   ③ **不猜**：同层矛盾（WORK 之间部分重叠、OFF 之间部分重叠、WORK 无窗口）⇒ **整日拒绝**，不用 `.First()` 硬取。
///
/// 【一处需与 3号位 确认的读法（已在代码内标注）】
///   「同层重叠矛盾」本实现按 **WORK↔WORK 部分重叠** 与 **OFF↔OFF 部分重叠** 判矛盾；
///   **同层 WORK 与 OFF 相互嵌套**（如 WORK 08:00–17:00 + OFF 12:00–13:00）**视为合法的午休编码**，按例外裁剪处理，不判矛盾。
///   若 3号位 治理侧口径为「同层任何重叠一律拒绝」，此处需同步收紧。
/// </summary>
public static class ResourceCalendarResolver
{
    /// <summary>有效窗口的来源层级。数值越大越「细」（越接近资源）。</summary>
    public enum CalendarLevel
    {
        None = 0,
        Factory = 1,
        Department = 2,
        Resource = 3
    }

    /// <summary>资源/人工槽**本级**的一条窗口（对应 `ResourceCalendarSlot` / `ManualCapacitySlotCalendar` 一行）。</summary>
    public sealed class LevelWindow
    {
        public DateTime Start { get; init; }
        public DateTime End { get; init; }

        /// <summary>true = WORK（可用）；false = 显式 OFF（停机 / 维修 / 禁用）——资源级 OFF 为**硬排除**，不可被覆盖。</summary>
        public bool IsAvailable { get; init; }
    }

    /// <summary>一个待解析的运行时可排单元（设备资源 或 人工能力槽合成资源）。</summary>
    public sealed class ResourceCalendarInput
    {
        /// <summary>运行时资源键（设备 = ResourceId 字符串；人工槽 = 合成 Id 字符串）。</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>归属工厂；不可靠（无主档）时传 null ⇒ 不解析工厂级、并计入诊断。</summary>
        public int? FactoryId { get; init; }

        /// <summary>归属生产部门；不可靠时传 null ⇒ 不解析部门级、并计入诊断。</summary>
        public int? DepartmentId { get; init; }

        /// <summary>本级窗口（**含 OFF 行**，由调用方按 AvailableFlag 拆分后传入）。</summary>
        public IReadOnlyList<LevelWindow> OwnWindows { get; init; } = Array.Empty<LevelWindow>();
    }

    public sealed class ResolvedWindow
    {
        public DateTime Start { get; init; }
        public DateTime End { get; init; }

        /// <summary>本片最终由哪一级的 WORK 决定（供追溯；1号位 载荷当前不含此列，见类注释「待 1号位 点名」）。</summary>
        public CalendarLevel Source { get; init; }
    }

    public sealed class ResolutionResult
    {
        /// <summary>解析出的日历；**只含至少一片有效窗口的资源**（无窗口 = 不在字典里 = 不可用）。</summary>
        public Dictionary<string, List<ResolvedWindow>> Calendars { get; } = new(StringComparer.Ordinal);

        /// <summary>上级日历（`ProductionWorkCalendarSlot`）是否有事实行。false ⇒ 走等价快路径。</summary>
        public bool UpperLevelFactsPresent { get; set; }

        /// <summary>同层矛盾被**整日拒绝**的天数（资源 × 日）。</summary>
        public int ConflictDayCount { get; set; }

        /// <summary>解析后**无任何窗口**的资源数（= 不可排）。</summary>
        public int ResourcesWithoutAnyWindow { get; set; }

        /// <summary>最终窗口按来源层级的片数分布（追溯用）。</summary>
        public int WindowCountFromFactory { get; set; }
        public int WindowCountFromDepartment { get; set; }
        public int WindowCountFromResource { get; set; }

        /// <summary>**有**窗口、且至少一片来自上级（工厂/部门）继承的资源数 ⇒ 「三级继承真的生效了」的计数。</summary>
        public int ResourcesWithInheritedWindow { get; set; }

        /// <summary>归属不可靠（缺工厂/部门）而无法解析上级的资源数。</summary>
        public int ResourcesWithUnreliableOwnership { get; set; }
    }

    /// <summary>
    /// 逐片合成合法可排窗口。
    /// </summary>
    /// <param name="upperLevelRows">`ProductionWorkCalendarSlot` 全量有效行（调用方已按 IsActive=1 过滤；空表传空集合）。</param>
    /// <param name="resources">待解析资源（设备 + 人工槽）。</param>
    /// <param name="horizonStart">计划期起（含）。</param>
    /// <param name="horizonEnd">计划期止（含）；调用方通常传 `PlanHorizonEnd + 软边界缓冲天数`。</param>
    public static ResolutionResult Resolve(
        IReadOnlyList<ProductionWorkCalendarSlot> upperLevelRows,
        IReadOnlyList<ResourceCalendarInput> resources,
        DateTime horizonStart,
        DateTime horizonEnd)
    {
        var result = new ResolutionResult();
        var rows = upperLevelRows ?? Array.Empty<ProductionWorkCalendarSlot>();
        result.UpperLevelFactsPresent = rows.Count > 0;

        // ── 等价快路径：上级表无事实行 ⇒ 两级全 MISSING ⇒ 有效窗口恒等于资源级自身 WORK ──
        // 该分支与下方三级合成的结果**逐字相同**（工厂/部门都 MISSING，交集不产生约束、OFF 集合为空），
        // 故保留它既是为性能，也是为「今日行为零变化」提供可验证的保证。
        if (!result.UpperLevelFactsPresent)
        {
            foreach (var r in resources)
            {
                var windows = OwnToResolved(r.OwnWindows);
                if (windows.Count == 0) { result.ResourcesWithoutAnyWindow++; continue; }
                result.Calendars[r.Key] = windows;
                result.WindowCountFromResource += windows.Count;
            }
            return result;
        }

        var factoryRows = rows
            .Where(x => ScopeEquals(x.ScopeType, "FACTORY"))
            .GroupBy(x => x.FactoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductionWorkCalendarSlot>)g.ToList());

        var departmentRows = rows
            .Where(x => ScopeEquals(x.ScopeType, "DEPARTMENT") && x.ProductionDepartmentId.HasValue)
            .GroupBy(x => x.ProductionDepartmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductionWorkCalendarSlot>)g.ToList());

        // 逐日计划缓存：同一工厂/部门下多个资源共享同一次解析（人工槽动辄上万，必须缓存）。
        var factoryDayCache = new Dictionary<(int ScopeId, int DayNumber), DayPlan>();
        var departmentDayCache = new Dictionary<(int ScopeId, int DayNumber), DayPlan>();

        foreach (var res in resources)
        {
            // 归属不可靠 = 缺工厂或部门 ⇒ 该资源的上级层解析不出来。
            // ⚠️ 必须在「退化为资源级口径」的短路**之前**计数，否则这一类资源永远不进诊断，
            //    而它恰恰是最需要被看见的一类（上级 OFF 对它静默失效）。
            if (res.FactoryId is null || res.DepartmentId is null)
                result.ResourcesWithUnreliableOwnership++;

            var hasFactoryLayer = res.FactoryId is int fid && factoryRows.ContainsKey(fid);
            var hasDepartmentLayer = res.DepartmentId is int did && departmentRows.ContainsKey(did);

            if (!hasFactoryLayer && !hasDepartmentLayer)
            {
                // 该资源所在工厂/部门都没有上级事实 ⇒ 退化为资源级口径（与快路径同义）。
                var onlyOwn = OwnToResolved(res.OwnWindows);
                if (onlyOwn.Count == 0) { result.ResourcesWithoutAnyWindow++; continue; }
                result.Calendars[res.Key] = onlyOwn;
                result.WindowCountFromResource += onlyOwn.Count;
                continue;
            }

            var ownByDay = BucketOwnByDay(res.OwnWindows);
            var resolved = new List<ResolvedWindow>();
            var inherited = false;

            foreach (var dayNumber in EnumerateDays(res.OwnWindows, ownByDay, horizonStart, horizonEnd))
            {
                var day = DateOnly.FromDayNumber(dayNumber).ToDateTime(TimeOnly.MinValue);

                var plans = new List<(CalendarLevel Level, DayPlan Plan)>(3);
                if (res.FactoryId is int f && factoryRows.TryGetValue(f, out var fRows))
                {
                    var p = GetDayPlan(factoryDayCache, f, dayNumber, fRows, day);
                    if (p.HasFact) plans.Add((CalendarLevel.Factory, p));
                }
                if (res.DepartmentId is int d && departmentRows.TryGetValue(d, out var dRows))
                {
                    var p = GetDayPlan(departmentDayCache, d, dayNumber, dRows, day);
                    if (p.HasFact) plans.Add((CalendarLevel.Department, p));
                }
                if (ownByDay.TryGetValue(dayNumber, out var ownList))
                    plans.Add((CalendarLevel.Resource, BuildOwnPlan(ownList)));

                if (plans.Count == 0) continue;                     // 三级全 MISSING ⇒ 该日无窗口（不兜底）
                if (plans.Any(x => x.Plan.IsConflict))               // 同层矛盾 ⇒ 整日拒绝，不猜
                {
                    result.ConflictDayCount++;
                    continue;
                }

                foreach (var w in Compose(plans))
                {
                    resolved.Add(w);
                    if (w.Source != CalendarLevel.Resource) inherited = true;
                }
            }

            if (resolved.Count == 0) { result.ResourcesWithoutAnyWindow++; continue; }

            result.Calendars[res.Key] = resolved
                .OrderBy(w => w.Start).ThenBy(w => w.End)
                .ToList();

            foreach (var w in result.Calendars[res.Key])
            {
                switch (w.Source)
                {
                    case CalendarLevel.Factory: result.WindowCountFromFactory++; break;
                    case CalendarLevel.Department: result.WindowCountFromDepartment++; break;
                    default: result.WindowCountFromResource++; break;
                }
            }
            if (inherited) result.ResourcesWithInheritedWindow++;
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 逐片合成（粗 → 细）：WORK 取交集、OFF 作例外裁剪，任何分支都不相加
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ResolvedWindow> Compose(List<(CalendarLevel Level, DayPlan Plan)> plans)
    {
        var effective = new List<(DateTime S, DateTime E)>();
        var pendingOff = new List<(DateTime S, DateTime E)>();
        var hasWorkConstraint = false;

        for (var i = 0; i < plans.Count; i++)
        {
            var (level, plan) = plans[i];

            // 本层 WORK 形成/收紧约束
            if (plan.Work.Count > 0)
            {
                // Normalize（去重 + 排序）而非直接拷贝：同层完全重复的 WORK 行（数据里很常见）
                // 若原样带入，会在最终载荷里产出**逐字相同的两片窗口**、并虚增来源层级片数。
                effective = hasWorkConstraint ? Intersect(effective, plan.Work) : Normalize(plan.Work);
                hasWorkConstraint = true;

                // 先前进到「只有 OFF、还没遇到 WORK」时挂起的 OFF，此刻才有对象可裁
                foreach (var off in pendingOff) effective = Subtract(effective, off);
                pendingOff.Clear();
            }

            // 本层 OFF：作例外裁剪；可覆盖的 OFF 先被更细层的**授权加班 WORK** 挖开（双向授权）
            foreach (var off in plan.Off)
            {
                var segments = new List<(DateTime S, DateTime E)> { (off.S, off.E) };
                if (off.Overridable)
                    segments = Subtract(segments, UnionAuthorizedWork(plans, i + 1));

                foreach (var seg in segments)
                {
                    if (hasWorkConstraint) effective = Subtract(effective, seg);
                    else pendingOff.Add(seg);     // 尚无 WORK 约束：挂起，等更细层 WORK 出现再裁
                }
            }
        }

        // 三级都拿不出 WORK ⇒ 无窗口（RC-007：绝不补 8h / 7×24）
        if (!hasWorkConstraint) return new List<ResolvedWindow>();

        var output = new List<ResolvedWindow>(effective.Count);
        foreach (var (s, e) in effective)
        {
            if (s >= e) continue;
            output.Add(new ResolvedWindow { Start = s, End = e, Source = ResolveSource(plans, s, e) });
        }
        return output;
    }

    /// <summary>本片最终由哪一级的 WORK 决定：从最细往最粗找第一个「完整包住该片」的 WORK。</summary>
    private static CalendarLevel ResolveSource(List<(CalendarLevel Level, DayPlan Plan)> plans, DateTime s, DateTime e)
    {
        for (var i = plans.Count - 1; i >= 0; i--)
            if (plans[i].Plan.Work.Any(w => w.S <= s && e <= w.E))
                return plans[i].Level;
        return plans[0].Level;
    }

    /// <summary>比 <paramref name="fromIndex"/> 更细的各层中，标记为「授权越权加班」的 WORK 并集。</summary>
    private static List<(DateTime S, DateTime E)> UnionAuthorizedWork(
        List<(CalendarLevel Level, DayPlan Plan)> plans, int fromIndex)
    {
        var output = new List<(DateTime S, DateTime E)>();
        for (var i = fromIndex; i < plans.Count; i++)
            if (plans[i].Plan.HasAuthorizedWork)
                output.AddRange(plans[i].Plan.Work);
        return output;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 单层单日计划
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class DayPlan
    {
        /// <summary>该层该日是否有有效行（false = MISSING，允许继承上级）。</summary>
        public bool HasFact { get; set; }

        /// <summary>同层矛盾 ⇒ 整日拒绝。</summary>
        public bool IsConflict { get; set; }

        public List<(DateTime S, DateTime E)> Work { get; } = new();

        public List<(DateTime S, DateTime E, bool Overridable)> Off { get; } = new();

        /// <summary>该层存在被授权的越权加班 WORK 行（`IsAuthorizedOverride=1`）。</summary>
        public bool HasAuthorizedWork { get; set; }
    }

    private static DayPlan GetDayPlan(
        Dictionary<(int, int), DayPlan> cache, int scopeId, int dayNumber,
        IReadOnlyList<ProductionWorkCalendarSlot> rows, DateTime day)
    {
        if (cache.TryGetValue((scopeId, dayNumber), out var cached)) return cached;
        var plan = BuildUpperDayPlan(rows, day);
        cache[(scopeId, dayNumber)] = plan;
        return plan;
    }

    private static DayPlan BuildUpperDayPlan(IReadOnlyList<ProductionWorkCalendarSlot> rows, DateTime day)
    {
        var plan = new DayPlan();

        var applicable = rows
            .Where(r => r.IsActive
                        && r.EffectiveFrom.Date <= day.Date
                        && (r.EffectiveTo is null || r.EffectiveTo.Value.Date >= day.Date))
            .ToList();
        if (applicable.Count == 0) return plan;                       // MISSING

        // 日期例外优先于周重复（RC-006 / 专项 §5）
        var dated = applicable
            .Where(r => r.WorkDate.HasValue && r.WorkDate.Value.Date == day.Date)
            .ToList();
        var chosen = dated.Count > 0
            ? dated
            : applicable.Where(r => !r.WorkDate.HasValue
                                    && r.WeekdayIso.HasValue
                                    && r.WeekdayIso.Value == IsoWeekday(day)).ToList();
        if (chosen.Count == 0) return plan;                            // MISSING

        plan.HasFact = true;

        foreach (var r in chosen)
        {
            if (ScopeStateIsWork(r.WorkState))
            {
                if (r.StartAt.HasValue && r.EndAt.HasValue)
                {
                    plan.Work.Add((day.Date + r.StartAt.Value, day.Date + r.EndAt.Value));
                    if (r.IsAuthorizedOverride) plan.HasAuthorizedWork = true;
                }
                else
                {
                    // DDL CK_PWCS_TimeWindow 保证 WORK 必带窗口；真出现即结构矛盾 ⇒ 拒绝，不猜。
                    plan.IsConflict = true;
                }
            }
            else
            {
                var start = r.StartAt.HasValue ? day.Date + r.StartAt.Value : day.Date;
                var end = r.EndAt.HasValue ? day.Date + r.EndAt.Value : day.Date.AddDays(1);
                plan.Off.Add((start, end, r.AllowLowerOverride));
            }
        }

        // 同层矛盾：WORK↔WORK 部分重叠 / OFF↔OFF 部分重叠。
        // 同层 WORK 与 OFF 相互嵌套**不判矛盾**（视为合法的例外裁剪编码，见类注释）。
        if (HasPartialOverlap(plan.Work) || HasPartialOverlap(plan.Off.Select(o => (o.S, o.E))))
            plan.IsConflict = true;

        return plan;
    }

    private static DayPlan BuildOwnPlan(List<(DateTime S, DateTime E, bool Available)> own)
    {
        var plan = new DayPlan { HasFact = true };
        foreach (var (s, e, available) in own)
        {
            if (available) plan.Work.Add((s, e));
            else plan.Off.Add((s, e, false));      // 资源/人工槽是最细层，自身 OFF 不可被覆盖
        }
        if (HasPartialOverlap(plan.Work) || HasPartialOverlap(plan.Off.Select(o => (o.S, o.E))))
            plan.IsConflict = true;
        return plan;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 小工具
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ResolvedWindow> OwnToResolved(IReadOnlyList<LevelWindow> own)
    {
        var output = new List<ResolvedWindow>();
        foreach (var w in own.Where(w => w.IsAvailable))
        {
            if (w.Start >= w.End) continue;
            output.Add(new ResolvedWindow { Start = w.Start, End = w.End, Source = CalendarLevel.Resource });
        }
        return output;
    }

    /// <summary>把本级窗口按自然日切桶（跨午夜拆片，RC-006 要求无遗漏、无重叠）。</summary>
    private static Dictionary<int, List<(DateTime S, DateTime E, bool Available)>> BucketOwnByDay(
        IReadOnlyList<LevelWindow> own)
    {
        var buckets = new Dictionary<int, List<(DateTime, DateTime, bool)>>();
        foreach (var w in own)
        {
            if (w.Start >= w.End) continue;
            foreach (var (dayNumber, s, e) in SplitByDay(w.Start, w.End))
            {
                if (!buckets.TryGetValue(dayNumber, out var list))
                    buckets[dayNumber] = list = new List<(DateTime, DateTime, bool)>();
                list.Add((s, e, w.IsAvailable));
            }
        }
        return buckets;
    }

    private static IEnumerable<int> EnumerateDays(
        IReadOnlyList<LevelWindow> own,
        Dictionary<int, List<(DateTime S, DateTime E, bool Available)>> ownByDay,
        DateTime horizonStart,
        DateTime horizonEnd)
    {
        var first = DayNumber(horizonStart);
        var last = DayNumber(horizonEnd);

        // 保留本级窗口的全部日期（哪怕落在计划期之外）——旧行为是**不过滤**地整体下传，此处不得收窄。
        if (own.Count > 0)
        {
            first = Math.Min(first, DayNumber(own.Min(w => w.Start)));
            last = Math.Max(last, DayNumber(own.Max(w => w.End.AddTicks(-1))));
        }

        // 防御：上限 10 年，避免异常数据（如 9999 年）把装载拖死。
        const int maxDays = 3653;
        if (last - first > maxDays) last = first + maxDays;

        for (var d = first; d <= last; d++) yield return d;
    }

    private static IEnumerable<(int DayNumber, DateTime S, DateTime E)> SplitByDay(DateTime start, DateTime end)
    {
        var cursor = start;
        while (cursor < end)
        {
            var nextDay = cursor.Date.AddDays(1);
            var segmentEnd = nextDay < end ? nextDay : end;
            yield return (DayNumber(cursor), cursor, segmentEnd);
            cursor = segmentEnd;
        }
    }

    /// <summary>自然日序号（`DateTime` 无 `DayNumber`，那是 `DateOnly` 的成员）。</summary>
    private static int DayNumber(DateTime value) => DateOnly.FromDateTime(value).DayNumber;

    private static List<(DateTime S, DateTime E)> Intersect(
        List<(DateTime S, DateTime E)> a, List<(DateTime S, DateTime E)> b)
    {
        var output = new List<(DateTime S, DateTime E)>();
        foreach (var x in a)
            foreach (var y in b)
            {
                var s = x.S > y.S ? x.S : y.S;
                var e = x.E < y.E ? x.E : y.E;
                if (s < e) output.Add((s, e));
            }
        return Normalize(output);
    }

    private static List<(DateTime S, DateTime E)> Subtract(
        List<(DateTime S, DateTime E)> a, (DateTime S, DateTime E) cut)
    {
        var output = new List<(DateTime S, DateTime E)>();
        foreach (var x in a)
        {
            if (cut.E <= x.S || cut.S >= x.E) { output.Add(x); continue; }
            if (cut.S > x.S) output.Add((x.S, cut.S));
            if (cut.E < x.E) output.Add((cut.E, x.E));
        }
        return Normalize(output);
    }

    private static List<(DateTime S, DateTime E)> Subtract(
        List<(DateTime S, DateTime E)> a, List<(DateTime S, DateTime E)> cuts)
    {
        var current = a;
        foreach (var cut in cuts) current = Subtract(current, cut);
        return current;
    }

    private static List<(DateTime S, DateTime E)> Normalize(List<(DateTime S, DateTime E)> intervals)
        => intervals.Where(x => x.S < x.E).Distinct().OrderBy(x => x.S).ThenBy(x => x.E).ToList();

    /// <summary>部分重叠 = 同层矛盾（完全相同区间已由 Distinct 归并，相邻不算重叠）。</summary>
    private static bool HasPartialOverlap(IEnumerable<(DateTime S, DateTime E)> intervals)
    {
        var list = intervals.Where(x => x.S < x.E).Distinct().OrderBy(x => x.S).ThenBy(x => x.E).ToList();
        for (var i = 1; i < list.Count; i++)
            if (list[i].S < list[i - 1].E) return true;
        return false;
    }

    private static bool ScopeEquals(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ScopeStateIsWork(string? state)
        => string.Equals(state, "WORK", StringComparison.OrdinalIgnoreCase);

    private static int IsoWeekday(DateTime day)
        => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
}
