using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 「本次 BOM 上下文下，某个**具体需求**的一条有序 Stage 链」的**纯收敛器**（零 IO、零日志、可单测）。
///
/// 【它解决什么】
///   装载侧原实现把同物料的 ROOT 行 ∪ EDGE 行、跨父件/跨边/跨请求一起 `GROUP BY ChildMaterialCode, StageCode`
///   取 `MIN(StageSeq)` ⇒ 得到一个**集合**再按 Seq 排成「链」。那是**静默并集**，直接违反冻结明文：
///     · `07 v5.1.13 :416`「1号位消费查询必须显式按 `StageScopeType` 区分（**不得混查EDGE+ROOT**）」；
///     · `07 v5.1.13 :723`「**消费方**：1号位**必须按StageScopeType区分查询**」；
///     · `07 v5.1.13 :722`／`:415`「多条不一致**取最长路径+记WARNING**（**不静默并集**）」。
///   并集在装载第一步就把 `StageScopeType` 这个区分轴销毁了 —— 下游（1号位）无论怎么查都**无法**再区分，
///   所以修必须在**载体侧**，不能推给消费侧。
///
/// 【隔离键＝「请求＋边实例」】（0号位 2026-10-16 第十二轮 J12-02／2026-10-17 第十四轮 J14-01/02）
///   实例键 = `(StageScopeType, ParentMaterialCode, WorksetId, BOMNO)`，逐行取自 `APS_BOM_STAGE_PATH_RAW`。
///   ⚠ **实例键只解决「不被合并」，不解决「该用哪个请求的链」**。后者必须由调用方**先按需求选行**：
///     调用方持有 `需求 → OrderId → OrderBomRequestLink(ResolvedBOMNO, RepWorksetId)` 与
///     `BOMNO + 子件 → 父件集`（`BomSnapshot`），把它们放进 <see cref="SelectionContext"/>。
///   本收敛器在**建实例之前**先按选择键收窄行集（`BomNo` → `WorksetId`（ROOT）→ `ParentMaterialCode`（EDGE）），
///   因此「同物料跨请求取最长」这一类误选在结构上不再可达：
///     · 实测（2026-10-17，当前批次）：同一 `(ChildMaterialCode, BOMNO)` 的 ROOT 链**恒为单条**（67,915 物料零例外）；
///     · 同一 `(ChildMaterialCode, BOMNO)` 存在多个父件的有 30,607 组 ⇒ EDGE 必须保留父边维度。
///
/// 【选哪条：按需求角色定 scope，不是「谁长选谁」】
///   调用方给出该**需求**是否为本订单的顶层根需求：
///     · 否（作为子件被父件消耗，**与其 `ChildRequiredStageCode` 是否为 NULL 无关**）⇒ 取 **EDGE** 链
///       —— `07 :724` 第二式「`WHERE ParentMaterialCode=@Parent AND ChildMaterialCode=@Child AND StageScopeType='EDGE'`」；
///       `ChildRequiredStageCode=NULL` 只表示「子件必须**全工艺**完成后才可供给父件」，是**截断点**语义（`RequiredStageCode` 留 null），
///       **不是 ROOT 身份**（J14-03）。
///     · 是（须自身完工）⇒ 取 **ROOT** 链 —— `07 :724` 第一式「`WHERE ChildMaterialCode=@Mat AND StageScopeType='ROOT'`」。
///   **首选 scope 无行 ⇒ 返回「无链」（`Stages` 空 + `MissingPreferredScope=true`），绝不改用另一 scope 顶上**
///   （J14-01：跨 scope 替代＝把另一类业务路径塞给 Solver）。缺链的合法降级由调用方交 `StageLeadTime`／未排真相处理。
///
/// 【两条不同情况，故意不统一】（J12-02 逐字：「不得统一」）
///   · **ROOT 同一请求内候选多链** ⇒ 冻结明文就是「取最长+记WARNING」（`:722`／`:415`），`RejectedChainKeys` 非空即告警。
///     **前提是身份为 `Anchored`**（真实请求键已等值收窄）—— 见下方准入条件。
///   · **EDGE 同子件不同父边** ⇒ 这些链按原文口径**各自独立**（`07 :724` 的 EDGE 式本来就带 `@Parent`），
///     不属于「取最长」那一类。本收敛器**不假装**它们是同一件事：仍单独报 `RejectedChainKeys`，
///     由调用方用**另一条告警**说明「超出本载体（每物料一条）表达能力」。
///
/// 【准入条件：身份未证实 ⇒ 候选必须完全一致】（0号位 2026-10-17 第十六轮 J16-01）
///   非 `Anchored` 身份下若候选链内容**不一致**，返回 `IdentityAmbiguous=true` 的**无链**结果。
///   理由：同 `BOMNO`／同父边不能证明不同 `RequestDetailId` 的路径内容等价，取最长＝把别的请求的路径交给 Solver。
///   空 `WorksetId` 的历史兼容**仅在候选有序内容完全一致时**成立，且恒标非请求级可信。
///
/// 【不变量】产出恒为「一条真实链或零条」：**绝不合成**（不并集、不补步、不改 Seq）。
/// </summary>
public static class EffectiveStageChainResolver
{
    /// <summary>根产品完工路径的 `StageScopeType` 取值（`07 :414`／`08` DDL 明文）。</summary>
    public const string ScopeRoot = "ROOT";

