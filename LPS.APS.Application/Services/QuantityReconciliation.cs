using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 「生产需求净量 = 已排净量 + 未排净量」这条**恒等式**的纯内存计算核。
///
/// 【为什么要独立成一个类】
///   该恒等式的原始实现**内联**在 `PeggingOrchestrator.PersistDomainAndPeggingInTransactionAsync`
///   的事务体里（B8 未排净量 ＋ B10 Run 级三量对账）。内联 ⇒ **无法被测试** ⇒ 键域取错也没人发现。
///   2026-10-13 P3「未排净量闭合验证」正是为此：把计算搬成纯函数，恒等式与它的**四种失效模式**
///   由单测逐条钉住（`QuantityReconciliationTests`）。纯函数不做 IO、不依赖库、不调求解器。
///
/// 【键域（本类存在的全部理由）—— 三个 key 只有两个是同一命名空间】
///   · `LogicalProductionDemand.LogicalDemandKey` = `{PlanVersionId}_{AllocationSequence}`
///     （见 `BuildLogicalProductionDemand`；分片再带 `/WO:{MESWorkOrderNo}`、`/FREE` 后缀）。
///   · `UnscheduledTaskResult.DraftId` = **同一个** `LogicalDemandKey`（1号位 原样回传）。
///   · `SupplyAllocation.DemandKey` = `ORDER_{OrderId}_{MaterialCode}_{Seq}` —— **业务需求键，
///     与上者不同名空间**（`ExtractOrderIdFromDemandKey` 能从它解出订单号，LogicalDemandKey 不能）。
///   ⇒ 任何「按 DemandKey 建索引、按 LogicalDemandKey 查表」的写法都**恒不命中**。
///   ⇒ 份额归集必须走 1号位 自己的任务↔需求链接：`FinalTaskDraft.FinalDraftId → SourceDraftId`，
///     后者即 `LogicalDemandKey`（含分片后缀），与 `UnscheduledTaskResult.DraftId` 同空间。
///
/// 【恒等式成立的两个前提，以及各自失守时的可观测信号】
///   ①并集完整（不丢需求）→ `MissingDemandKeys`（既无份额、又未报未排程）
///   ②交集为空（不重复）→ `OverlapDetected`（既排了又报未排程）
///   另有两条**原设计覆盖不到**的失效模式，本类补上：
///   ③**部分已排却未报未排程** → `UnderScheduledDemandKeys`。它会让 Δ≠0，但**不在** ①的名单里
///     （它确实产生了份额）⇒ 旧口径会出现「Δ 报警但清单为空」的哑火。
///   ④份额归到不在需求集内的键 → `UnmatchedScheduledKeys`（1号位 回传了 voucher 里没有的需求键）。
/// </summary>
public static class QuantityReconciliation
{
    /// <summary>份额归集的产物。`UnmappedShareCount` &gt; 0 表示有份额**未能**归到任何需求（须显性告警，不得静默）。</summary>
    public sealed record ScheduledAggregation(
        IReadOnlyDictionary<string, decimal> ScheduledQtyByDemandKey,
        int UnmappedShareCount);

    /// <summary>Run 级三量对账结果（对应 B10 写入 `ObjectType='DOMAIN'` 的那一行）。</summary>
    public sealed record RunLevelReconciliation(
        decimal DemandNetQtyTotal,
        decimal ScheduledQtyTotal,
        decimal UnscheduledQtyTotal,
        decimal DeltaQty,
        int DemandCount,
        int ScheduledDemandCount,
        int UnscheduledDemandCount,
        IReadOnlyList<string> MissingDemandKeys,
        IReadOnlyList<string> UnderScheduledDemandKeys,
        IReadOnlyList<string> UnmatchedScheduledKeys);

    /// <summary>
    /// 单个需求的未排净量：`max(需求净量 − 已排份额, 0)`，并给出重叠标记。
    /// `OverlapDetected=true` ⇒ 该需求**部分已排**却仍被 1号位 列为未排程，「整单记账」假定被打破。
    /// </summary>
    public static (decimal UnscheduledQty, bool OverlapDetected) PerDemandRemaining(
        decimal netOutputQty, decimal scheduledShareQty)
    {
        var remaining = netOutputQty - scheduledShareQty;
        if (remaining < 0m) remaining = 0m;
        return (remaining, scheduledShareQty > 0m);
    }

