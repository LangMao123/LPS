using System.Threading;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **求解器性能计数器（诊断用，非契约）**。
///
/// 【为什么在这里而不在 Core】
///   0号位 2026-10-08《未命名的Markdown文件 (2)(1).md》§九 要求性能压测输出
///   「BatchPlanCandidate 试跑次数 / Routing 试跑次数 / CloneOccupancy 次数 / CloneTaskList 次数 /
///     SlotSearch 次数 / Phase4 传播 Task 数」。
///   但把 Phase 级字段加进 `DomainSolveResult` / `SolveSummary` 属于 **Core 契约变更**（Core 归 2号位）
///   ⇒ 本类放在 1号位 自己的 `LPS.APS.Scheduling` 内，**不触碰任何契约 DTO**，
///     由压测工装（`LPS.APS.Tests/Benchmarks/`）直接读取。
///
/// 【并发语义（为什么用 AsyncLocal 而不是全局 static long）】
///   `Counters` 对象存在 <see cref="AsyncLocal{T}"/> 里，由 <see cref="BeginScope"/> 在**调用方**
///   建立 ⇒ `await SolveAsync(...)` 时 ExecutionContext 把该引用流进求解器内部，
///   内部只**改对象字段**（引用不变）⇒ 调用方读同一个对象即得本次数。
///   · 并发求解（Web 端多 Domain 并行）各持**各自**的 Counters ⇒ 互不污染；
///   · **未开 scope 时**（正常生产路径）所有 `Count*` 调用只做一次 null 判断 ⇒ 近乎零开销、零行为影响。
///   ⇒ 计数器**只增不减**、**不参与任何业务判定**，纯观测。
///
/// 【明确不做的】
///   · 不统计耗时（耗时由压测工装用 `Stopwatch` 在 Phase 边界测，避免热点内取时钟）；
///   · 不写 `SolveTraceNotes`（trace 体积约束 + 与 2号位 的 ReasonCode 命名待定项）。
/// </summary>
internal static class SolverDiagnostics
{
    /// <summary>本次求解的计数器（字段为 `long`，仅供单线程求解内自增；读取前请确保求解已结束）。</summary>
    internal sealed class Counters
    {
        /// <summary>`SelectBestBatchPlan` 里每个**批方案候选**触发一次完整 `RunBatchPlan` 试跑。</summary>
        public long BatchPlanCandidateTrials;

        /// <summary>`RunBatchPlan` 里每个（执行批 × Routing 候选）触发一次候选试排。</summary>
        public long RoutingTrials;

        /// <summary>`CloneOccupancy` 调用次数（每次复制全资源占用表）。</summary>
        public long CloneOccupancy;

        /// <summary>`CloneTaskList` 调用次数（每次复制全量已排任务表，O(N)）。</summary>
        public long CloneTaskList;

        /// <summary>`CloneShares` 调用次数（每次重建全量份额字典，O(N)）。</summary>
        public long CloneShares;

        /// <summary>
        /// 2026-10-19 性能定位：`TryMergeDemandIntoStageBatch` 摘除段累计摘除的 Task 数 **Σk**
        ///   （= Σ|锚批|）。与 <see cref="DetachListLen"/> 一起算出「每次调用摘几个 / 列表多长」，
        ///   用于判定「逐个 `RemoveAt` 的搬移量 ≈ k·n/2」到底是不是耗时主因。纯观测。
        /// </summary>
        public long DetachTaskCount;

        /// <summary>
        /// 2026-10-19 性能定位：摘除时 `scheduledTasks` 的累计长度 **Σn**。纯观测。
        /// </summary>
        public long DetachListLen;

        /// <summary>
        /// 2026-10-19 二次定位：摘除段**回落全表扫描**的次数。与
        ///   <see cref="DetachIndexBuild"/> 热点次数一起算命中率 ——
        ///   若命中率低，说明「预建下标表」优化没吃到，129.6 µs/次 是 O(n) 扫描而非 O(k) 定位。纯观测。
        /// </summary>
        public long DetachFallbackCount;

        /// <summary>2026-10-19 二次定位：回落原因 ①——**根本没有下标表**（`taskIndexById == null`）。纯观测。</summary>
        public long DetachFallbackNoIndex;

        /// <summary>2026-10-19 二次定位：回落原因 ②——锚批 Task 的 `FinalDraftId` 为 null。纯观测。</summary>
        public long DetachFallbackNullId;

        /// <summary>2026-10-19 二次定位：回落原因 ③——**下标表里查不到**该 `FinalDraftId`（或下标越界/回读不符）。纯观测。</summary>
        public long DetachFallbackMissing;

        /// <summary>2026-10-19 二次定位：回落原因 ④——同一批内**下标重复**（`seenPositions.Add` 失败）。纯观测。</summary>
        public long DetachFallbackDupPos;

