namespace LPS.APS.Scheduling.Solvers;

using LPS.APS.Core.Dto;

/// <summary>
/// Setup 换型规则解析骨架（v1.2 收口版规则模型）。
///
/// 【口径来源】《APS V1 Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916_最终收口版》：
/// - §1.1/§20.2 废止属性式换型（Mold/Tool/Material/Color SetupAttribute），旧 SelectBestCandidate/CalculateSetupTime 已删除；
/// - §2/§3 换型键 =「当前 Task 自身 OperationCode + 当前 Resource + 前产品 FromMaterial + 后产品 ToMaterial」，方向性，
///   不读上一 Task 工序（废止 FromOperation/ToOperation 模型）；
/// - §5 命中三层：EXACT 产品对 → DEFAULT 工序+设备 → 无规则 0 分钟 + 记「规则缺失」解释；
/// - §6 同产品 A→A 默认 0 分钟，允许显式规则覆盖；
/// - §1.2/§20.3 禁止 RoutingOperation.SetupTime / MES SetupTime fallback；
/// - §14.3/0号位 20260917 裁决：「生产日」= Resource Calendar 连续可用生产窗口（不按自然日切，跨零点不断开）；
///   无上一产品 → 初始设备状态 Setup=0（INITIAL_SETUP_STATE 追踪说明，非 ReasonCode），与「规则缺失降级」严格分开；
/// - §8.2 裁决项1 已由 0号位 终裁（20260918）：0号位 只冻结红线——**必须有界搜索、不得因 Setup 搜索破坏
///   夜间约 15 分钟总性能目标**；具体预算/邻域尝试次数由 1号位 按性能标定提「默认值+允许范围」
///   （标定基准：10 万 Task / 90 天 / 夜间约 15 分钟），3号位 纳入 ParameterSetVersion 版本化治理；
///   500（有界搜索预算）/50（最大邻域尝试）仅作 1号位 测试初值，不得写成 0号位 冻结值；
///   「单生产日保护阈值」删除（语义未定义；未来确需时由 1号位 提五件套：中文技术含义/触发条件/
///   为什么现有总预算不足/建议默认值/性能测试依据，再由 3号位 纳入治理，不提前造字段）；
///   （旧 SetupLookAheadSize=5 / DefaultSetupMinutes=30 是已废止旧锚，不得沿用——3号位 20260917 §三）。
///
/// 【数据通道】r13376（3号位 2026-09-18）已落地 `SetupTransitionRuleSnapshot`（FrozenStrategySnapshot 第⑦块，
/// 随 RuleSetVersionId 锚冻结）；2号位 按 Domain 裁剪投影进 DomainSolveRequest 后，Phase1 经
/// <see cref="BuildRuleLookups"/> 适配为字典传入 <see cref="ResolveSetup"/>；本骨架不自建规则读取、不读 3号位 数据库。
///
/// 【当前状态】骨架 + 纯函数 + 快照适配，五阶段流程尚未接线（接线依赖 2号位 把第⑦块透传进 DomainSolveRequest）。
/// 夜间 FULL 接线时按 0号位 红线设计：有界搜索（500/50 测试初值起步）+ 总耗时不破 15 分钟夜间目标，
/// 标定后把「默认值+允许范围」回传 3号位 入 ParameterSetVersion。
/// </summary>
public class SetupOptimizer
{
    /// <summary>
    /// EXACT 规则键：当前工序 + 当前设备 + 前产品 + 后产品（方向性，(A,B) ≠ (B,A)）。
    /// ProductionDepartment/Stage 作为规则裁剪与唯一性上下文由 2号位/3号位 治理，不进运行时查找键（v1.2 §19.3）。
    /// </summary>
    public readonly record struct SetupExactKey(string OperationCode, int ResourceId, int FromMaterialId, int ToMaterialId);

    /// <summary>DEFAULT 规则键：当前工序 + 当前设备（v1.2 §四）。</summary>
    public readonly record struct SetupDefaultKey(string OperationCode, int ResourceId);

    /// <summary>Setup 解析结果类型（0号位 20260917 Q4 裁决：初始状态与规则缺失严格分开，不得合并分支）。</summary>
    public enum SetupOutcome
    {
        /// <summary>命中 EXACT 产品转换规则（§5 第一优先）。</summary>
        ExactHit,
        /// <summary>命中当前工序+设备默认规则（§5 第二优先）。</summary>
        DefaultHit,
        /// <summary>同产品连续 A→A 无显式规则，默认 0 分钟（§6）。</summary>
        SameProductZero,
        /// <summary>无上一产品 → 初始设备状态，Setup=0，记 INITIAL_SETUP_STATE 追踪说明（§14.3，非 ReasonCode）。</summary>
        InitialState,
        /// <summary>有前后产品但 EXACT/DEFAULT 均缺失 → 规则缺失降级 Setup=0，必须记「换型规则缺失」解释（§5 第三优先）。</summary>
        RuleMissing
    }

