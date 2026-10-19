namespace LPS.APS.Application.Services.Dto;

/// <summary>
/// PlanVersion 查询映射（跨方法传递，故保持独立文件 + internal 可见性）
/// </summary>
internal class PlanVersionInfoDto
{
    public int Id { get; set; }
    public string VersionCode { get; set; } = string.Empty;
    public string DomainKey { get; set; } = string.Empty;
    public DateTime PlanHorizonStart { get; set; }
    public DateTime PlanHorizonEnd { get; set; }
    public int SourceScheduleRunId { get; set; }

    /// <summary>
    /// 执行进度状态（**冻结词表**：BUILDING / Computing / CANDIDATE / ACTIVE / ARCHIVED / FAILED）。
    /// 【2026-09-29 新增】仅供 <c>SchedulingOrchestrator.ExecuteRunAsync</c> 的重入守卫读终态用，
    /// 不参与其它查询的投影 —— 未 SELECT 该列的旧调用点保持 default(null)，不受影响。
    /// 【2026-10-15 词表对齐】原 D7 词表 Created / Computed / ComputeFailed 已按冻结文档改名为
    /// BUILDING / CANDIDATE / FAILED；Computing 保留（冻结 BUILDING 期间的必要子状态，见台账 §T-1037）。
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// 版本类别（DAILY_BASELINE / CANDIDATE / RECOVERY / TEST …）。
    /// 【2026-10-15 新增】仅供 <c>SchedulingOrchestrator.ExecuteDomainAsync</c> 判「是否夜间日基线」用。
    /// </summary>
    public string? VersionCategory { get; set; }

    /// <summary>
    /// 来源运行的 RunType（FULL_SCHEDULE / MANUAL_RESCHEDULE / …），由
    /// <c>PlanVersion.SourceScheduleRunId</c> 左连 <c>ScheduleRun</c> 取得。
    /// 【2026-10-15 新增】夜间全量成功自动采用（Status→ACTIVE）的唯一判据：只认 <c>FULL_SCHEDULE</c>，
    /// 白天候选（MANUAL_RESCHEDULE 等）不得自动采用。scheduleRunId=0 的手动单域调用取不到 ⇒ 为 null ⇒ 不自动采用。
    /// </summary>
    public string? RunType { get; set; }
}
