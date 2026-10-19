using System.Text.Json;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;

namespace LPS.APS.Application.Models;

/// <summary>
/// Batch Policy（批量策略）投影/编解码纯函数（0号位 2026-10-07 裁决：本轮落码）。无状态、无 IO，供单测钉死：
///   * ExtractRules / ProjectFromSnapshot：ContentSnapshotJson.BatchPolicy 子块 → 第⑧块反序列化（装载读取入口）；
///   * CropToDomain / BuildStates：装载端按域裁剪与三态载体；
///   * IsSnapshotEligible / FindOverlappingWindowIds / FindConfirmedDegenerateOneOneOneIds 等：治理校验纯判据。
/// **发布侧已退出旧⑧块注入**（0号位 2026-10-15 终裁；05d6070 已删生产调用，2026-10-17 移除已无调用者的 Project／SetRules 死方法）：
///   新 Run 不再经 <c>ContentSnapshotJson.BatchPolicy</c> 写入/读取超大 Batch 配置，Batch 源改由 2号位 按 <c>TaskSplitRuleConfig</c> 表直读（同批受控切换）。
/// 装载读取在 FrozenStrategySnapshotProvider.DeserializeBatchPolicy（切换前沿用 <c>ContentSnapshotJson.BatchPolicy</c> 子块 → 第⑧块）。
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
    /// **装载侧表直读**（2号位，2026-10-17 切换；PM 2026-10-15 §T-1038 第 1/2/4 条）：
    /// <c>TaskSplitRuleConfig</c> 实体行 → ⑧ 规则载体。
    ///
    /// **与发布侧已退休的 <c>Project</c> 的差异只有一条：不按生效窗过滤。**
    ///   · 发布侧在发布时刻筛过一次窗口 ⇒ 快照里只剩「发布时有效」的行 ⇒「发布后失效」在载体上结构性不可判；
    ///   · 表直读把 <c>EffectiveFrom / EffectiveTo</c> **原样交给 <see cref="BuildStates"/>**，
    ///     由其在 <c>DomainBatchEvaluationTime</c> 上判 `[EffectiveFrom, EffectiveTo)` 半开区间。
    ///     **装载层若提前按窗口剔行**，本该判 <see cref="BatchPolicyState.Invalid"/> 的键会退化成
    ///     <see cref="BatchPolicyState.Missing"/>（PM §T-1038 第 2 条明令禁止）。
    ///
    /// **入集条件与发布侧保持一致**：`IsActive` 且 `ProductionDepartmentId` 非空
    /// （v5.1.10 收口④：NULL 部门行不默认为所有部门生效）。
    ///
    /// `MaxOptimizationSplitCount` / `MaxBatchCandidates` / `BottleneckSplitStrategy` / `NonBottleneckStrategy`
    /// 四列**恒不投**（与发布侧投影同口径；1号位 技术预算归其版本化安全默认，策略列仅追溯）。
    /// </summary>
    public static List<BatchPolicyRuleSnapshot> ProjectFromTableRows(IEnumerable<TaskSplitRuleConfig> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .Where(r => r.IsActive && r.ProductionDepartmentId.HasValue)
            .Select(r => new BatchPolicyRuleSnapshot
            {
                MaterialId = r.MaterialId,
                ProductionDepartmentId = r.ProductionDepartmentId,
                EffectiveFrom = r.EffectiveFrom,
                EffectiveTo = r.EffectiveTo,
                MinExecutionBatchQty = r.MinExecutionBatchQty,
                MaxExecutionBatchQty = r.MaxExecutionBatchQty,
                PreferredBatchQty = r.PreferredBatchQty,
                AllowSplit = r.AllowSplit,
                AllowMerge = r.AllowMerge
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
    ///   （治理写路径 `Validate` 已拒绝新增/更新 NULL 部门规则，装载按 <c>TaskSplitRuleConfig</c> 表直读时仅取部门非空行）
    ///   ⇒ 旧「可空轴不敢裁」的顾虑在本链**无实例**。
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
    ///   * 06 集成接口 **v1.36 <c>:17</c>**（G2-T01 补正）——「按当前Run实际要求的全部求解物料（含下阶BOM）、
    ///     <c>MaterialId + ProductionDepartmentId + 运行版本/Scope/生效时点</c>的业务键准确区分并传递
    ///     <c>VALID / MISSING / INVALID</c>三态…**本部门无命中而别的部门存在记录属于MISSING，
    ///     有同键但不满足发布/有效化资格属于INVALID，不得当作MISSING**」；
    ///   * 规则清单 **v1.7 <c>B-013</c>**——「…2号位向1号位明确传递**状态、真实匹配来源**与 Q_C…」；
    ///   * 0号位 2026-10-13《未命名的Markdown文件3》<c>§2</c>——「接口未传递状态、规则数据装载失败或
    ///     DTO 无法表达三态，**不得直接视为 MISSING**」（第 4 类「不可用（技术失败）」）。
    ///
    /// 判定口径（**纯函数、零 IO、可单测钉死**）：
    /// <list type="table">
    ///   <item><term>Valid</term><description>精确业务键命中，**且**该规则在本 Run 生效时点 <paramref name="asOf"/>
    ///     的生效窗口 <c>[EffectiveFrom, EffectiveTo)</c> 内。**唯一合法来源值 = <see cref="BatchPolicyMatchSource.SnapshotHit"/>**。</description></item>
    ///   <item><term>Invalid</term><description>**本键有规则但不在生效窗口内** —— 即「历史有效、本 Run 时已失效
    ///     且未经治理隔离」。这是 INVALID 在本链上唯一可达的成因：合法配置（治理写路径 <c>Validate</c> 拒绝不合资格行）
    ///     对「<c>DomainBatchEvaluationTime</c> T 落在窗口外」的键判 INVALID，而「已失效」因窗口需按本 Run 时点重判。
    ///     来源值 = <see cref="BatchPolicyMatchSource.OutOfWindow"/>。</description></item>
    ///   <item><term>Missing</term><description>经完整核对后本业务键**确无匹配规则**：⑧块整体为空（完全缺配）／
    ///     本部门无命中而别的部门有记录／精确业务键无命中。三者**来源可区分**
    ///     （<see cref="BatchPolicyMatchSource.NoRuleForMaterial"/>／<see cref="BatchPolicyMatchSource.OtherDeptOnly"/>／
    ///     <see cref="BatchPolicyMatchSource.NoDept"/>；06 §「明确MTS PI来源时的运行时数量硬上限与缺配兜底」允许 <c>Q_C</c> 兜底）。</description></item>
    ///   <item><term>Unavailable</term><description>技术失败。两条成因：① <paramref name="unavailableSource"/> 非空
    ///     （装载失败／接口未传递状态；载体整体缺席由消费侧另判）；② **装载期一致性护栏** —— 本键的规则行
    ///     未过 <see cref="IsSnapshotEligible"/>（2026-10-14 新增，1号位 回执 §一.3 择定「乙」）。
    ///     来源值恒以 <see cref="BatchPolicyMatchSource.LoadError"/> 起头（可带明细）。**不得当作 MISSING**。</description></item>
    /// </list>
    ///
    /// **来源值域**见 <see cref="BatchPolicyMatchSource"/>（9 值封闭，1号位 定稿）。
    ///
    /// **载体只加 <c>(键, 状态, 来源)</c>，不含规则参数** —— 参数仍由 <c>BatchPolicies</c> 承载
    /// （06 v1.36 <c>:17</c>「不得增加Batch表」「不得把所有物料规则发送Solver」）。
    /// </summary>
    /// <param name="rules">本 Run 冻结 ⑧ 块规则（<c>FrozenStrategySnapshot.BatchPolicy</c>，**裁剪前**全量 ——
    /// 一致性护栏须看到全部行，故**不得传裁剪后的列表**）。</param>
    /// <param name="businessKeys">本 Run 求解业务键（<c>MaterialId + ProductionDepartmentId</c>）；
    /// <c>ProductionDepartmentId = null</c> 表示该物料的生产部门本 Run 未解析到（业务键不完整）。</param>
    /// <param name="asOf">**本 Run 生效时点**（调用方传 Run 冻结时点，勿用发布时刻）。</param>
    /// <param name="unavailableSource">非空 ⇒ 全部键判 <see cref="BatchPolicyState.Unavailable"/>
    /// （「规则数据装载失败」通道）。**该值已非「来源值」本身**：产出侧固定以常量
    /// <see cref="BatchPolicyMatchSource.LoadError"/> 锚定来源值，本参数只作**随行明细**
    /// （装载失败的具体原因只有调用方知道、且从条目上不可复原 ⇒ 不静默丢弃）。</param>
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
            // MatchSource 以常量 `LOAD_ERROR` 锚定（值域封闭）；具体原因**只有调用方知道、且从条目上不可复原**
            // ⇒ 作为明细随行带出，不静默丢弃（1号位 2026-10-14 回执 §三 明示两种形态都能接，此处取「锚定＋不丢明细」）。
            var unavailableMatchSource = string.IsNullOrWhiteSpace(unavailableSource)
                ? BatchPolicyMatchSource.LoadError
                : $"{BatchPolicyMatchSource.LoadError}: {unavailableSource}";
            foreach (var k in keys)
            {
                states.Add(Make(k.MaterialId, k.ProductionDepartmentId, BatchPolicyState.Unavailable, unavailableMatchSource));
            }

            return states;
        }

        if (rules.Count == 0)
        {
            // 完全缺配（合法 C 桶）：06 §「明确MTS PI来源时的运行时数量硬上限与缺配兜底」「按本业务键 MISSING 处理」。
            foreach (var k in keys)
            {
                states.Add(Make(k.MaterialId, k.ProductionDepartmentId, BatchPolicyState.Missing,
                    BatchPolicyMatchSource.NoRuleForMaterial));
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
                // ── 装载期一致性护栏（1号位 2026-10-14 回执 §一.3 择定「乙」；粒度 = **逐键**）──────────────
                // 版本内容里**本键**存在不合格行（Preferred 缺失/≤0、Min/Max 负值、Min > Max、Preferred 越界）
                // ⇒ 本键的规则真相不可信 ⇒ 判 `Unavailable`（1号位 侧 → Unknown → Fail Closed → **不取 Q_C**）。
                // · **逐键**：不因他键的不合格行把整域键全判 `Unavailable`（那是过度阻断，且与其 C-1「清单覆盖全量业务键」的粒度不一致）。
                // · **不降级成 `Missing`**：那是「业务无效」的出口，会把技术失败当业务无效处置（0号位 文件3 §2 所禁）。
                // · 合规治理路径上**零触发** —— 治理写路径 `Validate` 已拒绝不合资格行；本护栏只兜 `seed`／直写表/装载
                //   这类**绕过治理校验**的载荷（1号位 回执 §一.3 甲 行所承认的那条缝）。
                if (rows.Any(r => !IsSnapshotEligible(r.MinExecutionBatchQty, r.MaxExecutionBatchQty, r.PreferredBatchQty)))
                {
                    states.Add(Make(materialId, dept, BatchPolicyState.Unavailable,
                        $"{BatchPolicyMatchSource.LoadError}: 本键存在不合格规则行（Preferred 缺失/≤0、Min/Max 负值或 Min > Max）⇒ 装载期一致性护栏 Fail Closed"));
                    continue;
                }

                var inWindow = rows.Where(r => IsEffectiveAt(r, asOf)).ToList();
                states.Add(inWindow.Count > 0
                    ? Make(materialId, dept, BatchPolicyState.Valid, BatchPolicyMatchSource.SnapshotHit)
                    : Make(materialId, dept, BatchPolicyState.Invalid, BatchPolicyMatchSource.OutOfWindow));
                continue;
            }

            if (deptId is null)
            {
                states.Add(Make(materialId, null, BatchPolicyState.Missing, BatchPolicyMatchSource.NoDept));
                continue;
            }

            states.Add(materialsWithAnyRule.Contains(materialId)
                ? Make(materialId, deptId, BatchPolicyState.Missing, BatchPolicyMatchSource.OtherDeptOnly)
                : Make(materialId, deptId, BatchPolicyState.Missing, BatchPolicyMatchSource.NoRuleForMaterial));
        }

        return states;
    }

    /// <summary>
    /// 半开区间 <c>[EffectiveFrom, EffectiveTo)</c> 在 <paramref name="asOf"/> 是否生效（null = 无界）。
    /// 与 <see cref="FindOverlappingWindowIds"/>、治理写路径
    /// <c>EnsureUniqueEffectiveWindowAsync</c> **同口径**（严格 <c>&lt;</c>，相切不重叠）。
    /// </summary>
    public static bool IsEffectiveAt(BatchPolicyRuleSnapshot rule, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return (rule.EffectiveFrom is null || rule.EffectiveFrom <= asOf)
               && (rule.EffectiveTo is null || rule.EffectiveTo > asOf);
    }

    /// <summary>
    /// **发布/有效化资格**判据（唯一真相）。原先只存在于
    /// <c>TaskSplitRuleConfigGovernanceService.IsSnapshotEligible</c>（发布闸，收 <c>TaskSplitRuleConfig</c> 实体），
    /// 2026-10-14 提到此处**按三个数量参数**表达，使**发布闸**与**装载期一致性护栏**（<see cref="BuildStates"/>）
    /// 共用同一份判据 —— 两条链一旦各写一份，就会「发布闸说合格、装载期说不合格」而互相打脸。
    ///
    /// 规则（与治理写路径 <c>Validate</c> 同口径）：
    /// <list type="bullet">
    ///   <item>Min / Max 若有值须 ≥ 0；</item>
    ///   <item>Preferred **必填**且 &gt; 0；</item>
    ///   <item>Min ≤ Max（两者都有值时）；</item>
    ///   <item>Preferred ∈ [Min, Max]（相应边界有值时）。</item>
    /// </list>
    /// </summary>
    public static bool IsSnapshotEligible(decimal? minExecutionBatchQty, decimal? maxExecutionBatchQty, decimal? preferredBatchQty)
    {
        // 3-NEW-P0-03（0号位 再审核）：Min/Max 非负校验——存量负值行阻断发布，严禁默默当有效。
        if (minExecutionBatchQty.HasValue && minExecutionBatchQty.Value < 0)
        {
            return false;
        }

        if (maxExecutionBatchQty.HasValue && maxExecutionBatchQty.Value < 0)
        {
            return false;
        }

        if (!preferredBatchQty.HasValue || preferredBatchQty.Value <= 0)
        {
            return false;
        }

        if (minExecutionBatchQty.HasValue && maxExecutionBatchQty.HasValue
            && maxExecutionBatchQty.Value < minExecutionBatchQty.Value)
        {
            return false;
        }

        if (minExecutionBatchQty.HasValue && preferredBatchQty.Value < minExecutionBatchQty.Value)
        {
            return false;
        }

        return !maxExecutionBatchQty.HasValue || preferredBatchQty.Value <= maxExecutionBatchQty.Value;
    }

    /// <summary>
    /// **B-006 已确认异常 `1/1/1` 检出**（Min = Max = Preferred = 1，三者**都显式给出**且同为 1）。
    ///
    /// 依据逐字（规则清单 **B-006**，**生效**）：「`PreferredBatchQty`业务正数必填，经济软Min/Max及Preferred
    /// 数值须自洽并**由3号位治理**和授权车间维护。**发布数据质量校验不等于Solver对生产批量经济区间偏离的
    /// 硬拒排**；历史NULL物理兼容，**已确认异常`1/1/1`经核实后治理防重入**」。
    ///
    /// ⇒ 两点**必须同时成立**：
    ///   <list type="number">
    ///     <item>`1/1/1` 是**经济区间偏离**，**不是**自洽性违规 —— 它本身满足 Min ≤ Max 且 Preferred ∈ [Min, Max]。
    ///       故**不得**并入 <see cref="IsSnapshotEligible"/>：并入会让发布闸对全量存量行一次性 fail-closed，
    ///       与 B-006「不等于硬拒排」直接冲突。</item>
    ///     <item>它是**已确认异常**、须「**经核实后**治理」⇒ **必须可检出**。本判据只回答「是不是」，
    ///       **不决定**处置（处置 ＝ 3号位 治理；本类只提供可见性）。</item>
    ///   </list>
    /// </summary>
    public static bool IsConfirmedDegenerateOneOneOne(
        decimal? minExecutionBatchQty, decimal? maxExecutionBatchQty, decimal? preferredBatchQty)
        => minExecutionBatchQty == 1m && maxExecutionBatchQty == 1m && preferredBatchQty == 1m;

    /// <summary>
    /// 找出 <see cref="IsConfirmedDegenerateOneOneOne"/> 命中的行 Id（发布时告警用；纯函数、零 DB，单测可钉死）。
    /// 与 <see cref="FindOverlappingWindowIds"/> 同形：**只报 Id，不决定处置**。
    /// </summary>
    public static IReadOnlyList<int> FindConfirmedDegenerateOneOneOneIds(IEnumerable<TaskSplitRuleConfig> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules
            .Where(r => IsConfirmedDegenerateOneOneOne(r.MinExecutionBatchQty, r.MaxExecutionBatchQty, r.PreferredBatchQty))
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// `ContentSnapshotJson` 顶层子块键名白名单（**8 块**）。
    /// 来源：<c>FrozenStrategySnapshotProvider</c>（①–⑥ 逐字键名）＋ `EnsureRuleSetNormalized`（⑦）
    /// ＋ `EnsureParameterSetNormalized`／<see cref="BatchPolicyBlockName"/>（⑧）。
    /// **2026-10-14 已对全库实测**：RSV 4 份 ＋ PSV 4 份有内容，其顶层键**恰好**为下列 8 个，
    /// **零未知键** ⇒ 白名单完整，本检测器不会对现行数据产生噪音。
    /// </summary>
    private static readonly HashSet<string> KnownSnapshotBlockNames = new(StringComparer.Ordinal)
    {
        "DemandPriority", "Lock", "Supply", "Procurement",
        "SolverStrategy", "CandidateGuardrail", "SetupTransitionRules", "BatchPolicy",
    };

    /// <summary>
    /// 找出 `ContentSnapshotJson` 顶层**不在已知 8 块键名内**的键（发布/装载时告警用；纯函数、零 DB，单测可钉死）。
    ///
    /// **为什么需要**：⑦⑧ 两个投影器（本类 <see cref="ExtractRules"/> 与
    /// <see cref="SetupTransitionRuleProjector.ExtractRules"/>）都是**单点 <c>TryGetProperty</c>** ——
    /// 键名拼错时与「合法未配置」**不可区分**，一律静默返回空列表。
    /// 对照：①–⑥ 块在 <c>FrozenStrategySnapshotProvider</c> 里键缺失/拼错会 **fail-fast** 抛
    /// <see cref="InvalidOperationException"/> ⇒ 唯独 ⑦⑧ 是「拼错 = 永远发现不了」。
    /// 台账 §T-1014h 已记：**8 块键名一个都没冻结**（全库 grep 冻结文档 `SetupTransitionRules` 零命中）。
    ///
    /// 与 <see cref="FindOverlappingWindowIds"/>／<see cref="FindConfirmedDegenerateOneOneOneIds"/> 同形：
    /// **只报键名，不决定处置**。⚠ 调用方**不得**据此抛异常 —— 8 块键名未冻结，未知键**可能**是
    /// 3号位 合法新增的第 9 块，本函数只提供可见性。
    /// </summary>
    public static IReadOnlyList<string> FindUnknownSnapshotBlockNames(string? contentSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(contentSnapshotJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                // 根不是对象（如数组/标量）：不是 8 块形态，但也不是「未知键」——不承载该信号。
                return [];
            }

            return doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !KnownSnapshotBlockNames.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            // 快照整体损坏由各子块投影器按既有 fail-fast 语义处置（见 <see cref="ExtractRules"/>），
            // 本函数不重复承载该信号（否则同一损坏会被报两次、且第二次语义不同）。
            return [];
        }
    }

    /// <summary>
    /// 装载端便捷入口：ContentSnapshotJson.BatchPolicy 子块 → 第⑧块快照。
    /// ⚠ 2026-10-13 更正：本注释原写「fail-open：缺失/为空/损坏 → 空列表」，**与实现相反**——
    /// 本方法逐字转调 <see cref="ExtractRules"/>，而后者对 JSON 损坏是 **fail-fast**（抛
    /// <see cref="Exceptions.SetupRuleDataRedLineException"/>，见 <c>:163-169</c> 与单测
    /// <c>TaskSplitRuleConfigProjectorTests.ExtractRules_B010三态_缺失与空配置返回空列表_损坏抛红线异常</c>）。
    /// 「缺失/为空 → 空列表」只对① 缺子块/② 显式空配置成立；「损坏」走异常通道，**不得**被读成 MISSING。
    /// ⚠ 2026-10-14：同库 <see cref="SetupTransitionRuleProjector.ProjectFromSnapshot"/> 已**改为同轨**
    ///（仅 JSON 损坏抛 <see cref="Exceptions.SetupRuleDataRedLineException"/>；缺子块/null/显式空配置仍空列表）
    /// ⇒ ⑦/⑧ 两个投影器在**损坏语义上现已一致**。此前本条注释写 ⑦ 是「真 fail-open…勿互推」，
    /// 该差异已按 1号位 2026-10-13《BatchPolicy三态载体择定…回执》§五「应同轨」消除。
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

    /// <summary>子块序列化选项（属性名原样，不启用缩进——ContentSnapshotJson 单列存储）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}