    /// <summary>Setup 追踪统一 TraceType（0号位 20260917 正式回复 §5.1：TIME_CALCULATION）。</summary>
    public const string TraceType = "TIME_CALCULATION";

    /// <summary>初始设备状态解释类型符号锚（ExplainTrace 内部类型，非 ScheduleExplanationFact.ReasonCode——0号位 裁决项4 §5.1，INFO 级）。</summary>
    public const string InitialSetupStateType = "INITIAL_SETUP_STATE";

    /// <summary>换型规则缺失 0 分钟兜底解释类型符号锚（0号位 裁决项4 §5.3，WARNING 级；同时进 4号位「换型规则缺失/0分钟兜底」数据质量查询）。
    /// ContextData 需含 productionDepartmentId/stageCode/operationCode/resourceId/fromMaterialId/toMaterialId/setupMinutes——
    /// Dept/Stage 不在运行时查找键内（2号位 已按 Domain 裁剪），由调用方（Phase 接线时）从 Task 上下文补齐。</summary>
    public const string RuleMissingZeroFallbackType = "SETUP_RULE_MISSING_ZERO_FALLBACK";

    /// <summary>Setup 解析结果：分钟数 + 命中类型 + 追踪三元组（TraceLevel/ExplanationType/TraceMessage，按 0号位 §五口径；ExplainTrace 载体待 2号位 DTO 落地后写入）。</summary>
    public readonly record struct SetupResolution(
        decimal SetupMinutes,
        SetupOutcome Outcome,
        string? TraceMessage,
        string? TraceLevel = null,
        string? ExplanationType = null);

    /// <summary>
    /// 解析当前 Task 的换型时间（v1.2 §5 三层确定性命中，无额外 fallback）。
    /// </summary>
    /// <param name="operationCode">当前要排的 Task 自身小工序（不是上一 Task 的工序）。</param>
    /// <param name="resourceId">当前候选/实际设备。</param>
    /// <param name="fromMaterialId">该设备上一相邻 Task 的产品；null = 无可追溯上一产品（初始设备状态）。</param>
    /// <param name="toMaterialId">当前 Task 产品。</param>
    /// <param name="exactRules">EXACT 产品转换规则（2号位 装载，Phase1 适配传入）。</param>
    /// <param name="defaultRules">DEFAULT 工序+设备规则（同上）。</param>
    public SetupResolution ResolveSetup(
        string operationCode,
        int resourceId,
        int? fromMaterialId,
        int toMaterialId,
        IReadOnlyDictionary<SetupExactKey, decimal> exactRules,
        IReadOnlyDictionary<SetupDefaultKey, decimal> defaultRules)
    {
        // 初始设备状态：无上一产品 → 不存在转换关系，Setup=0（不人为构造虚拟前产品，§0.1-4/§14.3）。
        // 追踪规格：0号位 正式回复 §5.1（TraceType=TIME_CALCULATION / TraceLevel=INFO / INITIAL_SETUP_STATE）。
        if (fromMaterialId is null)
        {
            return new SetupResolution(0m, SetupOutcome.InitialState,
                "当前资源无可追溯上一产品，按初始设备状态处理，Setup=0。",
                "INFO", InitialSetupStateType);
        }

        var from = fromMaterialId.Value;

        // 第一优先：EXACT 产品对（同产品 A→A 显式规则可覆盖默认 0，§6）。
        if (exactRules.TryGetValue(new SetupExactKey(operationCode, resourceId, from, toMaterialId), out var exactMinutes))
        {
            return new SetupResolution(exactMinutes, SetupOutcome.ExactHit, null);
        }

        // 同产品连续且无显式规则 → 0 分钟（§6；只比较前后产品，与上一工序无关）。
        if (from == toMaterialId)
        {
            return new SetupResolution(0m, SetupOutcome.SameProductZero, null);
        }

        // 第二优先：当前工序+当前设备默认规则（§四）。
        // 追踪规格：0号位 正式回复 §5.2（正常 fallback，INFO 轻量追踪；解释类型符号待 1/2/3号位 三方统一）。
        if (defaultRules.TryGetValue(new SetupDefaultKey(operationCode, resourceId), out var defaultMinutes))
        {
            return new SetupResolution(defaultMinutes, SetupOutcome.DefaultHit,
                "未命中明确产品转换规则，使用当前工序/设备默认换型时间。",
                "INFO", null);
        }

        // 第三优先：无规则 → 0 分钟 + 必须记录缺失解释（§5；禁止任何额外 fallback）。
        // 追踪规格：0号位 正式回复 §5.3（WARNING + SETUP_RULE_MISSING_ZERO_FALLBACK，进 4号位 数据质量查询）。
        return new SetupResolution(0m, SetupOutcome.RuleMissing,
            $"当前部门/Stage/工序[{operationCode}]/设备[{resourceId}]/前后产品[{from}→{toMaterialId}]未维护Setup规则，本次按0分钟计算。",
            "WARNING", RuleMissingZeroFallbackType);
    }

