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
/// 工厂/部门上级工作日历维护控制器（5号位=业务 Owner；表模型/三级解析=2号位）
///
/// 路由规范：
///   GET  /api/production-work-calendar            - 分页查询（PageResult，字段 page；结果按授权 Factory/Department 过滤）
///   POST /api/production-work-calendar/weekly     - 周基准铺设（同范围同星期闭区间换版）
///   POST /api/production-work-calendar/exception  - 日期例外铺设（节假日OFF/补班WORK/授权加班）
///   POST /api/production-work-calendar/close      - 闭区间换版（旧开放行闭到新版前一日）
///   POST /api/production-work-calendar/rows/{id}/close?effectiveTo= - 单行闭区间（撤销已生效例外，保留审计）
///   DELETE /api/production-work-calendar/rows/{id} - 物理删除（仅限未生效行，纠错通道）
///
/// 【治理】条款=Resource Calendar 专项 v1.4 / DDL v5.1.8.5（G1 候选）；
/// G4 公告前用于联调与备料，真实班次数据写入与解析消费等公告生效。
/// 【权限】暂复用 aps.resource_calendar.*（5号位 日历域既有 3 码）；若 0号位/3号位 要求
/// 独立 aps.production_work_calendar.* 权限码，G4 前另行申请，届时仅换 Policy 常量。
/// 【S5-01 整改（2026-10-14）】功能权限之外，Service 层按目标行 Factory.Code（DEPARTMENT 行另加
/// ProductionDepartment.DeptCode）做业务范围二次校验（IDataScopeService，fail-closed，不同维度 AND，
/// 照 StageLeadTimeParam P0-03 先例）；越界抛 ScopeViolationException → 本控制器映射 403。
/// 【DDL 尾注四项发布强校验】①同级时间重叠/星期日期冲突 ②部门归属Factory匹配
/// ③操作权限（本控制器 Policy + Service 业务范围校验）④授权加班证据完整性 —— ①②③④ 在 Service 发布路径强校验。
/// </summary>
[ApiController]
[Route("api/production-work-calendar")]
public class ProductionWorkCalendarController : ControllerBase
{
    private readonly IProductionWorkCalendarService _service;
    private readonly ILogger<ProductionWorkCalendarController> _logger;