    /// <summary>子件供给路径的 `StageScopeType` 取值。</summary>
    public const string ScopeEdge = "EDGE";

    /// <summary>一条 StagePath 行实例（`APS_BOM_STAGE_PATH_RAW` 的一行，投影只取隔离与排序所需列）。</summary>
    public sealed record InstanceRow(
        string? ScopeType,
        string? ParentMaterialCode,
        long? WorksetId,
        string? BomNo,
        int StageSeq,
        string? StageCode);

    /// <summary>
    /// **请求身份的可信度**（0号位 2026-10-17 第十五轮 J15-01）。调用方必须区分三种状态，不得把
    /// 「仅按 BOMNO 回退」描述为「已实现请求隔离」，也不得把 `MAX(SyncedAt)` 之类的时间戳当成身份真相。
    /// </summary>
    public enum RequestIdentityState
    {
        /// <summary>
        /// 权威请求锚点齐全并**已按真实请求键等值收窄**。
        /// 当前只有 ROOT 可达（`WorksetId == RepWorksetId` 实际生效）。
        /// </summary>
        Anchored = 0,

        /// <summary>
        /// EDGE 已按「本请求 BOMNO ＋ 本请求内该子件的父边集」收窄。
        /// ⚠ 父边只是**边维度**的收窄，不等于请求实例等值 —— `08 :459-463` 定 EDGE 的 `WorksetId` 是
        /// 「该行所属具体 BOM 边实例」，与请求级 `RepWorksetId` 不同命名空间，本人无法据此证明请求实例。
        /// ⇒ 本状态**非请求级可信**（0号位 2026-10-17 第十六轮 J16-01：不得把「父边已过滤」称为 Anchored）。
        /// </summary>
        ParentEdgeScoped = 1,

        /// <summary>
        /// 有 `BOMNO` 但缺第二层锚点（ROOT 的 `WorksetId` 全为 NULL／EDGE 的父边集为空）。
        /// 此时只能做**内容等价兼容**：同一 BOMNO ＋ 同一角色（scope）已被证明，ROOT 侧父边恒 NULL、EDGE 侧父边
        /// 由行自带 —— 但**请求实例身份未验证**，调用方须以本状态留痕，不得声称已隔离到具体请求。
        /// </summary>
        UnverifiedRequestIdentity = 2,

        /// <summary>三键全空（无 `BOMNO`、无 `WorksetId`、无父边）⇒ 无权威身份，不得产出被当作权威的链。</summary>
        NoAnchor = 3
    }

    /// <summary>
    /// **需求侧选择键**（J14-02）。由调用方从 `LogicalProductionDemand` 与其订单的 `OrderBomRequestLink` 解析后传入。
    /// 三个分量各自独立生效；`BomNo` 为空 ＝ 无权威请求身份（J15-01 状态 c）。
    /// </summary>
    /// <param name="BomNo">本次需求所属请求的 `ResolvedBOMNO`（`OrderBomRequestLink.ResolvedBOMNO`）。</param>
    /// <param name="RootWorksetId">ROOT 代表锚点（`OrderBomRequestLink.RepWorksetId`）；ROOT 侧第二层收窄。</param>
    /// <param name="ParentMaterialCodes">EDGE 侧当前请求内该子件的父件集合（`BomSnapshot` 按 BOMNO 索引）。</param>
    public sealed record SelectionContext(
        string? BomNo,
        long? RootWorksetId,
        IReadOnlyCollection<string>? ParentMaterialCodes);

