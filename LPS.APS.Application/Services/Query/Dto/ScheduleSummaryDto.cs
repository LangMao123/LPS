namespace LPS.APS.Application.Services.Query.Dto;

/// <summary>
/// 排程概要 KPI（V1 最小集，供前端顶部面板展示）
/// </summary>
public class ScheduleSummaryDto
{
    public int PlanVersionId { get; set; }
    public string VersionCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public int TotalTasks { get; set; }
    public int ScheduledTasks { get; set; }
    public int UnscheduledTasks { get; set; }
    public int DelayedTasks { get; set; }

    public int TotalOrders { get; set; }
    public int DelayedOrders { get; set; }

    public DateTime? FirstTaskStart { get; set; }
    public DateTime? LastTaskEnd { get; set; }

    public int? ComputeDurationSeconds { get; set; }

    /// <summary>
    /// 版本是否真实存在。false = 该 planVersionId 不存在（此时 VersionCode/Status 为空串、计数全 0），
    /// 调用方不得把它当作「该版本存在但没有排程结果」。
    /// 与 <see cref="ScopeDenied"/> 配合区分三种空态（4号位 10-17 §六：原 200 + 默认值不可区分）。
    /// 命名统一（4号位 10-19 §四，裁 (a)）：与 <c>GanttDataDto.VersionExists</c> 同名同义。
    /// </summary>
    public bool VersionExists { get; set; }

    /// <summary>
    /// 是否因业务范围（Domain）不足被拒绝（fail-closed）。true = 越权，非「没数据」。
    /// </summary>
    public bool ScopeDenied { get; set; }
}
