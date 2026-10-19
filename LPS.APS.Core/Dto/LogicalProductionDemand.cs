namespace LPS.APS.Core.Dto;

/// <summary>
/// 逻辑生产需求（V1.2）
/// Pegging阶段形成，交给1号位Solver决定FinalTask
/// 不持久化到数据库，仅运行时内存DTO
/// </summary>
public sealed class LogicalProductionDemand
{
    /// <summary>
    /// 逻辑需求唯一键
    /// </summary>
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 计划版本ID
    /// </summary>
    public long PlanVersionId { get; init; }

    /// <summary>
    /// Domain键
    /// </summary>
    public string DomainKey { get; init; } = string.Empty;

    /// <summary>
    /// 与Pegging Allocation建立追溯
    /// </summary>
    public long AllocationSequence { get; init; }

    /// <summary>
    /// 需求键
    /// </summary>
    public string DemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 订单ID（可选）
    /// </summary>
    public long? OrderId { get; init; }

    /// <summary>
    /// 本需求是否为**该订单的顶层根需求**（BOM 展开的第 0 层）。
    ///
    /// 【为什么必须显式携带（0号位 2026-10-17 第十四轮 J14-03/J14-04）】
    ///   Stage 链角色此前由「该物料在本运行是否有 BOM 供给阈值」反推 ⇒ 子件阈值为 NULL 时被错判成 ROOT。
    ///   角色是**需求的结构属性**（本需求是不是本订单的根），阈值只决定 EDGE 链的**截断点**（<see cref="RequiredStageCode"/>）。
    /// </summary>
    public bool IsRootDemand { get; set; }

    /// <summary>
    /// 本需求所属 BOM 请求的 `ResolvedBOMNO`（来自 `OrderBomRequestLink`，按 `OrderId + PlanVersionId` 解析）。
    /// null = 无锚点（旧链路/未建链接）⇒ Stage 链装载不做请求收窄并须留痕。
    /// </summary>
    public string? BomNo { get; set; }

    /// <summary>本需求所属 BOM 请求的 `RequestDetailId`（`OrderBomRequestLink.RequestDetailId`，追溯用）。</summary>
    public long? RequestDetailId { get; set; }

    /// <summary>
    /// 本需求所属 BOM 请求的 ROOT 代表锚点（`OrderBomRequestLink.RepWorksetId` = 该 RequestDetail 下 Level=1 的 `MIN(Workset.Id)`）。
    /// ROOT 侧 Stage 链按它做第二层收窄（`WorksetId` 已回灌时生效）。
    /// </summary>
    public long? RepWorksetId { get; set; }

    /// <summary>
    /// 物料ID
    /// </summary>
    public int MaterialId { get; init; }

    /// <summary>
    /// 工厂ID
    /// </summary>
    public int FactoryId { get; init; }

    /// <summary>
    /// 从哪里开始继续生产（大工艺阶段码，机加工/氧化级，对应 RoutingOperation.StageCode）。
    /// 2号位在 PeggingLoop 后据 Routing 有向图「无入边源结点」回填（原 init 改为 set 以支持回填，见 PeggingOrchestrator.FillStartStageCodes）。
    /// </summary>
    public string StartStageCode { get; set; } = string.Empty;

    /// <summary>
    /// 要做到哪个大工艺阶段即算可供给父件（供给阈值 Stage）——对应 BOM 边的 `ChildRequiredStageCode`
    /// / `APS_BOM_STAGE_PATH_RAW.IsSupplyThreshold = 1` 的那一段。
    ///
    /// 【语义（PM《BOM取用_Pegging_Stage_Routing完整链路说明》§八，2026-09-28）】
    ///   「子件做到这个 Stage 以后，才成为当前父件可使用的 Supply。」
    ///   例：B 的 Stage 链为 CN_MACH → CN_SURF(IsSupplyThreshold=1)，则 B 完成 CN_MACH 尚不可供给 A，
    ///   须完成 CN_SURF。⇒ 本需求**不必走完该物料全部 Stage，做到本阶段即为终点**。
    ///
    /// null / 空 = 无阈值信息（源数据缺 `ChildRequiredStageCode`）⇒ 按既有保守口径「全工艺完成才可供给」
    /// （与冻结 DDL 的设计决策一致：`ChildRequiredStageCode=NULL 时按保守策略：子件必须全工艺完成后才可供给父件`）。
    /// </summary>
    public string? RequiredStageCode { get; set; }