        /// <summary>
        /// 2026-10-19 二次定位：`BuildTaskIndexById` **实际被调用**的次数。设计上每个 `RunBatchPlan` 只建一次
        ///   （`??=` 缓存）；若该读数远大于 `RunBatchPlan` 调用次数 ⇒ 说明它**返回了 null** ⇒ `??=` 缓存失效、
        ///   每个候选都重建一次 O(N) 表（既浪费建表时间，又让摘除段永远走全表扫描）。纯观测。
        /// </summary>
        public long TaskIndexBuilds;

        /// <summary>2026-10-19 二次定位：`BuildTaskIndexById` **返回 null** 的次数（表内出现 null 或重复 `FinalDraftId`）。纯观测。</summary>
        public long TaskIndexNullResults;

        /// <summary>2026-10-19 二次定位：`RunBatchPlan` 的**总调用次数**（两个调用点：`:833` 多批路径 + `:1624` 批方案候选）。纯观测。</summary>
        public long RunBatchPlanInvocations;

        /// <summary>
        /// 2026-10-19 二次定位：进入 `TryMergeOrSchedule` 的 Stage 合批分支（`operations.Count &gt; 1`）时
        ///   **`taskIndexById` 为 null** 的次数 ⇒ 直接指出是**哪条调用路径**没拿到索引。纯观测。
        /// </summary>
        public long MergeStageEnteredWithNullIndex;

        /// <summary>时间槽搜索次数（正排 `FindFirstAvailableSlot` + 倒排 `FindBackwardSlot`）。</summary>
        public long SlotSearches;

        /// <summary>Phase4 传播过程中实际访问（重排/改期）的 Task 次数。</summary>
        public long Phase4PropagatedTasks;

        /// <summary>Phase5 `CompactGaps`（空隙压实）**门控放行并进入优化体**的次数（0 = 未进入）。</summary>
        public long Phase5CompactionRuns;

        /// <summary>Phase5 `OptimizeSetupSequences`（Setup 序列优化）**门控放行并进入优化体**的次数。</summary>
        public long Phase5SetupOptimizationRuns;

        /// <summary>
        /// Phase5 `OptimizeSegment`（**资源 × 连续生产窗口 × 固定锚点间可移动段**）**真正进入有界局部优化体**
        ///   的段数 —— 即「段内 ≥2 个 Task 且全部可移动」的段。
        ///   与 <see cref="Phase5SetupOptimizationRuns"/> 的区别：后者只说明「整 Run 未被否决」，
        ///   本计数说明「**确实有一个可重排的段被送进优化体**」（P1-02 验收：混合方向下 FORWARD 段必须可达）。
        /// </summary>
        public long Phase5SetupSegmentsOptimized;

        /// <summary>
        /// V1_3 F-01：合批落定失败 / 交期违约时，按插入身份回滚占用表**未命中**的条目数累计。
        ///   **正常恒为 0** —— 非 0 说明有代码路径绕过 <c>AddOccupancyWindow</c> 的 `insertLog` 直写占用表
        ///   （⇒ 该窗不会被回滚 ⇒ 幽灵占用）。纯观测、不参与任何业务判定。
        /// </summary>
        public long OccupancyRollbackUnmatched;

        /// <summary>
        /// V1_3 F-03：B-009 合法量门禁**因输入整体未投影而不可评估**的批形成次数累计
        ///   （`piFacts is null`）。纯观测、不参与任何业务判定；**非 0 即表示生产态该门禁不可达**。
        /// </summary>
        public long PiLegalQuantityGateUnevaluated;

        /// <summary>V1_3 F-03：B-009 合法量门禁**判越限/事实不可信并 Fail Closed**的次数。纯观测。</summary>
        public long PiLegalQuantityGateBlocked;

        /// <summary>
        /// 2026-10-12 通电补：**有策略路径**（`ApplyPiLegalQuantityGate`）**完整评估后过闸**的次数（= 放行档）。
        ///   【为什么必须补】（2号位 2026-10-12《门禁通电接线回执》§三，理由成立）：
        ///     ① 不补则「放行」档**只在缺策略库态**（走 B-010 兜底）读得出来 ⇒ 3号位 修好 ⑧块、C 桶改走
        ///        有策略路径的那一刻，该验收指标**当场失效**（陷阱式指标）；
        ///     ② 「放行 = 评估总数 − 拦截」**推不出** —— 评估总数本身没有计数器。
        ///   口径：**本方法完整评估后过闸**才 +1；`!IsLegal` / A/B 豁免（`IsContinuation || NoSplitMerge`）/
        ///     `piFacts is null` 三条**早退不计**（那不是「放行」，是「门禁不适用」）。
        ///   ⇒ 真实「放行」档 = 本项 + <see cref="PiLegalQuantityGatePassedViaB010Fallback"/>。纯观测。
        /// </summary>
        public long PiLegalQuantityGatePassed;

