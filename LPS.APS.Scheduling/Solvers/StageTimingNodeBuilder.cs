using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 「无 Routing Stage」纯内存时间节点构造器 —— 0号位 2026-09-29 裁决 §十四第 4/5/9 项（④⑤⑨）。
///
/// 【它解决什么】
///   末端（或整条）无 Routing 的 Stage（如 `BJ_FINAL` 完工）**没有对应的真实 Operation Task**，
///   其提前期无法由 <see cref="FinalTaskDraft"/> 表达；而 0号位 §9.2 明文**禁止**为此伪造
///   假 RoutingOperation / Task.Id / TaskNo / MESWorkOrder。
///   ⇒ 本类产出**纯内存**的 <see cref="DemandCompletionFact"/>，随
///   <see cref="DomainSolveResult.DemandCompletions"/> 出口，**绝不进 <see cref="DomainSolveResult.FinalTasks"/>**。
///
/// 【④ 纯内存节点】产出的 <see cref="StageTimingSegmentFact.NodeType"/> 恒为 `"STAGE_TIMING"`；
///   不建 Task、不生成 TaskNo、不进 MES 下发、不进 Execution Batch、不作为真实 Operation。
/// 【⑤ 只认契约】只接受 0号位 定的 4 个 `MatchLevel`；其余（含旧名 `MATERIAL`/`FAMILY`）= 契约违例 ⇒ 兜底。
///   命中级（含 `STAGE_LEADTIME_MISSING`）的**值一律原样透传，我方不做符号判断**。
///
///   ⚠️ **2026-09-29 口径更正（2号位 r13519 载入 Core 契约，取代当日早先口径）**：
///   此前「命中到参数行但折算值 ≤ 0 **同样视同未命中**、同样给 72h 兜底」**已作废**。
///   **命中就是命中** —— 折算值为 0（或负）**按命中产出**：`LeadTimeHours = 0`、
///   `MatchLevel` = 实际命中级（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00` ⇒ 值 0 + `FACTORY_STAGE_DEFAULT`）。
///   ⇒ 消费侧认 `MatchLevel`，但**不要**假定「值 0 ⇒ 一定是兜底」：值 0 也可能是一条真实命中的参数。
///   **0 是参数行写下的值，装载层与消费侧都不得替参数侧改判。**
///
///   注：0号位「**不得默认 0 小时**」管的是**未命中时不得拿 0 顶上**（那是静默丢提前期），
///   与「参数行明确写了 0」是两回事 —— 后者是**真实命中**，透传即可。
/// 【⑨ 时间出口】末端真实 `Task.End` + 其**之后**各无 Routing Stage 的提前期之和 = `CompletionTime`/`AvailableTime`。
///
/// 【边界】本类只用 `DomainSolveRequest` / `ConstraintContext` 的既有数据，**不查库、不查参数表**
///   （0号位 §4.4 末句：1号位 只消费已解析的 StageLeadTimeFact）。
///
/// 【可见性说明】⚠ **2026-10-14 更正**：本处旧文曾称「<c>Scheduling</c> 无 <c>InternalsVisibleTo(Tests)</c>」
///   —— 该断言**已过期**：`LPS.APS.Scheduling.csproj` 现含 `<c>&lt;InternalsVisibleTo Include="LPS.APS.Tests" /&gt;</c>`
///   （故 <see cref="TracePathUndetermined"/>、<see cref="TraceCompletionTimeUnavailable"/> 等
///   <c>internal</c> 成员**可被单测直接引用**）。
///   尽管如此，**纯逻辑入口 <see cref="BuildCore"/> 仍保持 public 且只收公开类型** —— 这是**有意的接口约束**
///   （纯函数、可脱离 Context 单测），**不是**可见性限制所致；
///   依赖 <c>internal</c> 的 <see cref="ConstraintContext"/> 的装配留在 <see cref="Build"/>（internal）。
/// </summary>
public static class StageTimingNodeBuilder
{
    /// <summary>
    /// 契约码：三级均未命中 / 值不可用。PM 原码；1号位 回执 §四 定为 V1 合法 4 值之一。
    ///
    /// ⚠ 2026-10-15（0号位 第九轮 §3.1/§3.3）**口径更正**：命中本码 = **没有可用的合法参数**
    ///   ⇒ `BuildCore` **不产出时间段、不推进游标**，只留 Issue（不虚造时长）。
    ///   原「72h 兜底」被明禁 —— 逐字：「不使用 0 或 72h 臆造时间」。这与 2026-09-29 §4.4
    ///   「不得默认 0 小时 …… 对该 Stage 的后续正式时间无法证明时，应显式进入数据/参数 Issue」
    ///   本来就一致；72h 是 1号位 侧早先自行加的，不是该裁决给的。
    /// </summary>
    private const string MissingLevel = "STAGE_LEADTIME_MISSING";

