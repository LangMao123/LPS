namespace LPS.APS.Core.Dto;

/// <summary>
/// 一次 Run 启动时的完整冻结配置（不可变，Run 内不刷新）
/// 2↔3 接口传输 DTO（契约 v0.2 §二；阶段 B 实现依据）。
/// 冻结语义（C2-1/C2-2）：按 Run 已冻结的指定 StrategyProfileVersionId 装载一次，
/// 本 Run 所有 Domain 使用同一份 Snapshot，不因后续新版本发布/停用而漂移。
/// 六块为冻结文档 4.2 三块（Demand排序 / Supply-Lock / Solver策略）的细拆（C2-5 确认）。
/// 不提供 ToFrozenFactParameters()（C2-6：2号位在集成层从 Snapshot 抽取并转交 5号位）。
/// </summary>
public sealed class FrozenStrategySnapshot
{
    public long StrategyProfileVersionId { get; set; }        // 冻结锚点（C2-1：Run 绑定后按此获取，不再漂移）
    public long RuleSetVersionId { get; set; }                // 同一包内（C2-2）
    public long ParameterSetVersionId { get; set; }           // 同一包内（C2-2）
    public DateTime FrozenAt { get; set; }                    // 冻结时点（Run 启动）

    public DemandPriorityBlock DemandPriority { get; set; } = new();  // ① Demand 排序
    public LockBlock Lock { get; set; } = new();                        // ② Demand Protection / 锁定
    public SupplyBlock Supply { get; set; } = new();                    // ③ Inventory Availability + PI 排序
    public ProcurementBlock Procurement { get; set; } = new();          // ④ 采购/良率参数（含 PlanningYield，C2-5）
    public SolverStrategyBlock SolverStrategy { get; set; } = new();    // ⑤ Solver 策略
    public CandidateGuardrailBlock CandidateGuardrail { get; set; } = new(); // ⑥ Candidate 技术 Guardrail
    public List<SetupTransitionRuleSnapshot> SetupTransitionRules { get; set; } = []; // ⑦ 产品转换换型规则本 Run 冻结内容（v1.2 §九/§十；0号位 裁决项2）
    public List<BatchPolicyRuleSnapshot> BatchPolicy { get; set; } = []; // ⑧ 批量策略（Batch Policy）本 Run 冻结内容（0号位 2026-10-07 裁决：本轮落码；Material+ProductionDepartment 粒度）
}

/// <summary>
/// ① Demand 排序（清单 14~17 / 冻结 4.2-Demand排序）
/// 红线：无全局 PriorityScore；第一命中即止，命中后不再匹配后续 Segment。
/// </summary>
public sealed class DemandPriorityBlock
{
    /// <summary>有序 Segment 列表，按 SegmentOrder 升序；第一命中即止（清单 16）</summary>
    public List<PrioritySegment> Segments { get; set; } = [];
}

public sealed class PrioritySegment
{
    public int SegmentOrder { get; set; }                 // 排序序号（升序）
    public string SegmentName { get; set; } = "";         // 可解释名
    public bool IsEnabled { get; set; }

    /// <summary>命中条件（AND 列表；全部满足才命中本 Segment）——强类型表达，不建 DSL（清单 17 红线）</summary>
    public List<SegmentMatchCondition> MatchConditions { get; set; } = [];
    /// <summary>命中后本 Segment 内部排序字段（有序，依次生效）</summary>
    public List<SegmentSortField> SortFields { get; set; } = [];
    /// <summary>稳定 Tie-break 字段（并列时最终稳定顺序，避免随机）</summary>
    public List<string> StableTieBreakFields { get; set; } = [];
}

public sealed class SegmentMatchCondition
{
    public DemandField Field { get; set; }                // 强类型字段枚举
    public ConditionOperator Operator { get; set; }       // 强类型操作符枚举
    public object? Value { get; set; }                    // 标量；Operator=In 时为 List<object>
}

