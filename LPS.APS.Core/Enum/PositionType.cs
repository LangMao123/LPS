namespace LPS.APS.Core.Enum;

/// <summary>
/// 生产指示位置类型（v5.1.5更新）
/// 定义PI RemainingQty当前所处的位置状态
///
/// 变更说明（v5.1.5）：
/// - STAGE拆分为FIRST_STAGE_PENDING和STAGE_WAITING
/// - INTERPLANT_IN_TRANSIT改名为INTERPLANT_TRANSIT
/// - WAITING改名为STAGE_WAITING
/// </summary>
public enum PositionType
{
    /// <summary>
    /// 首工序待开始（PI刚进入Stage路径，首工序尚未开工）
    /// </summary>
    FIRST_STAGE_PENDING = 1,

    /// <summary>
    /// Stage等待（在某Stage等待加工，或已投料但未实际加工）
    /// </summary>
    STAGE_WAITING = 2,

    /// <summary>
    /// 生产指示级XC位置（线边仓，半成品临时存储）
    /// </summary>
    XC = 3,

    /// <summary>
    /// 生产指示跨厂途中位置（已从上游工厂发出，尚未到达目标工厂）
    /// INTERPLANT_TRANSIT属于Position，不属于Transit Supply
    /// </summary>
    INTERPLANT_TRANSIT = 4,

    /// <summary>
    /// 无法准确定位的位置（总量必须闭合）
    /// 消费口径（2026-10-19 与 2号位 实测对齐，锚点 PeggingOrchestrator.cs:10626-10627、:10639-10644）：
    /// 本类型**不在位置毛量白名单** ⇒ stageFree=0 ⇒ 不产执行批（Fail Closed：不排，而非错排）；
    /// 数量仍保留在 PI 供给（ERP 剩余）中，不丢量。
    /// ⚠ 旧注释「按保守策略从最早Stage开始形成计划需求」与实际不符（属文档漂移），已按可执行语句更正。
    /// </summary>
    UNLOCATED = 5
}
