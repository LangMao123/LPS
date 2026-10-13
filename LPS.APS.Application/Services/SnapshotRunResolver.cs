using Dapper;
using LPS.APS.Core.Exceptions;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 决定「本次排程该读哪一批**原始 MES 快照**（<c>ScheduleRun</c> 分区）」。
///
/// 依据 0号位《PM回复1009-1》§三 A-Q1 / A-Q2 / A-Q3：
/// <list type="bullet">
/// <item>夜间 FULL：读本 Run 自身的完整三类快照（<c>工单 / 工序 / Stage</c>）。</item>
/// <item>白天 Candidate / FAILED 恢复：**继承 Base 链上那批完整快照**——不得读「Run 号相同的快照」、
/// 不得读「最新同步的快照」，也不得用 <c>MAX(RunId)</c> / 最新 <c>RUNNING</c> / 静默 <c>COALESCE</c> 猜来源。</item>
/// <item>追溯路径：本 PlanVersion 所属 Run → <c>ScheduleRun.BasePlanVersionId</c> → Base PlanVersion →
/// 该 PlanVersion 所属 Run → 逐跳上溯，直到命中「有三类完整快照」的源 Run。</item>
/// <item>断链 / 成环 / Base 缺失 ⇒ 抛 <see cref="SnapshotRunResolutionException"/>（fail-closed）。</item>
/// </list>
///
/// <para>
/// <b>⚠️ 读 / 写分离（A-Q2 四类表）—— 本解析结果<b>只</b>用于「读原始 MESWorkOrder / Operation / Stage 快照」。</b>
/// 候选自身的运行身份 / 账本 / Task / Explanation 归属、以及本次重算的候选衍生结果
/// （如 <c>ProductionInstructionPositionSnapshot</c>）必须落**候选自己的 Run**，绝不能用本解析结果替换——
/// A-Q2 明令「不能把 <c>PeggingOrchestrator</c> 里所有使用 <c>SourceScheduleRunId</c> 的读取/写入位置统一替换为快照源 Run 号」。
/// </para>
///
/// <para>
/// <b>⚠️ 完整性证据（2026-10-12 定案）：</b>A-Q3 要求「三类装载成功证据齐备、同源 Run 与一致业务截止；
/// 不允许仅通过行数&gt;0 证明完整」，并优先复用 <c>APS_ETL_Log</c>。判据据此定为 <b>Run 级同步成功证据</b>：
/// <c>APS_ETL_Log.BatchNo</c> 本就等于 Run 号（三个快照 SP 分别写 <c>Cast(@ScheduleRunId AS NVARCHAR(50))</c> /
/// <c>CONVERT(NVARCHAR(30), @ScheduleRunId, 120)</c>，索引 <c>IX_ETL_Log_Batch(BatchNo, Step, CreatedAt)</c> 早已存在）
/// ⇒「该 Run 号在三个快照 Step 上**各自最新一条终态记录**均为 <c>Status='SUCCESS'</c>」即判完整
/// （取「最新终态」而非「曾有 SUCCESS」，理由见 <see cref="HasCompleteSnapshotBatchAsync"/>）。
/// </para>
/// <para>
/// <b>两个旧判据都已撤掉：</b>①「三表行数&gt;0」—— 合法同步成功、该类事实确实为零行会被误判为失败，
/// 正是 `PM回复1010.md` §四 第 81 行要求区分的情形；②「三表 <c>DataCutoffTime</c> 唯一」——
/// 同源早已由构造保证：三个 SP 由同一次夜间流程传入**同一个** <c>ScheduleRun.DataCutoffTime</c>
/// （<c>MESSnapshotSyncService.SyncSnapshotAsync</c>），残留的不一致来自事后人工重跑，不是设计形态，也不能反过来当判据。
/// </para>
/// <para>
/// <b>2026-10-12 曾试加 <c>RunId</c> 列，复验该关联本已可用 ⇒ 已按 §T-01 门槛回滚</b>
/// （列与索引已删、三个 SP 正文已从留底 <c>dbo.APS_ETL_Log_SPBackup_20261012</c> 恢复）。
/// </para>
/// </summary>
internal static class SnapshotRunResolver
{
    internal readonly record struct PlanVersionNode(int Id, int? SourceScheduleRunId);

    internal readonly record struct ScheduleRunNode(int Id, int? BasePlanVersionId);

