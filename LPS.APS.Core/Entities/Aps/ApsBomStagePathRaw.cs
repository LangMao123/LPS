using System.ComponentModel.DataAnnotations.Schema;

namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 统一阶段路径本地缓存（v5.0.7新增，v5.0.8升级支持ROOT）
/// 对应 APS_Production.APS_BOM_STAGE_PATH_RAW
/// 数据来源：从ODS库 MES_APS_BOM_Workset_StageDetail 拉取（2号位搬运，与 APS_BOM_RAW 同批次）
/// 
/// 消费方：1号位（必须按 StageScopeType 区分查询）
/// - 读取 StageSeq + StageCode → 对每个阶段串接 RoutingOperation 小工序排 Task
/// - 对无小工序的外协阶段查 StageLeadTimeParam 生成标准 Task
/// 
/// StageScopeType 区分两类记录：
/// - EDGE：子件供给路径（某条BOM边对应的子件在供给父件之前的完整大工艺顺序）
/// - ROOT：根产品完工路径（最上层产品自身完工所需的完整大工艺顺序）
/// </summary>
[Table("APS_BOM_STAGE_PATH_RAW")]
public class ApsBomStagePathRaw
{
    public long Id { get; set; }

    /// <summary>
    /// 关联ODS批次
    /// </summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>
    /// ODS 侧 `MES_APS_BOM_Workset.Id`（跨库引用，非 FK）；2号位搬运时透传。
    ///
    /// 【为什么必须有】冻结 DDL 明文要求（`08 v5.1.8.7 :2008`「WorksetId BIGINT NULL，
    ///   v5.0.26 ODS 侧 Workset.Id（跨库引用，非FK）；2号位搬运时透传；NULL=兼容旧批次」；
    ///   `07 v5.1.13 :2595` 同旨）。它是「请求＋边实例」隔离的**第二层锚点**：
    ///   `WorksetId → MES_APS_BOM_Workset.RequestDetailId`（`08 :363` 明文
    ///   「RequestDetailId **不进** StageDetail，维持通过 WorksetId 反查的既有设计」）。
    ///
    /// 【语义】`StageScopeType='EDGE'` 时 = 该行所属的**具体 BOM 边实例**
    ///   （`08 :459-463`：「每条 Workset 行分别写 StageDetail（WorksetId=Workset.Id），
    ///   同一 BOM 边出现 N 次写 N 组」）；`StageScopeType='ROOT'` 时 = 该 RequestDetail 下
    ///   Level=1 的**代表性锚点** `MIN(Workset.Id)`（`08 :359`），不表示「由某条 BOM 边派生」。
    ///
    /// 【实测】ODS 源表该列 **0 NULL**（2026-10-16：EDGE 2,052,683 行 / ROOT 321,127 行，NULL 均为 0）。
    /// </summary>
    public long? WorksetId { get; set; }

    /// <summary>
    /// MES系统的BOM编号
    /// </summary>
    public string BOMNO { get; set; } = string.Empty;

    /// <summary>
    /// 阶段路径类型：EDGE=子件供给路径 / ROOT=根产品完工路径
    /// </summary>
    public string StageScopeType { get; set; } = "EDGE";

    /// <summary>
    /// 父物料编码（EDGE=父件编码；ROOT=NULL）
    /// </summary>
    public string? ParentMaterialCode { get; set; }

    /// <summary>
    /// 子/根物料编码（EDGE=子件编码；ROOT=根产品自身编码）
    /// </summary>
    public string ChildMaterialCode { get; set; } = string.Empty;

    /// <summary>
    /// 阶段顺序号（10/20/30，间隔10）
    /// </summary>
    public int StageSeq { get; set; }

    /// <summary>
    /// 大工艺阶段码（如TJ_MACH/TJ_OUTS/BJ_PAINT）
    /// </summary>
    public string StageCode { get; set; } = string.Empty;

    /// <summary>
    /// 是否供给阈值点（仅EDGE有效；ROOT恒为0）
    /// </summary>
    public bool IsSupplyThreshold { get; set; }

    /// <summary>
    /// 2号位拉取时间
    /// </summary>
    public DateTime SyncedAt { get; set; }
}