public sealed class SegmentSortField
{
    public DemandField Field { get; set; }
    public SortDirection Direction { get; set; }
}

/// <summary>Demand 可匹配字段（强类型；V1 最小集，扩展需 0号位裁决）</summary>
/// <remarks>
/// P0-07：补充 DueDate / IssueDate（冻结文档 §4.2 日期排序；0 号位冻结示例
/// "Delayed SALES_ORDER → DueDate ASC → CustomerTier DESC → IssueDate ASC"）。
/// 新增枚举值必须同步：DemandRecord 字段 + DemandPriorityMatcher 三处 switch + DemandPriorityValidator 白名单。
/// </remarks>
public enum DemandField
{
    RemainingTimeHours,       // 与 NormalLT 的剩余时间
    DelayStatus,              // Delayed / OnTrack
    CustomerTier,             // VIP / A / B / C
    OrderType,                // SO / WO / Transfer ...
    IsPmcProtected,           // PMC 人工强保护
    PriorityLevel,            // 业务优先等级（若冻结允许）
    DueDate,                  // 统一交期（DATE/DATETIME2，客户要求交期）——P0-07
    IssueDate,                // 订单发行/下发日期（源事实字段，MTS 无值）——P0-07
}

public enum ConditionOperator { Equals, NotEquals, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual, In }
public enum SortDirection { Asc, Desc }

/// <summary>
/// ② LockBlock（Demand Protection / 锁定）（清单 18~19 / 冻结 4.2-Supply/Lock）
/// 触发条件治理（3号位配置，2号位执行触发与扣减——C5-1：不传给 5号位）
/// </summary>
public sealed class LockBlock
{
    public ProtectionTriggerParams Trigger { get; set; } = new();
    /// <summary>Sticky 语义治理（保护持续到 完成/取消/Supply失效/PMC 显式释放）</summary>
    public StickyProtectionParams Sticky { get; set; } = new();
}

public sealed class ProtectionTriggerParams
{
    public bool UseRemainingTimeThreshold { get; set; }    // RemainingTime < NormalLT 触发
    public double RemainingTimeThresholdHours { get; set; }
    public bool ProtectDelayed { get; set; }              // DelayStatus = DELAYED 触发
    public bool ProtectVipTier { get; set; }              // CustomerTier = VIP 触发
    public string? VipTierValue { get; set; }
    public IReadOnlyList<string> ProtectedOrderTypes { get; set; } = [];  // 指定 OrderType 触发
    public bool AllowPmcManualProtection { get; set; }    // PMC 人工强保护入口
}

public sealed class StickyProtectionParams
{
    public bool RequireReleaseRecord { get; set; }        // 手工释放须记录 Actor/Time/Reason（清单 19）
    public bool ProtectUntilCompletion { get; set; }
    public bool ProtectUntilSupplyInvalid { get; set; }   // Supply 失效才解除
}

/// <summary>③ SupplyBlock（Inventory Availability + PI 排序）（清单 20~22 / 冻结 4.2-Supply/Lock）</summary>
public sealed class SupplyBlock
{
    public InventoryAvailabilityRule Inventory { get; set; } = new();
    /// <summary>PI 简单排序（不建复杂评分引擎，清单 21）</summary>
    public PiSortParams PiSort { get; set; } = new();
}

public sealed class InventoryAvailabilityRule
{
    public bool IsEnabled { get; set; }
    /// <summary>参与可用的 Warehouse 有序列表（Priority 隐含于顺序）</summary>
    public IReadOnlyList<string> WarehousePriority { get; set; } = [];
    public bool RequireFactoryContext { get; set; }       // 必要 Factory 上下文
    public bool RequireProductFamilyContext { get; set; } // 必要 ProductFamily 上下文
}

public sealed class PiSortParams
{
    public PiSortBy SortBy { get; set; }                  // 默认 Issue/Create Time ASC
    public bool UseStablePiNoTieBreak { get; set; }
}