    /// <summary>
    /// 把求解器回传的份额按 **LogicalDemandKey** 聚合。
    ///
    /// 归集路径：`AllocationShare.FinalDraftId` → `FinalTaskDraft.SourceDraftId`(=LogicalDemandKey) → 累加 `ComponentQty`。
    /// **不经过 `SupplyAllocation.DemandKey`**（命名空间不同，见类注释）。
    /// 一层派生（`FinalDraftId→SourceDraftId`）即天然覆盖分片：一个 `AllocationSequence` 可派生
    /// 多个 `LogicalDemandKey`（`/WO:…`、`/FREE`），按需求身份归集才与 `UnscheduledTaskResult.DraftId` 可比。
    /// </summary>
    public static ScheduledAggregation AggregateScheduledByDemandKey(
        IReadOnlyList<FinalTaskDraft> finalTasks,
        IReadOnlyList<AllocationTaskShare> shares)
    {
        var draftIdToDemandKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in finalTasks)
        {
            if (!string.IsNullOrEmpty(f.FinalDraftId) && !string.IsNullOrEmpty(f.SourceDraftId))
                draftIdToDemandKey[f.FinalDraftId] = f.SourceDraftId;
        }

        var byDemandKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var unmapped = 0;
        foreach (var share in shares)
        {
            if (!draftIdToDemandKey.TryGetValue(share.FinalDraftId, out var demandKey)
                || string.IsNullOrEmpty(demandKey))
            {
                unmapped++;   // 不静默：由调用方计数告警
                continue;
            }

            byDemandKey.TryGetValue(demandKey, out var acc);
            byDemandKey[demandKey] = acc + share.ComponentQty;
        }

        return new ScheduledAggregation(byDemandKey, unmapped);
    }

    /// <summary>
    /// Run 级三量对账：`Δ = 需求净量Σ − (已排净量Σ + 未排净量Σ)`。
    /// Δ≠0 或任一清单非空 ⇒ 恒等式未闭合（调用方据此置 `Severity='ERROR'`）。**本函数不抛、不阻断**
    /// （PM 2026-09-11 硬口径：记账差异不得反过来阻断落库）。
    /// </summary>
    public static RunLevelReconciliation Reconcile(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyDictionary<string, decimal> scheduledQtyByDemandKey,
        IReadOnlyList<UnscheduledTaskResult> unscheduledTasks)
    {
        var demandNetQtyTotal = 0m;
        var netByDemandKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var d in demands)
        {
            if (string.IsNullOrEmpty(d.LogicalDemandKey)) continue;
            // 同键重复出现不得重复计入总量 ⇒ 只记首条。**总量必须与去重后的字典同源**，
            // 否则 Δ 会因重复键凭空多出来（本实现的第一版即栽在这：总量循环里累加、字典里去重）。
            if (netByDemandKey.ContainsKey(d.LogicalDemandKey)) continue;
            netByDemandKey[d.LogicalDemandKey] = d.NetOutputQty;
            demandNetQtyTotal += d.NetOutputQty;
        }

        var unscheduledKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in unscheduledTasks) unscheduledKeys.Add(u.DraftId);

        var missing = new List<string>();
        var underScheduled = new List<string>();

        foreach (var kv in netByDemandKey)
        {
            var key = kv.Key;
            scheduledQtyByDemandKey.TryGetValue(key, out var scheduled);
            if (unscheduledKeys.Contains(key)) continue;   // 已报未排程 ⇒ 由 ②/未排净量 管辖

            if (scheduled <= 0m)
            {
                missing.Add(key);              // ③之一：既无份额、又未报未排程
            }
            else if (scheduled < kv.Value)
            {
                underScheduled.Add(key);       // ③之二：有份额但不足，且未报未排程（旧口径的哑火点）
            }
        }

        var scheduledQtyTotal = 0m;
        foreach (var v in scheduledQtyByDemandKey.Values) scheduledQtyTotal += v;

        var unscheduledQtyTotal = 0m;
        foreach (var u in unscheduledTasks)
        {
            netByDemandKey.TryGetValue(u.DraftId, out var net);
            scheduledQtyByDemandKey.TryGetValue(u.DraftId, out var scheduled);
            var (rest, _) = PerDemandRemaining(net, scheduled);
            unscheduledQtyTotal += rest;
        }

        var unmatched = new List<string>();
        foreach (var key in scheduledQtyByDemandKey.Keys)
        {
            if (!netByDemandKey.ContainsKey(key)) unmatched.Add(key);
        }

        return new RunLevelReconciliation(
            DemandNetQtyTotal: demandNetQtyTotal,
            ScheduledQtyTotal: scheduledQtyTotal,
            UnscheduledQtyTotal: unscheduledQtyTotal,
            DeltaQty: demandNetQtyTotal - (scheduledQtyTotal + unscheduledQtyTotal),
            DemandCount: netByDemandKey.Count,
            ScheduledDemandCount: scheduledQtyByDemandKey.Count,
            UnscheduledDemandCount: unscheduledKeys.Count,
            MissingDemandKeys: missing,
            UnderScheduledDemandKeys: underScheduled,
            UnmatchedScheduledKeys: unmatched);
    }
}
