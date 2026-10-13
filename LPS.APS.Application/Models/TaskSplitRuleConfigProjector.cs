using System.Text.Json;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;

namespace LPS.APS.Application.Models;

/// <summary>
/// Batch Policy（批量策略）投影/编解码纯函数（0号位 2026-10-07 裁决：本轮落码）。无状态、无 IO，供单测钉死：
///   * Project：TaskSplitRuleConfig 实体 → BatchPolicyRuleSnapshot（筛 IsActive + 生效区间，去审计/兼容字段）；
///   * SetRules / ExtractRules：ContentSnapshotJson.BatchPolicy 子块编解码（与 SetupTransitionRules 同轨，零 DDL）。
/// 发布编排在 GovernanceVersionService.PublishParameterSetVersionAsync（读表 active 规则 → Project → 注入第⑧块源）；
/// 装载在 FrozenStrategySnapshotProvider.DeserializeBatchPolicy（ContentSnapshotJson.BatchPolicy 子块 → 第⑧块）。
/// 装载 B-010 三态（3-NEW-P0-04，0号位 再审核 2026-10-10）：缺子块/null/显式空配置 → 空列表（合法未配置）；
/// JSON 损坏 → 显式失败（抛 SetupRuleDataRedLineException，⑧ 为非必填块但损坏≠缺配）；
/// 匹配终端（某 Material+Dept 经 ①②③ 仍无命中）按冻结 §十五 ④ fail-closed，由 1号位 消费侧在 Strategy Snapshot 校验阶段执行，装载端不承载信号位。
/// 开发者：3号位
/// </summary>
public static class TaskSplitRuleConfigProjector
{
    /// <summary>ContentSnapshotJson 子块键名（契约登记 §6.10.5 扩展）。</summary>
    public const string BatchPolicyBlockName = "BatchPolicy";

    /// <summary>
    /// 把 active + 生效区间内规则投影为快照（去审计字段 Id/Created/Updated/IsActive + 兼容字段，仅业务键 + 参数）。
    /// 生效区间以 asOf 判定（发布时传入，默认当前本地时间，R03 与实现 DateTime.Now 对齐），采用**半开区间 [EffectiveFrom, EffectiveTo)**——
    /// P0-04（0号位 审核 2026-10-09）：与治理写路径有效期排重（严格 &lt; 判定相切不重叠）保持一致——
    /// 相切窗口 [10:00,11:00) + [11:00,12:00) 同一瞬间至多一条有效，杜绝投影重复键；
    /// IsActive=false、超出生效窗或 ProductionDepartmentId 为 NULL 的行排除
    /// （v5.1.10 收口④：NULL 部门历史记录不默认为所有部门的生效规则，仅显式部门规则入快照）。
    /// v5.1.10 收口①（2026-10-09 生效）：不再投 MaxOptimizationSplitCount/MaxBatchCandidates（1号位 Solver 技术预算）
    /// 与 BottleneckSplitStrategy/NonBottleneckStrategy（历史兼容列，V1 主链不得消费拆/合批倾向）——
    /// 三个字段恒 null，仅保留物理列历史；BatchPolicyRuleSnapshot 类上仍保留字段以兼容旧快照反序列化与 1号位 消费侧类型。
    /// </summary>
    public static List<BatchPolicyRuleSnapshot> Project(IEnumerable<TaskSplitRuleConfig> rules, DateTime? asOf = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var now = asOf ?? DateTime.Now;

        return rules
            .Where(r => r.IsActive
                && r.ProductionDepartmentId.HasValue
                && (r.EffectiveFrom is null || r.EffectiveFrom <= now)
                && (r.EffectiveTo is null || r.EffectiveTo > now))
            .Select(r => new BatchPolicyRuleSnapshot
            {
                MaterialId = r.MaterialId,
                ProductionDepartmentId = r.ProductionDepartmentId,
                // 2026-10-13：生效窗口**随行下发**（此前被丢弃）。发布侧只按发布时刻筛过一次；
                // 装载期须按**本 Run 生效时点**重判 ⇒ 「发布时有效、Run 时已失效」的行可被判成 INVALID
                //（06 v1.37 :25「业务键含生效时点」；否则该状态在载体上结构性不可达）。
                EffectiveFrom = r.EffectiveFrom,
                EffectiveTo = r.EffectiveTo,
                MinExecutionBatchQty = r.MinExecutionBatchQty,
                MaxExecutionBatchQty = r.MaxExecutionBatchQty,
                PreferredBatchQty = r.PreferredBatchQty,
                AllowSplit = r.AllowSplit,
                AllowMerge = r.AllowMerge,
                // v5.1.10 收口①：MaxOptimizationSplitCount / MaxBatchCandidates / BottleneckSplitStrategy / NonBottleneckStrategy 恒 null，不投快照。
            })
            .ToList();
    }

