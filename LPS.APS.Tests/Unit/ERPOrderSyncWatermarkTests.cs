using FluentAssertions;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// ERP 订单增量同步 —— 水位线推进边界（0号位 第二十二轮 J22-02 负例）。
///
/// 不变式：`APS_SyncWatermark.LastSyncTime` 只在**整条增量链全部成功**后推进。
/// 本条负例固定的是「提升已成功、新 BOM 号探测抛异常」这一条缝：
/// 旧写法在该路径上仍会推进水位线，而日志宣称「水位线保持、下次重试」——
/// 该批订单从此不会被再拉取，日志与重试边界互相矛盾。
///
/// 全程不触库：五个 IO 边沿在测试子类里覆写，连接管理器只被构造、不被使用。
/// </summary>
public class ERPOrderSyncWatermarkTests
{
    /// <summary>
    /// 覆写全部 IO 边沿的同步服务替身。
    /// </summary>
    private sealed class StubSyncService : ERPOrderSyncService
    {
        public static readonly DateTime LastWatermark = new(2026, 10, 17, 8, 0, 0);
        public static readonly DateTime OrderUpdatedAt = new(2026, 10, 17, 9, 30, 0);

        public bool BomProbeThrows { get; init; }
        public bool PromotionThrows { get; init; }
        public bool NoIncrementalOrders { get; init; }

        public int BulkWriteCalls { get; private set; }
        public int WatermarkUpdateCalls { get; private set; }
        public DateTime? WatermarkAdvancedTo { get; private set; }

        public StubSyncService(
            DatabaseConnectionManager connectionManager,
            IOrderPromotionService promotionService,
            IOrderLoadingService orderLoadingService)
            : base(connectionManager, promotionService, orderLoadingService,
                   NullLogger<ERPOrderSyncService>.Instance)
        {
        }

        protected override Task<DateTime> GetLastSyncWatermarkAsync()
            => Task.FromResult(LastWatermark);

        protected override Task<List<ERPOrderDto>> QueryOrdersFromODSAsync(
            string whereClause, object? parameters, int commandTimeout = 300)
        {
            List<ERPOrderDto> orders = NoIncrementalOrders
                ? new List<ERPOrderDto>()
                : new List<ERPOrderDto>
                {
                    new() { OrderNo = "T22-02-1", UpdatedAt = OrderUpdatedAt }
                };
            return Task.FromResult(orders);
        }

        protected override Task BulkWriteToStagingAsync(List<ERPOrderDto> orders)
        {
            BulkWriteCalls++;
            return Task.CompletedTask;
        }

        protected override Task<List<string>> DetectNewBOMNOsAsync()
            => BomProbeThrows
                ? throw new InvalidOperationException("模拟：提升已成功，新BOM号探测失败")
                : Task.FromResult(new List<string>());

        protected override Task UpdateSyncWatermarkAsync(DateTime syncTime)
        {
            WatermarkUpdateCalls++;
            WatermarkAdvancedTo = syncTime;
            return Task.CompletedTask;
        }
    }

    private static (StubSyncService Service, Mock<IOrderPromotionService> Promotion) Build(
        bool bomProbeThrows = false, bool promotionThrows = false, bool noIncrementalOrders = false)
    {
        var options = Options.Create(new DatabaseOptions
        {
            // 构造需要 IsValid() 通过；本用例不打开任何连接
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=APS" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=ODS" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=Auth" }
        });

        var connectionManager = new DatabaseConnectionManager(options);

        var promotion = new Mock<IOrderPromotionService>();
        if (promotionThrows)
        {
            promotion
                .Setup(p => p.ValidateAndPromoteAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("模拟：提升整体失败"));
        }
        else
        {
            promotion
                .Setup(p => p.ValidateAndPromoteAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((1, 0));
        }

        var loading = new Mock<IOrderLoadingService>();
        loading
            .Setup(l => l.LoadOrdersToActivePlanVersionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var service = new StubSyncService(connectionManager, promotion.Object, loading.Object)
        {
            BomProbeThrows = bomProbeThrows,
            PromotionThrows = promotionThrows,
            NoIncrementalOrders = noIncrementalOrders
        };

        return (service, promotion);
    }

    [Fact]
    public async Task 提升成功但新BOM号探测抛异常_水位线不得推进()
    {
        var (service, _) = Build(bomProbeThrows: true);

        // 内层 catch 吞掉异常：不向外抛，本批订单留给下轮重拉
        await service.Invoking(s => s.IncrementalSyncAsync())
            .Should().NotThrowAsync();

        service.BulkWriteCalls.Should().Be(1, "提升前的批量写入已发生，事实已落 Staging");
        service.WatermarkUpdateCalls.Should().Be(
            0, "J22-02：探测失败属整链失败，水位线必须保持原值，否则本批订单永不再被拉取");
    }

    [Fact]
    public async Task 提升整体失败_水位线不得推进()
    {
        var (service, _) = Build(promotionThrows: true);

        await service.Invoking(s => s.IncrementalSyncAsync())
            .Should().NotThrowAsync();

        service.WatermarkUpdateCalls.Should().Be(0);
    }

    [Fact]
    public async Task 整链成功_水位线推进到本批最大UpdatedAt()
    {
        var (service, _) = Build();

        await service.Invoking(s => s.IncrementalSyncAsync())
            .Should().NotThrowAsync();

        service.WatermarkUpdateCalls.Should().Be(1);
        service.WatermarkAdvancedTo.Should().Be(StubSyncService.OrderUpdatedAt);
    }

    [Fact]
    public async Task 无增量订单_水位线推进到当前时刻且不写Staging()
    {
        var (service, _) = Build(noIncrementalOrders: true);

        await service.Invoking(s => s.IncrementalSyncAsync())
            .Should().NotThrowAsync();

        service.BulkWriteCalls.Should().Be(0);
        service.WatermarkUpdateCalls.Should().Be(1);
        service.WatermarkAdvancedTo.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
    }
}