    /// <summary>
    /// 续排起点工序码（工序级，比 StartStageCode 更细，对应 5号位 的 NextOperation/StartOperation）
    /// 2号位 Pegging 不扩展 Operation，此字段由 5号位 按执行进度交付；null = 尚无工序级起点（新单从第一道工序起 / 未接 5号位交付）。
    /// </summary>
    public string? StartOperationCode { get; set; }

    /// <summary>
    /// 净产出数量
    /// </summary>
    public decimal NetOutputQty { get; init; }

    /// <summary>
    /// 计划加工数量
    /// </summary>
    public decimal PlannedProcessQty { get; init; }

    /// <summary>
    /// 数量单位（P1-08 方案a）：2号位 装载时透传；1号位 FinalTaskDraft.UOM 据此原样回填，
    /// 2号位落盘不再反查订单补 UOM。null = 无单位来源（物料缺行 / Material.UOM 为空）。
    ///
    /// 【2026-10-09 修正取值源】原注释写「来自需求侧订单 <c>Order.UOM</c>」，但此处的 <c>order</c> 是**根订单**
    /// （成品），经 <c>TraverseBomNode</c> 逐层原样下传 ⇒ 与 PI 身份同源的串味：子件需求会拿到**成品的单位**。
    /// 实测 PV2：<c>[Order].UOM</c> 22,144 行**恒为 'PS'**；而 <c>Material.UOM</c> 1,022,985 行**零空值**
    /// 且按物料各不相同（EA/…）。⇒ 取值改为**该需求自身物料**的 <c>Material.UOM</c>，由
    /// <c>PeggingOrchestrator.FillDemandUomAsync</c> 在 Pegging 后按需求物料集统一回填（与
    /// <see cref="StartStageCode"/> / <see cref="RequiredStageCode"/> 同款「Pegging 后解析」模式）。
    /// 1号位 <c>PhaseFiveCompression.cs</c> 的注释「如需子件 UOM 请 2号位 指明」即此口径。
    /// </summary>
    public string? UOM { get; set; }

    /// <summary>
    /// 下游要求的可用时间
    /// </summary>
    public DateTime RequiredAvailableTime { get; init; }

    /// <summary>
    /// 已按冻结规则排好的业务顺序
    /// 不是全局PriorityScore，而是计算层→Priority Segment→段内排序的结果
    /// </summary>
    public int DemandSequence { get; init; }

    /// <summary>
    /// 生产指示号（PI类需求使用）
    /// </summary>
    public string? ProductionInstructionNo { get; init; }

    /// <summary>
    /// 是否未定位（PI Position为UNLOCATED）
    /// </summary>
    public bool IsUnlocated { get; init; }

    /// <summary>
    /// 软偏好资源（P1-11）：上一 ACTIVE Task 的 ResourceId。非硬锁——1号位在合法资源集内优先尝试，
    /// 不满足则回落其它合法资源；硬锁走 ExecutionConstraint。null = 无上一 ACTIVE 资源偏好（自由/新单）。
    /// </summary>
    public int? PreferredResourceId { get; init; }

    /// <summary>
    /// 备选软偏好资源（P1-11）：PreferredResourceId 不可用时的次优偏好。null = 无。
    /// </summary>
    public int? FallbackResourceId { get; init; }

    /// <summary>
    /// 是否连续份额（跨版本连续性的输入标记）。
    ///
    /// 【A/B/C 模型（《APS_V1_1号位有限产能排程开发实施包 v1.6》§本轮统一执行红线 Q2/Q3，0号位 2026-10-07 裁决 §四）】
    ///   · **Continuation Slice（A桶 / B桶）** = true —— 已有执行连续份额：带 ContinuationKey、
    ///     固定真实 RouteCode/PathId、StartOperation，按 NoSplitMerge 处理，**不得切换 Routing**。
    ///     A 与 B 对 Solver 语义**完全一致**（差别仅在 FinalTask 后 TaskNo 归属，属 2号位 身份处理，
    ///     1号位 不需要区分）。
    ///   · **Free Slice（C桶）** = false/缺省 —— 自由需求：Routing 由 1号位 在候选内联合择优选择。
    ///
    /// 1号位 消费语义：
    ///   · true  → 走「不拆合(P0-07) + 连续先行/单活跃资源(P0-08)」语义，且**固定路径**（不选路）；
    ///   · false → 若物料有 &gt;1 条候选 Path，走 C桶候选内择优（见 Phase2 需求级选路）。
    ///
    /// ⚠ **禁止用「候选 Path 条数」反推 A/B/C 身份**（0号位 2026-10-07 裁决 §四）：
    ///   A/B 所在物料本来就可能有多条合法 Routing，C 也可能当前只有 1 条合法 Path。
    ///   Path 数量 ≠ 桶身份，桶身份只能由本字段承载。
    /// </summary>
    public bool IsContinuation { get; init; }