    /// <summary>
    /// 装载端**按域裁剪**（2号位，2026-10-12 落；依据 0号位《致1、2号位联合业务回复》§三.4
    /// 「由1、2、3号位…设计可行的**最小投影**与性能方案，防止巨大载荷和Run中规则漂移」）：
    /// 只保留 **本 Domain 需求物料**（`MaterialId` ∈ <paramref name="domainMaterialIds"/>）的批量策略行。
    ///
    /// **为什么按 `MaterialId` 单轴裁、不按 `(MaterialId, ProductionDepartmentId)` 精确对裁**：
    ///   单轴裁保留该物料的**全部部门变体**，结构上不可能裁掉一条 1号位 本可命中的行；
    ///   按精确对裁则会丢掉「同物料、其它部门」的行。且正式链上**不存在 NULL 部门行**
    ///   （发布侧 `GetActiveRulesForSnapshotAsync` 已 `ProductionDepartmentId IS NOT NULL`，
    ///   `Project()` 再滤 `HasValue`）⇒ 旧「可空轴不敢裁」的顾虑在本链**无实例**。
    ///
    /// **与 1号位 行为等价（已读其代码核实，非推断）**：`PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy`
    ///   在「集合为空」（`:2932`）与「非空但无命中」（末尾 `return null`）两条路径上**同样返回 `null`**
    ///   ⇒ 裁剪**不改变任何判定结果**，只减少传输量。
    ///
    /// **与 ⑦ 对称**：`SetupTransitionRuleProjector.CropToDomain` 按 `(Dept, StageCode)` 裁；本方法按 `MaterialId` 裁。
    /// </summary>
    public static IReadOnlyList<BatchPolicyRuleSnapshot> CropToDomain(
        IReadOnlyList<BatchPolicyRuleSnapshot> rules,
        IReadOnlyCollection<int> domainMaterialIds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(domainMaterialIds);

        if (rules.Count == 0 || domainMaterialIds.Count == 0)
        {
            return Array.Empty<BatchPolicyRuleSnapshot>();
        }

        var domainKeys = new HashSet<int>(domainMaterialIds);
        return rules.Where(r => domainKeys.Contains(r.MaterialId)).ToList();
    }