    /// <summary>
    /// DB 封装：读全部 <c>PlanVersion</c> / <c>ScheduleRun</c> 元数据（两表皆为小字典）后走纯逻辑
    /// <see cref="ResolveCoreAsync"/>。仅供 2号位 的原始 MES 快照读点调用。
    /// </summary>
    internal static async Task<int> ResolveSnapshotRunIdAsync(
        DatabaseConnectionManager connectionManager,
        int planVersionId,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var pvRows = await connectionManager.QueryAsync<PvRow>(
            "SELECT Id, SourceScheduleRunId FROM PlanVersion", null, db: DatabaseId.APS);
        var runRows = await connectionManager.QueryAsync<RunRow>(
            "SELECT Id, BasePlanVersionId FROM ScheduleRun", null, db: DatabaseId.APS);

        var planVersions = pvRows.ToDictionary(r => r.Id, r => new PlanVersionNode(r.Id, r.SourceScheduleRunId));
        var runs = runRows.ToDictionary(r => r.Id, r => new ScheduleRunNode(r.Id, r.BasePlanVersionId));

        // 链上同一 Run 可能被重复问及 ⇒ 缓存，避免重复查三表。
        var completeCache = new Dictionary<int, bool>();
        async Task<bool> CompleteAsync(int runId)
        {
            if (completeCache.TryGetValue(runId, out var cached)) return cached;
            var v = await HasCompleteSnapshotBatchAsync(connectionManager, runId);
            completeCache[runId] = v;
            return v;
        }

        var resolved = await ResolveCoreAsync(planVersionId, planVersions, runs, CompleteAsync);
        logger?.LogInformation(
            "[Pegging] 原始 MES 快照源 Run 解析：请求 PlanVersionId={PlanVersionId} ⇒ 读快照 Run={SnapshotRunId}",
            planVersionId, resolved);
        return resolved;
    }

    /// <summary>
    /// 纯逻辑（无 DB，可单测）。给定 PV / Run 元数据与「某 Run 是否具备三类完整快照」的判定委托，
    /// 返回应当读取的源快照 RunId；断链 / 成环 / Base 缺失时抛 <see cref="SnapshotRunResolutionException"/>。
    /// </summary>
    internal static async Task<int> ResolveCoreAsync(
        int planVersionId,
        IReadOnlyDictionary<int, PlanVersionNode> planVersions,
        IReadOnlyDictionary<int, ScheduleRunNode> runs,
        Func<int, Task<bool>> hasCompleteSnapshotBatchAsync)
    {
        if (!planVersions.TryGetValue(planVersionId, out var pv))
            throw new SnapshotRunResolutionException($"PlanVersionId={planVersionId} 不存在，无法确定快照源 Run");

        if (pv.SourceScheduleRunId is not int ownRunId || ownRunId <= 0)
            throw new SnapshotRunResolutionException($"PlanVersionId={planVersionId} 无 SourceScheduleRunId，无法确定快照源 Run");

        if (!runs.TryGetValue(ownRunId, out var ownRun))
            throw new SnapshotRunResolutionException($"ScheduleRunId={ownRunId} 不存在（PlanVersionId={planVersionId}）");

        // 无 Base ⇒ 本 PV 所属 Run 即原始来源（夜间 FULL）。夜间时序为
        // 「00:30 nightly-batch 建 Run 并锁 DataCutoffTime → 00:40/45/50 三个快照 SP 同步 → 02:00 scheduling-trigger 排程」，
        // 故本 Run 必须自证「三类快照同步成功」；同步缺失/失败 ⇒ fail-closed，
        // 不得把空快照当成零在制继续排（`PM回复1010.md` §四 第 74–81 行）。
        if (ownRun.BasePlanVersionId is not int basePvId || basePvId <= 0)
        {
            if (!await hasCompleteSnapshotBatchAsync(ownRunId))
                throw new SnapshotRunResolutionException(
                    $"Run{ownRunId}（PlanVersionId={planVersionId}，无 Base）不具备三类 MES 快照的 Run 级同步成功证据 ⇒ fail-closed");
            return ownRunId;
        }

        // 有 Base ⇒ 候选 / FAILED 恢复：不得读候选自身 Run，须沿 Base 链找到「有三类完整快照」的源 Run。
        var visited = new HashSet<int> { planVersionId };
        var trail = new List<string> { $"PV{planVersionId}(Run{ownRunId})" };
        var cursorPv = basePvId;
        while (true)
        {
            if (!visited.Add(cursorPv))
                throw new SnapshotRunResolutionException($"Base 链成环：{string.Join(" → ", trail)} → PV{cursorPv}");

            if (!planVersions.TryGetValue(cursorPv, out var node))
                throw new SnapshotRunResolutionException($"Base 链上 PlanVersionId={cursorPv} 不存在：{string.Join(" → ", trail)}");

            if (node.SourceScheduleRunId is not int runId || runId <= 0)
                throw new SnapshotRunResolutionException($"Base 链上 PlanVersionId={cursorPv} 无 SourceScheduleRunId：{string.Join(" → ", trail)}");

            if (!runs.TryGetValue(runId, out var run))
                throw new SnapshotRunResolutionException($"Base 链上 ScheduleRunId={runId} 不存在：{string.Join(" → ", trail)}");

            trail.Add($"PV{cursorPv}(Run{runId})");

            if (await hasCompleteSnapshotBatchAsync(runId))
                return runId;

            if (run.BasePlanVersionId is not int nextBase || nextBase <= 0)
                throw new SnapshotRunResolutionException(
                    $"Base 链未命中完整三类 MES 快照且无更上层 Base ⇒ fail-closed：{string.Join(" → ", trail)}");

            cursorPv = nextBase;
        }
    }

