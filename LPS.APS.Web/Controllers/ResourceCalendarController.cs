using System.Security.Claims;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 设备资源日历维护控制器（5号位提供，供4号位页面）
///
/// 路由规范：
///   POST   /api/resource-calendar/slots          - 批量铺窗（覆盖式：先删同区间旧窗再生成；Days=1 覆盖逐条）
///   GET    /api/resource-calendar/{resourceId}    - 查询某设备所有窗口
///   DELETE /api/resource-calendar/slots/{id}      - 物理删除某窗口
///
/// 【权限码】3号位已落：ResourceCalendarView / Edit / Delete（43 码）
/// 【S5-01 整改（2026-10-14）】功能权限之外，Service 层按目标 Resource 关联 Factory/Department
/// 做业务范围二次校验（IDataScopeService，fail-closed）；越界抛 ScopeViolationException → 本控制器映射 403。
/// 【职责边界】5号位提供维护接口；不改 Solver / 不触 Pegging
/// </summary>
[Authorize(Policy = PermissionCodes.ResourceCalendarView)]
[ApiController]
[Route("api/resource-calendar")]
public class ResourceCalendarController : ControllerBase
{
    private readonly IResourceCalendarService _service;
    private readonly ILogger<ResourceCalendarController> _logger;

    public ResourceCalendarController(IResourceCalendarService service, ILogger<ResourceCalendarController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认，照 MesDispatchController 先例）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>批量铺窗（覆盖式：一次生成连续多天窗口）</summary>
    [HttpPost("slots")]
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    public async Task<ApiResponse<int>> BulkCreate(ResourceCalendarBulkRequest request, CancellationToken ct = default)
    {
        try
        {
            var inserted = await _service.BulkCreateAsync(request, GetCurrentUserId(), ct);
            return ApiResponse<int>.Success(inserted);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ResourceCalendar BulkCreate 业务范围越界：ResourceId={ResourceId}", request?.ResourceId);
            return ApiResponse<int>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResourceCalendar BulkCreate failed");
            return ApiResponse<int>.Fail(400, ex.Message);
        }
    }

    /// <summary>
    /// 单条窗口追加（2026-10-14 放开"一天多条"方向2）：只插不删；同日重叠 409；相切合法；不跨天。
    /// 与批量端点分工：批量=整段重铺（覆盖式），本端点=按条增补。
    /// </summary>
    [HttpPost("slot")]
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    public async Task<ApiResponse<long>> AddSingle(ResourceCalendarSingleRequest request, CancellationToken ct = default)
    {
        try
        {
            var id = await _service.AddSingleAsync(request, GetCurrentUserId(), ct);
            return ApiResponse<long>.Success(id);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ResourceCalendar AddSingle 业务范围越界：ResourceId={ResourceId}", request?.ResourceId);
            return ApiResponse<long>.Fail(403, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ApiResponse<long>.Fail(409, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return ApiResponse<long>.Fail(404, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResourceCalendar AddSingle failed");
            return ApiResponse<long>.Fail(400, ex.Message);
        }
    }

    /// <summary>
    /// 三级合并解析预览（4号位 10-14 函 R1）：某批设备在指定区间内三级继承后的最终可排窗口与来源层级。
    /// 只读、不写库；业务范围 fail-closed（越界设备剔除）。
    /// </summary>
    [HttpGet("resolve-preview")]
    public async Task<ApiResponse<List<ResourceCalendarResolvePreviewDto>>> ResolvePreview(
        [FromQuery] string resourceIds,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken ct = default)
    {
        try
        {
            var ids = (resourceIds ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), out var v) ? v : 0)
                .Where(v => v > 0).ToList();
            if (ids.Count == 0)
                return ApiResponse<List<ResourceCalendarResolvePreviewDto>>.Fail(400, "resourceIds 不能为空（逗号分隔）");

            var rows = await _service.ResolvePreviewAsync(ids, startDate, endDate, GetCurrentUserId(), ct);
            return ApiResponse<List<ResourceCalendarResolvePreviewDto>>.Success(rows);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<ResourceCalendarResolvePreviewDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResourceCalendar ResolvePreview failed");
            return ApiResponse<List<ResourceCalendarResolvePreviewDto>>.Fail(400, ex.Message);
        }
    }

    /// <summary>查询某设备窗口（分页 PageResult；可按时间范围过滤；目标设备须在业务范围内）</summary>
    [HttpGet("{resourceId}")]
    public async Task<ApiResponse<PageResult<ResourceCalendarEntryDto>>> Get(
        [FromRoute] int resourceId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            if (pageIndex <= 0) pageIndex = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 20;

            var rows = await _service.GetByResourceAsync(resourceId, from, to, pageIndex, pageSize, GetCurrentUserId(), ct);
            return ApiResponse<PageResult<ResourceCalendarEntryDto>>.Success(rows);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ResourceCalendar Get 业务范围越界：ResourceId={ResourceId}", resourceId);
            return ApiResponse<PageResult<ResourceCalendarEntryDto>>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResourceCalendar Get failed");
            return ApiResponse<PageResult<ResourceCalendarEntryDto>>.Fail(400, ex.Message);
        }
    }

    /// <summary>物理删除某窗口（窗口所属设备须在业务范围内）</summary>
    [HttpDelete("slots/{id}")]
    [Authorize(Policy = PermissionCodes.ResourceCalendarDelete)]
    public async Task<ApiResponse<bool>> Delete(
        [FromRoute] long id,
        CancellationToken ct = default)
    {
        try
        {
            var ok = await _service.DeleteAsync(id, GetCurrentUserId(), ct);
            return ApiResponse<bool>.Success(ok);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ResourceCalendar Delete 业务范围越界：Id={Id}", id);
            return ApiResponse<bool>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResourceCalendar Delete failed");
            return ApiResponse<bool>.Fail(400, ex.Message);
        }
    }
}