using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using LPS.APS.Core.DTOs.Governance;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 参数集版本仓储接口（阶段 A-2：Engine 治理仓储，Dapper + DatabaseConnectionManager 实现）
/// 对应 APS_Production.ParameterSetVersion。
/// 红线：已发布版本不可原地修改；新 Run 可引用新版本、旧 Run 引用不变（R02 验收场景）。
/// </summary>
public interface IParameterSetVersionRepository
{
    /// <summary>按 Id 查询参数集版本</summary>
    Task<ParameterSetVersion?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>按参数集 Id 查询全部版本（按 VersionCode 排序）</summary>
    Task<IReadOnlyList<ParameterSetVersion>> GetByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default);

    /// <summary>
    /// 按参数集 Id 查询版本摘要（2026-10-15 4号位 来文：版本列表端点 SELECT * 整列搬 ContentSnapshotJson 大 LOB 致 PSV 1305 稳定 37s）。
    /// 显式列仅交付摘要字段（排除 ContentSnapshotJson 及五主题中间态 JSON 载体列），列表读端点专用；
    /// 需要完整快照的消费方（G3 当前生效版本投影、详情端点）仍走 <see cref="GetByParameterSetIdAsync"/>。
    /// </summary>
    Task<IReadOnlyList<ParameterSetVersionSummaryDto>> GetSummariesByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default);

    /// <summary>
    /// 按 Id 查询版本详情（方案 A 落点，2026-10-16：详情端点 SELECT * 整列搬 ContentSnapshotJson 大 LOB 致 39s）。
    /// DB 侧 OPENJSON 单次解析按块提取五主题（Lock/Supply/Procurement/SolverStrategy/CandidateGuardrail），
    /// ContentSnapshotJson 不读回应用层——消除 2.15 亿字符 LOB 搬运与 5 次全量 parse；响应形状与既有投影一致（五主题 JSON 原样）。
    /// 损坏/空快照（ISJSON=0 或 NULL）按块返回 null（与 ExtractBlockJson 容错等价，由发布前校验报 EMPTY/INVALID）。
    /// </summary>
    Task<ParameterSetVersion?> GetDetailByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// 按参数集 Id 查询全部版本详情（方案 A 落点，published-version 读路径；语义同 <see cref="GetDetailByIdAsync"/>，
    /// 返回列表供 SelectCurrentPublished（当前已发布版本解析）0/1/多三态判定）。
    /// </summary>
    Task<IReadOnlyList<ParameterSetVersion>> GetDetailByParameterSetIdAsync(long parameterSetId, CancellationToken ct = default);

    /// <summary>新增版本（新版本须为新记录，历史不可覆盖）</summary>
    Task<ParameterSetVersion> AddAsync(ParameterSetVersion version, CancellationToken ct = default);

    /// <summary>更新版本（仅限非 PUBLISHED 状态的可编辑字段）</summary>
    Task UpdateAsync(ParameterSetVersion version, CancellationToken ct = default);
}
