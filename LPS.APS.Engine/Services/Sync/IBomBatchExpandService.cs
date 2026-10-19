namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// BOM 批展开驱动服务（2号位职责）。
///
/// 【存在理由】夜间三段的第 ② 步原本没有执行者：
///   ① 推送  : Hangfire <c>bom-request-push</c>（每日 00:00）→ MES_API_BOM_Request(PENDING) + _Detail
///   ② 展开  : <c>sp_ExpandBOMBatch_vNext</c>（ODS 侧，5号位 对象）→ 重建四表、批次转 READY
///   ③ 接货  : Hangfire <c>nightly-batch</c>（每日 00:30）Step 5 → 按 READY 闸门取回
/// 我方代码三处注释曾记载「SQL Agent Job 00:05 自动拾取 PENDING 批次执行 sp_ExpandBOMBatch_vNext」，
/// 但 2026-10-17 在 ODS <c>msdb</c> 实测该作业不存在（jobstep 命令含 ExpandBOM / MES_API_BOM / BOM_Workset
/// 的作业 0 个，无 00:05 调度）⇒ ② 恒不执行 ⇒ ③ 的 READY 闸门恒返回空、静默跳过。
/// 本服务即用于补回 ② 的执行者，时刻与原记载一致（00:05，介于 ① 与 ③ 之间）。
///
/// 【为何显式传 @BatchNo】该 SP 的 <c>@BatchNo IS NULL</c> 兜底是
///   <c>SELECT TOP 1 [BatchNo] FROM MES_API_BOM_Request ORDER BY CreatedAt DESC</c> —— **不看 Status**。
/// 而该 SP 定义内前四条语句就是清空 ODS 四表，把「清空谁的表」交给隐式排序不可接受 ⇒ 本方自取最新 PENDING 批并显式传参。
///
/// 【⚠ 调用是破坏性的】<c>sp_ExpandBOMBatch_vNext</c> 定义内 L468-471 四条 <c>truncate table</c>
/// （MES_APS_BOM_Workset / _StageDetail / _Issues / _CrossFactoryEdge）位于 L476 <c>BEGIN TRAN;</c> **之外**
/// ⇒ 清空立即落定、事务回滚救不回；且 MES_APS_BOM_Workset_Archive 实测 0 行且全库零引用 ⇒ 无恢复路径。
/// 清空—重建期间四表处于中间态，故安排在 00:05（无其他消费方），接货在 00:30。
///
/// 【失败语义】照 <see cref="IBomRealtimeExpandService"/>「不得作为硬失败打断」的既有口径：
/// 任何异常都捕获转 Issue，**不 rethrow**（Hangfire 会判成功）。因此溯源只能靠 APS_ETL_Log（Step=BomBatchExpand）。
/// </summary>
public interface IBomBatchExpandService
{
    /// <summary>
    /// 展开**本次夜间**的推送批次，调用 ODS 的 <c>sp_ExpandBOMBatch_vNext</c> 展开为 READY。
    ///
    /// 「本次」由 <see cref="BomNightBatchIdentity"/> 决定：读 `APS_ETL_Log` 中 `Step=BomRequestPush`、
    /// `Status=SUCCESS`、`NightKey=今日` 的记录 —— **恰好一条**才是本次；0 条或多条一律 `SKIPPED` 并留 Issue，
    /// **不再扫描「最新 PENDING」猜选**（0号位 2026-10-17 §二.2／§三.B）。
    /// 选中后还要过 ODS 批次状态闸门（`PENDING` 才展开；`PROCESSING/READY/CONSUMED/FAILED/不存在` 均不动）。
    /// 任何不满足条件的情形都不调用 SP、不抛异常，返回 <see cref="Dto.BomBatchExpandResult.Expanded"/> = false。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>展开结果（含 PENDING 积压数与失败转译的 Issue 清单）</returns>
    Task<Dto.BomBatchExpandResult> ExpandNightBatchAsync(CancellationToken cancellationToken = default);
}