public enum PiSortBy { IssueDateAsc, CreatedAtAsc, StablePiNoAsc }

/// <summary>
/// ④ ProcurementBlock（采购/良率参数，含 PlanningYield）（清单 23~27 / 3.3 / C2-5）
/// C2-5：PlanningYield 必须进入 Snapshot，归属本块；2号位读取算 NetOutputQty → PlannedProcessQty；
/// 红线：已有 PI Supply 不得按 Yield 再次放大。
/// 0号位 2026-08-28 最终裁决：采购/Timed Supply 排序链（Eligibility → Warehouse Priority → AvailableTime
/// → PO Release Time → PO+Line）固定、不可重排、**无 Enable/Disable 开关**；3号位 只治理本块四个参数值：
/// WarehousePriority / DefaultPurchaseLt / OverdueMargin / ArrivalToUsableOffsets
/// （原 ProcurementSortParams 四开关类型已按裁决删除）。
/// </summary>
public sealed class ProcurementBlock
{
    /// <summary>采购排序链"Warehouse Priority"步的仓库优先级（有序列表，隐含优先级；0号位 2026-08-28 裁决补齐）</summary>
    public IReadOnlyList<string> WarehousePriority { get; set; } = [];
    /// <summary>Default Purchase LT（按 Receiving Warehouse + 必要 Material 维度，清单 23；不增加 ProductFamily 维度）</summary>
    public IReadOnlyList<PurchaseLtRule> DefaultPurchaseLt { get; set; } = [];
    /// <summary>逾期 Margin（保守修正，清单 25）</summary>
    public OverdueMarginParams OverdueMargin { get; set; } = new();
    /// <summary>Arrival-to-Usable Offset（按 Receiving Warehouse，清单 26）</summary>
    public IReadOnlyList<WarehouseOffsetRule> ArrivalToUsableOffsets { get; set; } = [];
    /// <summary>Planning Yield（Material/Stage 维度，清单 27）——C2-5：必须进入 Snapshot，归属本块</summary>
    public IReadOnlyList<PlanningYieldRule> PlanningYields { get; set; } = [];
}

public sealed class PurchaseLtRule
{
    public string WarehouseCode { get; set; } = "";
    public string? MaterialId { get; set; }               // 空 = Warehouse 级默认
    public double DefaultLtDays { get; set; }
}

public sealed class OverdueMarginParams
{
    public decimal MarginPercent { get; set; }            // 保守修正比例
    public int MinimumExtraDays { get; set; }             // 最小加天
}

public sealed class WarehouseOffsetRule
{
    public string WarehouseCode { get; set; } = "";
    public double OffsetHours { get; set; }               // ArrivalTime → AvailableTime 偏移
}

public sealed class PlanningYieldRule
{
    public string MaterialId { get; set; } = "";
    public string? StageCode { get; set; }                // 空 = Material 级默认
    public decimal YieldPercent { get; set; }             // 0 < YieldPercent <= 100
}

/// <summary>
/// ⑤ SolverStrategyBlock（Solver 策略）（清单 28~33 / 冻结 4.2-Solver策略）
/// 与现有 StrategyConfig 的映射（盘点 §三.4）：SchedulingMode → SolverStrategyMode 在阶段 E 定义，不静默改 StrategyConfig 语义。
/// </summary>
public sealed class SolverStrategyBlock
{
    public SolverStrategyMode Mode { get; set; }          // AUTO / FORWARD / BACKWARD / MIXED（冻结 E1；AUTO=1号位按求解上下文决定最终方向，0号位 2026-10-07 裁决）
    public bool AllowMerge { get; set; } = false;         // 是否允许合批（1号位 PhaseTwoInitialScheduler.cs:158 消费；3号位 2026-09-04 补源）
    public DynamicBottleneckMode BottleneckMode { get; set; }   // 选「哪个资源当瓶颈锚点」的枚举（Auto/PreferAnchor/ForceAnchor/NotAnchor）
    public string? AnchorResourceCode { get; set; }        // 锚点资源编码（业务编码，非 int ResourceId）；BottleneckMode=PreferAnchor/ForceAnchor/NotAnchor 时生效，Auto 忽略；空=未指定→等价 Auto。P1-02 归 3号位 冻结、2号位 传入、1号位 消费
    public OnTimeTargetParams OnTimeTarget { get; set; } = new();
    public SplitParams Split { get; set; } = new();
    public SetupParams Setup { get; set; } = new();
    public StageOverlapParams StageOverlap { get; set; } = new();