    /// <summary>
    /// 连续份额身份键（《APS_V1_1号位有限产能排程开发实施包 v1.6》Q3，0号位 2026-10-07 裁决 §六 授权补载体）。
    ///
    /// 【口径（Q3 逐字）】一个 ScheduleRun 内，一个 MESWorkOrderNo 一个且仅一个 ContinuationKey；
    ///   同一 MES 工单多个 Slice 共享同一 Key；**不得把 LogicalDemandKey 拼入 Key**（否则同一 MES 工单裂分）。
    ///   由 2号位 按 ScheduleRun + MESWorkOrderNo 生成真实值；1号位 只做**透明消费 + FinalTask 原样回传**，
    ///   **不生成、不解析、不拼接**（生成权属 2号位）。
    ///   null = 非连续份额（Free Slice）或无 MES 工单身份。
    /// </summary>
    public string? ContinuationKey { get; init; }

    /// <summary>
    /// 固定工艺路径编码（《APS_V1_1号位有限产能排程开发实施包 v1.6》§1号位新增/替换实施要求）：
    /// 「A/B输入必须带真实固定 `RouteCode / PathId / StartOperation` 与完整指定 Path DAG」。
    /// 非空 ⇒ 该需求走**固定路径**，1号位 不选路（Continuation Slice）。
    /// null ⇒ 无固定路径，由 1号位 在候选内择优（Free Slice / C桶）。
    /// 与 <see cref="PathId"/> 成对使用；**A/B 缺值应 Fail Closed，不得回退猜唯一 Path**（0号位 2026-10-07 裁决 §三）。
    ///
    /// ⚠ 2026-10-09：由 <c>init</c> 放开为 <c>set</c> —— 分桶（<c>ApplyContinuityBucketing</c>）发生在
    ///   <c>LoadRoutingContextAsync</c> **之前**，切片构造时无法拿到路由上下文，只能在路由装载后由
    ///   <c>FillContinuationRouteIdentities</c> 统一回填（与 <see cref="StartStageCode"/> 同款理由）。
    ///
    /// 🔴 **桥接期取值 ≠ 业务真值**：该回填的取值源是上游刚被 <c>NormalizeToSingleRoute</c> 就地改写的路由载荷
    ///   ⇒ 当前实际回填的是 **`DEFAULT / 1`**（1号位 技术适配值）。冻结 <c>11_2号位v2.1</c> 执行红线 <b>Q1</b>
    ///   明令「`DEFAULT / 1` **不得**作为归一化后的业务真值」「A/B 向 1号位 发送固定**真实** Route/Path，
    ///   **禁止预先压成 DEFAULT/1**」⇒ 本字段在桥接期**仅供 1号位 查图，不得当作持久化/FinalTask/Task/MES
    ///   执行身份的真实路径来源**。终态 = ① 1号位 去 <c>StageTimingNodeBuilder.GetRoutelessStages</c> 的
    ///   <c>RouteCode != "DEFAULT"</c> 判断（我方归一化删除补丁已备好、压住不发）+ ② 5号位 按 slice 交付
    ///   真实 <c>RouteCode / PathId</c>（Q-1008-1）。两者齐备后 <c>FillContinuationRouteIdentities</c> 因
    ///   「已带真值即跳过」自动失效，本字段即为真值。桥接期每次 Run 都有 WARNING 留痕，不静默。
    /// </summary>
    public string? RouteCode { get; set; }