    /// <summary>④ 纯内存节点的显式标记（0号位 §9.3；2号位 写库口的判据）。</summary>
    private const string StageTimingNodeType = "STAGE_TIMING";

    /// <summary>
    /// 追溯码（**决策说明层**，非冻结 `ReasonCode` 字典）：路径未唯一确定 ⇒ 无 Routing Stage 提前期
    /// 取自候选路径并集，`CompletionTime` 只是候选口径。见 <see cref="Build"/>。
    /// </summary>
    internal const string TracePathUndetermined = "STAGE_TIMING_PATH_UNDETERMINED";

    /// <summary>
    /// 追溯码（**决策说明层**，非冻结 `ReasonCode` 字典）：**既无末端真实 Task、又无任何无 Routing Stage
    /// 提前期**进入 `CompletionTime` ⇒ 该时间退化为 `request.PlanningStart`，**不是**已排/预计完工/供给可用时间。
    ///
    /// 【为什么单列】0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》§三.2 逐字：
    ///   「`BuildCore`仍用`terminal?.PlannedEndTime ?? PlanningStart`作游标，在`terminal==null`且
    ///    `segments.Count==0`时仍可能生成`CompletionTime=PlanningStart`的`DemandCompletionFact`，
    ///    而r13685新增`SolveTraceNote`仅在`!PathDetermined && segments.Count>0`时触发。请补此可见反例…」
    ///   判词**成立**（源码已核）。本码把它从「静默」改成「显式留痕」。
    ///   ⚠ 与「仅有 StageLeadTime 的业务 Stage」（`segments.Count>0`，0号位 §10.3 认可）**不可混为一类**。
    /// </summary>
    internal const string TraceCompletionTimeUnavailable = "DEMAND_COMPLETION_TIME_UNAVAILABLE";

    /// <summary>0号位 2026-09-29 裁决 §4.2/§4.3：V1 只认的三级命中码（Material / ProductFamily 级不认）。</summary>
    private static readonly HashSet<string> HitLevels = new(StringComparer.Ordinal)
    {
        "DEPT_EXACT",
        "FACTORY_STAGE_DEFAULT",
        "GLOBAL_STAGE_DEFAULT"
    };

    /// <summary>
    /// 装配入口：从 <see cref="ConstraintContext"/> 取出求解范围与依赖图，定出各 Demand 的末端真实 Task，
    /// 再交给纯逻辑 <see cref="BuildCore"/>。
    /// </summary>
    internal static List<DemandCompletionFact> Build(
        DomainSolveRequest request,
        ConstraintContext constraints,
        IReadOnlyList<FinalTaskDraft> allScheduledTasks)
    {
        var tasksByDemand = new Dictionary<string, List<FinalTaskDraft>>(StringComparer.Ordinal);
        foreach (var group in allScheduledTasks.GroupBy(t => t.SourceDraftId, StringComparer.Ordinal))
        {
            tasksByDemand[group.Key] = group.ToList();
        }

        var terminalByDemandKey = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal);
        foreach (var demand in request.LogicalProductionDemands)
        {
            tasksByDemand.TryGetValue(demand.LogicalDemandKey, out var demandTasks);
            var terminal = PickTerminalTask(demandTasks, constraints, demand.MaterialId);
            if (terminal != null)
            {
                terminalByDemandKey[demand.LogicalDemandKey] = terminal;
            }
        }

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var missingLeadTimeStages = new List<(string DemandKey, string StageCode)>();
        var facts = BuildCore(
            request, constraints.EffectiveStages, terminalByDemandKey,
            pathUndetermined, completionTimeUnavailable, missingLeadTimeStages);

