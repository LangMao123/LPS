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
/// 人工能力槽维护控制器（5号位提供，供4号位页面）
///
/// 路由规范：
///   POST   /api/manual-capacity/slots                     - 新增人工槽主档
///   GET    /api/manual-capacity/slots                     - 查询人工槽主档（分页；可按部门/工序过滤）
///   DELETE /api/manual-capacity/slots/{manualSlotId}      - 软删人工槽主档（IsActive=0）
///   POST   /api/manual-capacity/calendar                  - 批量铺人工槽窗口（覆盖式；Days=1 覆盖逐条）
///   GET    /api/manual-capacity/calendar/{manualSlotId}   - 查询某人工槽所有窗口
///   DELETE /api/manual-capacity/calendar/{id}             - 物理删除某窗口
///
/// 【权限码】3号位已落：ManualCapacityView / Edit / Delete（43 码）
/// 【S5-01 整改（2026-10-14）】功能权限之外，Service 层按目标槽所属 ProductionDepartment.DeptCode
/// 做业务范围二次校验（IDataScopeService，fail-closed，照 Routing/Setup 治理先例）；
/// 越界抛 ScopeViolationException → 本控制器映射 403。列表查询按授权部门过滤。
/// 【职责边界】主档软删 IsActive=0；窗口物理删 DELETE；5号位维护，不重算业务
/// </summary>
[Authorize(Policy = PermissionCodes.ManualCapacityView)]
[ApiController]
[Route("api/manual-capacity")]
public class ManualCapacityController : ControllerBase
{
    private readonly IManualCapacityService _service;
    private readonly ILogger<ManualCapacityController> _logger;

