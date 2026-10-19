namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// 夜间 BOM 三段链的「**本次**」身份（0号位 2026-10-17 BOM 夜间批展开联合技术收口 §二.2／§三.A）。
///
/// 【为什么要一个显式身份】
///   00:00 `bom-request-push`、00:05 `bom-batch-expand`、00:30 `nightly-batch` 是**三个独立 Hangfire 任务**，
///   彼此没有上下文传递。此前三段各自按「最新 PENDING」「任意最新 READY」猜，彼此**没有同批次保证**。
///   0号位 明确要求：不能只按 `CreatedAt` 最新算完成，必须从推送成功结果持久记录准确 BatchNo，
///   并通过**可证关联键**读取；同日多批、重复 Job、跨零点或恢复后无法唯一识别时**停止猜测、留 Issue**。
///
/// 【本实现的关联键】
///   推送成功后在 `APS_ETL_Log` 写一行 `Step=BomRequestPush / Status=SUCCESS / BatchNo=REQ_…`，
///   `Message` 里带 `NightKey=yyyyMMdd`（本地日历日，禁 UTC）。展开与接货都用同一个 `NightKey` 选行：
///     · 恰好 1 条 SUCCESS ⇒ 就是本次；
///     · 0 条 ⇒ 本次没有成功推送，`SKIPPED`；
///     · 多条 ⇒ 无法唯一识别，`SKIPPED` + Issue，**不取最新**。
///
/// 【没有新增 DDL】复用既有 `APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)` 五列。
/// </summary>
public static class BomNightBatchIdentity
{
    /// <summary>`APS_ETL_Log.Step`：00:00 推送。</summary>
    public const string PushStep = "BomRequestPush";

    /// <summary>`APS_ETL_Log.Step`：00:05 批展开。</summary>
    public const string ExpandStep = "BomBatchExpand";

    /// <summary>`APS_ETL_Log.Step`：00:30 接货。</summary>
    public const string PullStep = "BomResultPull";

    /// <summary>本次夜间身份的关联键（本地时间，禁 UTC）。</summary>
    public static string NightKey(DateTime localNow) => localNow.ToString("yyyyMMdd");

    /// <summary>本次夜间身份的取行窗口（本地 00:00 至次日 00:00，左闭右开）。</summary>
    public static (DateTime Start, DateTime End) NightWindow(DateTime localNow)
    {
        var start = localNow.Date;
        return (start, start.AddDays(1));
    }
}
