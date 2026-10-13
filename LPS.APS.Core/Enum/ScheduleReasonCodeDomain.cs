namespace LPS.APS.Core.Enum;

/// <summary>
/// <c>ScheduleExplanationFact.ReasonCode</c> 的**权威值域**——冻结《APS 数据库字段说明文档 v5.1.10》§八.1 逐字列出的 15 码。
///
/// <para>
/// <b>【为什么是一个显式常量集而不是散落各处的字面量】</b>该列在冻结文档里被定为
/// 「**权威枚举（全文 ReasonCode 必须属于此列表，0号位审批冻结）**」，而**实库无 CHECK 约束**
/// （实测 2026-10-12：列 <c>nvarchar(200) NOT NULL</c>，无 check、无 default）⇒ **值域只能由写入方自律**。
/// 写入方（2号位 `PeggingOrchestrator` 的未排程记账行）此前把 1号位
/// <c>UnscheduledTaskResult.Reason</c> **原样直写**进本列，而该 Reason 无任何值域声明
/// （1号位《未排程原因出口与 ReasonCode15码值域提报》已就此提请 0号位裁决）
/// ⇒ 实库出现 `BATCH_POLICY_MISSING` 与**中文自由文本**等越域值。
/// </para>
///
/// <para>
/// <b>【本轮（0号位 2026-10-12 裁决 §六.3）处置】</b>原文：「当前**不授权新增任何其它 ReasonCode**；
/// <c>ReasonCode=''</c>+<c>EvidenceJson</c> 记录缺证可以暂作诊断……已发现 <c>BATCH_POLICY_MISSING</c>
/// 等非标值需**单列整改**」⇒ 写入方改为：**值域内原样写码；值域外一律写空串，
/// 并把原始值原样搬进 <c>EvidenceJson.UpstreamReasonCode</c>**（证据不丢、按值计数可查、可逆）。
/// </para>
///
/// <para>
/// ⚠ <b>本集合只收录逐字冻结的 15 码</b>。0号位 历次**亲裁指定**但尚未回写进冻结码表的值
/// （<c>BATCH_POLICY_MISSING</c> / <c>BATCH_POLICY_CONFLICT</c> / <c>STAGE_SEQUENCE_CONFLICT</c> /
/// <c>CALENDAR_COVERAGE_INSUFFICIENT</c> / <c>MISSING_PRODUCTION_DEPARTMENT_CONTEXT</c>）**不在其中**——
/// 它们不在此处被"偷偷放行"，而是按裁决 §六.3 走「非标值单列」通道，
/// 待 0号位 判断是否增码 / 冻结码表回写后再由本集合放开（**改这一处即恢复，无需再动写入点**）。
/// </para>
/// </summary>
public static class ScheduleReasonCodeDomain
{
    /// <summary>冻结 §八.1 逐字 15 码（序与文档一致，便于与文档逐条对照）。</summary>
    public static readonly IReadOnlyList<string> Frozen15 = new[]
    {
        "RESOURCE_CAPACITY_WAIT",
        "MATERIAL_SHORTAGE",
        "PRECEDENCE_WAIT",
        "FROZEN_ZONE_LOCK",
        "ROUTING_FALLBACK",
        "STAGE_LEADTIME_FALLBACK",
        "BOM_DEGRADE",
        "CROSS_ORG_HANDOFF",
        "PRIORITY_LOWER_THAN_OTHERS",
        "DUE_DATE_RISK",
        "LOGISTICS_DELAY",
        "PRIORITY_INHERITANCE",
        "CROSS_DOMAIN_VERSION_MISMATCH_RISK",
        "MANUAL_COMPLETED_SHORT",
        "EQUIPMENT_BREAKDOWN_RISK"
    };

    private static readonly HashSet<string> Lookup = new(Frozen15, StringComparer.Ordinal);

    /// <summary>
    /// 该值是否可直接写入 <c>ScheduleExplanationFact.ReasonCode</c>。
    /// 空白与域外值一律 <c>false</c>（⇒ 写空串 + 转 <c>EvidenceJson</c> 诊断位）。
    /// </summary>
    public static bool IsAuthoritative(string? reasonCode)
        => !string.IsNullOrWhiteSpace(reasonCode) && Lookup.Contains(reasonCode.Trim());

    /// <summary>规范化后的权威值；域外/空白返回 <see cref="string.Empty"/>。</summary>
    public static string Normalize(string? reasonCode)
        => IsAuthoritative(reasonCode) ? reasonCode!.Trim() : string.Empty;
}
