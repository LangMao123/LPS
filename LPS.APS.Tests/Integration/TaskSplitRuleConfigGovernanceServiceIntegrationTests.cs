using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// TaskSplitRuleConfig 治理服务集成测试（4号位 P0：Dapper nullable 字段 DBNull → 500，2026-10-08）。
/// 复现 4号位 函件触发 A/B：任意 nullable 字段为 null（含 productionDepartmentId=null）时 POST/PUT
/// 不再抛 NotSupportedException——修复前 DynamicParameters.Add(name, (object?)X ?? DBNull.Value) 无 DbType，
/// Dapper 编译阶段抛「The member X of type System.DBNull cannot be used as a parameter value」。
/// 直连真实 APS_Production 库（与 4号位 dev 库 curl 同路径）；测试物料 = 运行时挑「尚无规则的既有 Material」
/// （满足 FK→Material.Id，且保证首建不触发去重）；Dispose 仅清理自建数据。
/// 开发者：3号位
/// </summary>
[Collection("TaskSplitRuleConfigGovernance")]
public class TaskSplitRuleConfigGovernanceServiceIntegrationTests : IDisposable
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly List<AuditLog> _auditLogs = new();
    private readonly TaskSplitRuleConfigGovernanceService _service;
    private int _testMaterialId;

    public TaskSplitRuleConfigGovernanceServiceIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var dbOptions = configuration.GetSection("Database").Get<LPS.APS.Engine.Configuration.DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration not found in appsettings.Test.json");

        _connectionManager = new DatabaseConnectionManager(Options.Create(dbOptions));

        var auditRepo = new Mock<IAuditLogRepository>();
        auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditLog entity, CancellationToken _) => { _auditLogs.Add(entity); return entity; });

        _service = new TaskSplitRuleConfigGovernanceService(_connectionManager, auditRepo.Object);
    }

    /// <summary>挑一个「尚无 TaskSplitRuleConfig 规则」的既有 Material（FK→Material.Id 必须存在；且无既有规则保证首建不触发去重）。</summary>
    private async Task<int> PickFreeMaterialIdAsync()
    {
        const string sql = @"
SELECT TOP 1 m.Id
FROM Material m
LEFT JOIN TaskSplitRuleConfig t ON t.MaterialId = m.Id
WHERE t.Id IS NULL
ORDER BY m.Id";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<int>(sql, null, db: DatabaseId.APS);
        if (id <= 0)
        {
            throw new InvalidOperationException("APS_Production.Material 无可用（无既有 TaskSplitRuleConfig 规则的）物料，集成测试无法运行。");
        }
        return id;
    }

    [Fact]
    public async Task CreateAsync_全部nullable字段null_不抛NotSupported_成功入库()
    {
        // 4号位 函件 §二 触发 A+B 合并：productionDepartmentId=null 且 6 个 optional 字段全 null
        _testMaterialId = await PickFreeMaterialIdAsync();

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = null,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = null,
            PreferredBatchQty = null,
            AllowSplit = true,
            AllowMerge = true,
            MaxOptimizationSplitCount = null,
            MaxBatchCandidates = null,
            BottleneckSplitStrategy = null,
            NonBottleneckStrategy = null,
            EffectiveFrom = null,
            EffectiveTo = null,
        };

        var dto = await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        dto.Id.Should().BeGreaterThan(0);
        dto.ProductionDepartmentId.Should().BeNull();
        dto.MaxOptimizationSplitCount.Should().BeNull();
        _auditLogs.Should().ContainSingle(x => x.ActionCode == "Create");
    }

    [Fact]
    public async Task CreateAsync_同Material同NullDept重复_命中去重IS_NULL路径_抛去重异常()
    {
        _testMaterialId = await PickFreeMaterialIdAsync();

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = null,
            AllowSplit = true,
            AllowMerge = true,
        };

        await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        // 第二次同 (Material, NULL Dept) → EnsureNotDuplicateAsync 的 (@Dept IS NULL AND ProductionDepartmentId IS NULL) 分支须拦截（NULL==NULL 语义）
        var act = async () => await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);
        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task UpdateAsync_部分nullable字段null_成功更新()
    {
        _testMaterialId = await PickFreeMaterialIdAsync();

        var created = await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = null,
                MinExecutionBatchQty = 1,
                MaxExecutionBatchQty = 2000,
                AllowSplit = true,
                AllowMerge = true,
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        var update = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = null,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = null,
            AllowSplit = false,
            AllowMerge = false,
            BottleneckSplitStrategy = null,
            EffectiveFrom = null,
            EffectiveTo = null,
        };

        var dto = await _service.UpdateAsync(created.Id, update, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        dto.AllowSplit.Should().BeFalse();
        dto.ProductionDepartmentId.Should().BeNull();
        _auditLogs.Should().ContainSingle(x => x.ActionCode == "Update");
    }

    public void Dispose()
    {
        // 清理本测试自建数据（仅按测试期选定的 MaterialId，绝不误删 4号位 curl / 真实数据）
        if (_testMaterialId > 0)
        {
            _connectionManager.ExecuteAsync(
                "DELETE FROM TaskSplitRuleConfig WHERE MaterialId = @MaterialId",
                new { MaterialId = _testMaterialId },
                db: DatabaseId.APS).GetAwaiter().GetResult();
        }
        _connectionManager.Dispose();
    }
}
