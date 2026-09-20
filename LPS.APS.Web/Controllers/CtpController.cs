using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Shared.Models;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// CTP（承诺交期）评估接口（骨架）。
/// 3号位 职责：挂权限码 + F-G4 业务范围（Domains）二次校验；核心试算能力归 1号位/2号位，接入前返回 501。
/// </summary>
/// <remarks>开发者：3号位</remarks>
[ApiController]
[Route("api/ctp")]
public class CtpController : ControllerBase
{
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<CtpController> _logger;

    public CtpController(IDataScopeService dataScopeService, ILogger<CtpController> logger)
    {
        _dataScopeService = dataScopeService;
        _logger = logger;
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>发起承诺交期评估（CTP 试算）——骨架：仅 scope 二次校验，核心计算待 1号位/2号位 接入</summary>
    /// <remarks>开发者：3号位（编排骨架）；核心试算能力归 1号位/2号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanCtp)]
    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate([FromBody] CtpEvaluateRequest request, CancellationToken ct)
    {
        try
        {
            // F-G4 fail-closed：Domains ∋ request.DomainKey（Global 放行）
            await _dataScopeService.EnsureInScopeAsync(GetCurrentUserId(), DataScopeTypes.Domain, request.DomainKey, ct);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "CTP 评估业务范围越界：{DomainKey}", request.DomainKey);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }

        // 核心承诺交期评估计算归 1号位/2号位，接入前返回 501
        _logger.LogInformation("CTP 评估端点（骨架）被调用：{DomainKey}，核心能力待接入", request.DomainKey);
        return StatusCode(501, ApiResponse.Fail(501, "承诺交期评估（CTP 试算）核心能力由 1号位/2号位 接入，暂未开放"));
    }
}

/// <summary>CTP 评估请求体</summary>
public sealed record CtpEvaluateRequest
{
    /// <summary>目标域 Key（FAMILY_INJECTION / BJ_FAMILY_INJECTION 等）</summary>
    [Required(ErrorMessage = "DomainKey 必填")]
    public required string DomainKey { get; init; }
}