        // ── S2-01（0号位 2026-10-14《…再次整改回执正式审核回复》§二）──
        //   判词逐字：「**对尚未选定Path或实际未排下的需求，不得把任意候选路径并集产生的时间当作权威承诺时间，
        //   也不得据此生成假Task、漏计真实StageLeadTime或静默报完整完成。**」
        //   ⇒ 路径未唯一确定、且**确实有**无 Routing Stage 提前期进入了 `CompletionTime` 时，
        //     该需求的时间只是**候选/诊断**口径 ⇒ 经既有合法追溯通道（`SolveTraceNote`，
        //     与 `DEMAND_GOAL_ABSENT` 同一先例，见 `PhaseTwoInitialScheduler:739`）**显式留痕，绝不静默**。
        //   ⚠ **不自造 `ReasonCode` 枚举**：此位承载的是决策说明层取值（0号位 Q4）。
        //   ⚠ 归一化 ON（V1 现状：全部 op 均为 `DEFAULT/1` ⇒ 该 Route 唯一 Path ⇒ 已确定）时
        //     `pathUndetermined` **恒空** ⇒ **零回归**。仅「归一化 OFF + 真实多 Path」才可能非空。
        foreach (var key in pathUndetermined)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = key,
                ReasonCode = TracePathUndetermined,
                Message = "无 Routing Stage 提前期取自**候选路径并集**（该需求未选定唯一 Path，或整条未排下）"
                          + "⇒ 本需求 `CompletionTime` 为**候选口径**，不得作为权威承诺时间；"
                          + "待真实 Path 事实（2号位/5号位）落地后重算。",
                Level = "Warning"
            });
        }

        // ── 0号位 2026-10-14《…综合正式审核回复 v1.1》§三.2：**不得静默**给出退化的 `CompletionTime` ──
        //   判词：「不得将`PlanningStart`或候选路径并集时间作为正式已排/预计完工/供给可用时间；
        //     不能编造Task或制造业务短缺原因。」⇒ 此位显式留痕，供 2号位 读模型/订单交付/Supply 可用时间侧
        //   识别「该 Demand 的 `CompletionTime` 无真实依据」，不得当作已排承诺。
        foreach (var key in completionTimeUnavailable)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = key,
                ReasonCode = TraceCompletionTimeUnavailable,
                Message = "该 Demand **无末端真实 Task（未排下）**，且其 `StageTimings` 为空 —— "
                          + "或该物料纯无 Routing 且无任何提前期段，或该物料**有真实 Routing** 而其无 Routing "
                          + "Stage 提前期属**候选口径**已按第七轮 §八.3 抑制 ⇒ `CompletionTime`/`AvailableTime` "
                          + "退化为 `PlanningStart`，**不是**已排/预计完工/供给可用时间，"
                          + "**不得**作为承诺或数量闭合依据；真实未排原因见 `UnscheduledTasks`。",
                Level = "Warning"
            });
        }

        // ── N8-01（0号位 2026-10-15 第九轮 §3.1/§3.3）：缺参的 Stage 显式进 Issue，**不虚造时间** ──
        //   冻结依据：规则 S-006「缺参报 STAGE_LEADTIME_MISSING」；《04》场景7「无参数时形成
        //   `STAGE_LEADTIME_MISSING`」「缺失参数进入 Issue/Explanation」；0号位 2026-09-29 §4.4
        //   「不得默认 0 小时 …… 无法证明时显式进入数据/参数 Issue」「也不得删除 Stage」。
        //   ⇒ 该 Stage 仍在范围内、仍被识别（不丢），只是**没有时长**可用，故只留 Issue、不产出时间段。
        foreach (var (demandKey, stageCode) in missingLeadTimeStages)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = demandKey,
                ReasonCode = MissingLevel,
                Message = $"Stage `{stageCode}` 无可用 `StageLeadTimeParam`（DEPT_EXACT / "
                          + "FACTORY_STAGE_DEFAULT / GLOBAL_STAGE_DEFAULT 三级均未命中）"
                          + "⇒ 该 Stage 的后续正式时间**无法证明**：**不产出时间段、不推进游标**，"
                          + "**不使用 0 或 72h 臆造时长**。该 Stage **保留在求解范围内、不得删除**；"
                          + "待 3号位 补参数后重算（S-006 /《04》场景7 / 0号位 2026-09-29 §4.4）。",
                Level = "Warning"
            });
        }

        return facts;
    }

    /// <summary>
    /// 纯逻辑：为每个 Demand 构造 Demand 级完成时间事实。**无副作用、不读 Context、可单测。**
    /// </summary>
    /// <param name="request">求解请求（用其 StageLeadTimes / StageSequenceChains / LogicalProductionDemands / PlanningStart）。</param>
    /// <param name="effectiveStages">本次求解范围内的可达 Stage：MaterialId → StageCode 集合。</param>
    /// <param name="terminalByDemandKey">各 Demand 的末端真实 Task；无真实 Task 的 Demand 不出现在此字典中。</param>
    /// <param name="pathUndeterminedDemandKeys">
    /// 可选收集器（S2-01）：**路径未唯一确定且确实有无 Routing Stage 提前期进入 `CompletionTime`** 的
    /// Demand 键。装配层 <see cref="Build"/> 据此写 `SolveTraceNote`（本方法保持**纯函数、无副作用**）。
    /// </param>
    /// <param name="completionTimeUnavailableDemandKeys">
    /// 可选收集器（S2-01 补）：**既无末端真实 Task、又无任何提前期段** ⇒ `CompletionTime` 退化为
    /// `PlanningStart` 的 Demand 键（0号位 2026-10-14 §三.2）。同样由 <see cref="Build"/> 写 `SolveTraceNote`。
    /// </param>
    /// <param name="missingLeadTimeStages">
    /// 可选收集器（N8-01）：**在范围内、但三级均未命中合法 `StageLeadTimeParam`** 的
    /// `(DemandKey, StageCode)`。这些 Stage **不产出时间段**（不虚造时长），由 <see cref="Build"/>
    /// 写 `STAGE_LEADTIME_MISSING` 的 `SolveTraceNote`（0号位 2026-10-15 第九轮 §3.1/§3.3）。
    /// </param>
    public static List<DemandCompletionFact> BuildCore(
        DomainSolveRequest request,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        IReadOnlyDictionary<string, FinalTaskDraft> terminalByDemandKey,
        ICollection<string>? pathUndeterminedDemandKeys = null,
        ICollection<string>? completionTimeUnavailableDemandKeys = null,
        ICollection<(string DemandKey, string StageCode)>? missingLeadTimeStages = null)
    {
        var result = new List<DemandCompletionFact>();
        if (request.LogicalProductionDemands.Count == 0)
        {
            return result;
        }

        var leadTimeByKey = new Dictionary<(int MaterialId, string StageCode), StageLeadTimeFact>();
        foreach (var fact in request.StageLeadTimes)
        {
            leadTimeByKey[(fact.MaterialId, fact.StageCode)] = fact;
        }

        var chainByMaterial = new Dictionary<int, StageSequenceChain>();
        foreach (var chain in request.StageSequenceChains)
        {
            chainByMaterial[chain.MaterialId] = chain;
        }

        foreach (var demand in request.LogicalProductionDemands)
        {
            terminalByDemandKey.TryGetValue(demand.LogicalDemandKey, out var terminal);

            // 有末端真实 Task ⇒ 从它的 End 起累加；整条需求无真实 Operation Task ⇒ 自计划起点纯提前期累计
            // （0号位 §10.3：整条无真实 Operation Task 时 1号位 仍必须返回 Demand 级 CompletionTime）
            var cursor = terminal?.PlannedEndTime ?? request.PlanningStart;

            var segments = new List<StageTimingSegmentFact>();
            DateTime? requiredStageEnd = null;

            // ①「严格改法」（2026-10-12 / 2026-10-14 S2-01）：传入本需求**末端真实 Task 的选中路径**。
            //   已确定唯一 Path ⇒ 严格过滤（权威）；仅确定到 Route / 未确定 ⇒ 并集（**候选口径**）。
            //   归一化 ON（两列为空且载荷全 `DEFAULT/1`）⇒ 唯一 Path ⇒ 已确定，零行为变化。
            var routeless = ResolveRoutelessStages(
                demand.MaterialId, effectiveStages, request,
                terminal?.RouteCode, terminal?.PathId);
            var routelessStages = routeless.Stages;

            // ── 0号位 2026-10-14 第七轮正式复核 §八.3 门控（2026-10-15 落码）──
            //   判词逐字：「必须可由**结果本身及稳定身份**区分**权威已排完成、真正纯无Routing且经合法
            //     StageLeadTime形成的时间约束、未排占位/候选诊断**」「有StageTiming片段但**无选定合法Path/
            //     末端Task**时，也可能存在**仅供诊断的时间**」「不接受只写一条Warning或只证明没有FinalTask」。
            //
            //   读码实测的**结构事实**：`PathDetermined` 取 `terminal?.RouteCode` ⇒ `terminal == null` 时
            //   **恒为 false** ⇒ 未排需求的提前期**一律**是候选口径；而此前它照样写进 `StageTimings`
            //   ⇒ 「(ii) 真正纯无Routing的**合法时间约束**」与「(iii) 有Routing但未排的**占位时间**」
            //   在 `DemandCompletionFact` 上**完全同形**（`TerminalFinalDraftId == null` 且 `StageTimings` 非空）
            //   ⇒ 下游无法「由结果本身」区分。
            //
            //   门控判据 = **是否有真实 Routing 在求解范围内**（`HasRoutingInScope`），**不是** `PathDetermined`：
            //     · 未排 **且** 有 Routing（`true`）⇒ 时间无真实依据 ⇒ **不产出** `StageTimings`
            //       ⇒ 该 Fact 落为「无末端 Task + 无时间段」= 0号位 点名的 (iii) **占位/候选诊断**；
            //     · 未排 **且** 纯无 Routing（`false`）⇒ 0号位 §10.3 明文认可的「自起点起的纯提前期累计」
            //       ⇒ **保留**（(ii) 合法时间约束）—— **不**因本门控被取消能力。
            //   ⇒ 门控后三态在 Fact 上**可判**：
            //     (i) `TerminalFinalDraftId != null`；(ii) 二者皆无但 `StageTimings.Count > 0`；
            //     (iii) `TerminalFinalDraftId == null && StageTimings.Count == 0`。
            //   ⚠ **不静默**：被抑制的候选口径由下方既有 `completionTimeUnavailable` 通道留痕
            //     （`DEMAND_COMPLETION_TIME_UNAVAILABLE`），其文案已同步覆盖「含候选口径被抑制」这一支。
            //   ⚠ **零回归**：`terminal != null`（已排下）时本判据恒 false ⇒ 逐字不变；
            //     纯无 Routing 未排 时 `HasRoutingInScope == false` ⇒ 亦不变。
            var suppressCandidateLeadTimes = terminal is null && routeless.HasRoutingInScope;

            if (!suppressCandidateLeadTimes &&
                routelessStages.Count > 0 &&
                chainByMaterial.TryGetValue(demand.MaterialId, out var chain) &&
                chain.Stages.Count > 0)
            {
                var seqByStage = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var step in chain.Stages)
                {
                    seqByStage[step.StageCode] = step.StageSeq;
                }

                int? terminalSeq = null;
                if (terminal != null && seqByStage.TryGetValue(terminal.StageCode, out var terminalStageSeq))
                {
                    terminalSeq = terminalStageSeq;
                }

                // 按 StageSeq 升序（`StageSequenceChain.Stages` 契约已是升序，此处再排一次防上游乱序）；
                // 同 Seq 时按 StageCode 定序，保证结果可复现。
                foreach (var stageCode in routelessStages
                             .Where(seqByStage.ContainsKey)
                             .OrderBy(s => seqByStage[s])
                             .ThenBy(s => s, StringComparer.Ordinal))
                {
                    var seq = seqByStage[stageCode];

                    // 只取末端真实 Task **之后**的 Stage；整条需求无真实 Operation Task 时全取。
                    if (terminalSeq.HasValue && seq <= terminalSeq.Value)
                    {
                        continue;
                    }

                    var (hours, level) = ResolveLeadTime(leadTimeByKey, demand.MaterialId, stageCode);

                    // ── N8-01（0号位 2026-10-15 第九轮 §3.1/§3.3）：缺参**不虚造时间** ──
                    //   逐字：「对无合法参数 …… 的情形，保留真实 Issue 与诊断，**不使用 0 或 72h 臆造时间**」；
                    //   反证要求「缺参数→Issue 不虚造 Time」。与 2026-09-29 §4.4 一致：
                    //   「不得默认 0 小时 …… 对该 Stage 的后续正式时间无法证明时，应显式进入数据/参数 Issue」，
                    //   且「也不得删除 Stage」。
                    //   ⇒ 该 Stage **不产出时间段、不推进游标**（编不出时长），改由 Issue 表示（见 `Build`）。
                    if (string.Equals(level, MissingLevel, StringComparison.Ordinal))
                    {
                        missingLeadTimeStages?.Add((demand.LogicalDemandKey, stageCode));
                        continue;
                    }

                    var end = cursor.AddHours((double)hours);

                    segments.Add(new StageTimingSegmentFact
                    {
                        NodeType = StageTimingNodeType,
                        StageCode = stageCode,
                        LeadTimeHours = hours,
                        MatchLevel = level,
                        StartTime = cursor,
                        EndTime = end
                    });

                    cursor = end;

                    if (!string.IsNullOrEmpty(demand.RequiredStageCode) &&
                        string.Equals(stageCode, demand.RequiredStageCode, StringComparison.Ordinal))
                    {
                        requiredStageEnd = end;
                    }
                }
            }

            // RequiredStageCompletionTime：做到供给阈值 Stage 为止的完成时间（0号位 §10.3 两个口径都给）
            DateTime? requiredCompletion = null;
            if (!string.IsNullOrEmpty(demand.RequiredStageCode))
            {
                if (requiredStageEnd.HasValue)
                {
                    requiredCompletion = requiredStageEnd;
                }
                else if (terminal != null &&
                         chainByMaterial.TryGetValue(demand.MaterialId, out var requiredChain))
                {
                    var requiredStep = requiredChain.Stages.FirstOrDefault(
                        s => string.Equals(s.StageCode, demand.RequiredStageCode, StringComparison.Ordinal));
                    var terminalStep = requiredChain.Stages.FirstOrDefault(
                        s => string.Equals(s.StageCode, terminal.StageCode, StringComparison.Ordinal));

                    // 供给阈值 Stage 在末端真实 Task 处或之前已完成 ⇒ 完成时间即末端 Task.End
                    if (requiredStep != null && terminalStep != null && requiredStep.StageSeq <= terminalStep.StageSeq)
                    {
                        requiredCompletion = terminal.PlannedEndTime;
                    }
                }
            }

            // ── S2-01 留痕判据（见 Build 的 TracePathUndetermined）──
            //   只有「路径未唯一确定」**且**「确实有提前期段进入了 CompletionTime」才算候选口径；
            //   无 Routing Stage 一个都没有 ⇒ 时间完全来自真实 Task.End ⇒ 与路径无关，**不误报**。
            if (!routeless.PathDetermined && segments.Count > 0)
            {
                pathUndeterminedDemandKeys?.Add(demand.LogicalDemandKey);
            }

            // ── 0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》§三.2 **代码可核风险（判词成立）** ──
            //   判词逐字：「`BuildCore`仍用`terminal?.PlannedEndTime ?? PlanningStart`作游标，在`terminal==null`
            //     且`segments.Count==0`时仍可能生成`CompletionTime=PlanningStart`的`DemandCompletionFact`…」
            //   ⇒ 此时 `cursor` **从未推进**：既无真实 Task 的 `End`，也无任何无 Routing Stage 提前期依据
            //     ⇒ `CompletionTime` = `PlanningStart` 是**退化值**，**不是**已排/预计完工/供给可用时间。
            //   ⚠ 与「整条无真实 Operation Task、但**有** StageLeadTime 链」（`segments.Count>0`，
            //     0号位 §10.3 明文认可：自起点起的纯提前期累计）**结构上不同**，必须分开留痕。
            //   ── 2026-10-15（第七轮 §八.3 门控落地后）本判据**覆盖两支**，两者都是「时间无真实依据」：
            //     · 支A：该物料**纯无 Routing** 且无任何提前期段 ⇒ 游标从未推进；
            //     · 支B：该物料**有真实 Routing 但未排下** ⇒ 其无 Routing Stage 提前期本是**候选口径**，
            //       已被上方 `suppressCandidateLeadTimes` 抑制（`segments` 因此为空）⇒ 同样无权威依据。
            //   ⚠ 两支**共用同一条留痕**（不新造码）：对下游而言「该 Demand 的 `CompletionTime` 不可作权威」
            //     是同一件事；分列会让 2号位 读模型侧出现两个等价的告警键。文案已覆盖两支。
            if (terminal is null && segments.Count == 0)
            {
                completionTimeUnavailableDemandKeys?.Add(demand.LogicalDemandKey);
            }

            result.Add(new DemandCompletionFact
            {
                LogicalDemandKey = demand.LogicalDemandKey,
                MaterialId = demand.MaterialId,
                AllocationSequence = demand.AllocationSequence,
                RequiredStageCode = demand.RequiredStageCode,
                CompletionTime = cursor,
                AvailableTime = cursor, // V1 与 CompletionTime 同值（已对齐）
                RequiredStageCompletionTime = requiredCompletion,
                TerminalFinalDraftId = terminal?.FinalDraftId,
                StageTimings = segments
            });
        }

        return result;
    }

    /// <summary>
    /// 取该 Demand 的**末端真实生产 Task**（0号位 §10.2「唯一末端真实 Task」）。
    /// 规则与 Phase5 <c>GenerateAllocationShares</c> 的末端判定**必须一致** —— 改其一请同步另一处。
    /// </summary>
    private static FinalTaskDraft? PickTerminalTask(
        List<FinalTaskDraft>? tasks,
        ConstraintContext constraints,
        int materialId)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return null;
        }

        if (tasks.Count == 1)
        {
            return tasks[0];
        }

        // 标记所有有 downstream 的 Task（非末端）。节点身份 = (StageCode, OperationCode)（0号位 §5.3）。
        // Path-aware（0号位 2026-10-07 裁决 Q-3）：本列表全属同一需求 ⇒ 路径身份取自首个 Task，不按物料猜。
        var downstream = new HashSet<string>(StringComparer.Ordinal);
        var sample = tasks[0];
        if (constraints.TryGetRoutingGraph(sample.MaterialId, sample.RouteCode, sample.PathId, out var graph))
        {
            foreach (var depList in graph.Dependencies.Values)
            {
                foreach (var dep in depList)
                {
                    var upstream = tasks.FirstOrDefault(t =>
                        t.OperationCode == dep.From.OperationCode &&
                        string.Equals(t.StageCode, dep.From.StageCode, StringComparison.Ordinal));

                    if (upstream != null)
                    {
                        downstream.Add(upstream.FinalDraftId);
                    }
                }
            }
        }

        var terminals = tasks.Where(t => !downstream.Contains(t.FinalDraftId)).ToList();
        if (terminals.Count == 0)
        {
            // 兜底：全部被标为非末端（异常输入）⇒ 退回最晚结束者，**不抛、不静默丢**
            terminals = tasks;
        }

        return terminals
            .OrderByDescending(t => t.PlannedEndTime)
            .ThenBy(t => t.FinalDraftId, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// 本**求解范围**内「有效 Stage 但零 RoutingOperation」的 Stage 集合。
    /// 用求解范围（Phase1 可达 Stage）而非全量 BOM StagePath ——
    /// 否则会把 StartOperation/StartStage 之前已完成、本次不求解的 Stage 也算进来，凭空多出提前期。
    /// </summary>
    /// <remarks>
    /// **路径确定性三档**（0号位 2026-10-14《再次整改回执正式审核回复》§二 S2-01）：
    ///   · **已确定**（`PathDetermined = true`）—— 选中 `RouteCode` 在该物料载荷下**唯一**对应一条 `PathId`
    ///     （显式 `PathId` 命中，或该 `RouteCode` 只有一条 Path）⇒ 按 `(RouteCode, PathId)` **严格过滤**
    ///     ⇒ 该结果**可作权威完成时间**依据。
    ///   · **仅确定到 Route**（`PathDetermined = false`，`RouteCode` 非空但该 Route 有多条 Path）
    ///     ⇒ 按 `RouteCode` 过滤（该 Route 全部 Path 的并集）⇒ **候选口径**。
    ///   · **未确定**（`RouteCode` 空 / 载荷无该 Route 的工序 / 声称的 `PathId` 不在载荷内）
    ///     ⇒ 取该物料**全部** RouteCode 的工序并集 ⇒ **候选口径**。
    ///   ⇒ 后两档**不得**被下游当作**权威承诺时间**（见 <see cref="ResolveRoutelessStages"/> 的
    ///     `PathDetermined` 与 <see cref="Build"/> 的 `TRACE:STAGE_TIMING_PATH_UNDETERMINED` 留痕）。
    ///   归一化 ON（V1 现状：全部 op 均为 `DEFAULT/1`）时，三档**逐元素相同** ⇒ **严格 no-op / 零回归**。
    /// </remarks>
    // 可见性：`internal`（而非 private）—— 供 `LPS.APS.Tests` 经 `InternalsVisibleTo` **直接覆盖**。
    // 生产调用点只有 `BuildCore`。保留 5 参签名（既有已提交单测依赖）。
    internal static List<string> GetRoutelessStages(
        int materialId,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        DomainSolveRequest request,
        string? selectedRouteCode = null,
        long? selectedPathId = null)
        => ResolveRoutelessStages(materialId, effectiveStages, request, selectedRouteCode, selectedPathId)
            .Stages;

    /// <summary>
    /// 「无 Routing Stage」判定结果 + **该判定是否建立在唯一确定的 Path 上**。
    /// `PathDetermined = false` ⇒ <see cref="Stages"/> 只是**候选/诊断**口径，
    /// **不得**被下游当作权威承诺时间（0号位 2026-10-14《…正式审核回复》§二 S2-01）。
    ///
    /// <see cref="HasRoutingInScope"/>（2026-10-15 新增，第七轮 §八.3）：
    ///   本求解范围内**是否存在任何 Routing 工序** —— 它是「**真正纯无 Routing**」与
    ///   「**有真实 Routing 但未排下/路径未定**」的分界，也是 <see cref="BuildCore"/> 门控的判据。
    ///   ⚠ 为什么不复用 `PathDetermined`：`PathDetermined` 取 `terminal?.RouteCode` ⇒ **未排下的需求恒为
    ///     false**（无 terminal 就无 RouteCode），于是「物料根本没有 Routing」与「有 Routing 但没排下」
    ///     被**混成同一档**，无法区分 0号位 第七轮 §八.3 点名的 (ii)/(iii) 两态。
    /// </summary>
    internal readonly record struct RoutelessStageResolution(
        List<string> Stages, bool PathDetermined, bool HasRoutingInScope = false);

    /// <summary>
    /// 见 <see cref="GetRoutelessStages(int, IReadOnlyDictionary{int, HashSet{string}}, DomainSolveRequest, string?, long?)"/>
    /// 的 remarks（路径确定性三档）。
    /// </summary>
    internal static RoutelessStageResolution ResolveRoutelessStages(
        int materialId,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        DomainSolveRequest request,
        string? selectedRouteCode,
        long? selectedPathId)
    {
        if (!effectiveStages.TryGetValue(materialId, out var inScopeStages) || inScopeStages.Count == 0)
        {
            return new RoutelessStageResolution(new List<string>(), true, false);
        }

        var hasRoute = !string.IsNullOrWhiteSpace(selectedRouteCode);
        long? pathId = null;
        var pathDetermined = hasRoute && TryResolveDeterminedPath(
            materialId, request, selectedRouteCode!, selectedPathId, out pathId);

        var stagesWithRouting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in request.RoutingOperations)
        {
            if (op.MaterialId != materialId || string.IsNullOrEmpty(op.StageCode))
            {
                continue;
            }

            if (hasRoute && !string.Equals(op.RouteCode, selectedRouteCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (pathDetermined && pathId.HasValue && op.PathId != pathId.Value)
            {
                continue;
            }

            stagesWithRouting.Add(op.StageCode!);
        }

        // 本范围内**存在** Routing 工序 ⇒ 「有真实 Routing」（与「纯无 Routing」相对）。
        // 注意：此处按 in-scope 判定，与 `routelessStages` 同源 —— 范围外的 Stage 不计入，
        //   否则会把本次不求解的 Stage 也算成「有 Routing」（与 `GetRoutelessStages` 的口径一致）。
        var hasRoutingInScope = inScopeStages.Any(stage => stagesWithRouting.Contains(stage));

        return new RoutelessStageResolution(
            inScopeStages.Where(stage => !stagesWithRouting.Contains(stage)).ToList(),
            pathDetermined,
            hasRoutingInScope);
    }

    /// <summary>
    /// 判定「该需求的可执行路径是否**唯一确定**」。
    ///   已确定 ⇒ `true`，并给出 `(routeCode, pathId?)`（`pathId` 恒有值 —— 唯一 Path 时自动补全）。
    ///   未确定 ⇒ `false`（`RouteCode` 空 / 载荷无该 Route 的工序 / 声称的 `PathId` 不在载荷内 / 该 Route 多 Path）。
    /// </summary>
    internal static bool TryResolveDeterminedPath(
        int materialId,
        DomainSolveRequest request,
        string selectedRouteCode,
        long? selectedPathId,
        out long? pathId)
    {
        pathId = null;
        if (string.IsNullOrWhiteSpace(selectedRouteCode))
        {
            return false;
        }

        var pathIds = new HashSet<long>();
        foreach (var op in request.RoutingOperations)
        {
            if (op.MaterialId != materialId)
            {
                continue;
            }
            if (!string.Equals(op.RouteCode, selectedRouteCode, StringComparison.Ordinal))
            {
                continue;
            }
            pathIds.Add(op.PathId);
        }

        if (pathIds.Count == 0)
        {
            return false;   // 载荷里没有该 RouteCode 的工序 ⇒ 无从判定
        }

        if (selectedPathId.HasValue)
        {
            if (!pathIds.Contains(selectedPathId.Value))
            {
                return false;   // 声称的 PathId 不在载荷内 ⇒ 契约不一致 ⇒ 不可判定（不按"近似路径"放行）
            }
            pathId = selectedPathId.Value;
            return true;
        }

        if (pathIds.Count == 1)
        {
            pathId = pathIds.First();
            return true;
        }

        return false;   // 该 RouteCode 下多条 Path ⇒ 单凭 RouteCode 不可判定
    }

    /// <summary>
    /// ⑤ 提前期解析：只认契约 4 值；**命中级的值原样透传，不做符号判断**。
    /// 契约违例值（含旧名 `MATERIAL` / `FAMILY`）**不向外透传**，避免下游把它当合法层级审计。
    ///
    /// ⚠️ 2026-09-29 口径更正：**值 ≤ 0 不再视同未命中** —— 命中就是命中，0 是参数行写下的值。
    /// （早先那版带 `fact.LeadTimeHours > 0m` 的守卫即被作废的口径，已删。）
    ///
    /// ⚠️ 2026-10-15：命中 `MissingLevel` 时**返回值不参与时间累计** —— 调用方（`BuildCore`）
    /// 见到该 Level 直接走 Issue 分支、不产出时间段（见 `MissingLevel` 注释）。
    /// 故此处回落值取 `0m` 只是占位，**不是**「按 0 小时排」。
    /// </summary>
    private static (decimal Hours, string Level) ResolveLeadTime(
        Dictionary<(int MaterialId, string StageCode), StageLeadTimeFact> index,
        int materialId,
        string stageCode)
    {
        if (index.TryGetValue((materialId, stageCode), out var fact))
        {
            var level = fact.MatchLevel;
            var isLegalLevel = !string.IsNullOrEmpty(level) &&
                               (HitLevels.Contains(level) ||
                                string.Equals(level, MissingLevel, StringComparison.Ordinal));

            if (isLegalLevel)
            {
                return (fact.LeadTimeHours, level);
            }
        }

        return (0m, MissingLevel);
    }
}