    // P1-02 补充（2026-09-14，2号位 出字段 / 3号位 冻结参数值 / 1号位 换读）：
    // BottleneckMode 只定「选哪个锚点」，不覆盖「利用率超多少判瓶颈」的百分比阈值——这是两个可调旋钮。
    // 0.85 判瓶颈资源（1号位 PhaseThreeDiagnostics.cs:92，填 BottleneckResourceIds，影响展示）；
    // 0.90 判延期根因 RESOURCE_CAPACITY_WAIT（PhaseThreeDiagnostics.cs:183，影响产能短缺责任归属）。
    //    2026-10-12（1号位 §2.1 附带）：旧名 RESOURCE_CAPACITY_SHORTAGE 已退役、不在 15 码内，此处仅为注释引用，随之改名。
    // 拆两个独立阈值（目标不同、可单独调）。归 3号位 冻结。
    public decimal BottleneckUtilizationThreshold { get; set; } = 0.85m;           // 利用率 > 此值 → 判瓶颈资源
    public decimal CapacityShortageUtilizationThreshold { get; set; } = 0.90m;     // 利用率 > 此值 → 判延期根因产能短缺
}

public enum SolverStrategyMode { Forward, Backward, Mixed, Auto }
public enum DynamicBottleneckMode { Auto, PreferAnchor, ForceAnchor, NotAnchor }

public sealed class OnTimeTargetParams
{
    public int TargetPercent { get; set; }                // 0~100（发布前校验）
    /// <summary>On-time 为业务优化目标，不被 Setup/WIP/Utilization 反向压过（清单 30 红线）</summary>
    public bool IsPrimaryObjective { get; set; } = true;
}

public sealed class SplitParams
{
    public int MaxOptimizationSplitCount { get; set; } = 3;   // 如 3（清单 31）
    public bool LimitMandatorySplit { get; set; }
    public decimal MinBatchQty { get; set; } = 0.1m;          // 拆分下限（每批最小数量）；P1-02 由 0→0.1
}

public sealed class SetupParams
{
    // 2026-09-16 v1.2 废止属性式换型维度（Mold/Tool/Material/Color，原 Dimensions 字段）：
    // 换型规则改走 RuleSetVersion 的 SetupTransitionRule（EXACT/DEFAULT），本块不再承载换型维度。
    // 原 DefaultSetupMinutes / SetupLookAheadSize 两个旧锚已于 2026-09-20 退役（2号位删字段，生产零读者）。

    // 预算参数（2026-09-20 1号位 提值，3号位 落 ParameterSetVersion 治理槽位）：
    // 均为「次数」量纲，非毫秒——保证输入+同种子 → 同搜索轨迹 → 结果可重放（审计/回归依赖确定性）。
    public int SetupSearchBudget { get; set; } = 500;              // 有界搜索预算：单「可移动段」邻域算子总尝试次数上限，允许范围 [100, 5000]
    public int SetupMaxNeighborhoodTries { get; set; } = 50;       // 最大邻域尝试次数：连续无改善（Level 3 目标未下降）上限，活跃改善即清零重计，允许范围 [10, 500]
}

public sealed class StageOverlapParams
{
    public bool AllowOverlap { get; set; }
    public decimal TransferBatchQty { get; set; }
    public decimal ThresholdQty { get; set; }
    public decimal ThresholdPercent { get; set; }
}

