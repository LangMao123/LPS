using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Shared.Models;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 产品转换换型规则（SetupTransitionRule）端点（3号位）。
/// 权限：读端挂 <c>RuleView</c>，写端挂 <c>RuleMaintain</c>；审计身份一律后端取（禁止信任请求体）。
/// 红线：已 PUBLISHED 版本的规则禁止原地改——由版本治理层兜底，本端点经 <see cref="ISetupTransitionRuleService"/> 承载 CRUD + §十 唯一键冲突校验。
/// 运行时依赖：<see cref="ISetupTransitionRuleRepository"/>（2号位 Dapper 实现）注册后方可 resolve。
/// </summary>
/// <remarks>开发者：3号位</remarks>
[ApiController]
[Route("api/[controller]")]
public class SetupTransitionRuleController : ControllerBase
{
    private readonly ISetupTransitionRuleService _ruleService;
    private readonly ILogger<SetupTransitionRuleController> _logger;

    public SetupTransitionRuleController(
        ISetupTransitionRuleService ruleService,
        ILogger<SetupTransitionRuleController> logger)
    {
        _ruleService = ruleService;
        _logger = logger;
    }

    /// <summary>解析当前登录用户 Id（无效时 0 → 拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>解析当前登录用户工号（审计真实 Actor，P1-04 强制后端取身份）</summary>
    private string GetCurrentUserCode()
        => User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    /// <summary>按规则集版本列出换型规则</summary>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] long ruleSetVersionId, CancellationToken ct)
    {
        var rules = await _ruleService.ListAsync(ruleSetVersionId, ct);
        return Ok(ApiResponse<IReadOnlyList<SetupTransitionRule>>.Success(rules));
    }

    /// <summary>获取单条换型规则</summary>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var rule = await _ruleService.GetByIdAsync(id, ct);
        if (rule == null)
        {
            return NotFound(ApiResponse.Fail(404, $"换型规则不存在：{id}"));
        }
        return Ok(ApiResponse<SetupTransitionRule>.Success(rule));
    }

    /// <summary>新增换型规则（§十 唯一键冲突校验 + 落审计），返回新主键</summary>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SetupTransitionRule rule, CancellationToken ct)
    {
        try
        {
            var id = await _ruleService.CreateAsync(rule, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return CreatedAtAction(nameof(Get), new { id }, ApiResponse<long>.Success(id));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "换型规则新增失败：{OperationCode}", rule.OperationCode);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>更新换型规则（§十 唯一键冲突校验 + 落审计，含变更前后快照）</summary>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SetupTransitionRule rule, CancellationToken ct)
    {
        if (id != rule.Id)
        {
            return BadRequest(ApiResponse.Fail(400, $"路径 Id（{id}）与请求体 Id（{rule.Id}）不一致。"));
        }
        try
        {
            await _ruleService.UpdateAsync(rule, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse<SetupTransitionRule>.Success(rule));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "换型规则更新失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>删除换型规则（落删除前快照审计）</summary>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        try
        {
            await _ruleService.DeleteAsync(id, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse.Ok("删除成功"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "换型规则删除失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }
}