        /// <summary>
        /// 2026-10-12 通电补：**B-010 缺策略保守兜底路径**（`TryFormConservativePiBatch`）被同一门禁
        ///   `EvaluatePiLegalQuantityGate` 判**未满足**的次数。
        ///   【为什么必须单列】<see cref="PiLegalQuantityGateBlocked"/> 只在**有策略路径**
        ///   （`ApplyPiLegalQuantityGate`）自增；C 桶缺策略走 B-010 分支时**此前一次都不计** ⇒
        ///   验收按「拦截」档读计数器会把 B-010 的拦截读成 0（2号位 2026-10-12《门禁通电信号》§四 即按此口径验收）。
        ///   ⇒ 真实「拦截」档 = <see cref="PiLegalQuantityGateBlocked"/> + 本项。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGateBlockedViaB010Fallback;

        /// <summary>
        /// 2026-10-12 通电补：**B-010 缺策略保守兜底路径**真正**过闸并成批**（= 放行档）的次数。
        ///   与 <see cref="PiLegalQuantityGateBlockedViaB010Fallback"/> 对称，使「放行 / 拦截」两档可直接读。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGatePassedViaB010Fallback;

        /// <summary>
        /// R-1/R-2（2026-10-12，B-011）：**经济软区间偏离**被登记的**批数**（**软**偏离，**不**构成硬失败）。
        ///   经济 `Min/Max` 自 2026-10-12 起是**软优化输入**（03 §60 / 07 §4651「**不得再将「Qty 大于
        ///   经济 Max」本身作为硬冲突条件**」）⇒ 某批落在经济区间外只**登记偏离**，**不再**判
        ///   `BATCH_POLICY_CONFLICT`（旧口径 = 04 BP-R1 的「假硬不可排」）。纯观测、不参与任何业务判定。
        /// </summary>
        public long EconomicBatchDeviations;

        /// <summary>
        /// B-013 / 06 v1.36 §281（2026-10-13）：**Batch Policy 三态之 `INVALID`** 被消费的需求数
        ///   （「本键**有** Batch Policy 记录但**不满足发布/有效化资格**」⇒ 06 §281 逐字
        ///   「**不得假装 MISSING 兜底，应暴露 Issue 等待治理**」⇒ Fail Closed、**不**走 B-010 `Q_C` 兜底）。
        ///
        /// ⚠ **当前恒 0（载体不可达，如实登记）**：投影端 `FrozenStrategySnapshot.cs:294` 自述本块
        ///   「仅业务键 + 参数，**不含审计字段**（…IsActive —— 投影时**只取有效规则**）」⇒ INVALID 行在上游
        ///   已被滤掉。⇒ **恒 0 是「载体未到」而非「无 INVALID」**；待 2号位 按最小技术合同修正传递状态后
        ///   才具观测意义。非 0 即说明载体已到位。纯观测、不参与任何业务判定。
        /// </summary>
        public long BatchPolicyInvalids;

        /// <summary>
        /// **2026-10-13 新增**（0号位《未命名的Markdown文件4.md》§3.3）：本 Run 中判为
        /// **Batch Policy 状态不可获得**（`BatchPolicyMatchState.Unknown`）而 Fail Closed 的需求数。
        ///
        /// ⚠ **当前必然非 0**（与 <see cref="BatchPolicyInvalids"/> 恒 0 恰好相反）：现行载荷**无状态位**、
        ///   投影**只取有效规则** ⇒ 1号位 **对任一非命中键都无法证明「完全缺配」** ⇒ 一律判 `Unknown`
        ///   （0号位 §3.2 逐字：「从空集合无法区分『真正无规则』和『存在无效规则但未传入』」）。
        ///   ⇒ 本计数是**「输入契约缺陷规模」的观测量**：**归零即说明 2号位 可信状态载体已到位**。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long BatchPolicyUnknowns;

        /// <summary>
        /// V1_3 F-04：本 Run **实际生效**的 <c>SolverBatchBudget</c> 快照 —— 版本号
        ///   （<c>SolverBatchBudget/v1</c>）、取源（`…:SplitParams` / `…:default`）与两个**生效值**。
        ///   供「Run 快照 / 追踪证据」逐 Run 复现「到底用了什么技术预算」。
        ///   ⚠ 只记录**实际生效值**；业务旧列（`BatchPolicyRuleSnapshot.MaxOptimizationSplitCount` /
        ///     `MaxBatchCandidates`）**不参与**，改动它们不得改变本快照（B-007 单源可追溯）。
        /// </summary>
        public string? SolverBatchBudgetVersion;

        /// <summary>V1_3 F-04：本 Run 生效技术预算的**取源**标识（单源可追溯）。</summary>
        public string? SolverBatchBudgetSource;

        /// <summary>V1_3 F-04：本 Run 生效的 `MaxOptimizationSplitCount`。</summary>
        public long SolverBatchBudgetMaxOptimizationSplitCount;

