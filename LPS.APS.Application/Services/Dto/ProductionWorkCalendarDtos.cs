namespace LPS.APS.Application.Services.Dto;

/// <summary>
/// 工厂/部门上级工作日历（dbo.ProductionWorkCalendarSlot）DTO 集
/// 语义（G1 候选条款，生效等 G4）：MISSING=无行（继承/覆盖不足），表内仅 WORK/OFF；
/// 日期例外优先于周周期；AllowLowerOverride 仅挂 OFF；IsAuthorizedOverride 仅 WORK+证据。
/// </summary>
public sealed class ProductionWorkCalendarDto
{
    public long Id { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public int FactoryId { get; set; }
    public string? FactoryCode { get; set; }
    public int? ProductionDepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public DateTime? WorkDate { get; set; }
    public byte? WeekdayIso { get; set; }
    public TimeSpan? StartAt { get; set; }
    public TimeSpan? EndAt { get; set; }
    public string WorkState { get; set; } = string.Empty;
    public bool AllowLowerOverride { get; set; }
    public bool IsAuthorizedOverride { get; set; }
    public string? SourceSystem { get; set; }
    public string? SourceReference { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>周基准批量项：某星期几一条（1=周一…7=周日）</summary>
public sealed class WeeklyCalendarEntry
{
    public int WeekdayIso { get; set; }
    /// <summary>WORK / OFF</summary>
    public string WorkState { get; set; } = string.Empty;
    /// <summary>WORK 必填；OFF 全天则两者均空</summary>
    public TimeSpan? StartAt { get; set; }
    public TimeSpan? EndAt { get; set; }
    /// <summary>仅 OFF 行可=1（允许下级持授权加班覆盖本停机）</summary>
    public bool AllowLowerOverride { get; set; }
}

/// <summary>周基准铺设计求（同范围同星期：未到生效期的旧行覆盖删、已到期的闭区间换版）</summary>
public sealed class WeeklyCalendarUpsertRequest
{
    /// <summary>FACTORY / DEPARTMENT</summary>
    public string ScopeType { get; set; } = string.Empty;
    public int FactoryId { get; set; }
    /// <summary>DEPARTMENT 必填；FACTORY 必须为空</summary>
    public int? ProductionDepartmentId { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public List<WeeklyCalendarEntry> Entries { get; set; } = new();
    public string? SourceSystem { get; set; }
    public string? SourceReference { get; set; }
}

/// <summary>日期例外铺设计求（节假日 OFF / 补班 WORK / 授权加班，均可携双授权字段）</summary>
public sealed class WorkCalendarExceptionRequest
{
    public string ScopeType { get; set; } = string.Empty;
    public int FactoryId { get; set; }
    public int? ProductionDepartmentId { get; set; }
    public DateTime WorkDate { get; set; }
    /// <summary>WORK / OFF</summary>
    public string WorkState { get; set; } = string.Empty;
    public TimeSpan? StartAt { get; set; }
    public TimeSpan? EndAt { get; set; }
    /// <summary>仅 OFF 行；=1 允许下级授权覆盖本停机</summary>
    public bool AllowLowerOverride { get; set; }
    /// <summary>仅 WORK 行且必须携 SourceReference 证据（下级持授权覆盖上级 OFF）</summary>
    public bool IsAuthorizedOverride { get; set; }
    public string? SourceSystem { get; set; }
    public string? SourceReference { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
}

/// <summary>闭区间换版请求：把该范围全部未闭（EffectiveTo NULL）行闭到 newFrom-1</summary>
public sealed class CalendarCloseOpenRequest
{
    public string ScopeType { get; set; } = string.Empty;
    public int FactoryId { get; set; }
    public int? ProductionDepartmentId { get; set; }
    /// <summary>新版本起始日；旧开放行 EffectiveTo 闭到此日前一天</summary>
    public DateTime NewEffectiveFrom { get; set; }
}
