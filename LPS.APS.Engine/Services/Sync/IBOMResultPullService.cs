namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// BOM展开结果接货服务接口（2号位职责 — 2.3.4）
///
/// 数据路径：
///   ODS库: MES_API_BOM_Request（READY状态） + MES_APS_BOM_Workset（展开结果）
///   → 流式 SqlBulkCopy →
///   APS库: APS_BOM_RAW（本地BOM缓存）
///   → 后续: sp_CalculateLLC 计算低阶码
///   → 后续: 生成 OrderBomRequestLink（v5.0.31）
/// </summary>
public interface IBOMResultPullService
{
    /// <summary>
    /// 从ODS拉取BOM展开结果到APS本地库，并生成 OrderBomRequestLink。
    /// BOM 是物料级事实、按批次接货一次；OrderBomRequestLink 跨本批全部 Domain PlanVersion 映射
    /// （每顶层 Order 只归一个真实 Domain，故 OrderCanonicalId 在本批各 PlanVersion 中唯一命中）。
    /// </summary>
    /// <param name="batchNo">批次号（由 BOMRequestService 推送时生成）</param>
    /// <param name="planVersionIds">本批全部 Domain 的 PlanVersionID 集合（由 NightlyBatchOrchestrator 显式传入，禁止内部猜测）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>拉取的行数</returns>
    Task<int> PullBOMResultFromODSAsync(string batchNo, IReadOnlyList<int> planVersionIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验**指定批次**是否已 READY（用于 NightlyBatchOrchestrator 自动接货）。
    ///
    /// 0号位 2026-10-17 §三.C：接货必须按「本次夜间那一个批号」校验，
    /// `WHERE BatchNo = @expectedBatchNo AND Status = 'READY'` —— **不再查「任意最新 READY」**。
    /// 批号由 <see cref="BomNightBatchIdentity"/> 从推送成功日志唯一解析；解析不出则不接货。
    /// </summary>
    /// <param name="expectedBatchNo">本次夜间应接货的批号（必传；空/空白 ⇒ 直接返回 null，不做任何回落）</param>
    /// <returns>该批号在 ODS 确为 READY 且展开行数 &gt; 0 时返回同一批号；否则 null</returns>
    Task<string?> FindReadyBatchAsync(string expectedBatchNo, CancellationToken cancellationToken = default);

    /// <summary>
    /// 独立接货：接**本次夜间**的批次（与 NightlyBatchOrchestrator 解耦，供白天补接货 / 联调单独触发）。
    ///
    /// 批号来源与夜间链一致：<see cref="BomNightBatchResolver"/> 从推送成功日志唯一解析；
    /// 解析不出则不接货（**不回落「任意最新 READY」**，0号位 2026-10-17 §三.C）。
    /// 无可用批次时返回空结果（不抛异常）。
    /// </summary>
    /// <param name="planVersionIds">本批全部 Domain 的 PlanVersionID 集合（供 OrderBomRequestLink 映射，禁止内部猜测）</param>
    /// <param name="expectedBatchNo">显式指定批号（联调/补接货用）；为 null 时按本次夜间唯一推送记录解析</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<BOMIntakeResult> IntakeNightBatchAsync(IReadOnlyList<int> planVersionIds, string? expectedBatchNo = null, CancellationToken cancellationToken = default);
}
