namespace LPS.APS.Engine.Services.Sync.Dto;

/// <summary>
/// BOM 批展开结果（IBomBatchExpandService 返回）。
///
/// 【与 BomRealtimeExpandResult 的区别】实时展开按「订单明细」逐条展开（_Realtime 表，永不阻塞）；
/// 批展开是 ODS 侧整批重建（四表 truncate + 重写），一批一次，产出 READY 供夜间链接货。
/// </summary>
public class BomBatchExpandResult
{
    /// <summary>本次展开的批次号（无 PENDING 批时为 null）</summary>
    public string? BatchNo { get; init; }

    /// <summary>是否实际调用了 sp_ExpandBOMBatch_vNext 并成功返回</summary>
    public bool Expanded { get; init; }

    /// <summary>
    /// 当前 PENDING 积压批次数（不含白天 <c>RT:</c> 批头 —— 其 Status 按设计恒为 PENDING）。
    /// 本任务每日只展开最新一批，历史批不会自动消化，故每次运行都报告此数以便观测。
    /// </summary>
    public int PendingBacklogCount { get; init; }

    /// <summary>问题信息（失败转 Issue，不硬断）</summary>
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}
