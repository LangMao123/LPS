using LPS.APS.Application.Services;
using LPS.APS.Core.Exceptions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 原始 MES 快照源 Run 解析（Base 链）纯逻辑测试 —— 0号位《PM回复1009-1》§三 A-Q1/A-Q2/A-Q3。
/// 契约：
/// <list type="bullet">
/// <item>无 Base（夜间 FULL）⇒ 读本 PV 所属 Run，**但须自证「三类快照 Run 级同步成功证据」**；无证据 ⇒ 抛（fail-closed）。</item>
/// <item>有 Base（候选 / FAILED 恢复）⇒ 沿 <c>ScheduleRun.BasePlanVersionId</c> 逐跳上溯，命中「有三类完整快照」的源 Run。</item>
/// <item>断链 / 成环 / Base PlanVersion 缺失 ⇒ <see cref="SnapshotRunResolutionException"/>（fail-closed，不静默降级）。</item>
/// </list>
/// </summary>
public class SnapshotRunResolverTests
{
    private static Dictionary<int, SnapshotRunResolver.PlanVersionNode> Pvs(params (int Id, int? Run)[] items)
        => items.ToDictionary(i => i.Id, i => new SnapshotRunResolver.PlanVersionNode(i.Id, i.Run));

    private static Dictionary<int, SnapshotRunResolver.ScheduleRunNode> Runs(params (int Id, int? BasePv)[] items)
        => items.ToDictionary(i => i.Id, i => new SnapshotRunResolver.ScheduleRunNode(i.Id, i.BasePv));

    private static Func<int, Task<bool>> Complete(params int[] completeRuns)
        => runId => Task.FromResult(completeRuns.Contains(runId));

    [Fact(DisplayName = "夜间无Base且有Run级快照同步证据 ⇒ 读本Run")]
    public async Task Nightly_NoBase_ReadsOwnRun()
    {
        var pvs = Pvs((10, 1));
        var runs = Runs((1, null));

        var resolved = await SnapshotRunResolver.ResolveCoreAsync(10, pvs, runs, Complete(1));

        Assert.Equal(1, resolved);
    }

    [Fact(DisplayName = "夜间无Base但无快照同步证据（同步缺失/失败）⇒ fail-closed，不当作零在制继续排")]
    public async Task Nightly_NoBase_WithoutSnapshotEvidence_FailsClosed()
    {
        var pvs = Pvs((10, 1));
        var runs = Runs((1, null));

        await Assert.ThrowsAsync<SnapshotRunResolutionException>(
            () => SnapshotRunResolver.ResolveCoreAsync(10, pvs, runs, Complete()));
    }

    [Fact(DisplayName = "候选一跳 ⇒ 继承Base的完整快照Run，不读候选自身Run")]
    public async Task Candidate_OneHop_InheritsBaseSnapshotRun()
    {
        var pvs = Pvs((20, 5), (10, 1));      // 候选 PV20→Run5；Base PV10→Run1
        var runs = Runs((5, 10), (1, null));  // Run5.Base=PV10；Run1 无 Base
        var complete = Complete(1);           // 仅 Run1 有完整三类快照

        var resolved = await SnapshotRunResolver.ResolveCoreAsync(20, pvs, runs, complete);

        Assert.Equal(1, resolved);            // 不是候选自身的 Run5
    }

    [Fact(DisplayName = "候选两跳（基于已激活候选）⇒ 沿Base链继续上溯到夜间源Run")]
    public async Task Candidate_TwoHops_WalksChainToNightlyRun()
    {
        var pvs = Pvs((30, 7), (20, 5), (10, 1));
        var runs = Runs((7, 20), (5, 10), (1, null));
        var complete = Complete(1);

        var resolved = await SnapshotRunResolver.ResolveCoreAsync(30, pvs, runs, complete);

        Assert.Equal(1, resolved);
    }

    [Fact(DisplayName = "命中链中第一个有完整快照的Run即停（不越级到更上层）")]
    public async Task Candidate_StopsAtFirstCompleteRun()
    {
        var pvs = Pvs((30, 7), (20, 5), (10, 1));
        var runs = Runs((7, 20), (5, 10), (1, null));
        var complete = Complete(5, 1);        // Run5 也具备完整快照

        var resolved = await SnapshotRunResolver.ResolveCoreAsync(30, pvs, runs, complete);

        Assert.Equal(5, resolved);
    }

    [Fact(DisplayName = "Base链成环 ⇒ fail-closed")]
    public async Task Cycle_FailsClosed()
    {
        var pvs = Pvs((20, 5), (10, 1));
        var runs = Runs((5, 10), (1, 20));    // Run1.Base 回指 PV20 ⇒ 环
        var complete = Complete();            // 无任何 Run 完整

        await Assert.ThrowsAsync<SnapshotRunResolutionException>(
            () => SnapshotRunResolver.ResolveCoreAsync(20, pvs, runs, complete));
    }

    [Fact(DisplayName = "Base链断链（无更上层Base且未命中完整快照）⇒ fail-closed")]
    public async Task BrokenChain_FailsClosed()
    {
        var pvs = Pvs((20, 5), (10, 1));
        var runs = Runs((5, 10), (1, null));
        var complete = Complete();            // Run1 无完整快照且无 Base

        await Assert.ThrowsAsync<SnapshotRunResolutionException>(
            () => SnapshotRunResolver.ResolveCoreAsync(20, pvs, runs, complete));
    }

    [Fact(DisplayName = "Base链上PlanVersion无SourceScheduleRunId ⇒ fail-closed")]
    public async Task BasePvWithoutSourceRun_FailsClosed()
    {
        var pvs = Pvs((20, 5), (10, null));
        var runs = Runs((5, 10), (1, null));

        await Assert.ThrowsAsync<SnapshotRunResolutionException>(
            () => SnapshotRunResolver.ResolveCoreAsync(20, pvs, runs, Complete(1)));
    }