    public ProductionWorkCalendarController(
        IProductionWorkCalendarService service,
        ILogger<ProductionWorkCalendarController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string GetOperator()
        => User.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认，照 MesDispatchController 先例）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>分页查询上级工作日历（MISSING=无行，不造虚拟行；结果按授权 Factory/Department 过滤）</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarView)]
    [HttpGet]
    public async Task<ApiResponse<PageResult<ProductionWorkCalendarDto>>> Query(
        [FromQuery] string? scopeType = null,
        [FromQuery] int? factoryId = null,
        [FromQuery] int? departmentId = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.QueryAsync(scopeType, factoryId, departmentId, includeInactive, pageIndex, pageSize, sortBy, sortDir, GetCurrentUserId(), ct);
            return ApiResponse<PageResult<ProductionWorkCalendarDto>>.Success(result);
        }
        catch (ArgumentException ex) { return ApiResponse<PageResult<ProductionWorkCalendarDto>>.Fail(400, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar query failed");
            return ApiResponse<PageResult<ProductionWorkCalendarDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>周基准铺设（1..7 星期几一批；同星期可多窗口并存=白班/小夜/大夜多班制，窗口重叠拒绝；
    /// 同范围同星期整组替换：未生效旧行覆盖、已生效闭区间换版）</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    [HttpPost("weekly")]
    public async Task<ApiResponse<int>> UpsertWeekly([FromBody] WeeklyCalendarUpsertRequest request, CancellationToken ct = default)
    {
        try
        {
            var n = await _service.UpsertWeeklyAsync(request, GetCurrentUserId(), GetOperator(), ct);
            return ApiResponse<int>.Success(n, $"Weekly rows upserted: {n}");
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ProductionWorkCalendar weekly 业务范围越界：FactoryId={FactoryId}, DepartmentId={DepartmentId}", request?.FactoryId, request?.ProductionDepartmentId);
            return ApiResponse<int>.Fail(403, ex.Message);
        }
        catch (ArgumentException ex) { return ApiResponse<int>.Fail(400, ex.Message); }
        catch (KeyNotFoundException ex) { return ApiResponse<int>.Fail(404, ex.Message); }
        catch (InvalidOperationException ex) { return ApiResponse<int>.Fail(409, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar weekly upsert failed");
            return ApiResponse<int>.Fail(500, $"Upsert failed: {ex.Message}");
        }
    }

    /// <summary>日期例外铺设（节假日 OFF / 补班 WORK / 授权加班；同级同日重叠拒绝）</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    [HttpPost("exception")]
    public async Task<ApiResponse<long>> CreateException([FromBody] WorkCalendarExceptionRequest request, CancellationToken ct = default)
    {
        try
        {
            var id = await _service.CreateExceptionAsync(request, GetCurrentUserId(), GetOperator(), ct);
            return ApiResponse<long>.Success(id, $"Exception row created: {id}");
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ProductionWorkCalendar exception 业务范围越界：FactoryId={FactoryId}, DepartmentId={DepartmentId}", request?.FactoryId, request?.ProductionDepartmentId);
            return ApiResponse<long>.Fail(403, ex.Message);
        }
        catch (ArgumentException ex) { return ApiResponse<long>.Fail(400, ex.Message); }
        catch (KeyNotFoundException ex) { return ApiResponse<long>.Fail(404, ex.Message); }
        catch (InvalidOperationException ex) { return ApiResponse<long>.Fail(409, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar exception create failed");
            return ApiResponse<long>.Fail(500, $"Create failed: {ex.Message}");
        }
    }

    /// <summary>闭区间换版（把该范围全部未闭行闭到 NewEffectiveFrom-1，供新版班制接力）</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    [HttpPost("close")]
    public async Task<ApiResponse<int>> CloseOpen([FromBody] CalendarCloseOpenRequest request, CancellationToken ct = default)
    {
        try
        {
            var n = await _service.CloseOpenAsync(request, GetCurrentUserId(), GetOperator(), ct);
            return ApiResponse<int>.Success(n, $"Open rows closed: {n}");
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ProductionWorkCalendar close 业务范围越界：FactoryId={FactoryId}, DepartmentId={DepartmentId}", request?.FactoryId, request?.ProductionDepartmentId);
            return ApiResponse<int>.Fail(403, ex.Message);
        }
        catch (ArgumentException ex) { return ApiResponse<int>.Fail(400, ex.Message); }
        catch (KeyNotFoundException ex) { return ApiResponse<int>.Fail(404, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar close failed");
            return ApiResponse<int>.Fail(500, $"Close failed: {ex.Message}");
        }
    }

    /// <summary>单行闭区间（撤销已生效例外行：历史保留、审计完整；不可重复闭）</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarEdit)]
    [HttpPost("rows/{id:long}/close")]
    public async Task<ApiResponse<bool>> CloseRow([FromRoute] long id, [FromQuery] DateTime effectiveTo, CancellationToken ct = default)
    {
        try
        {
            await _service.CloseRowAsync(id, effectiveTo, GetCurrentUserId(), GetOperator(), ct);
            return ApiResponse<bool>.Success(true, $"Row {id} closed at {effectiveTo:yyyy-MM-dd}");
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ProductionWorkCalendar close row 业务范围越界：Id={Id}", id);
            return ApiResponse<bool>.Fail(403, ex.Message);
        }
        catch (ArgumentException ex) { return ApiResponse<bool>.Fail(400, ex.Message); }
        catch (KeyNotFoundException ex) { return ApiResponse<bool>.Fail(404, ex.Message); }
        catch (InvalidOperationException ex) { return ApiResponse<bool>.Fail(409, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar close row failed");
            return ApiResponse<bool>.Fail(500, $"Close row failed: {ex.Message}");
        }
    }

    /// <summary>物理删除——仅限未生效行（EffectiveFrom&gt;今天，纯纠错）；已生效行 409 提示走 close</summary>
    [Authorize(Policy = PermissionCodes.ResourceCalendarDelete)]
    [HttpDelete("rows/{id:long}")]
    public async Task<ApiResponse<bool>> DeleteRow([FromRoute] long id, CancellationToken ct = default)
    {
        try
        {
            await _service.DeleteRowAsync(id, GetCurrentUserId(), ct);
            return ApiResponse<bool>.Success(true, $"Row {id} deleted");
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "ProductionWorkCalendar delete row 业务范围越界：Id={Id}", id);
            return ApiResponse<bool>.Fail(403, ex.Message);
        }
        catch (KeyNotFoundException ex) { return ApiResponse<bool>.Fail(404, ex.Message); }
        catch (InvalidOperationException ex) { return ApiResponse<bool>.Fail(409, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductionWorkCalendar delete row failed");
            return ApiResponse<bool>.Fail(500, $"Delete row failed: {ex.Message}");
        }
    }
}