        /// <summary>V1_3 F-04：本 Run 生效的 `MaxBatchCandidates`。</summary>
        public long SolverBatchBudgetMaxBatchCandidates;

        /// <summary>
        /// V1_4 NEW-05：MIXED「倒排部分成功后失败 ⇒ 转正排」时，按插入身份撤销的**倒排残留占用窗**数累计。
        ///   **正常 ≥0 且只在倒排确实写过窗后失败时非 0**（例：倒排最后一道成功、上一道失败）。
        ///   与 <see cref="OccupancyRollbackUnmatched"/> 的区别：后者是「该撤却撤不掉」的**异常**计数，
        ///   本项是「**确实撤销了多少**」的**正常**计数 —— 若本项长期恒 0 而 MIXED 走回落，说明回落路径**未撤销**（旧缺陷）。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long MixedBackwardRollbackWindows;

        /// <summary>
        /// V1_4 NEW-04：本 Run 内**新增 Stage 执行批**对 (PI × 物料 × Stage) 余额的累计登记次数
        ///   （<c>PiStageCommitLedger.Commit</c> 成功次数）。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiStageLedgerCommits;

        /// <summary>V1_4 NEW-04：本 Run 内累计登记被**按身份回滚**（候选试排隔离 / 需求未落定）的次数。纯观测。</summary>
        public long PiStageLedgerRollbacks;

        /// <summary>Phase 1（硬约束构建）耗时 ms。</summary>
        public long Phase1Ms;

        /// <summary>Phase 2（初始有限产能排程）耗时 ms。</summary>
        public long Phase2Ms;

        /// <summary>Phase 3（可行性与延期诊断）耗时 ms。</summary>
        public long Phase3Ms;

        /// <summary>Phase 4（有界局部修复）耗时 ms。</summary>
        public long Phase4Ms;

        /// <summary>Phase 5（压缩空隙与最终评价）耗时 ms。</summary>
        public long Phase5Ms;

        // ══════════════════════════════════════════════════════════════════
        // **热点累计耗时**（ticks；含嵌套，故各项之和会大于 Phase2Ms —— 用途是「谁最慢」，
        //   不是「把 Phase2 拆完」）。用 `Stopwatch.GetTimestamp()` 零分配采集：
        //   热点方法每次调用都可能执行 9.5 万次以上，`new Stopwatch()` 会自己成为热点。
        // ══════════════════════════════════════════════════════════════════
        private readonly long[] _hotspotTicks = new long[HotspotCount];

        /// <summary>
        /// 2026-10-19 性能定位：各热点的**调用次数**（与 <see cref="_hotspotTicks"/> 同索引）。
        ///   有次数才能把「累计 ms」换算成「每次多少 µs」——判定「是不是单次开销过大」必须靠它。
        /// </summary>
        private readonly long[] _hotspotCalls = new long[HotspotCount];

        /// <summary>热点数量（= <c>SolverDiagnostics.Hotspot</c> 枚举项数）。</summary>
        public const int HotspotCount = 16;

        internal void AddHotspotTicks(int hotspot, long ticks)
        {
            if ((uint)hotspot < HotspotCount)
            {
                _hotspotTicks[hotspot] += ticks;
                _hotspotCalls[hotspot]++;
            }
        }

        /// <summary>取热点累计耗时（ms）。</summary>
        public double HotspotMs(int hotspot)
            => _hotspotTicks[hotspot] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>2026-10-19 性能定位：取热点**调用次数**（配合 <see cref="HotspotMs"/> 算每次耗时）。</summary>
        public long HotspotCallCount(int hotspot)
            => (uint)hotspot < HotspotCount ? _hotspotCalls[hotspot] : 0L;
    }

