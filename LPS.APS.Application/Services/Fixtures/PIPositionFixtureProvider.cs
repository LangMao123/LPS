using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services.Fixtures;

/// <summary>
/// PI Position Fixture提供器（临时模拟5号位输出）
///
/// INTEGRATION TODO: 替换为真实5号位接口调用
/// - 5号位负责计算PI在工厂内的物理位置
/// - 当前用Fixture模拟结果，仅用于2号位主流程开发和测试
/// - 不得将此Fixture逻辑作为生产Fallback
/// </summary>
public sealed class PIPositionFixtureProvider
{
    /// <summary>
    /// 获取PI Position结果（Fixture模拟）
    ///
    /// INTEGRATION TODO: 调用5号位真实接口
    /// - 接口路径待5号位提供
    /// - 替换前确保契约一致（ProductionInstructionPositionResult结构）
    /// </summary>
    public Task<ProductionInstructionPositionResult> GetPositionAsync(
        string productionInstructionNo,
        CancellationToken cancellationToken = default)
    {
        // INTEGRATION TODO: 实际实现应调用5号位API
        // var result = await _position5Client.GetPIPositionAsync(productionInstructionNo, cancellationToken);
        // return result;

        // Fixture: 模拟PI全部在第一个Stage
        var fixtureResult = new ProductionInstructionPositionResult
        {
            ProductionInstructionNo = productionInstructionNo,
            TotalRemainingQty = 100m,
            Positions = new[]
            {
                new PositionSlice
                {
                    PositionType = "STAGE",
                    StageCode = "MACH",
                    LocationKey = "FAC01-MACH",
                    Quantity = 100m,
                    AvailableTime = DateTime.UtcNow,
                    IsStrongEvidence = false,
                    SourceKey = productionInstructionNo,
                    IsUnlocated = false
                }
            },
            Issues = Array.Empty<PositionIssue>()
        };

        return Task.FromResult(fixtureResult);
    }

    /// <summary>
    /// 批量获取PI Position结果（Fixture模拟）
    ///
    /// INTEGRATION TODO: 调用5号位真实批量接口
    /// </summary>
    public Task<IReadOnlyList<ProductionInstructionPositionResult>> GetPositionBatchAsync(
        IEnumerable<string> productionInstructionNos,
        CancellationToken cancellationToken = default)
    {
        // INTEGRATION TODO: 实际实现应调用5号位批量API
        var results = productionInstructionNos
            .Select(piNo => new ProductionInstructionPositionResult
            {
                ProductionInstructionNo = piNo,
                TotalRemainingQty = 100m,
                Positions = new[]
                {
                    new PositionSlice
                    {
                        PositionType = "STAGE",
                        StageCode = "MACH",
                        LocationKey = "FAC01-MACH",
                        Quantity = 100m,
                        AvailableTime = DateTime.UtcNow,
                        IsStrongEvidence = false,
                        SourceKey = piNo,
                        IsUnlocated = false
                    }
                },
                Issues = Array.Empty<PositionIssue>()
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ProductionInstructionPositionResult>>(results);
    }
}
