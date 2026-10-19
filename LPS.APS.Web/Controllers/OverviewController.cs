using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Application.Services.Query;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// Overview概览页控制器（5号位提供给4号位）
///
/// 路由规范：
///   GET /api/overview/active-plan           - 当前ACTIVE计划版本信息（按 Domain 列表）
///   GET /api/overview/resource-bottleneck   - 资源负荷/Bottleneck
///   GET /api/overview/candidate-summary     - Candidate待处理摘要
///
/// 【2026-10-16 下线】GET /api/overview/task-summary 已移除 ——
///   订单维度 KPI 由 4号位 改走 order-query/summary（订单粒度，口径正确）；
///   本端点为任务粒度，且其 6 字段中 4 个恒 0/不可判（详因见 5号位致 4号位 函 §三）：
///     · EstimatedOnlyCount / UnscheduledCount —— 查 Task.Status 的 'ESTIMATED'/'PENDING'/'QUEUED'
///       三个字面量均不在冻结正式值域（PLANNED/RELEASED/IN_PROGRESS/COMPLETED/CANCELLED）内 → 恒 0
///     · OnTimeCount / DelayedCount —— "准时"需实际完成时间与计划比，而 Task 表无 ActualStartTime/ActualEndTime
///   （另 RiskCount 口径系 5号位 自造、无冻结依据；OtherCount 字面量列表错。）
///   全仓已核对无消费方（前端类型/测试/其他端点均无引用）。
///
/// 【职责边界】
/// - 5号位提供只读查询接口，直接读取APS事实表
/// - 不重算业务结果
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/overview")]
public class OverviewController : ControllerBase
{
    private readonly IOverviewQueryService _service;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<OverviewController> _logger;

    public OverviewController(
        IOverviewQueryService service,
        IDataScopeService dataScopeService,
        ILogger<OverviewController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析当前登录用户 Id（无效返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>
    /// 查询当前ACTIVE计划版本信息（R1-a：按 Domain 列表，每 Domain 至多一条）
    /// 不传 domainKey → 返回授权范围内全部生效 Domain；传 domainKey → 该 Domain 的单元素列表（越界 403）
    /// </summary>
    [HttpGet("active-plan")]
    public async Task<ApiResponse<List<OverviewActivePlanDto>>> GetActivePlan(
        [FromQuery] string? domainKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);

            // 校验：非空入参必须落在授权范围（Global 全放行，无范围全拒绝）
            if (!string.IsNullOrEmpty(domainKey) && !scope.Allows(DataScopeTypes.Domain, domainKey))
                return ApiResponse<List<OverviewActivePlanDto>>.Fail(403, "Domain 范围越界");

            // 过滤：空参按授权范围收窄结果集，避免越权返回全域数据
            var allowedDomains = scope.GetValues(DataScopeTypes.Domain);

            var result = await _service.GetActivePlanAsync(domainKey, cancellationToken, allowedDomains);
            return ApiResponse<List<OverviewActivePlanDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active plan");
            return ApiResponse<List<OverviewActivePlanDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询资源负荷/Bottleneck
    /// </summary>
    [HttpGet("resource-bottleneck")]
    public async Task<ApiResponse<List<OverviewResourceBottleneckDto>>> GetResourceBottleneck(
        [FromQuery] int planVersionId,
        [FromQuery] int topN = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (planVersionId <= 0)
                return ApiResponse<List<OverviewResourceBottleneckDto>>.Fail(400, "planVersionId is required");

            var result = await _service.GetResourceBottleneckAsync(planVersionId, topN, cancellationToken);
            return ApiResponse<List<OverviewResourceBottleneckDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get resource bottleneck");
            return ApiResponse<List<OverviewResourceBottleneckDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询Candidate待处理摘要
    /// </summary>
    [HttpGet("candidate-summary")]
    public async Task<ApiResponse<OverviewCandidateSummaryDto>> GetCandidateSummary(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetCandidateSummaryAsync(cancellationToken);
            return ApiResponse<OverviewCandidateSummaryDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get candidate summary");
            return ApiResponse<OverviewCandidateSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
