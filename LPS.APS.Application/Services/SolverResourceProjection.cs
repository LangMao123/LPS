namespace LPS.APS.Application.Services;

/// <summary>
/// 人工能力槽 → SolverResource 的运行时投影规则（PM 0923 资源模型裁决）。
/// ManualCapacitySlot 不进入正式 Resource 主数据；装载时投影为合成 Solver Resource，
/// 合成 ResourceId 与设备 Resource.Id 分属不同数值空间、避免冲突：
///   合成 ResourceId = ManualSlotResourceOffset + ManualSlotId。
/// 该键仅存在于运行时对象（DomainSolveRequest.Resources / CalendarSlots / OperationResourceEligibility），
/// 不落库、不暴露负号规则；结果落库时拆回：Task.ResourceId = NULL + Task.ManualSlotId。
/// </summary>
internal static class SolverResourceProjection
{
    /// <summary>人工槽合成 ResourceId 偏移（实测设备 Resource.Id MAX=14508，10 亿偏移绝不冲突）。</summary>
    public const int ManualSlotResourceOffset = 1_000_000_000;

    /// <summary>人工槽合成 ResourceCode 前缀（落库用：MAN: + OperationName + SlotCode，与设备业务码通过前缀区分，不冲突）。</summary>
    public const string ManualSlotResourceCodePrefix = "MAN:";

    /// <summary>
    /// 合成键上界守卫：`ManualSlotId` 超过此值会使 `ManualSlotResourceOffset + ManualSlotId` 溢出 `int`。
    /// **仅用于断言/告警**，不改变投影与拆回行为。
    /// </summary>
    public const int ManualSlotIdMaxGuard = int.MaxValue - ManualSlotResourceOffset;

    /// <summary>判断一个运行时 ResourceId 是否为人工槽合成键。（下界判据；上界见 <see cref="ManualSlotIdMaxGuard"/>）</summary>
    public static bool IsManualSlotId(int resourceId) => resourceId >= ManualSlotResourceOffset;

    /// <summary>从合成键拆回 ManualSlotId。</summary>
    public static int ToManualSlotId(int resourceId) => resourceId - ManualSlotResourceOffset;

    /// <summary>
    /// `ManualSlotId` 是否落在可安全合成（不溢出 int）、可安全拆回的正数范围内。
    /// **守卫用途**：越界时由调用方记 WARNING；投影/拆回本身行为不变。
    /// </summary>
    public static bool IsWithinManualSlotRange(int manualSlotId)
        => manualSlotId > 0 && manualSlotId <= ManualSlotIdMaxGuard;

    /// <summary>
    /// 从运行时资源集合中筛出**纯设备** `ResourceId`（剔除人工槽合成键）——
    /// 供 <c>DomainSolveRequest.DeviceResourceIds</c> 投影（1号位 2026-10-16 R-2 甲案「入参载体我方点名」）。
    ///
    /// 【为什么需要它】R-2 口径 =「**设备**有效稼动」，但 1号位 侧的资源集合**含**人工槽合成键
    ///   （1号位 已书面自证其 occupancy 为人工槽预建）⇒ 单一排序键无法区分「含人工槽」与「纯设备」两个口径。
    ///   1号位 点名：由 2号位 在装载侧给出纯设备白名单，1号位 据此**分别**算两个排序键，
    ///   **默认仍取含人工槽那个** ⇒ 默认行为不变、零回归。
    ///
    /// 【为什么由 2号位 给、而不是 1号位 自判】判据 `IsManualSlotId` 属**合成键规则**，而合成键由 2号位 生成
    ///   （见本类顶部注释）；1号位 侧 `ManualSlot`/`1_000_000_000` grep = 0 命中、不得硬编码阈值
    ///   （1号位 回执 §四 逐字：1号位「**不做任何资源身份判别、不硬编码任何阈值**」）。
    ///
    /// 【形态】升序去重，结果**确定性**；**空集合 ≠ null** —— 空 = 「本 Run 投影了、但一个纯设备都没有」（有信息量），
    ///   null = 「未投影」（1号位 保持现行行为）。故本函数只负责筛，null 语义由调用方决定。
    /// </summary>
    public static IReadOnlyList<int> SelectDeviceResourceIds(IEnumerable<int> resourceIds)
        => resourceIds is null
            ? Array.Empty<int>()
            // 必须是**正数**：0 与负数都不是 `Resource` 主数据里的设备（实测最小 Id=6,444），
            //   混入即为「凭空造设备」（0号位 第十四轮 J14-07）。
            : resourceIds.Where(id => id > 0 && !IsManualSlotId(id)).Distinct().OrderBy(id => id).ToList();
}