    /// <summary>热点标识（索引须与 <see cref="Counters.HotspotCount"/> 对齐）。</summary>
    internal static class Hotspot
    {
        /// <summary>候选试排里的 `ProductTimeline.Clone()`。</summary>
        public const int TimelineClone = 0;
        /// <summary>`CloneOccupancy`（复制全资源占用表）。</summary>
        public const int CloneOccupancy = 1;
        /// <summary>`new List&lt;FinalTaskDraft&gt;(scheduledTasks)`（复制全量任务表，O(N)）。</summary>
        public const int CloneTaskList = 2;
        /// <summary>`CloneShares`（重建全量份额字典，O(N)）。</summary>
        public const int CloneShares = 3;
        /// <summary>时间槽搜索（正排 `FindFirstAvailableSlot` + 倒排 `FindBackwardSlot`）。</summary>
        public const int SlotSearch = 4;
        /// <summary>`RunBatchPlan` 整体（批方案候选试跑，含其内部候选路径试排）。</summary>
        public const int BatchPlanTrial = 5;
        /// <summary>`RunDemandSchedule`（单条候选路径的完整试排）。</summary>
        public const int DemandSchedule = 6;
        /// <summary>Phase4 传播循环整体。</summary>
        public const int Phase4Propagation = 7;
        /// <summary>
        /// `FindMergeableStageBatches` 首段「按 `ExecutionBatchDraftKey` 把**全量** `scheduledTasks` 重新分组」
        ///   （O(N) 扫描 + 每个批键一个 `List` 的分配）。该段在**每个 Routing 候选试跑**里被整表重建一次
        ///   ⇒ 实测是 `DemandSchedule` 热点里最大的**未埋点**成本（2026-10-19 定位）。
        /// </summary>
        public const int BatchGroupIndex = 8;
        /// <summary>`ScheduleDemandOperations`（单条需求全部工序的完整排程；`DemandSchedule` 的最大子项）。</summary>
        public const int ScheduleDemandOps = 9;
        /// <summary>
        /// `TryMergeDemandIntoStageBatch`（多工序 Stage 合批候选：移出锚批 → 以锚身份+两需求量之和**重跑完整链**）。
        ///   在锚点循环内逐锚调用（`TryMergeOrSchedule` 的 `stageBatches` 擂台），实测 2026-10-19 单次求解
        ///   调用 84,110 次（= `CloneOccupancy` 115,521 − `CloneTaskList` 31,411）。
        /// </summary>
        public const int TryMergeStageBatch = 10;
        /// <summary>
        /// `TryMergeDemandIntoStageBatch` 的**摘除段**：全量扫描 `scheduledTasks` 找锚批 Task（`ids.Contains`）
        ///   + 逐个 `RemoveAt`（O(N) 内存搬移）+ 摘占用窗/时间线/份额。调用 84,110 次 ⇒ 约 9.4 亿次迭代。
        /// </summary>
        public const int MergeBatchDetach = 11;
        /// <summary>
        /// 2026-10-19 二次定位：`MergeBatchDetach` 的**第 1 段** —— **定位**锚批 Task 的下标
        ///   （快路径 = 预建 `FinalDraftId → 下标` 表逐条查找；慢路径 = `ids` 全表扫描）
        ///   + `removedTasks` 登记 + `CountDetachShape`。**不含** `RemoveAt` 搬移（见 <see cref="DetachRemoveAt"/>）。
        /// </summary>
        public const int DetachIndexBuild = 12;
        /// <summary>
        /// 2026-10-19 二次定位：摘除段的 **`RemoveAt` 搬移循环**（嵌套在 <see cref="DetachIndexBuild"/> 计时区间内，
        ///   故本项已计入第 1 段总读数，此处只是**再切一刀**看「定位」与「搬移」各占多少）。
        ///   形状实测：每次摘 k=3 个、列表 n≈10,568 ⇒ 理论搬移 ≈ k·n·8B ≈ 250 KB/次。
        /// </summary>
        public const int DetachRemoveAt = 13;
        /// <summary>
        /// 2026-10-19 二次定位：`MergeBatchDetach` 的**第 3 段**（逐 Task 摘占用窗 / 时间线登记 / 份额血缘）。
        ///   ⚠ **已停用**，理由同 <see cref="DetachEnsureOwned"/>（实测 1,512 ms ≈ ΣPhase 的 1.7%）。
        ///   ⚠ **已停用**：一次性定位已完成（实测 1,512 ms ≈ ΣPhase 的 1.7%，**不是**摘除段主体）。
        ///   逐 Task 埋点自身开销会污染 `MergeBatchDetach` 总读数，故已从热路径移除。槽位保留以免全仓常量重新编号。
        /// </summary>
        public const int DetachBookkeeping = 14;
        /// <summary>
        /// 2026-10-19 二次定位：`MergeBatchDetach` 的**第 4 段** —— 收尾快照
        ///   （`CaptureOccupancyResourceIds` + `occupancyInsertLog` + `ProductTimeline.Clone()`）。
        /// </summary>
        public const int DetachTail = 15;
    }

    private static readonly AsyncLocal<Counters?> Current = new();

    /// <summary>
    /// V1_4 NEW-06：当前**是否已有**计数作用域。供 <c>FiniteCapacitySolver.SolveAsync</c> 判断
    ///   是否需要**自动建立**作用域（生产 Run 也产生可追溯 Run 快照），已开则由调用方作用域沿用。
    /// </summary>
    internal static bool HasScope => Current.Value is not null;

    /// <summary>
    /// 建立计数作用域。**必须在调用 `SolveAsync` 之前**建立（ExecutionContext 在 async 方法入口捕获）。
    /// ⚠ V1_4 NEW-06 起：`SolveAsync` **自身会在未开作用域时自动建立**（生产 Run 亦可追溯）；
    ///   调用方显式建立的作用域仍然优先（压测工装按此读本次 Run 的计数器）。
    /// </summary>
    internal static Scope BeginScope() => new();

    /// <summary>计数作用域：`Counters` 在 <see cref="Dispose"/> 后仍可安全读取（只是不再累加）。</summary>
    internal sealed class Scope : System.IDisposable
    {
        private readonly Counters? _previous;

