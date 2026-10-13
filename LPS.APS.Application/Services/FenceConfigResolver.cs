namespace LPS.APS.Application.Services;

/// <summary>
/// `[FenceConfig]` 一行的只读投影（**参数治理归 3号位**；2号位 只读，见
/// 《…业务裁决建议稿 v1.0 20260908》§7.1「3号位：维护/治理 FenceConfig 中的 FrozenDays、FirmDays 及版本」）。
///
/// 列形状对齐冻结 DDL `08` §…／字段说明 `07` §7.2（实库已核：10 列）。
/// </summary>
public sealed record FenceConfigRow
{
    public int Id { get; init; }
    public int ProductFamilyId { get; init; }

    /// <summary>NULL = 该产品族下**全厂**通用（作用域通配）。</summary>
    public int? FactoryId { get; init; }

    /// <summary>
    /// 工艺类型（冻结 DDL 值域：`ASSEMBLY` / `MACHINING` / `PROCUREMENT_DOMESTIC` / `PROCUREMENT_IMPORT`）。
    /// ⚠ 2号位 侧**没有** stage→本值域的映射（实库 `RoutingOperation.ProcessType` 是**中文**，如 `CN组加`/`CN机加`，
    /// 与本值域对不上）⇒ 本列**不参与**匹配，改由 <see cref="FenceConfigResolver"/> 用「参数是否一致」消歧，
    /// 不一致就**不猜**。映射归 3号位 铺数时一并治理。
    /// </summary>
    public string ProcessType { get; init; } = string.Empty;

    public int FrozenDays { get; init; }
    public int FirmDays { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
}

/// <summary>
/// Fence 参数解析结果。
/// **无参数不是"默认 3/7 天"** —— §7.1/§15 明令 2号位 只读快照、不得自己重判
/// ⇒ 解析不出就返回 <c>Parameters = null</c> + 明确的 <see cref="Status"/>，由调用侧**显式留痕**。
/// </summary>
public sealed record FenceResolution(FenceParameters? Parameters, string Status, string Detail);

/// <summary>
/// **`FenceConfig` 作用域解析**（纯函数，无 IO ⇒ 可单测）。
///
/// 【解析口径】
///   1. 生效过滤：`EffectiveFrom &lt;= 基准日` 且（`EffectiveTo IS NULL` 或 `EffectiveTo &gt;= 基准日`）。
///      ⚠ `EffectiveTo` 可空，**显式**处理 NULL，**不依赖三值逻辑**
///      （`EffectiveTo &gt;= x` 遇 NULL 得 UNKNOWN ⇒ 会静默滤掉全部"永久生效"行）。
///   2. 作用域：`ProductFamilyId` 必须等值；`FactoryId` **精确优先**，无精确行再退 `FactoryId IS NULL`（通配）。
///   3. 同一作用域下若只剩**参数不一致**的多行（差异只能是 `ProcessType`，见
///      <see cref="FenceConfigRow.ProcessType"/>）⇒ **歧义 ⇒ 不猜、返回 null**。
///   4. 命中多行且参数一致 ⇒ 取 `EffectiveFrom` 最新的一行（版本化治理的自然语义）。
///
/// 【为什么按 (ProductFamilyId, FactoryId) 而不带 ProcessType】见 <see cref="FenceConfigRow.ProcessType"/>：
///   2号位 侧无该值域的映射。带一个**猜出来的** ProcessType 去查，比不查更糟。
/// </summary>
public static class FenceConfigResolver
{
    /// <summary>解析成功。</summary>
    public const string StatusOk = "OK";

    /// <summary>表中无任何在基准日生效的行（**实库当前即此状态：0 行**）。</summary>
    public const string StatusNoEffectiveRow = "FENCE_CONFIG_EMPTY";

    /// <summary>有生效行，但无匹配该 (产品族, 工厂) 作用域的行。</summary>
    public const string StatusScopeNotFound = "FENCE_SCOPE_NOT_FOUND";

    /// <summary>命中多行且参数不一致（只可能因 ProcessType 分裂）⇒ 不猜。</summary>
    public const string StatusAmbiguous = "FENCE_SCOPE_AMBIGUOUS";