/// <summary>⑥ CandidateGuardrailBlock（Candidate 技术 Guardrail）（清单 34~35 / 冻结 4.2-Solver策略）</summary>
public sealed class CandidateGuardrailBlock
{
    public int NormalMs { get; set; } = 60_000;          // 正常 ≈ 60s
    public int SoftMs { get; set; } = 90_000;            // 软超时 ≈ 90s
    public int LocalHardMs { get; set; } = 180_000;      // 局部硬 ≈ 180s
    public int ImpactedTaskWarningPercent { get; set; } = 30;   // 受影响 Task 警戒
    public int MaxRepairAttempts { get; set; } = 5;
    public int MaxPropagationRounds { get; set; } = 10;
    public int ResourceTopN { get; set; } = 5;
    public int SplitAlternatives { get; set; } = 3;
    /// <summary>红线（清单 35）：MaxImpactedOrders 超阈值仅 Warning + 人工确认，不得停止传播返回伪可行结果</summary>
    public bool WarnOnlyOnMaxImpacted { get; set; } = true;
}

/// <summary>
/// ⑦ 产品转换换型规则快照（SetupTransitionRuleSnapshot）——本 Run 冻结内容（v1.2 §九/§十）
/// 由 2号位 从 RuleSetVersion 已发布的 SetupTransitionRule 投影填充（3号位 出字段，2号位 接投影）；
/// 2号位 按 Domain（ProductionDepartmentId + StageCode）裁剪建内存索引，1号位 DomainSolveRequest 消费。
/// 仅业务键 + 分钟 + 类型，不含审计字段（Id/Created/Updated/IsActive——装载时只取有效规则）。
/// 红线：Solver 不得运行中逐 Task 查 3号位规则库（0号位 裁决项2）。
/// </summary>
public sealed class SetupTransitionRuleSnapshot
{
    public int ProductionDepartmentId { get; set; }         // 生产部门（业务范围）
    public string StageCode { get; set; } = "";              // 大工艺阶段码
    public string OperationCode { get; set; } = "";          // 当前小工序
    public int ResourceId { get; set; }                      // 当前设备
    public int? FromMaterialId { get; set; }                 // 前产品（EXACT 明确；DEFAULT 为 null）
    public int? ToMaterialId { get; set; }                   // 后产品（EXACT 明确；DEFAULT 为 null）
    public string RuleType { get; set; } = "DEFAULT";       // "EXACT" / "DEFAULT"（v1.2 §九）
    public decimal SetupMinutes { get; set; }                // 换型分钟（真实资源占用，非负）
}

/// <summary>
/// ⑧ 批量策略（Batch Policy）规则快照——本 Run 冻结内容（0号位 2026-10-07 裁决：本轮落码）。
/// 粒度：Material + ProductionDepartment（MaterialId NOT NULL；ProductionDepartmentId 正式业务键须明确，NULL 仅历史兼容，
/// v5.1.10 收口④：NULL 部门历史记录不默认为所有部门生效，投影端已按 HasValue 排除）。
/// 来源：**本 Run 冻结策略包的 ⑧ 块**（`ParameterSetVersion.ContentSnapshotJson.BatchPolicy`），
/// 由 FrozenStrategySnapshotProvider.DeserializeBatchPolicy 反序列化（投影端 TaskSplitRuleConfigProjector.ProjectFromSnapshot）。
/// ⚠ 该子块是**「发布」时点**从 TaskSplitRuleConfig 物理表 active 规则**抄录的快照**，发布后**不再随物理表变化**
/// （刷新唯一路径 = 重新发布参数集版本）；装载层**不回读**物理表。
/// 缺策略（某 Material+Dept 无命中规则）由消费侧 fail-closed（BATCH_POLICY_MISSING），本块不承载信号位。
/// 仅业务键 + 参数，不含审计字段（Id/Created/Updated/IsActive——投影时只取有效规则）。
/// v5.1.10 收口①②（2026-10-09 生效）：MaxOptimizationSplitCount / MaxBatchCandidates / BottleneckSplitStrategy / NonBottleneckStrategy
/// 投影恒 null、主链不得消费（Solver 技术预算归 1号位 版本化安全默认；策略列仅追溯）。字段保留仅为兼容旧快照反序列化与 1号位 消费侧类型，
/// 不构成有效预算/拆合批倾向来源。
/// </summary>
public sealed class BatchPolicyRuleSnapshot
{
    public int MaterialId { get; set; }                         // 物料（正式业务键）
    public int? ProductionDepartmentId { get; set; }            // 生产部门（正式命中须明确；可空仅历史兼容，不默认为所有部门生效）