    /// <summary>
    /// **三态＋不可用**载体构造（2号位，2026-10-13 落；1号位 明示「最小形态由 2号位 择定」）。
    ///
    /// 依据（逐字）：
    ///   * 06 集成接口 v1.37 <c>:25</c>（G2-T01 补正）——「按当前Run实际要求的全部求解物料（含下阶BOM）、
    ///     <c>MaterialId + ProductionDepartmentId + 运行版本/Scope/生效时点</c>的业务键准确区分并传递
    ///     <c>VALID / MISSING / INVALID</c>三态…**本部门无命中而别的部门存在记录属于MISSING，
    ///     有同键但不满足发布/有效化资格属于INVALID，不得当作MISSING**」；
    ///   * 规则清单 v1.8 <c>B-013</c>——「…2号位向1号位明确传递**状态、真实匹配来源**与 Q_C…」；
    ///   * 0号位 2026-10-13《未命名的Markdown文件3》<c>§2</c>——「接口未传递状态、规则数据装载失败或
    ///     DTO 无法表达三态，**不得直接视为 MISSING**」（第 4 类「不可用（技术失败）」）。
    ///
    /// 判定口径（**纯函数、零 IO、可单测钉死**）：
    /// <list type="table">
    ///   <item><term>Valid</term><description>精确业务键命中，**且**该规则在本 Run 生效时点 <paramref name="asOf"/>
    ///     的生效窗口 <c>[EffectiveFrom, EffectiveTo)</c> 内。</description></item>
    ///   <item><term>Invalid</term><description>**本键有规则但不在生效窗口内** —— 即「发布时有效、本 Run 时已失效
    ///     且未经治理隔离」。这是 INVALID 在本链上**唯一可达**的成因：发布侧 <see cref="Project"/> 已把
    ///     「不满足发布资格」的行拦在版本之外（<c>GetActiveRulesForSnapshotAsync</c> fail-closed 抛异常），
    ///     而「发布后失效」此前因窗口被丢弃而结构性不可判。</description></item>
    ///   <item><term>Missing</term><description>经完整核对后本业务键**确无匹配规则**：⑧块整体为空（完全缺配）／
    ///     本部门无命中而别的部门有记录／精确业务键无命中。三者**来源可区分**（06 <c>:281</c> 允许 <c>Q_C</c> 兜底）。</description></item>
    ///   <item><term>Unavailable</term><description>装载失败或接口未传递状态（<paramref name="unavailableSource"/> 非空，
    ///     或载体整体缺席）。**不得当作 MISSING**。</description></item>
    /// </list>
    ///
    /// **载体只加 <c>(键, 状态, 来源)</c>，不含规则参数** —— 参数仍由 <c>BatchPolicies</c> 承载
    /// （06 <c>:25</c>「不得增加Batch表」「不得把所有物料规则发送Solver」）。
    /// </summary>
    /// <param name="rules">本 Run 冻结 ⑧ 块规则（<c>FrozenStrategySnapshot.BatchPolicy</c>，**裁剪前**全量）。</param>
    /// <param name="businessKeys">本 Run 求解业务键（<c>MaterialId + ProductionDepartmentId</c>）；
    /// <c>ProductionDepartmentId = null</c> 表示该物料的生产部门本 Run 未解析到（业务键不完整）。</param>
    /// <param name="asOf">**本 Run 生效时点**（调用方传 Run 冻结时点，勿用发布时刻）。</param>
    /// <param name="unavailableSource">非空 ⇒ 全部键判 <see cref="BatchPolicyState.Unavailable"/> 并以此为其来源
    /// （「规则数据装载失败」通道）。</param>
    public static List<BatchPolicyStateSnapshot> BuildStates(
        IReadOnlyList<BatchPolicyRuleSnapshot> rules,
        IEnumerable<(int MaterialId, int? ProductionDepartmentId)> businessKeys,
        DateTime asOf,
        string? unavailableSource = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(businessKeys);

        var keys = businessKeys.Distinct().ToList();
        var states = new List<BatchPolicyStateSnapshot>(keys.Count);

        BatchPolicyStateSnapshot Make(int materialId, int? deptId, BatchPolicyState state, string source)
            => new()
            {
                MaterialId = materialId,
                ProductionDepartmentId = deptId,
                State = state,
                MatchSource = source
            };

        if (unavailableSource is not null)
        {
            // 第 4 类：不可用（技术失败）—— 0号位 文件3 §2「不得直接视为 MISSING」。
            foreach (var k in keys)
            {
                states.Add(Make(k.MaterialId, k.ProductionDepartmentId, BatchPolicyState.Unavailable, unavailableSource));
            }

            return states;
        }

        if (rules.Count == 0)
        {
            // 完全缺配（合法 C 桶）：06 :281「按本业务键 MISSING 处理」。
            foreach (var k in keys)
            {
                states.Add(Make(k.MaterialId, k.ProductionDepartmentId, BatchPolicyState.Missing, "⑧块整体为空（完全缺配）"));
            }

            return states;
        }

        var byKey = new Dictionary<(int MaterialId, int DeptId), List<BatchPolicyRuleSnapshot>>();
        var materialsWithAnyRule = new HashSet<int>();
        foreach (var r in rules)
        {
            materialsWithAnyRule.Add(r.MaterialId);
            if (r.ProductionDepartmentId is not int d)
            {
                continue; // NULL 部门行仅历史兼容，不参与正式命中（B-001 / v5.1.10 收口④ / AUD-1-004）。
            }

            if (!byKey.TryGetValue((r.MaterialId, d), out var rows))
            {
                byKey[(r.MaterialId, d)] = rows = [];
            }

            rows.Add(r);
        }

        foreach (var (materialId, deptId) in keys)
        {
            if (deptId is int dept && byKey.TryGetValue((materialId, dept), out var rows))
            {
                var inWindow = rows.Where(r => IsEffectiveAt(r, asOf)).ToList();
                states.Add(inWindow.Count > 0
                    ? Make(materialId, dept, BatchPolicyState.Valid,
                        $"精确命中 (Material={materialId}, Dept={dept})")
                    : Make(materialId, dept, BatchPolicyState.Invalid,
                        $"本键有规则但本 Run 生效时点 {asOf:yyyy-MM-dd HH:mm:ss} 不在生效窗口 [EffectiveFrom, EffectiveTo) 内（发布后失效、未治理隔离）"));
                continue;
            }

            if (deptId is null)
            {
                states.Add(Make(materialId, null, BatchPolicyState.Missing,
                    "业务键不完整（本 Run 未解析到该物料的 ProductionDepartmentId）⇒ 无命中"));
                continue;
            }

            states.Add(materialsWithAnyRule.Contains(materialId)
                ? Make(materialId, deptId, BatchPolicyState.Missing,
                    $"本部门无命中而别的部门存在记录 (Material={materialId}, Dept={deptId})")
                : Make(materialId, deptId, BatchPolicyState.Missing,
                    $"精确业务键无命中 (Material={materialId}, Dept={deptId}；⑧块有行)"));
        }

        return states;
    }