    [Fact(DisplayName = "起始PlanVersion不存在 ⇒ fail-closed")]
    public async Task StartPvMissing_FailsClosed()
    {
        await Assert.ThrowsAsync<SnapshotRunResolutionException>(
            () => SnapshotRunResolver.ResolveCoreAsync(99, Pvs(), Runs(), Complete()));
    }

    // ── 完整性证据判据（纯函数）：每 Step **最新终态**须为 SUCCESS（PM回复1010 §七）──

    private const string Wo = "sp_SyncMESWorkOrderSnapshot";
    private const string Op = "sp_SyncMESOperationProgressSnapshot";
    private const string St = "sp_SyncMESStageProgressSnapshot";

    private static SnapshotRunResolver.EtlLogRow Row(string step, string status, int seconds, long id = 0)
        => new()
        {
            Step = step,
            Status = status,
            CreatedAt = new DateTime(2026, 10, 12, 0, 0, 0).AddSeconds(seconds),
            Id = id == 0 ? seconds : id
        };

    /// <summary>三类各一条 SUCCESS ⇒ 完整。这是夜间正常形态。</summary>
    [Fact(DisplayName = "完整性证据：三 Step 各一条 SUCCESS ⇒ 完整")]
    public void Evidence_ThreeSuccess_IsComplete()
    {
        Assert.True(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2), Row(St, "SUCCESS", 3)
        }));
    }

    /// <summary>
    /// 🔴 本次整改的正题：某 Step **先 SUCCESS、后 FAILED**（重跑/异常恢复失败）
    /// ⇒ 只按「历史上出现过 SUCCESS」会误判完整，等于用历史成功掩盖后续失败。
    /// </summary>
    [Fact(DisplayName = "完整性证据：Step 先SUCCESS后FAILED ⇒ 不完整（历史SUCCESS不得掩盖后续失败）")]
    public void Evidence_SuccessThenFailed_IsIncomplete()
    {
        Assert.False(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2),
            Row(St, "SUCCESS", 3),
            Row(St, "FAILED", 9)          // 之后重跑失败
        }));
    }

    /// <summary>先 FAILED 后 SUCCESS（重跑成功）⇒ 以最新终态为准，判完整。</summary>
    [Fact(DisplayName = "完整性证据：Step 先FAILED后SUCCESS（重跑成功）⇒ 完整")]
    public void Evidence_FailedThenSuccess_IsComplete()
    {
        Assert.True(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "FAILED", 1), Row(Wo, "SUCCESS", 5),
            Row(Op, "SUCCESS", 2), Row(St, "SUCCESS", 3)
        }));
    }

    /// <summary>ERROR 亦为终态：最新终态为 ERROR ⇒ 不完整（fail-closed，不当作零在制继续排）。</summary>
    [Fact(DisplayName = "完整性证据：Step 最新终态为 ERROR ⇒ 不完整")]
    public void Evidence_LatestError_IsIncomplete()
    {
        Assert.False(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2),
            Row(St, "SUCCESS", 3),
            Row(Op, "ERROR", 7)
        }));
    }

    /// <summary>
    /// <c>INFO</c>/<c>WARN</c> 是**同一次执行**的进度行与辅助动作告警行（实库三 SP 的 WARN 均出自
    /// 历史分区清理失败的 catch，且可能晚于 SUCCESS 数毫秒写入）⇒ 不得把「时间最后一行」当同步结论。
    /// </summary>
    [Fact(DisplayName = "完整性证据：SUCCESS 之后跟着 INFO/WARN 旁路行 ⇒ 仍判完整")]
    public void Evidence_TrailingInfoWarn_StillComplete()
    {
        Assert.True(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Wo, "WARN", 2),
            Row(Op, "SUCCESS", 1), Row(Op, "INFO", 2, id: 3),
            Row(St, "SUCCESS", 1), Row(St, "WARN", 4)
        }));
    }

    /// <summary>缺任一 Step 的终态记录 ⇒ 不完整（不加「缺则跳过」的宽容）。</summary>
    [Fact(DisplayName = "完整性证据：缺某 Step 的终态记录 ⇒ 不完整")]
    public void Evidence_MissingStep_IsIncomplete()
    {
        Assert.False(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2),
            Row(St, "INFO", 3)            // 只有旁路行、无终态
        }));
    }

    /// <summary>同秒写入时以自增 Id 定序：SUCCESS(Id=1) → FAILED(Id=2) ⇒ 以 FAILED 为最新。</summary>
    [Fact(DisplayName = "完整性证据：CreatedAt 同秒 ⇒ 以自增 Id 定序判最新终态")]
    public void Evidence_SameTimestamp_OrdersById()
    {
        Assert.False(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2),
            Row(St, "SUCCESS", 3, id: 10), Row(St, "FAILED", 3, id: 11)
        }));

        Assert.True(SnapshotRunResolver.IsCompleteSnapshotEvidence(new[]
        {
            Row(Wo, "SUCCESS", 1), Row(Op, "SUCCESS", 2),
            Row(St, "FAILED", 3, id: 10), Row(St, "SUCCESS", 3, id: 11)
        }));
    }

    /// <summary>空日志（该 Run 号无三快照记录）⇒ 不完整。</summary>
    [Fact(DisplayName = "完整性证据：无任何记录 ⇒ 不完整")]
    public void Evidence_Empty_IsIncomplete()
    {
        Assert.False(SnapshotRunResolver.IsCompleteSnapshotEvidence(
            Array.Empty<SnapshotRunResolver.EtlLogRow>()));
    }
}