    /// <param name="rows">`[FenceConfig]` 全量行（由调用侧一次读入）。</param>
    /// <param name="basisTime">`FenceBasisTime`（只取日期部分参与生效比较）。</param>
    /// <param name="productFamilyId">`[Order].ProductFamilyId`。</param>
    /// <param name="factoryId">`[Order].FactoryId`；null = 未知工厂 ⇒ 只认通配行。</param>
    public static FenceResolution Resolve(
        IReadOnlyList<FenceConfigRow> rows,
        DateTime basisTime,
        int productFamilyId,
        int? factoryId)
    {
        if (rows is null || rows.Count == 0)
            return new FenceResolution(null, StatusNoEffectiveRow, "FenceConfig 无行");

        var basisDate = basisTime.Date;

        // ① 生效过滤（EffectiveTo 的 NULL 显式处理，勿依赖三值逻辑）。
        var effective = rows
            .Where(r => r.EffectiveFrom.Date <= basisDate
                        && (r.EffectiveTo is null || r.EffectiveTo.Value.Date >= basisDate))
            .ToList();

        if (effective.Count == 0)
            return new FenceResolution(null, StatusNoEffectiveRow,
                $"FenceConfig 无行在基准日 {basisDate:yyyy-MM-dd} 生效（共 {rows.Count} 行）");

        // ② 产品族等值。
        var byFamily = effective.Where(r => r.ProductFamilyId == productFamilyId).ToList();
        if (byFamily.Count == 0)
            return new FenceResolution(null, StatusScopeNotFound,
                $"FenceConfig 无 ProductFamilyId={productFamilyId} 的生效行（生效行 {effective.Count} 条）");

        // ③ 工厂：精确优先，其次通配（FactoryId IS NULL）。
        List<FenceConfigRow> scoped;
        string scopeNote;
        if (factoryId is int fid)
        {
            scoped = byFamily.Where(r => r.FactoryId == fid).ToList();
            if (scoped.Count > 0)
            {
                scopeNote = $"FactoryId={fid}（精确）";
            }
            else
            {
                scoped = byFamily.Where(r => r.FactoryId is null).ToList();
                scopeNote = $"FactoryId={fid} 无精确行 ⇒ 退通配行(NULL)";
            }
        }
        else
        {
            scoped = byFamily.Where(r => r.FactoryId is null).ToList();
            scopeNote = "FactoryId 未知 ⇒ 只认通配行(NULL)";
        }

        if (scoped.Count == 0)
            return new FenceResolution(null, StatusScopeNotFound,
                $"FenceConfig 无匹配作用域（ProductFamilyId={productFamilyId}, {scopeNote}）");

        // ④ 同作用域内参数必须一致（差异只可能来自 ProcessType 分裂 —— 2号位 无映射 ⇒ 不猜）。
        var distinctParams = scoped
            .Select(r => (r.FrozenDays, r.FirmDays))
            .Distinct()
            .ToList();

        if (distinctParams.Count > 1)
        {
            var detail = string.Join(" / ", scoped.Select(r => $"[ProcessType={r.ProcessType}] {r.FrozenDays}+{r.FirmDays}"));
            return new FenceResolution(null, StatusAmbiguous,
                $"FenceConfig 作用域（ProductFamilyId={productFamilyId}, {scopeNote}）命中参数不一致的 {scoped.Count} 行 ⇒ 不猜：{detail}");
        }

        // ⑤ 参数一致 ⇒ 取 EffectiveFrom 最新的一行（版本化治理的自然语义）。
        var chosen = scoped.OrderByDescending(r => r.EffectiveFrom).First();

        return new FenceResolution(
            new FenceParameters(basisTime, chosen.FrozenDays, chosen.FirmDays),
            StatusOk,
            $"FenceConfig Id={chosen.Id}（ProductFamilyId={productFamilyId}, {scopeNote}, ProcessType={chosen.ProcessType}, "
            + $"FrozenDays={chosen.FrozenDays}, FirmDays={chosen.FirmDays}, EffectiveFrom={chosen.EffectiveFrom:yyyy-MM-dd}）");
    }
}
