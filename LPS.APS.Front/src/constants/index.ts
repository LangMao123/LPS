/**
 * 请求成功状态码
 */
export const SUCCESS_CODE = 0

/**
 * 请求contentType
 */
export const CONTENT_TYPE: AxiosContentType = 'application/json'

/**
 * 请求超时时间
 */
export const REQUEST_TIMEOUT = 60000

/**
 * 不重定向白名单
 */
export const NO_REDIRECT_WHITE_LIST = ['/login']

/**
 * 不重置路由白名单
 */
export const NO_RESET_WHITE_LIST = ['Redirect', 'RedirectWrap', 'Login', 'NoFind', 'Root']

/**
 * 表格默认过滤列设置字段
 */
export const DEFAULT_FILTER_COLUMN = ['expand', 'selection']

/**
 * 是否根据headers->content-type自动转换数据格式
 */
export const TRANSFORM_REQUEST_DATA = true

/**
 * 全局图标前缀
 */
export const ICON_PREFIX = 'vi-'

/**
 * 登录后 / 直接访问根路径时的默认落地页
 *  10-16 用户裁决：「登录进来后默认进入排产总览」——此前落 framework 演示页「分析页」，
 *  而首页菜单已 hidden，用户登录后看到的是一个侧边栏里根本不存在的页面。
 *  选「排产总览」：它是 APS 的模块入口（aps.ts 第一个顶级节点 /aps 的第一页），
 *  门控码 aps.plan.view 5 个角色全持（rbac.md §1.2），全员可达，不会把人挡在 403。
 *  同时用于两处，别各写一份字面量：
 *    ① router/index.ts 根路由 `/` 的 redirect（未登录时先被 guard 拦去登录页并带 redirect 参数）
 *    ② LoginForm.vue 登录成功后 push 的兜底路径
 */
export const APS_HOME_PATH = '/aps/overview'