    /// <summary>
    /// 生效窗口起点（半开区间 <c>[EffectiveFrom, EffectiveTo)</c>；null = 无下界）。
    /// **2026-10-13 新增**：由 <c>TaskSplitRuleConfig</c> 表直读装载（发布侧⑧投影已退休）填充，
    /// 装载期按**本 Run 生效时点**重判 —— 「发布时有效、Run 时已失效」的行因此可被显式判成
    /// <see cref="BatchPolicyState.Invalid"/>，而不再被原样下发为有效。
    /// 依据：冻结 06 集成接口 **v1.36 <c>:17</c>**（＝v1.37 <c>:25</c> 同一段；业务键含**生效时点**）
    /// ＋ 规则清单 **v1.7 <c>B-013</c>**（1号位 2026-10-14《BatchPolicy三态载体…回执》§五 逐字核对）。
    /// 旧快照（如 PSV 807）无此字段 ⇒ 反序列化为 null ⇒ 无界，**向后兼容、零行为变化**。
    /// </summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>生效窗口终点（半开区间；null = 无上界）。与 <see cref="EffectiveFrom"/> 同批新增、同口径。</summary>
    public DateTime? EffectiveTo { get; set; }

    public decimal? MinExecutionBatchQty { get; set; }          // 硬最小批量
    public decimal? MaxExecutionBatchQty { get; set; }          // 硬最大批量（NULL=无硬上限）
    public decimal? PreferredBatchQty { get; set; }             // 软偏好切点（业务生效必填且 >0、处于硬 Min/Max 内，由治理发布校验）
    public bool AllowSplit { get; set; }                        // 是否允许拆分
    public bool AllowMerge { get; set; }                        // 是否允许合并
    public int? MaxOptimizationSplitCount { get; set; }         // v5.1.10 收口①：恒 null 不投（1号位 Solver 技术预算）；仅类型兼容
    public int? MaxBatchCandidates { get; set; }                // v5.1.10 收口①：恒 null 不投（1号位 Solver 技术预算）；仅类型兼容
    public string? BottleneckSplitStrategy { get; set; }        // v5.1.10 收口②：恒 null 不投（历史兼容列，V1 不得消费拆/合批倾向）；仅类型兼容
    public string? NonBottleneckStrategy { get; set; }          // v5.1.10 收口②：恒 null 不投（历史兼容列，V1 不得消费拆/合批倾向）；仅类型兼容
}

