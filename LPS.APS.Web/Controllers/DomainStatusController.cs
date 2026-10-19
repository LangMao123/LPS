using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Interfaces;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 排程域状态查询控制器（G8：5号位业务接口接入层）
///
/// 路由规范：
///   GET /api/domain-status/{scheduleRunId}  - 查询排程域状态
///
/// 【职责边界 - 2026-09-06审核确认】
/// - 普通结果查询：4 → 5 → 2（5号位中转，2号位运行真值）
/// - 当前实现：5号位调用3号位RunLifecycleService获取状态真源
/// - 后续目标：G8数据源从5→3迁移到5→2（2号位Query Service）
/// - 5号位不自行聚合状态，不重算业务真值
/// - 状态取值：COMPLETED / CANDIDATE / RUNNING / FAILED / BLOCKED / NOT_STARTED
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/domain-status")]
public class DomainStatusController : ControllerBase
{
    private readonly IRunLifecycleService _runLifecycleService;
    private readonly ILogger<DomainStatusController> _logger;

    public DomainStatusController(
        IRunLifecycleService runLifecycleService,
        ILogger<DomainStatusController> logger)
    {
        _runLifecycleService = runLifecycleService ?? throw new ArgumentNullException(nameof(runLifecycleService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 查询排程域状态（G8）
    ///
    /// 【异常→HTTP 语义修正 2026-10-16（4号位 R4 联调回执）】
    ///   原实现 catch Exception → ApiResponse.Fail(500)。但 Fail 只改 body.code、HTTP 状态恒 200，
    ///   前端无法区分「查询失败」与「该 Run 无域状态记录」。现与 GovernanceController 同逻辑端点对齐：
    ///     · Run 不存在 / ExpectedDomainKeysJson 缺失或损坏（InvalidOperationException）→ 404
    ///     · 其他异常 → 500
    ///     · 正常空域状态（合法 ExpectedDomainKeys 但计划域当前无记录）→ 200 + []
    /// </summary>
    /// <param name="scheduleRunId">排程运行ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    [HttpGet("{scheduleRunId:int}")]
    public async Task<IActionResult> GetRunDomainStatus(
        int scheduleRunId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _runLifecycleService.GetRunDomainStatusAsync(scheduleRunId, cancellationToken);
            return Ok(ApiResponse<IReadOnlyList<RunDomainStatusDto>>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Run 域级状态查询失败：{ScheduleRunId}", scheduleRunId);
            return NotFound(ApiResponse<IReadOnlyList<RunDomainStatusDto>>.Fail(404, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run 域级状态查询异常：{ScheduleRunId}", scheduleRunId);
            return StatusCode(500, ApiResponse<IReadOnlyList<RunDomainStatusDto>>.Fail(500, $"查询失败：{ex.Message}"));
        }
    }
}