    /// <summary>收敛结果。</summary>
    /// <param name="ScopeType">首选 scope（恒等于 `prefersEdgeScope ? EDGE : ROOT`；不再有回退）。</param>
    /// <param name="MissingPreferredScope">true = 按选择键收窄后首选 scope 无行 ⇒ 本次**无链**（调用方须记 Issue／走合法降级）。</param>
    /// <param name="Stages">唯一一条有序链（按 `StageSeq` 升序；同 `StageCode` 取本实例内 `MIN(StageSeq)`）。无链时为空。</param>
    /// <param name="RejectedChainKeys">同 scope 内**其它**合法但未采用的链签名（非空 ⇒ 调用方必须告警，不得静默）。</param>
    /// <param name="OtherScopeLongestChainKey">另一 scope 最长链的签名；另一 scope 无行时为 null（供「两 scope 是否分歧」计数）。</param>
    /// <param name="OtherScopeDistinctChainCount">另一 scope 的去重链条数（0 = 该 scope 无行）。</param>
    /// <param name="BomNoFilterApplied">是否按 `BomNo` 收窄过（留痕：false ＝ 无请求锚点）。</param>
    /// <param name="WorksetIdFilterApplied">ROOT 侧是否真正按 `WorksetId` 收窄过（当前批次该列为 NULL ⇒ false）。</param>
    /// <param name="IdentityState">请求身份可信度（J15-01 三态 ＋ J16-01 的 `ParentEdgeScoped`）。</param>
    /// <param name="IdentityAmbiguous">
    /// true ＝ **非请求级身份**（`ParentEdgeScoped`／`UnverifiedRequestIdentity`／`NoAnchor`）下候选链内容不一致
    /// ⇒ 本需求**不出链**（`Stages` 空、`RejectedChainKeys` 列全部候选供取证），Q/U 不受影响。
    /// 这是 J16-01 的准入条件：只有 `Anchored`（真实请求键等值）才允许在候选不一致时按冻结 `07 :722` 取最长；
    /// 请求实例未经证明时，「同 BOMNO／同父边」不足以说明这些路径等价，取最长＝把别人的路径塞给本需求。
    /// </param>
    public sealed record Resolution(
        string ScopeType,
        bool MissingPreferredScope,
        IReadOnlyList<StageSequenceStep> Stages,
        IReadOnlyList<string> RejectedChainKeys,
        string? OtherScopeLongestChainKey,
        int OtherScopeDistinctChainCount,
        bool BomNoFilterApplied,
        bool WorksetIdFilterApplied,
        RequestIdentityState IdentityState,
        bool IdentityAmbiguous = false);

