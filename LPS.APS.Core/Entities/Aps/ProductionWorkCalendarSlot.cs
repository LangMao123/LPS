namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 工厂 / 生产部门 **上级**工作日历（对应 dbo.ProductionWorkCalendarSlot）。
///
/// 【它是三级继承的中上两级】
///   工厂（本表 ScopeType=FACTORY） → 生产部门（本表 ScopeType=DEPARTMENT） → 设备 `ResourceCalendarSlot`
///   / 人工 `ManualCapacitySlotCalendar`。`StageCode` **不是**第四个继承层。
///
/// 【Owner 划分（RC-005）】
///   物理与解析 Owner = 2号位（建表 + Run 装载时逐片合成合法窗口）；
///   真实业务事实维护 = 5号位；页面 = 4号位；消费有效 Slot = 1号位。
///
/// 【三态语义（专项 v1.4 §2）—— 本表**不存 MISSING 虚拟行**】
///   MISSING = 本层该日无有效行 ⇒ **允许继承上级**已认证工作窗口；
///   WORK    = 本层有权明确可用窗口；
///   OFF     = 显式休息 / 维修 / 停机 / 禁用 ⇒ **不得被上级 WORK 复活**（方向：上级 OFF 压下级 WORK）。
///
/// 【关键列语义】
///   `WorkDate` 与 `WeekdayIso` 由 CK 保证**二选一**；两者同时命中时 **日期例外优先于周重复**。
///   `WorkState=OFF` 且 `StartAt/EndAt` 均 NULL ⇒ **全天关闭**。
///   `AllowLowerOverride` 只允许出现在 OFF 行（DDL CK：`AllowLowerOverride=0 OR WorkState='OFF'`），
///     表示**该条 OFF 允许下级越权改 WORK**；且下级那条 WORK 必须 `IsAuthorizedOverride=1`
///     并带 `SourceReference`（DDL CK）⇒ 即「**双向授权**」才放行。
///   `EffectiveFrom/EffectiveTo` 为有效期（含端点，`EffectiveTo IS NULL` = 无终止）。
///
/// ⚠️ 本表由 5号位 建表、当前 **0 行**（2026-10-13 实测）。装载侧 `ResourceCalendarResolver`
///    在无行时走**等价快路径**（等价于「上级两级全 MISSING ⇒ 按资源级口径」），故今日行为不变；
///    数据一落，上级 OFF / 继承即自动生效、无需改码。
/// </summary>
public class ProductionWorkCalendarSlot
{
    public long Id { get; set; }

    /// <summary>FACTORY / DEPARTMENT（DDL CK_PWCS_Scope 限定，且 DEPARTMENT 时 ProductionDepartmentId 必填）。</summary>
    public string ScopeType { get; set; } = string.Empty;

    public int FactoryId { get; set; }

    /// <summary>DEPARTMENT 时必填；FACTORY 时必须为 NULL。</summary>
    public int? ProductionDepartmentId { get; set; }

    /// <summary>特定日期例外；与 <see cref="WeekdayIso"/> 二选一。日期例外**优先于**周重复。</summary>
    public DateTime? WorkDate { get; set; }

    /// <summary>周重复：1=周一 … 7=周日（ISO）。与 <see cref="WorkDate"/> 二选一。</summary>
    public byte? WeekdayIso { get; set; }

    /// <summary>WORK 时必填；OFF 且与 EndAt 同为 NULL 表示**全天关闭**。</summary>
    public TimeSpan? StartAt { get; set; }

    public TimeSpan? EndAt { get; set; }

    /// <summary>WORK / OFF（DDL CK_PWCS_WorkState）。</summary>
    public string WorkState { get; set; } = string.Empty;

    /// <summary>仅 OFF 行可为 1：该 OFF 允许下级 WORK 越权覆盖（须下级 IsAuthorizedOverride=1 配合）。</summary>
    public bool AllowLowerOverride { get; set; }

    /// <summary>仅 WORK 行可为 1：本条 WORK 是被授权的越权加班（须带 SourceReference）。</summary>
    public bool IsAuthorizedOverride { get; set; }

    public string? SourceSystem { get; set; }

    public string? SourceReference { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public bool IsActive { get; set; }

    public string? CreatedBy { get; set; }

    /// <summary>⚠ 该表两列时间戳默认 **SYSUTCDATETIME()**（UTC），是全库唯一用 UTC 的表；见台账待报 0号位 项。</summary>
    public DateTime CreatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}
