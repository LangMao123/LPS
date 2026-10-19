using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LPS.APS.Web.Filters;

/// <summary>
/// 全局异常过滤器
/// 捕获所有 Controller 未处理的异常，统一包装为 ApiResponse 格式返回。
/// 对外只回 message + traceId；异常类型/明细/堆栈**一律不出服务端**（含开发环境）——
/// 堆栈已由 <c>LogError</c> 完整落日志，响应体带栈帧仅利于排查、无功能价值（4号位 10-17 报障，
/// Owner 2026-10-19 裁定本文件 5号位 可改后恢复 10-17 曾被回滚的同一收紧）。
/// </summary>
public class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        _logger.LogError(context.Exception,
            "未处理异常: {Method} {Path}",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path);

        var (code, message) = context.Exception switch
        {
            UnauthorizedAccessException => (401, "未授权访问"),
            KeyNotFoundException => (404, "资源不存在"),
            ArgumentException ex => (400, ex.Message),
            InvalidOperationException ex => (400, ex.Message),
            _ => (500, "服务器内部错误")
        };

        var response = ApiResponse.Fail(code, message);
        response.TraceId = context.HttpContext.TraceIdentifier;
        // 不再向任何环境（含 dev）回传 exception/detail/stackTrace——完整异常已由上方 LogError 落日志，
        // 排障用 traceId 关联即可；响应体携栈帧会把内部实现（Dapper/线程栈/SQL 片段）泄给客户端。

        context.Result = new ObjectResult(response) { StatusCode = code };
        context.ExceptionHandled = true;
    }
}