    /// <summary>
    /// 把**一个需求**在本批 StagePath 的候选行收敛为**唯一一条**有序链。
    /// 返回 null 仅当 `rows` 为空；首选 scope 无行时返回 `MissingPreferredScope=true` 的空链结果（不再跨 scope 回退）。
    /// </summary>
    /// <param name="rows">该物料的全部 StagePath 行（scope 不限）。</param>
    /// <param name="prefersEdgeScope">true = 该需求作为子件被父件消耗（取 EDGE）；false = 本订单顶层根需求（取 ROOT）。</param>
    /// <param name="selection">需求侧选择键；null ＝ 不按请求收窄。</param>
    /// <param name="allowUnanchoredCompatibility">
    /// 无请求锚点时是否仍按「不做请求收窄」产出链。**默认 false**（J15-01 状态 c：三键全空 ⇒ 无链 + 诊断）。
    /// 仅当调用方证明**整个需求集**都没有锚点载体（例如该 PV 的 `OrderBomRequestLink` 根本未装载）时，
    /// 才可传 true 走系统性兼容路径，且必须按 `IdentityState=NoAnchor` 留痕，不得当作权威链。
    /// ⚠ 放行后仍受 J16-01 等价守卫约束：本物料候选链内容不一致时**依旧无链**（`IdentityAmbiguous=true`）。
    /// </param>
    public static Resolution? Resolve(
        IReadOnlyList<InstanceRow> rows,
        bool prefersEdgeScope,
        SelectionContext? selection = null,
        bool allowUnanchoredCompatibility = false)
    {
        if (rows is null || rows.Count == 0)
            return null;

        var preferredScope = prefersEdgeScope ? ScopeEdge : ScopeRoot;
        var fallbackScope = prefersEdgeScope ? ScopeRoot : ScopeEdge;

        // ⓪ 选行前置（J14-02）：先把「别的请求/别的父边」的行剔掉，再谈选链。
        var bomNoFilterApplied = !string.IsNullOrWhiteSpace(selection?.BomNo);

        // ⓪' 三键全空 ⇒ 无权威身份（J15-01 状态 c）：默认不产链，只回诊断；系统性兼容需调用方显式放行。
        if (!bomNoFilterApplied && !allowUnanchoredCompatibility)
        {
            return new Resolution(preferredScope, true, Array.Empty<StageSequenceStep>(),
                Array.Empty<string>(), null, 0, false, false, RequestIdentityState.NoAnchor);
        }

        var scoped = bomNoFilterApplied
            ? rows.Where(r => SameText(r.BomNo, selection!.BomNo)).ToList()
            : rows.ToList();

        if (scoped.Count == 0)
        {
            // 请求锚点存在、但本批没有任何该请求的行 ⇒ 无链（绝不拿别的请求顶上）。
            return new Resolution(preferredScope, true, Array.Empty<StageSequenceStep>(),
                Array.Empty<string>(), null, 0, bomNoFilterApplied, false,
                bomNoFilterApplied ? RequestIdentityState.Anchored : RequestIdentityState.NoAnchor);
        }

        var worksetIdFilterApplied = false;
        var parentEdgeFilterApplied = false;
        if (!prefersEdgeScope && selection?.RootWorksetId is long rootWorksetId
            && scoped.Any(r => r.WorksetId.HasValue))
        {
            scoped = scoped.Where(r => r.WorksetId == rootWorksetId).ToList();
            worksetIdFilterApplied = true;
        }
        else if (prefersEdgeScope && selection?.ParentMaterialCodes is { Count: > 0 } parents)
        {
            var parentSet = new HashSet<string>(parents.Where(p => p is not null), StringComparer.Ordinal);
            scoped = scoped.Where(r => r.ParentMaterialCode is not null && parentSet.Contains(r.ParentMaterialCode)).ToList();
            parentEdgeFilterApplied = true;
        }

        // ⓪'' 身份可信度（J15-01 ＋ J16-01）：
        //   · ROOT 只有**真按 `WorksetId` 等值收窄**才算 Anchored —— 那是真实请求键；
        //   · EDGE 的父边只是边维度收窄（`08 :459-463` 的 EDGE `WorksetId` 与请求级 `RepWorksetId` 不同命名空间）
        //     ⇒ 本状态**不得**叫 Anchored（0号位 第十六轮 J16-01：「如果只是父边已过滤，应改名或降级为非请求级状态」）；
        //   · 只剩 `BOMNO`（ROOT 该列全 NULL／EDGE 父边集未知）⇒ 内容等价兼容，标 Unverified。
        var identityState = !bomNoFilterApplied
            ? RequestIdentityState.NoAnchor
            : prefersEdgeScope
                ? (parentEdgeFilterApplied
                    ? RequestIdentityState.ParentEdgeScoped
                    : RequestIdentityState.UnverifiedRequestIdentity)
                : (worksetIdFilterApplied
                    ? RequestIdentityState.Anchored
                    : RequestIdentityState.UnverifiedRequestIdentity);

        // ① 行 → 实例链：实例 = (scope, 父件, WorksetId, BOMNO)；实例内同 StageCode 取 MIN(StageSeq)
        //    ⚠ 绝不跨实例合并 —— 跨实例合并就是「静默并集」本身。
        var byScope = new Dictionary<string, List<(string ChainKey, List<StageSequenceStep> Stages)>>(StringComparer.Ordinal);
        foreach (var group in scoped
                     .Where(r => !string.IsNullOrEmpty(r.StageCode))
                     .GroupBy(r => (Scope: NormalizeScope(r.ScopeType), Instance: InstanceKey(r))))
        {
            var steps = group
                .GroupBy(r => r.StageCode!, StringComparer.Ordinal)
                .Select(g => new StageSequenceStep
                {
                    StageCode = g.Key,
                    StageSeq = g.Min(x => x.StageSeq)
                })
                .OrderBy(s => s.StageSeq)
                .ThenBy(s => s.StageCode, StringComparer.Ordinal)
                .ToList();

            if (steps.Count == 0)
                continue;

            if (!byScope.TryGetValue(group.Key.Scope, out var list))
                byScope[group.Key.Scope] = list = new List<(string, List<StageSequenceStep>)>();
            list.Add((BuildChainKey(steps), steps));
        }

        // ② 首选 scope 无行 ⇒ 无链（J14-01：删除跨 scope 回退）。
        if (!TryGetDistinctChains(byScope, preferredScope, out var picked))
        {
            string? otherOnlyLongest = null;
            var otherOnlyCount = 0;
            if (TryGetDistinctChains(byScope, fallbackScope, out var otherOnly))
            {
                otherOnlyLongest = otherOnly[0].ChainKey;
                otherOnlyCount = otherOnly.Count;
            }

            return new Resolution(preferredScope, true, Array.Empty<StageSequenceStep>(),
                Array.Empty<string>(), otherOnlyLongest, otherOnlyCount, bomNoFilterApplied, worksetIdFilterApplied,
                identityState);
        }

        // ③ 另一 scope 的最长链（仅供调用方统计「两 scope 是否分歧」，不参与选定）
        string? otherLongest = null;
        var otherCount = 0;
        if (TryGetDistinctChains(byScope, fallbackScope, out var other))
        {
            otherLongest = other[0].ChainKey;
            otherCount = other.Count;
        }

        // ④ **请求实例等价守卫**（0号位 2026-10-17 第十六轮 J16-01，本方法的准入条件）：
        //   非 Anchored 身份下，候选链内容**必须完全一致**才准许兼容。
        //   仅有「同 BOMNO／同父边」不足以证明不同 `RequestDetailId`/`WorksetId` 的路径内容等价；
        //   不一致 ⇒ **不出链**，把全部候选签名作为异常证据交调用方，Q/U 不受影响（不得用某一条冒充）。
        if (identityState != RequestIdentityState.Anchored && picked.Count > 1)
        {
            return new Resolution(preferredScope, false, Array.Empty<StageSequenceStep>(),
                picked.Select(c => c.ChainKey).ToList(), otherLongest, otherCount,
                bomNoFilterApplied, worksetIdFilterApplied, identityState, IdentityAmbiguous: true);
        }

        // ⑤ 同 scope 内去重后「取最长」；条数相同时按链签名 ordinal 降序取定（**确定性**，不随机、不按行序）
        //   —— 到达此处时要么身份是 Anchored（真实请求键已等值收窄），要么候选唯一。
        var chosen = picked[0];
        var rejected = picked.Skip(1).Select(c => c.ChainKey).ToList();

        return new Resolution(preferredScope, false, chosen.Stages, rejected, otherLongest, otherCount,
            bomNoFilterApplied, worksetIdFilterApplied, identityState);
    }