    /// <summary>
    /// 固定路径序号（与 <see cref="RouteCode"/> 成对，语义见该字段）。
    /// 类型与 <c>RoutingOperation.PathId</c> 一致（int），可直接用于 <c>RoutePathKey</c> 图查找。
    /// </summary>
    public int? PathId { get; set; }

    /// <summary>
    /// 不拆不合硬标记（《APS_V1_1号位有限产能排程开发实施包 v1.6》：「A/B输入…并按 `NoSplitMerge` 处理」）。
    /// true ⇒ 本份额不得拆分、不得与他份额合批（A/B Continuation Slice 恒为 true，由 2号位 按桶置位）。
    /// false/缺省 ⇒ 按既有 AllowSplit/AllowMerge 策略处理。
    /// 与 <see cref="IsContinuation"/> 的 P0-07 语义同向，本字段是**显式载体**（0号位 2026-10-07 裁决 §四：
    /// 「`NoSplitMerge` 及固定 Route/Path 应显式落实」）。
    /// </summary>
    public bool NoSplitMerge { get; init; }

    /// <summary>
    /// 首选资源编码（软偏好，《APS_V1_1号位有限产能排程开发实施包 v1.6》§1号位新增/替换实施要求 + 红线 Q4）。
    ///
    /// 【口径】5号位 保留 Operation 级 `LastReportResourceCode` 真实事实；2号位 关联当前 Continuation Slice
    ///   并校验当前 Operation Eligibility，**合法可靠时**形成本值，否则为 NULL。
    ///   1号位 对非空值必须作为**资源连续性软偏好**进入目标函数/排序，
    ///   **不得升级为 Hard Lock**，不得突破 Eligibility / Calendar / Firm·Frozen / Material / Routing 等硬约束。
    ///
    /// 与旧字段 <see cref="PreferredResourceId"/> 的关系：旧字段是「上一 ACTIVE Task 的 ResourceId」的 ID 形态；
    ///   本字段是 v1.6 冻结的 **Code 形态正式输入**。两者并存，1号位 优先消费本字段（Code→Resource 适配），
    ///   本字段为空时回落旧字段（行为不变）。
    /// </summary>
    public string? PreferredResourceCode { get; init; }