        internal Scope()
        {
            Counters = new Counters();
            _previous = Current.Value;
            Current.Value = Counters;
        }

        /// <summary>本次作用域的计数器。</summary>
        public Counters Counters { get; }

        public void Dispose() => Current.Value = _previous;
    }

    /// <summary>
    /// 开始一个 Phase 计时。**未开 scope 时返回 `null`** ⇒ 调用方零开销、零行为影响
    /// （Phase 边界由 <see cref="FiniteCapacitySolver.SolveAsync"/> 埋点）。
    /// </summary>
    internal static System.Diagnostics.Stopwatch? StartPhase()
        => Current.Value is null ? null : System.Diagnostics.Stopwatch.StartNew();

    /// <summary>结束 Phase 计时并登记到对应 Phase 槽（<paramref name="phase"/> ∈ 1..5）。</summary>
    internal static void EndPhase(System.Diagnostics.Stopwatch? sw, int phase)
    {
        if (sw is null) return;
        sw.Stop();
        if (Current.Value is not { } c) return;

        switch (phase)
        {
            case 1: c.Phase1Ms = sw.ElapsedMilliseconds; break;
            case 2: c.Phase2Ms = sw.ElapsedMilliseconds; break;
            case 3: c.Phase3Ms = sw.ElapsedMilliseconds; break;
            case 4: c.Phase4Ms = sw.ElapsedMilliseconds; break;
            case 5: c.Phase5Ms = sw.ElapsedMilliseconds; break;
        }
    }

    internal static void CountBatchPlanCandidateTrial()
    {
        if (Current.Value is { } c) c.BatchPlanCandidateTrials++;
    }

    internal static void CountRoutingTrial()
    {
        if (Current.Value is { } c) c.RoutingTrials++;
    }

    internal static void CountCloneOccupancy()
    {
        if (Current.Value is { } c) c.CloneOccupancy++;
    }

    internal static void CountCloneTaskList()
    {
        if (Current.Value is { } c) c.CloneTaskList++;
    }

    internal static void CountCloneShares()
    {
        if (Current.Value is { } c) c.CloneShares++;
    }

    /// <summary>2026-10-19 性能定位：累计摘除 Task 数 Σk 与摘除时列表长度 Σn。</summary>
    internal static void CountDetachShape(int removedCount, int listLength)
    {
        if (Current.Value is { } c)
        {
            c.DetachTaskCount += removedCount;
            c.DetachListLen += listLength;
        }
    }

    /// <summary>2026-10-19 二次定位：`RunBatchPlan` 总调用次数。</summary>
    internal static void CountRunBatchPlanInvocation()
    {
        if (Current.Value is { } c) c.RunBatchPlanInvocations++;
    }

    /// <summary>2026-10-19 二次定位：进 Stage 合批分支时无索引的次数。</summary>
    internal static void CountMergeStageNoIndex()
    {
        if (Current.Value is { } c) c.MergeStageEnteredWithNullIndex++;
    }

    /// <summary>2026-10-19 二次定位：`BuildTaskIndexById` 调用次数 / 返回 null 次数。</summary>
    internal static void CountTaskIndexBuild(bool nullResult)
    {
        if (Current.Value is not { } c) return;
        c.TaskIndexBuilds++;
        if (nullResult) c.TaskIndexNullResults++;
    }

    /// <summary>2026-10-19 二次定位：摘除段回落全表扫描次数（判「预建下标表」优化命中率）。</summary>
    internal static void CountDetachFallback(int reason)
    {
        if (Current.Value is not { } c) return;
        c.DetachFallbackCount++;
        switch (reason)
        {
            case 0: c.DetachFallbackNoIndex++; break;
            case 1: c.DetachFallbackNullId++; break;
            case 2: c.DetachFallbackMissing++; break;
            default: c.DetachFallbackDupPos++; break;
        }
    }

    internal static void CountSlotSearch()
    {
        if (Current.Value is { } c) c.SlotSearches++;
    }

    internal static void CountPhase4PropagatedTask()
    {
        if (Current.Value is { } c) c.Phase4PropagatedTasks++;
    }

    /// <summary>Phase5 `CompactGaps` 门控放行（进入压实优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5CompactionRun()
    {
        if (Current.Value is { } c) c.Phase5CompactionRuns++;
    }

    /// <summary>Phase5 `OptimizeSetupSequences` 门控放行（进入序列优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupOptimizationRun()
    {
        if (Current.Value is { } c) c.Phase5SetupOptimizationRuns++;
    }

    /// <summary>Phase5 `OptimizeSegment` 真正进入有界局部优化体（可重排段）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupSegmentOptimized()
    {
        if (Current.Value is { } c) c.Phase5SetupSegmentsOptimized++;
    }

