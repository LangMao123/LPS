using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// PlanVersion 治理仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.PlanVersion（冻结 DDL v5.1.2 §3.2）
/// 边界：仅更新确认/激活列（Status / ActivatedAt / ActivatedBy / ArchivedAt）；不重写 2号位结果持久化流转。
/// </summary>
/// <remarks>
/// 开发者：3号位
///
/// 【2026-10-15 口径回退：原「A 治理」作废，改走 B】正式采用**记在 <c>Status</c>**：
///   冻结字段说明 07 §4.2（「正式采用=ACTIVE」）／冻结 DDL 08:8723 逐字「正式采用直接通过
///   PlanVersion.Status = ACTIVE 表示」／我方 2026-09-03《PlanVersion状态词表说明》§一.2（ACTIVE 列在 Status 表）—— 三处同口径。
///   <c>VersionCategory</c> = **版本类别（出生即定、终身不变）**，治理动作**不得翻转它**。
/// A 的问题：让激活翻转 VersionCategory（CANDIDATE→ACTIVE→ARCHIVED），违反上述三处 ＋「出生即定」不变量，
///   并把唯一索引谓词改成 VersionCategory='ACTIVE'，与冻结 DDL 08:4057 的 `WHERE Status = 'ACTIVE'` 不符。
/// 真 bug 只在**读侧判定顺序**（RunLifecycleService.GetRunDomainStatusAsync 先判 VersionCategory=='CANDIDATE'，
///   而每行 ACTIVE 的 VersionCategory 恒为 CANDIDATE ⇒「已采用 ⇒ COMPLETED」分支永不可达）—— 已改为**先判 Status**。
/// 归档：Status→ARCHIVED ＋ ArchivedAt；VersionCategory 全程不动。
/// </remarks>
public class PlanVersionRepository : IPlanVersionRepository
{
    private const string PlanVersionActiveStatus = "ACTIVE";
    private const string PlanVersionArchivedStatus = "ARCHIVED";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PlanVersionRepository> _logger;

    public PlanVersionRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<PlanVersionRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<PlanVersion?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[PlanVersion]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<PlanVersion>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task UpdateAsync(PlanVersion version, CancellationToken ct = default)
    {
        // 口径回退（2026-10-15）：治理动作只动 Status/ActivatedAt/ActivatedBy ——
        // VersionCategory 是出生即定的版本类别，治理侧不得翻转。当前生产无调用方（激活走 ReplaceActiveAsync）。
        const string sql = @"
            UPDATE [dbo].[PlanVersion]
            SET [Status]      = @Status,
                [ActivatedAt] = @ActivatedAt,
                [ActivatedBy] = @ActivatedBy
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(
            sql,
            new
            {
                version.Id,
                version.Status,
                version.ActivatedAt,
                version.ActivatedBy,
            },
            db: DatabaseId.APS);
    }

    public async Task ReplaceActiveAsync(
        PlanVersion candidate,
        string actor,
        DateTime activatedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidate.DomainKey))
        {
            throw new InvalidOperationException($"原子替换失败：候选版本 {candidate.Id} 的 DomainKey 为空");
        }

        // 单事务原子替换：① 同域既有 ACTIVE 归档（Status→ARCHIVED + ArchivedAt）；
        //                 ② 本 Candidate 置 ACTIVE（Status→ACTIVE + ActivatedAt/ActivatedBy）。
        // 口径回退（2026-10-15）：① 的筛选与 ② 的赋值都回到 Status ——
        // 冻结 DDL 08:4057 的唯一索引谓词就是 `WHERE Status = 'ACTIVE'`，身份列 VersionCategory 全程不动。
        // UQ_PlanVersion_OneActivePerDomain 红线保留：任意时点同域最多一个 ACTIVE。
        var archivedCount = await _connectionManager.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const string archiveSql = @"
                UPDATE [dbo].[PlanVersion]
                SET [Status]     = @ArchivedStatus,
                    [ArchivedAt] = @ArchivedAt
                WHERE [DomainKey] = @DomainKey
                  AND [Status]    = @ActiveStatus
                  AND [Id]       <> @CandidateId";

            const string activateSql = @"
                UPDATE [dbo].[PlanVersion]
                SET [Status]      = @ActiveStatus,
                    [ActivatedAt] = @ActivatedAt,
                    [ActivatedBy] = @ActivatedBy
                WHERE [Id] = @CandidateId";

            var affected = await connection.ExecuteAsync(archiveSql,
                new
                {
                    ArchivedStatus = PlanVersionArchivedStatus,
                    ActiveStatus = PlanVersionActiveStatus,
                    DomainKey = candidate.DomainKey,
                    ArchivedAt = activatedAt,
                    CandidateId = candidate.Id,
                },
                transaction);

            await connection.ExecuteAsync(activateSql,
                new
                {
                    ActiveStatus = PlanVersionActiveStatus,
                    ActivatedAt = activatedAt,
                    ActivatedBy = actor,
                    CandidateId = candidate.Id,
                },
                transaction);

            return affected;
        }, db: DatabaseId.APS);

        _logger.LogInformation("PlanVersion 原子替换成功：CandidateId={CandidateId}, Domain={DomainKey}, 归档同域 ACTIVE 数={ArchivedCount}",
            candidate.Id, candidate.DomainKey, archivedCount);
    }

    public async Task<PlanVersion?> GetActiveByDomainKeyAsync(string domainKey, CancellationToken ct = default)
    {
        // 口径回退（2026-10-15）：按 Status 取「当前正式采用」版本（与冻结 DDL 唯一索引谓词同列）。
        const string sql = @"
            SELECT TOP 1 * FROM [dbo].[PlanVersion]
            WHERE [DomainKey] = @DomainKey
              AND [Status] = @ActiveStatus
            ORDER BY [CreatedAt] DESC, [Id] DESC";

        return await _connectionManager.QueryFirstOrDefaultAsync<PlanVersion>(
            sql, new { DomainKey = domainKey, ActiveStatus = PlanVersionActiveStatus }, db: DatabaseId.APS);
    }

    public async Task<PlanVersion?> GetLatestByScheduleRunIdAsync(int scheduleRunId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP 1 * FROM [dbo].[PlanVersion]
            WHERE [SourceScheduleRunId] = @ScheduleRunId
            ORDER BY [CreatedAt] DESC, [Id] DESC";

        return await _connectionManager.QueryFirstOrDefaultAsync<PlanVersion>(
            sql, new { ScheduleRunId = scheduleRunId }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<PlanVersion>> GetByScheduleRunIdAsync(int scheduleRunId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[PlanVersion]
            WHERE [SourceScheduleRunId] = @ScheduleRunId
            ORDER BY [DomainKey], [CreatedAt] DESC, [Id] DESC";

        var results = await _connectionManager.QueryAsync<PlanVersion>(
            sql, new { ScheduleRunId = scheduleRunId }, db: DatabaseId.APS);

        return results.ToList();
    }
}