    /// <summary>一条链的稳定签名：`StageSeq:StageCode` 按升序 `|` 连接（用于去重比较与告警输出）。</summary>
    public static string BuildChainKey(IReadOnlyList<StageSequenceStep> steps) =>
        steps.Count == 0
            ? string.Empty
            : string.Join("|", steps.Select(s => s.StageSeq.ToString() + ":" + s.StageCode));

    private static bool TryGetDistinctChains(
        IReadOnlyDictionary<string, List<(string ChainKey, List<StageSequenceStep> Stages)>> byScope,
        string scope,
        out List<(string ChainKey, List<StageSequenceStep> Stages)> distinct)
    {
        distinct = new List<(string, List<StageSequenceStep>)>();
        if (!byScope.TryGetValue(scope, out var list) || list.Count == 0)
            return false;

        distinct = list
            .GroupBy(c => c.ChainKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(c => c.Stages.Count)
            .ThenByDescending(c => c.ChainKey, StringComparer.Ordinal)
            .ToList();
        return distinct.Count > 0;
    }

    /// <summary>
    /// 「请求＋边实例」隔离键（J12-02）。`WorksetId` 缺失（旧批次）时退化为 `BOMNO`。
    /// </summary>
    private static string InstanceKey(InstanceRow r) =>
        string.Join("~",
            NormalizeScope(r.ScopeType),
            r.ParentMaterialCode ?? "\u0000",          // NULL 与空串必须区分（ROOT 的父件恒为 NULL）
            r.WorksetId?.ToString() ?? "\u0000",
            r.BomNo ?? "\u0000");

    /// <summary>`StageScopeType` 归一：trim + 大写。未知/空值**不落 ROOT／EDGE 任一桶**（不静默当作合法 scope）。</summary>
    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? string.Empty : scope.Trim().ToUpperInvariant();

    /// <summary>`BOMNO` 比较：trim 后 ordinal 相等；NULL/空白互不匹配。</summary>
    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
}