    /// <summary>
    /// 需求的业务目标（DemandGoal）—— 冻结规则清单 v1.5 <c>P-006</c> 两个取值：
    /// <c>CUSTOMER_COMMITMENT</c>（客户承诺）／<c>INVENTORY_REPLENISHMENT</c>（库存补充）。
    /// <c>P-008</c> 要求 Pegging 侧**传播**该目标（不传播 Direction）。
    ///
    /// 【粒度（0号位 2026-10-12 裁决《DemandGoal与SH整单及关联问题正式裁决回执 v1.0》）】
    ///   Goal 是**需求（Demand）级**的业务目标，不是 Supply／Task／MES工单 的固有属性（DG-01）。
    ///   ⇒ 载体＝本类（需求级），**不是** <c>PeggingSupplyAllocation</c>（份额级）；
    ///   同一 MTS PI 在 Supply 层承接不同需求，**不**因「600 已承接客户／剩 400」就把该 PI 自身
    ///   拆成两种 Goal（DG-03.4）——是否存在多目标只看**真实 Demand 关系**。
    ///
    /// 【字段形状（1号位 2026-10-12《技术事项汇总回执答复 v1.0》§四「需求级」分支点名）】
    ///   `public string? DemandGoal { get; set; }` —— 可空；取值**逐字大写、不归一**；
    ///   1号位 消费：缺值 ⇒ 登记 <c>DEMAND_GOAL_ABSENT</c> 进 SolveTraceNotes（既有行为不变）。
    ///   契约约定：1↔2 契约字段只按 1号位 点名字段扩 —— 本字段即按其点名落地。
    ///
    /// 【取值规则（0号位 裁决 §一 DG-05 ／ §2.1–§2.4）】
    ///   按**实际 Pegging 供需承接及需求份额血缘**确定，**不得**按 <c>OrderType</c>／单据类型硬映射
    ///   （DG-02/DG-04、§一.1）；正式交给 Solver 且血缘可唯一判定的需求**不应**长期为空；
    ///   仅「事实确实不足、血缘无法唯一判定」的异常输入才允许留空，且必须显式 Issue／诊断，
    ///   **不猜值、不一律当补库**（§2.4）。
    ///
    /// ✅ **2026-10-13 状态：取值链已投产**（原 2026-10-12「恒 null」段作废）。
    ///   取值点 = `PeggingOrchestrator.FillDemandGoalsAsync`，在 `BuildSolveRequestAsync` 内、
    ///   **跨版本连续性分桶之前**调用（切片 <c>CloneDemandSlice</c>／<c>CloneDemandRescaled</c>
    ///   均原样继承本字段，故先填即两片自动带值）。
    ///
    ///   【判据 —— 按**需求血缘根**一刀切，粒度=需求级（0号位 §3.3 答复）】
    ///   <list type="bullet">
    ///     <item><b>O 行</b>（客户订单行，<c>OrderNo</c> 非 'P' 开头）且 <c>CustomerSegment ≠ '跨厂'</c>
    ///       ⇒ <c>CUSTOMER_COMMITMENT</c>（客户履约需求）。</item>
    ///     <item><b>P 行</b>（PI 自身指令行，<c>OrderNo</c> 以 'P' 开头）⇒ <c>INVENTORY_REPLENISHMENT</c>。
    ///       依 DG-03 §一.1：顶层/中间层 MTS 自成独立补库需求，**被上层客户需求消费并不改写其自身需求身份**。</item>
    ///     <item><b>跨厂 SH</b>（<c>CustomerSegment = '跨厂'</c>）⇒ **当前留空 + 诊断**（⚠ 未落地项）。
    ///       SH-02 要求整单判「被真实上层需求承接 ⇒ 客户承诺 / 孤儿 ⇒ 补库」，但唯一可得信号
    ///       （<c>SupplyAllocations</c> 里 <c>ShippingInstructionNo = SH号</c> 的分配行）**区分不出**
    ///       「SH 供给在目标厂自扣自己那张订单」与「被上层承接」——实测 PV2 四个 SH 订单
    ///       <c>Allocation=4</c> 而需求=0（即自扣）。用它会恒判客户承诺（含真孤儿），违反 SH-02；
    ///       故按 DG-05 §2.4「不猜、不一律当补库」留空，待 2→1 联调定载体。**当前 PV2 SH 产出 0 条需求**，
    ///       本分支无实例，不得记作「已验证」。</item>
    ///     <item><b>BOM 子件需求</b>⇒ 自动随根（子需求 <c>OrderId</c> 即**根订单** Id，BOM 传播无需另判）。</item>
    ///     <item><b>血缘无法唯一确定</b>（判例 8：订单主档缺行等）⇒ **留 null + Warning 诊断**，
    ///       不猜、不自动归补库；1号位 侧按既有 <c>DEMAND_GOAL_ABSENT</c> 登记缺口（行为不变）。</item>
    ///   </list>
    ///
    ///   【为什么不用 <c>OrderType</c>】PV2 实测 **533 行** <c>OrderType='PRODUCTION_INSTRUCTION'</c>
    ///   却是 O 行（客户订单引用了 PI）⇒ 按 <c>OrderType</c> 硬映射会把它们误判（DG-02 明令禁止）。
    ///   干净判据 = <c>OrderNo</c> 前缀 ＋ <c>CustomerSegment</c>。
    /// </summary>
    public string? DemandGoal { get; set; }
}

/// <summary>
/// <see cref="LogicalProductionDemand.DemandGoal"/> 的取值常量（冻结规则清单 <c>P-006</c>）。
///
/// **为什么用常量而非 enum**（我方 2026-10-12 致 1号位 建议、对方采纳）：
///   跨号位契约取值走 <c>string</c> 更耐演进——新增取值时无需两边同步改枚举并重编译；
///   且 1号位 侧 `SchedulingDirectionResolver` 的比对是 <c>StringComparison.Ordinal</c>，
///   ⇒ **逐字大写、大小写敏感**，本类取值必须与之逐字一致（不得归一化、不得小写）。
/// </summary>
public static class DemandGoalValues
{
    /// <summary>客户承诺（P-006 取值一）。1号位 消费为**反向**信号。</summary>
    public const string CustomerCommitment = "CUSTOMER_COMMITMENT";

    /// <summary>库存补充（P-006 取值二）。1号位 消费为**正向**信号。</summary>
    public const string InventoryReplenishment = "INVENTORY_REPLENISHMENT";
}