    /// <summary>V1_3 F-01：按插入身份回滚占用表时**未命中**条目数 +n。纯观测，不参与判定。</summary>
    internal static void CountOccupancyRollbackUnmatched(int n)
    {
        if (n > 0 && Current.Value is { } c) c.OccupancyRollbackUnmatched += n;
    }

    /// <summary>V1_4 NEW-05：MIXED 回落正排时按身份撤销的倒排残留占用窗数 +n。纯观测，不参与判定。</summary>
    internal static void CountMixedBackwardRollbackWindows(int n)
    {
        if (n > 0 && Current.Value is { } c) c.MixedBackwardRollbackWindows += n;
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账 <c>Commit</c> 次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerCommit()
    {
        if (Current.Value is { } c) c.PiStageLedgerCommits++;
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账按身份回滚次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerRollback()
    {
        if (Current.Value is { } c) c.PiStageLedgerRollbacks++;
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁因输入未投影而不可评估 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateUnevaluated()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateUnevaluated++;
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁判越限/事实不可信并 Fail Closed +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlocked()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateBlocked++;
    }

    /// <summary>
    /// 2026-10-12：**有策略路径**完整评估后**过闸** +1（= 放行档）。
    ///   只在 `ApplyPiLegalQuantityGate` 走完全部判据、未 Conflict 时调用；三条早退不计。纯观测，不参与判定。
    /// </summary>
    internal static void CountPiLegalQuantityGatePassed()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGatePassed++;
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径被同一门禁判未满足 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlockedViaB010Fallback()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateBlockedViaB010Fallback++;
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径过闸并成批 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGatePassedViaB010Fallback()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGatePassedViaB010Fallback++;
    }

    /// <summary>
    /// R-1/R-2（2026-10-12，B-011）：经济软区间偏离的批数登记 +<paramref name="n"/>（**软**偏离、非硬失败）。
    /// 纯观测，不参与判定 —— 让「经济软参数被如实消费」可观测（10 §19「只有一种合法选择时记录限制来源」）。
    /// </summary>
    internal static void CountBatchPolicyInvalid(int n = 1)
    {
        if (Current.Value is { } c) c.BatchPolicyInvalids += n;
    }

    /// <summary>
    /// 2026-10-13：Batch Policy **状态不可获得**（输入契约缺陷）的需求数 —— 见
    /// <see cref="SolverRunCounters.BatchPolicyUnknowns"/>。纯观测，不参与判定。
    /// </summary>
    internal static void CountBatchPolicyUnknown(int n = 1)
    {
        if (Current.Value is { } c) c.BatchPolicyUnknowns += n;
    }

    internal static void CountEconomicBatchDeviation(int n = 1)
    {
        if (Current.Value is { } c) c.EconomicBatchDeviations += n;
    }

    /// <summary>
    /// V1_4 NEW-06：**可检索的 Run 预算正式快照**（进程级，**未开 scope 也可读**）。
    ///
    /// 【为什么需要它】V1_3 F-04 把预算快照写进 <see cref="Counters"/>，而 `Counters` 挂在
    ///   <see cref="AsyncLocal{T}"/> 上、**只有调用方显式 <see cref="BeginScope"/> 才存在** ⇒
    ///   正常生产 Run（`FiniteCapacitySolver.SolveAsync` 未自动开 scope）**根本没有任何记录** ⇒
    ///   复审 NEW-06 判「预算诊断不是生产 Run 正式快照」成立。
    ///   ⇒ 本记录**无论是否开 scope 都写**，并携带可追溯的 Run 版本源三元组
    ///     （`ScheduleRunId` / `StrategyProfileVersionId` / `ParameterSetVersionId`）。
    ///
    /// ⚠ 仍**不是**持久化载体：真正的 Run 快照落盘（`ScheduleRun` 表）归 2号位。
    ///   1号位 提供的是**进程内可检索**的正式快照（版本源 + 生效值），供联验 / 日志 / 诊断读取；
    ///   持久化与 Run 上下文完整性属 1↔2 契约（见回执归口）。
    /// </summary>
    internal sealed record SolverRunBudgetSnapshot(
        long? ScheduleRunId,
        long? StrategyProfileVersionId,
        long? ParameterSetVersionId,
        string BudgetVersion,
        string BudgetSource,
        int MaxOptimizationSplitCount,
        int MaxBatchCandidates,
        long ResolvedAtUtcTicks);

    private static SolverRunBudgetSnapshot? _lastRunBudgetSnapshot;

    /// <summary>
    /// V1_4 NEW-06：**按 Run 可检索**的预算正式快照（键 = `DomainSolveRequest.ScheduleRunId`）。
    ///
    /// 【为什么在 `LastRunBudgetSnapshot` 之外还要这一份】`LastRunBudgetSnapshot` 是「最后写入者胜」
    ///   的**进程级单槽** ⇒ 并发求解（生产多域并行 / 测试集合并行）下会被**别的 Run 覆盖**，
    ///   拿到的快照**不再属于你要查的那个 Run** ⇒ 不满足复审判词的「**可检索**」。
    ///   本表按 `ScheduleRunId` 分槽 ⇒ 每个 Run 的快照**互不覆盖**，可事后按 Run Id 精确检索。
    ///   `ConcurrentDictionary` 保证并发写入安全（读-改-写均原子）。
    ///
    /// ⚠ 仍**不是**持久化载体（进程内）；落盘 `ScheduleRun` 归 2号位。
    /// ⚠ `ScheduleRunId` 为 null 或 0 时**不入表**（无 Run 身份 ⇒ 无可检索键），只写单槽。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SolverRunBudgetSnapshot>
        _runBudgetSnapshotsById = new();

    /// <summary>
    /// V1_4 NEW-06：最近一次**已解析**的 Run 预算正式快照（进程级单槽）。
    /// **未开 scope 也非 null**（只要本进程跑过 `ResolveSolverBatchBudget`）。
    /// 并发求解下为「最后写入者胜」—— 需要**按 Run 精确检索**请用
    /// <see cref="TryGetRunBudgetSnapshot"/>（不被并发覆盖）。
    /// </summary>
    internal static SolverRunBudgetSnapshot? LastRunBudgetSnapshot => _lastRunBudgetSnapshot;

    /// <summary>
    /// V1_4 NEW-06：按 <paramref name="scheduleRunId"/> **精确检索**该 Run 的预算正式快照；
    ///   无该 Run 的记录（未跑过 / 未给 Run Id）返回 <c>null</c>。
    ///   与 <see cref="LastRunBudgetSnapshot"/> 不同，本方法的结果**不受其它 Run 并发写入影响**。
    /// </summary>
    internal static SolverRunBudgetSnapshot? TryGetRunBudgetSnapshot(long scheduleRunId)
        => _runBudgetSnapshotsById.TryGetValue(scheduleRunId, out var snapshot) ? snapshot : null;

    /// <summary>V1_4 NEW-06：仅供测试复位进程级快照与按 Run 检索表（避免用例间串味）。</summary>
    internal static void ResetLastRunBudgetSnapshotForTest()
    {
        _lastRunBudgetSnapshot = null;
        _runBudgetSnapshotsById.Clear();
    }

    /// <summary>
    /// V1_3 F-04 + V1_4 NEW-06：登记本 Run **实际生效**的技术预算快照（版本 / 取源 / 两个生效值 +
    ///   Run 版本源三元组）。**三写**：scope 打开时写 <see cref="Counters"/>（按 Run 隔离），
    ///   同时**无条件**写进程级 <see cref="LastRunBudgetSnapshot"/>（单槽，最后写入者胜），
    ///   并按 <paramref name="scheduleRunId"/> 写 <see cref="_runBudgetSnapshotsById"/>（按 Run 可检索）。
    /// </summary>
    internal static void RecordSolverBatchBudget(
        long? scheduleRunId,
        long? strategyProfileVersionId,
        long? parameterSetVersionId,
        string version,
        string source,
        int maxOptimizationSplitCount,
        int maxBatchCandidates)
    {
        var snapshot = new SolverRunBudgetSnapshot(
            scheduleRunId, strategyProfileVersionId, parameterSetVersionId,
            version, source, maxOptimizationSplitCount, maxBatchCandidates,
            DateTime.UtcNow.Ticks);

        _lastRunBudgetSnapshot = snapshot;

        // 按 Run 身份分槽（无 Run 身份不入表）—— 使「未开 scope 也**可按 Run 检索**」成立。
        if (scheduleRunId is { } runId && runId != 0L)
        {
            _runBudgetSnapshotsById[runId] = snapshot;
        }

        if (Current.Value is not { } c) return;
        c.SolverBatchBudgetVersion = version;
        c.SolverBatchBudgetSource = source;
        c.SolverBatchBudgetMaxOptimizationSplitCount = maxOptimizationSplitCount;
        c.SolverBatchBudgetMaxBatchCandidates = maxBatchCandidates;
    }

    // ══════════════════════════════════════════════════════════════════
    // 热点计时（**零分配**）：未开 scope 时 `HotspotStart()` 返回 0 ⇒ `HotspotEnd` 立刻返回
    //   ⇒ 生产路径只多两次比较，无行为影响、无分配。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>开始一次热点计时；未开 scope 返回 0（调用方原样传回 <see cref="HotspotEnd"/>）。</summary>
    internal static long HotspotStart()
        => Current.Value is null ? 0L : System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>结束热点计时并累计到 <paramref name="hotspot"/> 槽。</summary>
    internal static void HotspotEnd(long startTimestamp, int hotspot)
    {
        if (startTimestamp == 0L) return;
        if (Current.Value is not { } c) return;
        c.AddHotspotTicks(hotspot, System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp);
    }
}