/// <summary>
/// ⑧块 Batch Policy **三态＋不可用**（2→1 最小技术合同，2026-10-13）。
///
/// 依据（逐字）：06 集成接口 **v1.36 <c>:17</c>**（G2-T01 补正）——2号位须按
/// <c>MaterialId + ProductionDepartmentId + 运行版本/Scope/生效时点</c> 的业务键**准确区分并传递**
/// <c>VALID/MISSING/INVALID</c> 三态；「现有2→1合同确实不能无歧义表达三态时，仅允许经1/2号位契约审核确认
/// **最小DTO或逻辑状态载体**，**不得增加Batch表**或**把所有物料规则发送Solver**；此处不预命名新字段。」
/// ＋ 规则清单 **v1.7 <c>B-013</c>**（「不创第二Batch数据库真相」）
/// ＋ 0号位 2026-10-13《未命名的Markdown文件3》<c>§2</c>（**新增第 4 类「不可用（技术失败）」**：
/// 「接口未传递状态、规则数据装载失败或 DTO 无法表达三态，**不得直接视为 MISSING**」）。
///
/// 本载体**不新增数据库表、不新增第二份规则真相、不把全部物料规则发给 Solver** —— 只随本 Run 载荷
/// 逐业务键附一条 <c>(状态, 真实匹配来源)</c>；规则参数本身仍走既有的
/// <c>SolverStrategySnapshot.BatchPolicies</c>（仅承载 VALID 行）。
/// </summary>
public enum BatchPolicyState
{
    /// <summary>精确业务键命中，且记录**满足发布/有效化资格**且在**本 Run 生效时点**的生效窗口内。</summary>
    Valid = 0,

    /// <summary>经完整核对后本业务键**确无匹配规则**（含「⑧块整体为空（完全缺配）」与「本部门无命中而别的部门有记录」）
    /// ⇒ 06 §「明确MTS PI来源时的运行时数量硬上限与缺配兜底」允许 1号位 走 B-010 取 <c>Q_C</c> 作本 Run 软 Preferred 兜底。</summary>
    Missing = 1,

    /// <summary>**本键有规则但不合法/冲突/失效且未治理隔离**（含「发布时有效、本 Run 生效时点已失效」）
    /// ⇒ 06 同节逐字「**存在INVALID匹配时，不得假装MISSING兜底，应暴露Issue等待治理**」⇒ 1号位 Fail Closed，**不启 Q_C**。</summary>
    Invalid = 2,

    /// <summary>**不可用（技术失败）**：接口未传递状态／规则数据装载失败／DTO 无法表达三态
    /// ⇒ 0号位 文件3 <c>§2</c>「**不得直接视为 MISSING**」⇒ 1号位 Fail Closed，**不启 Q_C**。
    /// 过渡形态：载体整体缺席（<c>SolverStrategySnapshot.BatchPolicyStates is null</c>）亦按本类处置。</summary>
    Unavailable = 3
}

/// <summary>
/// <see cref="BatchPolicyStateSnapshot.MatchSource"/> 的**封闭取值域**（9 值，1号位 2026-10-14 定稿；
/// 见《APS_V1_20261014_BatchPolicy三态载体_六条答复的回执与两处非对称处置_1号位致2号位_v1.0_20261014.md》§三）。
///
/// **为什么要有常量而不是自由文本**：<c>MatchSource</c> 是 B-013 逐字要求的「**真实匹配来源**」，
/// 它要被消费侧当作**可判定的域值**使用（1号位 的 C-3 将按本值域做 Fail Closed 校验），自由文本使该判定不可能。
///
/// **强制落点（1号位 §一.1 定稿，2号位 已确认）**：值域校验**只作用于 <see cref="BatchPolicyState.Valid"/> 的条目**
/// —— 另三态的出口由 <see cref="BatchPolicyState"/> 决定，<c>MatchSource</c> 只进诊断文本
/// ⇒ 对它们强制值域无安全收益，且有把「技术失败」按「业务无效」处置的误伤风险（0号位 文件3 §2 所禁）。
///
/// **取值规则（2号位 产出侧）**：除 <see cref="LoadError"/> 外**一律是裸常量** —— 各态所需的附加信息
/// （物料/部门/是否跨部门）本就在条目的业务键字段里，重复塞进 <c>MatchSource</c> 只会破坏值域封闭。
/// <see cref="LoadError"/> 是**唯一例外**：装载失败的具体原因**只有调用方知道、且从条目上不可复原**
/// ⇒ 以 <c>"LOAD_ERROR: &lt;原因&gt;"</c> 形式带出（值域以**前导常量**锚定，原因不丢失）。
/// </summary>
public static class BatchPolicyMatchSource
{
    /// <summary><c>Valid</c> 的**唯一合法值**：精确业务键命中且在生效窗口内。</summary>
    public const string SnapshotHit = "SNAPSHOT_HIT";

