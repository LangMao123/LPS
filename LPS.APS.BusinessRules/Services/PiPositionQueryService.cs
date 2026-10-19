using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// PI Position查询业务服务
///
/// 5号位提供给4号位的PI Position查询
/// 直接读取ProductionInstructionPositionSnapshot表，不重算Position
/// </summary>
public class PiPositionQueryService
{
    private readonly IPiPositionQueryRepository _repository;

    public PiPositionQueryService(IPiPositionQueryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 查询PI Position列表
    /// </summary>
    public async Task<List<PiPositionDto>> QueryAsync(
        int planVersionId,
        string? productionInstructionNo = null,
        string? materialCode = null,
        string? positionType = null,
        string? stageCode = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        // take 越界不再静默回落 100（4号位 10-17 R3：传 1000 实返 100，调用方误以为"就这么多"）。
        // 语义：非正数 → 取默认 100；超过上限 → 夹到上限 500（可预测，不返回与请求无关的第三个数）。
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        return await _repository.QueryAsync(
            planVersionId, productionInstructionNo, materialCode,
            positionType, stageCode, skip, take, ct, allowedFactories);
    }

    /// <summary>
    /// 查询PI Position汇总
    /// </summary>
    public async Task<PiPositionSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");

        return await _repository.GetSummaryAsync(planVersionId, ct);
    }

    /// <summary>
    /// 根据生产指令号查询该 PI 的所有 Position（单 PI 详情）
    /// </summary>
    public async Task<List<PiPositionDto>> GetByProductionInstructionAsync(
        int planVersionId,
        string productionInstructionNo,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (string.IsNullOrWhiteSpace(productionInstructionNo))
            throw new ArgumentException("productionInstructionNo is required");

        return await _repository.GetByProductionInstructionAsync(planVersionId, productionInstructionNo, ct);
    }
}