    /// <summary>
    /// 半开区间 <c>[EffectiveFrom, EffectiveTo)</c> 在 <paramref name="asOf"/> 是否生效（null = 无界）。
    /// 与 <see cref="Project"/>、<see cref="FindOverlappingWindowIds"/>、治理写路径
    /// <c>EnsureUniqueEffectiveWindowAsync</c> **同口径**（严格 <c>&lt;</c>，相切不重叠）。
    /// </summary>
    public static bool IsEffectiveAt(BatchPolicyRuleSnapshot rule, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return (rule.EffectiveFrom is null || rule.EffectiveFrom <= asOf)
               && (rule.EffectiveTo is null || rule.EffectiveTo > asOf);
    }

    /// <summary>
    /// 装载端便捷入口：ContentSnapshotJson.BatchPolicy 子块 → 第⑧块快照。
    /// ⚠ 2026-10-13 更正：本注释原写「fail-open：缺失/为空/损坏 → 空列表」，**与实现相反**——
    /// 本方法逐字转调 <see cref="ExtractRules"/>，而后者对 JSON 损坏是 **fail-fast**（抛
    /// <see cref="Exceptions.SetupRuleDataRedLineException"/>，见 <c>:163-169</c> 与单测
    /// <c>TaskSplitRuleConfigProjectorTests.ExtractRules_B010三态_缺失与空配置返回空列表_损坏抛红线异常</c>）。
    /// 「缺失/为空 → 空列表」只对① 缺子块/② 显式空配置成立；「损坏」走异常通道，**不得**被读成 MISSING。
    /// 注意同库 <see cref="SetupTransitionRuleProjector.ProjectFromSnapshot"/> 是**真 fail-open**（损坏→空列表），
    /// 两个投影器语义不同，勿互推。
    /// </summary>
    public static List<BatchPolicyRuleSnapshot> ProjectFromSnapshot(string? contentSnapshotJson)
        => ExtractRules(contentSnapshotJson);