    /// <summary><c>Missing</c>：⑧块整体为空（完全缺配）／精确业务键无命中（⑧块有行但本键无行）。</summary>
    public const string NoRuleForMaterial = "NO_RULE_FOR_MATERIAL";

    /// <summary><c>Missing</c>：本部门无命中而**别的部门**存在记录（06 v1.36 <c>:17</c> 逐字归 MISSING）。</summary>
    public const string OtherDeptOnly = "OTHER_DEPT_ONLY";

    /// <summary><c>Missing</c>／<c>Invalid</c>：命中一条已停用（<c>IsActive = false</c>）的行。
    /// （发布侧⑧投影已退休；装载由 2号位 按 <c>TaskSplitRuleConfig</c> 表直读，是否过滤 Inactive 按其装载口径。）</summary>
    public const string Inactive = "INACTIVE";

    /// <summary><c>Missing</c>：业务键不完整（本 Run 未解析到该物料的 <c>ProductionDepartmentId</c>）⇒ 无命中。</summary>
    public const string NoDept = "NO_DEPT";

    /// <summary><c>Invalid</c>：本键有规则但本 Run 生效时点不在 <c>[EffectiveFrom, EffectiveTo)</c> 内
    /// —— **本链 <c>Invalid</c> 唯一可达成因**（发布时有效、Run 时已失效、未治理隔离）。</summary>
    public const string OutOfWindow = "OUT_OF_WINDOW";

    /// <summary><c>Invalid</c>：**预留** —— <c>PreferredBatchQty</c> 缺失／≤ 0。
    /// 当前链路上不可达（发布闸 <c>IsSnapshotEligible</c> 已阻断；装载期走「一致性护栏 ⇒ Unavailable」而非本值）。</summary>
    public const string PreferredNull = "PREFERRED_NULL";

    /// <summary><c>Invalid</c>：**预留** —— 同键生效窗口重叠。<c>FindOverlappingWindowIds</c> 已在发布闸阻断。</summary>
    public const string WindowConflict = "WINDOW_CONFLICT";

    /// <summary><c>Unavailable</c>：装载失败／技术失败。**唯一带明细的取值** —— 形如
    /// <c>"LOAD_ERROR: &lt;原因&gt;"</c>；前导常量恒为本值。</summary>
    public const string LoadError = "LOAD_ERROR";
}

/// <summary>
/// 三态载体的**逐业务键**条目（2→1 最小技术合同，2026-10-13）。**不含规则参数** —— 参数仍由
/// <c>BatchPolicies</c> 承载，本条目只回答「这个键是什么状态、凭什么判的」。
/// </summary>
public sealed class BatchPolicyStateSnapshot
{
    /// <summary>业务键①：物料。</summary>
    public int MaterialId { get; set; }

    /// <summary>业务键②：生产部门。**null = 本 Run 未能确定该物料的生产部门**（业务键不完整）
    /// —— 显式给出而非省略，使消费侧不必把「键缺失」误读成「未传递状态」。</summary>
    public int? ProductionDepartmentId { get; set; }

    /// <summary>状态。</summary>
    public BatchPolicyState State { get; set; }

    /// <summary>**真实匹配来源**（B-013 逐字要求「状态、**真实匹配来源**与 <c>Q_C</c>」）—— 供 1号位 追溯本状态据以判定的来源事实。
    /// 取值域见 <see cref="BatchPolicyMatchSource"/>（9 值封闭；<see cref="BatchPolicyMatchSource.LoadError"/> 允许
    /// <c>"LOAD_ERROR: &lt;原因&gt;"</c> 形式带明细）。</summary>
    public string MatchSource { get; set; } = "";
}