    /// <summary>
    /// 某 Run 是否具备「完整三类 MES 快照」——判据 = <b>Run 级同步成功证据，取每 Step 的「最新一次终态」</b>：
    /// <c>APS_ETL_Log</c> 中该 Run 号在三个快照 Step 上，**各自最后一条终态记录**均为 <c>Status='SUCCESS'</c>。
    ///
    /// <list type="bullet">
    /// <item><b>为什么取「最新终态」而不是「曾有 SUCCESS」</b>：同一 Step 可因人工重跑/异常恢复写入多条记录。
    /// 只要历史上成功过就判完整，会让 **`SUCCESS` → 之后 `FAILED`/`ERROR`** 的 Step 仍被算过，
    /// 即用历史成功掩盖后续失败（<c>PM回复1010.md</c> §七「须验证…三类同步成功记录的**最新有效性**，
    /// 不能用历史 SUCCESS 掩盖后续失败」）。</item>
    /// <item><b>为什么终态集只含 SUCCESS/FAILED/ERROR</b>：三个 SP 在同一次执行里还会写 <c>INFO</c>（进度）
    /// 与 <c>WARN</c>（**辅助动作失败但不影响同步本身**，实测三 SP 的 WARN 均出自历史分区清理失败的 catch，
    /// 且可能晚于 SUCCESS 数毫秒写入）。它们是同一次执行的旁路行、不是同步结论，
    /// 若拿「按时间最后一行」当判据会把清理告警误判成同步失败。故只比**终态**之间谁最新。</item>
    /// <item><b>不以行数&gt;0 为判据</b>：合法同步成功但该类事实确实零行（如当天 MES 无在制）同样算完整。</item>
    /// </list>
    /// </summary>
    private static async Task<bool> HasCompleteSnapshotBatchAsync(
        DatabaseConnectionManager connectionManager, int runId)
    {
        // 取该 Run 号在这三个快照 Step 上的**全部**日志行（终态与旁路行都要读回来，
        // 判据本身在纯函数里，便于单测「先成功后失败」这类时序），逐个 Run 量级 ~10 行。
        var rows = await connectionManager.QueryAsync<EtlLogRow>(
            @"SELECT Step, Status, CreatedAt, Id
              FROM dbo.APS_ETL_Log
              WHERE BatchNo = CAST(@RunId AS NVARCHAR(50))
                AND Step IN ('sp_SyncMESWorkOrderSnapshot',
                             'sp_SyncMESOperationProgressSnapshot',
                             'sp_SyncMESStageProgressSnapshot')",
            new { RunId = runId },
            db: DatabaseId.APS);

        return IsCompleteSnapshotEvidence(rows);
    }

    /// <summary>
    /// 纯判据（无 DB，可单测）：三个快照 Step **各自最后一条终态记录**均为 <c>SUCCESS</c>。
    /// 缺任一 Step 的终态记录 ⇒ 不完整（fail-closed）。
    /// </summary>
    internal static bool IsCompleteSnapshotEvidence(IEnumerable<EtlLogRow> rows)
    {
        var latestTerminalByStep = new Dictionary<string, EtlLogRow>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            // INFO/WARN 是同一次执行的进度行与旁路告警行（见方法注释），不参与终态比较。
            if (!IsTerminalStatus(row.Status)) continue;

            if (!latestTerminalByStep.TryGetValue(row.Step, out var prev)
                || IsLaterThan(row, prev))
            {
                latestTerminalByStep[row.Step] = row;
            }
        }

        return SnapshotStepNames.Names.All(step =>
            latestTerminalByStep.TryGetValue(step, out var latest)
            && string.Equals(latest.Status, "SUCCESS", StringComparison.Ordinal));
    }

    /// <summary>终态 = SP 对本次同步给出的结论；<c>INFO</c>（进度）与 <c>WARN</c>（辅助动作告警）不是结论。</summary>
    private static bool IsTerminalStatus(string status) =>
        status is "SUCCESS" or "FAILED" or "ERROR";

    /// <summary>CreatedAt 同秒/同毫秒时用自增 <c>Id</c> 兜底定序（写入顺序即日志顺序）。</summary>
    private static bool IsLaterThan(EtlLogRow candidate, EtlLogRow current) =>
        candidate.CreatedAt != current.CreatedAt
            ? candidate.CreatedAt > current.CreatedAt
            : candidate.Id > current.Id;

    /// <summary>三类 MES 快照 Step 名（与三个 SP 的 <c>APS_ETL_Log.BatchNo</c> 打戳位置对应）。</summary>
    internal static class SnapshotStepNames
    {
        internal static readonly string[] Names =
        {
            "sp_SyncMESWorkOrderSnapshot",
            "sp_SyncMESOperationProgressSnapshot",
            "sp_SyncMESStageProgressSnapshot"
        };
    }

    internal sealed class EtlLogRow
    {
        public string Step { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long Id { get; set; }
    }

    private sealed class PvRow
    {
        public int Id { get; set; }
        public int? SourceScheduleRunId { get; set; }
    }

    private sealed class RunRow
    {
        public int Id { get; set; }
        public int? BasePlanVersionId { get; set; }
    }
}