    /// <summary>
    /// R02（0号位 再审核反证 2026-10-10）：找出 (MaterialId, ProductionDepartmentId) 分组内生效窗口重叠的规则 Id
    ///（组内两两判定，O(n²)，组内行数少）。与治理写路径唯一有效期（EnsureUniqueEffectiveWindowAsync）同口径：
    /// 半开区间 [EffectiveFrom, EffectiveTo)、NULL 无界、严格 &lt; 相切不重叠。纯函数、零 DB——发布前对存量行全量判定。
    /// </summary>
    public static IReadOnlyList<int> FindOverlappingWindowIds(IEnumerable<TaskSplitRuleConfig> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var rows = rules.ToList();
        var overlapIds = new HashSet<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var a = rows[i];
                var b = rows[j];
                if (a.MaterialId != b.MaterialId || a.ProductionDepartmentId != b.ProductionDepartmentId)
                {
                    continue;
                }

                if (!Overlaps(a, b))
                {
                    continue;
                }

                overlapIds.Add(a.Id);
                overlapIds.Add(b.Id);
            }
        }

        return overlapIds.ToList();
    }

    /// <summary>半开区间重叠判定（[EffectiveFrom, EffectiveTo)，NULL 无界；严格 &lt; 相切不重叠——与 EnsureUniqueEffectiveWindowAsync SQL 口径一致）。</summary>
    private static bool Overlaps(TaskSplitRuleConfig a, TaskSplitRuleConfig b)
        => (a.EffectiveFrom is null || b.EffectiveTo is null || a.EffectiveFrom < b.EffectiveTo)
           && (b.EffectiveFrom is null || a.EffectiveTo is null || b.EffectiveFrom < a.EffectiveTo);

    /// <summary>
    /// 提取 BatchPolicy 子块（反序列化 BatchPolicyRuleSnapshot 列表）。
    /// B-010 三态（3-NEW-P0-04，0号位 再审核 2026-10-10）：缺子块/null/显式空配置 → 空列表（合法未配置）；
    /// JSON 结构损坏 → 抛 <see cref="Exceptions.SetupRuleDataRedLineException"/>（显式失败，不再静默空列表）。
    /// 冻结 §十五 fail-closed 属匹配终端 ④（某 Material+Dept 经 ①②③ 仍无命中 → Strategy Snapshot 校验失败），
    /// 由 1号位 消费侧执行，本装载端不承载信号位。
    /// </summary>
    public static List<BatchPolicyRuleSnapshot> ExtractRules(string? contentSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            // B-010 三态①：子块缺失 / null / 空串 → 该版本未配置批量策略（合法，非装载失败）。
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(contentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(BatchPolicyBlockName, out var block)
                || block.ValueKind == JsonValueKind.Null)
            {
                // B-010 三态①：子块缺失或为 null → 该版本未配置批量策略（合法，非装载失败）。
                return [];
            }

            // B-010 三态②：显式空配置 "BatchPolicy":[] → 空列表（明确空配置，合法）。
            return block.Deserialize<List<BatchPolicyRuleSnapshot>>(JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // B-010 三态③（3-NEW-P0-04）：JSON 结构损坏 → 显式失败。「完全无匹配」≠「已有配置但损坏」；
            // 损坏子块不再被误当「缺配兜底」消费，版本追溯不失真（调用方 422 数据红线）。
            throw new SetupRuleDataRedLineException(
                $"ContentSnapshotJson.BatchPolicy 子块 JSON 损坏，无法装载批量策略快照：{ex.Message}", ex);
        }
    }

    /// <summary>将 BatchPolicy 规则列表写回 ContentSnapshotJson（保留其它子块，原子替换 BatchPolicy 键）。</summary>
    public static string SetRules(string? contentSnapshotJson, IReadOnlyList<BatchPolicyRuleSnapshot> rules)
    {
        var blocks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(contentSnapshotJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, BatchPolicyBlockName, StringComparison.Ordinal))
                    {
                        blocks[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // 原快照损坏 → 忽略原内容，仅以 BatchPolicy 子块重建（与 Setup 规则写路径同语义）。
            }
        }

        blocks[BatchPolicyBlockName] = JsonSerializer.SerializeToElement(rules, JsonOptions);

        return JsonSerializer.Serialize(blocks, JsonOptions);
    }

    /// <summary>子块序列化选项（属性名原样，不启用缩进——ContentSnapshotJson 单列存储）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}