    /// <summary>
    /// 把 2号位/3号位 冻结的 <see cref="SetupTransitionRuleSnapshot"/> 列表（FrozenStrategySnapshot 第⑦块，
    /// r13376 落地）适配为运行时 EXACT/DEFAULT 查找字典。
    /// - 只取 IsActive 语义已由装载端保证（快照只含有效规则），此处按 RuleType 分流；
    /// - EXACT 要求 From/To 均有值，DEFAULT 忽略产品字段；
    /// - 同键重复时取第一条（唯一性由 3号位 发布前冲突校验保证，Solver 不随机选规则——v1.2 §十）。
    /// </summary>
    public static (Dictionary<SetupExactKey, decimal> Exact, Dictionary<SetupDefaultKey, decimal> Default) BuildRuleLookups(
        IEnumerable<SetupTransitionRuleSnapshot>? rules)
    {
        var exact = new Dictionary<SetupExactKey, decimal>();
        var def = new Dictionary<SetupDefaultKey, decimal>();

        if (rules is null) return (exact, def);

        foreach (var rule in rules)
        {
            if (string.Equals(rule.RuleType, "EXACT", StringComparison.Ordinal))
            {
                if (rule.FromMaterialId is null || rule.ToMaterialId is null)
                    continue;   // EXACT 缺产品 → 无效行，防御性跳过

                exact.TryAdd(new SetupExactKey(rule.OperationCode, rule.ResourceId, rule.FromMaterialId.Value, rule.ToMaterialId.Value),
                    rule.SetupMinutes);
            }
            else if (string.Equals(rule.RuleType, "DEFAULT", StringComparison.Ordinal))
            {
                def.TryAdd(new SetupDefaultKey(rule.OperationCode, rule.ResourceId), rule.SetupMinutes);
            }
            // 其它 RuleType 值不识别，防御性忽略。
        }

        return (exact, def);
    }

    /// <summary>
    /// 生产日窗口切分（0号位 20260917 Q1 裁决）：同一 Resource 的日历 Slot 按 Start 排序，
    /// 取 IsAvailable=true 的连续无间断区间合并为一个「连续可用生产窗口」；跨自然日零点不断开；
    /// 遇停机/非可用/班次断点结束当前窗口。生产日只是搜索边界，不重置设备上一产品状态（v1.2 §14.3）。
    /// </summary>
    /// <param name="slots">单一 Resource 的日历 Slot（Start, End, IsAvailable）。</param>
    /// <returns>合并后的连续生产窗口列表（按时间升序，空输入返回空列表）。</returns>
    public static List<(DateTime Start, DateTime End)> BuildProductionWindows(
        IEnumerable<(DateTime Start, DateTime End, bool IsAvailable)> slots)
    {
        var windows = new List<(DateTime Start, DateTime End)>();

        foreach (var slot in slots.Where(s => s.IsAvailable).OrderBy(s => s.Start))
        {
            if (windows.Count > 0 && windows[^1].End == slot.Start)
            {
                // 相邻且时间无间断 → 并入当前窗口（含跨零点场景）。
                windows[^1] = (windows[^1].Start, slot.End);
            }
            else if (windows.Count > 0 && slot.Start < windows[^1].End)
            {
                // 重叠可用段 → 扩展窗口末端（容错日历数据重叠）。
                if (slot.End > windows[^1].End)
                    windows[^1] = (windows[^1].Start, slot.End);
            }
            else
            {
                // 间断（停机/班次断点）→ 开新窗口。
                windows.Add((slot.Start, slot.End));
            }
        }

        return windows;
    }
}
