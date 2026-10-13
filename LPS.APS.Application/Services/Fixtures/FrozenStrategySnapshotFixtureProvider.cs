using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services.Fixtures;

/// <summary>
/// 冻结策略快照 Fixture 提供器（联调/测试专用，不得进入生产 DI）
///
/// 与 <see cref="DemandPriorityFixtureProvider"/> 同理：生产用真实
/// <see cref="IFrozenStrategySnapshotProvider"/>（FrozenStrategySnapshotProvider），
/// 本 Fixture 仅供集成测试在测试库缺失真实 PUBLISHED StrategyProfileVersion 时
/// 通过策略上下文完整性校验，并让 2号位 Supply 多键排序按稳定兜底执行。
///
/// INTEGRATION TODO: 测试库补齐真实六块 ContentSnapshotJson 后，移除本 Fixture，
/// 复用生产真实 Provider。不得将此 Fixture 逻辑作为生产 Fallback。
/// </summary>
public sealed class FrozenStrategySnapshotFixtureProvider : IFrozenStrategySnapshotProvider
{
    /// <summary>合成夹具物料业务键（`SyntheticSchedulingIntegrationTest.TEST_MATERIAL_ID`）。</summary>
    private const int SYNTHETIC_MATERIAL_ID = 9900001;

    /// <summary>合成夹具部门业务键（`SyntheticSchedulingIntegrationTest.TEST_DEPT_ID`）。</summary>
    private const int SYNTHETIC_DEPARTMENT_ID = 999001;

    /// <summary>
    /// 返回最小有效快照：Supply 块按默认值（WarehousePriority 空、PiSort 默认 IssueDateAsc），
    /// 其余块保持默认空对象。2号位据此按 AvailableAt + 稳定兜底排序。
    ///
    /// 【⑧ Batch Policy 例外（2026-10-12 补，**仅覆盖合成夹具物料**）】
    ///   1号位 P0-01（svn r13558，2026-10-08）把「C 桶缺有效 Batch Policy」改为**硬失败**
    ///   （`BATCH_POLICY_MISSING` ⇒ 该需求不成批），按 `(MaterialId, ProductionDepartmentId)` **精确命中**，
    ///   **禁止**回落 Material 级默认 / 全局默认。⇒ 本 Fixture 若⑧块留空，**合成自洽夹具**（自建物料
    ///   9900001 + 自建部门 999001，本该「同一部门闭环」）必然拿不到策略、全链产 0 个 Task。
    ///   故此处只给**该合成物料 + 该合成部门**补一条最小策略（`AllowSplit=true`、`Min=1`、无上限），
    ///   使单条需求量 1 有且仅有 1 批的合法切分。
    ///   ⚠ **刻意只覆盖合成业务键**：真实数据用例（`ContinuityRedLineIntegrationTest` 等）走本 Fixture 时
    ///   仍拿不到任何策略 ⇒ `BATCH_POLICY_MISSING` 的真实信号**不被本例外掩盖**
    ///   （真实缺口 = ⑧块 `ContentSnapshotJson.BatchPolicy` 仅 4 条，归 3号位 治理侧重新发布，见台账 §T-1010b 段）。
    /// </summary>
    public Task<FrozenStrategySnapshot> GetFrozenStrategySnapshotAsync(
        long strategyProfileVersionId,
        CancellationToken ct)
    {
        var snapshot = new FrozenStrategySnapshot
        {
            StrategyProfileVersionId = strategyProfileVersionId,
            Supply = new SupplyBlock
            {
                Inventory = new InventoryAvailabilityRule
                {
                    IsEnabled = false,
                    WarehousePriority = []
                },
                PiSort = new PiSortParams
                {
                    SortBy = PiSortBy.IssueDateAsc,
                    UseStablePiNoTieBreak = false
                }
            },
            BatchPolicy =
            [
                new BatchPolicyRuleSnapshot
                {
                    MaterialId             = SYNTHETIC_MATERIAL_ID,
                    ProductionDepartmentId = SYNTHETIC_DEPARTMENT_ID,
                    MinExecutionBatchQty   = 1m,
                    MaxExecutionBatchQty   = null,
                    PreferredBatchQty      = 1m,
                    AllowSplit             = true,
                    AllowMerge             = true
                }
            ]
        };

        return Task.FromResult(snapshot);
    }
}