    public ManualCapacityController(IManualCapacityService service, ILogger<ManualCapacityController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认，照 MesDispatchController 先例）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>新增人工槽主档（目标部门须在业务范围内）</summary>
    [HttpPost("slots")]
    [Authorize(Policy = PermissionCodes.ManualCapacityEdit)]
    public async Task<ApiResponse<ManualCapacitySlotDto>> CreateSlot(ManualCapacitySlotDto slot, CancellationToken ct = default)
    {
        try
        {
            var created = await _service.CreateSlotAsync(slot, GetCurrentUserId(), ct);
            return ApiResponse<ManualCapacitySlotDto>.Success(created);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity CreateSlot 业务范围越界：DepartmentId={DepartmentId}", slot?.ProductionDepartmentId);
            return ApiResponse<ManualCapacitySlotDto>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity CreateSlot failed");
            return ApiResponse<ManualCapacitySlotDto>.Fail(400, ex.Message);
        }
    }

    /// <summary>查询人工槽主档（软删不含在默认查询；可按部门/工序过滤；分页 PageResult，4号位 10-12 函；结果按授权部门过滤）</summary>
    [HttpGet("slots")]
    public async Task<ApiResponse<PageResult<ManualCapacitySlotDto>>> GetSlots(
        [FromQuery] int? departmentId = null,
        [FromQuery] string? operationName = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            if (pageIndex <= 0) pageIndex = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 20;

            var rows = await _service.GetSlotsAsync(
                departmentId, operationName, includeInactive, pageIndex, pageSize, GetCurrentUserId(), ct);
            return ApiResponse<PageResult<ManualCapacitySlotDto>>.Success(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity GetSlots failed");
            return ApiResponse<PageResult<ManualCapacitySlotDto>>.Fail(400, ex.Message);
        }
    }

    /// <summary>软删人工槽主档（IsActive=0；目标槽所属部门须在业务范围内）</summary>
    [HttpDelete("slots/{manualSlotId}")]
    [Authorize(Policy = PermissionCodes.ManualCapacityDelete)]
    public async Task<ApiResponse<bool>> SoftDeleteSlot([FromRoute] int manualSlotId, CancellationToken ct = default)
    {
        try
        {
            var ok = await _service.SoftDeleteSlotAsync(manualSlotId, GetCurrentUserId(), ct);
            return ApiResponse<bool>.Success(ok);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity SoftDeleteSlot 业务范围越界：ManualSlotId={ManualSlotId}", manualSlotId);
            return ApiResponse<bool>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity SoftDeleteSlot failed");
            return ApiResponse<bool>.Fail(400, ex.Message);
        }
    }

    /// <summary>批量铺人工槽窗口（覆盖式；目标槽所属部门须在业务范围内）</summary>
    [HttpPost("calendar")]
    [Authorize(Policy = PermissionCodes.ManualCapacityEdit)]
    public async Task<ApiResponse<int>> BulkCreateCalendar(ManualSlotCalendarBulkRequest request, CancellationToken ct = default)
    {
        try
        {
            var inserted = await _service.BulkCreateCalendarAsync(request, GetCurrentUserId(), ct);
            return ApiResponse<int>.Success(inserted);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity BulkCreateCalendar 业务范围越界：ManualSlotId={ManualSlotId}", request?.ManualSlotId);
            return ApiResponse<int>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity BulkCreateCalendar failed");
            return ApiResponse<int>.Fail(400, ex.Message);
        }
    }

    /// <summary>
    /// 单条窗口追加（2026-10-14 放开"一天多条"方向2，与设备日历同口径）：
    /// 只插不删；同日重叠 409；相切合法；不跨天。批量端点覆盖语义不变。
    /// </summary>
    [HttpPost("calendar/single")]
    [Authorize(Policy = PermissionCodes.ManualCapacityEdit)]
    public async Task<ApiResponse<long>> AddCalendarSingle(ManualSlotCalendarSingleRequest request, CancellationToken ct = default)
    {
        try
        {
            var id = await _service.AddCalendarSingleAsync(request, GetCurrentUserId(), ct);
            return ApiResponse<long>.Success(id);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity AddCalendarSingle 业务范围越界：ManualSlotId={ManualSlotId}", request?.ManualSlotId);
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
            _logger.LogError(ex, "ManualCapacity AddCalendarSingle failed");
            return ApiResponse<long>.Fail(400, ex.Message);
        }
    }

    /// <summary>查询某人工槽窗口（分页 PageResult；可按时间范围过滤；目标槽所属部门须在业务范围内）</summary>
    [HttpGet("calendar/{manualSlotId}")]
    public async Task<ApiResponse<PageResult<ManualSlotCalendarDto>>> GetCalendar(
        [FromRoute] int manualSlotId,
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

            var rows = await _service.GetCalendarBySlotAsync(manualSlotId, from, to, pageIndex, pageSize, GetCurrentUserId(), ct);
            return ApiResponse<PageResult<ManualSlotCalendarDto>>.Success(rows);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity GetCalendar 业务范围越界：ManualSlotId={ManualSlotId}", manualSlotId);
            return ApiResponse<PageResult<ManualSlotCalendarDto>>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity GetCalendar failed");
            return ApiResponse<PageResult<ManualSlotCalendarDto>>.Fail(400, ex.Message);
        }
    }

    /// <summary>物理删除某人工槽窗口（窗口所属槽的部门须在业务范围内）</summary>
    [HttpDelete("calendar/{id}")]
    [Authorize(Policy = PermissionCodes.ManualCapacityDelete)]
    public async Task<ApiResponse<bool>> DeleteCalendar([FromRoute] long id, CancellationToken ct = default)
    {
        try
        {
            var ok = await _service.DeleteCalendarAsync(id, GetCurrentUserId(), ct);
            return ApiResponse<bool>.Success(ok);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ManualCapacity DeleteCalendar 业务范围越界：Id={Id}", id);
            return ApiResponse<bool>.Fail(403, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManualCapacity DeleteCalendar failed");
            return ApiResponse<bool>.Fail(400, ex.Message);
        }
    }
}