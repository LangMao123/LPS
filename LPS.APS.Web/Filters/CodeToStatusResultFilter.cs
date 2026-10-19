using System.Collections.Concurrent;
using System.Reflection;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LPS.APS.Web.Filters;

/// <summary>
/// 全局「信封业务码 → HTTP 状态码」统一桥（Owner 裁定 2026-10-19，统一收口方案 (b1)）。
///
/// <para><b>问题</b>：<c>ApiResponse</c> 是纯 POCO，<c>Fail(code,…)</c> 只写 JSON 里的 code 字段；
/// Controller <c>return ApiResponse&lt;T&gt;.Fail(400,…)</c> 裸返回时 HTTP 恒 200——
/// 网关/监控/裸 HTTP 消费方把失败记成成功（同根事故三起：1305/2026-10-14、domain-status/10-16、gantt resourceIds/10-19）。</para>
///
/// <para><b>机制</b>：仅当「HTTP 仍是 null/200」且「信封 Code∈[400,599]」同时成立才改写状态码——
/// 成功响应（Code=200）、已显式 <c>NotFound()/BadRequest()/StatusCode()</c> 包装的端点（HTTP 已非 200）、
/// 以及 <c>GlobalExceptionFilter</c> 已设过状态码的结果，全部天然跳过，不扩散。</para>
///
/// <para><b>豁免</b>：见 <see cref="IsExempt"/>，两条规则各有解除前提，**默认全升、豁免才要理由**。</para>
///
/// <para><b>边界</b>：不改 <c>ApiResponse</c>、不改任何 Controller 调用点（3号位 15 处与 5号位 ≈105 处零代码改动）；
/// 清单与出处：《5号位-2026-10-19-清单-裸Fail返回HTTP200分类与统一收口征询-给3号位.md》。</para>
/// </summary>
public sealed class CodeToStatusResultFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        // 只处理裸对象响应且框架尚未设定非 200 状态的情形
        if (context.Result is not ObjectResult { Value: not null } obj)
            return;
        if (obj.StatusCode is not (null or 200))
            return;

        var code = GetEnvelopeCode(obj.Value);
        if (code is null or (< 400) or (> 599))
            return;

        // 无豁免判定（2026-10-19 三处全解除，见下方记录）：信封码直通 HTTP 状态。
        obj.StatusCode = code;
    }

    public void OnResultExecuted(ResultExecutedContext context) { }

    // ───────────────────────── 豁免名单（当前为空——历史与解除记录留此，防再加回） ─────────────────────────
    //
    // 无豁免。HTTP 状态 == 信封 Code 是全项目不变式（2026-10-19 起完整成立）。
    //
    // 曾短暂存在、已于 2026-10-19 同日全部解除（4号位《信封码收口前置审计与三处豁免表态》§二，逐条亲验后端原文后采纳）：
    // ① POST /api/auth/login|refresh 的 401 —— 解除依据：前端拦截器 http.ts:121-142 的 isAuthEndpoint 已把
    //    「登录/刷新端点 401」与「会话过期 401」分流（refresh 走裸 axios 不进拦截器）；4号位 并顺带修掉
    //    「登录失败无提示」真缺陷（login 加 silent + 登录页补 catch）⇒ 升真 401 无死循环、提示照常。
    // ② 详情类 GET 404 三条前缀（/api/pi-position/、/api/order-query/、/api/procurement-manual-eta/）——
    //    解除依据：4号位 store 已将 err.code===404 归页面空态文案（PI/订单），手工 ETA 详情端点前端零消费方；
    //    其 unwrap 使 ApiError.code 两种响应形态同值 ⇒ 前端行为与部署时序解耦。
    //
    // 若将来再要新增豁免：必须带「解除前提」写进本注释区，参照 3号位 清单函先例。

    // ───────────────────────── 信封 Code 反射读取（类型缓存） ─────────────────────────

    // ApiResponse<T> 与非泛型 ApiResponse 无公共接口，用泛型定义比较识别；PropertyInfo 按类型缓存。
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> CodePropertyCache = new();

    private static int? GetEnvelopeCode(object value)
    {
        var type = value.GetType();
        var pi = CodePropertyCache.GetOrAdd(type, static t =>
        {
            bool isEnvelope = t == typeof(ApiResponse) ||
                              (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ApiResponse<>));
            return isEnvelope ? t.GetProperty("Code") : null;
        });
        return pi?.GetValue(value) as int?;
    }
}
