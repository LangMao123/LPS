using LPS.APS.Engine.Data;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// 从 `APS_ETL_Log` 解析「**本次夜间**」的推送批次（0号位 2026-10-17 BOM 收口 §二.2／§三.B／§三.C）。
///
/// 规则只有一条：**恰好一条** `BomRequestPush / SUCCESS` 记录才算本次；0 条或多条一律不认。
/// 展开（00:05）与接货（00:30）共用本解析器，保证两段看到的是同一个批号。
/// </summary>
public static class BomNightBatchResolver
{
    /// <summary>一条推送成功记录。</summary>
    public sealed record PushRecord(string BatchNo, DateTime CreatedAt);

    /// <summary>
    /// 判定结果。<see cref="BatchNo"/> 非空 ⇔ <see cref="IsUnique"/>。
    /// </summary>
    public sealed record Resolution(bool IsUnique, string? BatchNo, IReadOnlyList<PushRecord> Records, string Reason);

    /// <summary>读取指定夜间（本地日历日）的推送成功记录。</summary>
    public static async Task<Resolution> ResolveAsync(
        DatabaseConnectionManager connectionManager,
        DateTime localNow,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = BomNightBatchIdentity.NightWindow(localNow);
        var nightKey = BomNightBatchIdentity.NightKey(localNow);

        var rows = (await connectionManager.QueryAsync<PushRecord>(
            @"SELECT BatchNo, CreatedAt
              FROM APS_ETL_Log
              WHERE Step = @Step AND Status = 'SUCCESS'
                AND CreatedAt >= @Start AND CreatedAt < @End
                AND BatchNo IS NOT NULL AND BatchNo NOT LIKE 'RT:%'
              ORDER BY CreatedAt DESC",
            new { Step = BomNightBatchIdentity.PushStep, Start = start, End = end },
            db: DatabaseId.APS)).ToList();

        return rows.Count switch
        {
            1 => new Resolution(true, rows[0].BatchNo, rows, $"本次夜间（{nightKey}）恰好一条推送成功记录"),
            0 => new Resolution(false, null, rows, $"本次夜间（{nightKey}）无 BomRequestPush SUCCESS 记录"),
            _ => new Resolution(false, null, rows,
                $"本次夜间（{nightKey}）有 {rows.Count} 条推送成功记录，无法唯一识别"),
        };
    }
}
