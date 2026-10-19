/**
 * APS V1 4号位 — Resource Calendar / ManualCapacity DTO（设备资源日历 + 人工能力槽）
 *
 * 依据：
 *  - 冻结文档 v1.3《APS_V1_Resource_Calendar资源日历能力补充冻结方案_v1.3_人工能力槽模型与职责边界修订版》
 *      §三 设备 Resource + ResourceCalendarSlot；§五 ManualCapacitySlot + ManualCapacitySlotCalendar
 *      §九 缺省语义：无有效日历 = 不可用（禁止 7×24 缺省）
 *  - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》
 *
 * 后端（lps/** 只读参照）：
 *  - DTO        lps/LPS.APS.Application/Services/Dto/ResourceCalendarDtos.cs
 *  - 控制器     lps/LPS.APS.Web/Controllers/ResourceCalendarController.cs
 *               lps/LPS.APS.Web/Controllers/ManualCapacityController.cs
 *  - 服务       lps/LPS.APS.Application/Services/{ResourceCalendarService,ManualCapacityService}.cs
 *  - 权限码     lps/LPS.APS.Core/Authorization/PermissionCodes.cs:82-96
 *
 * 字段命名：**camelCase**（后端 Program.cs:93 PropertyNamingPolicy=CamelCase；
 *  5号位 2026-09-23《命名细节回执》明确"全为默认 CamelCase、无任何 JsonPropertyName"。
 *  ⚠️ 对接函 §三.2 写"PascalCase"与后端实际相反，已发函勘误，前端按 camelCase 实现）
 *
 * 删除语义（对接函 §二.7）：
 *  - 设备窗口      → 物理删（DELETE，行消失）
 *  - 人工槽主档    → 软删（DELETE → isActive=0，行保留，查询默认不含）
 *  - 人工槽窗口    → 物理删（DELETE，行消失）
 */

/** 六权限码常量（与后端 PermissionCodes.cs:82-96 严格对齐，43 码种子已由 3号位 落） */
export const RESOURCE_CALENDAR_PERMISSIONS = {
  view: 'aps.resource_calendar.view',
  edit: 'aps.resource_calendar.edit',
  delete: 'aps.resource_calendar.delete'
} as const

export const MANUAL_CAPACITY_PERMISSIONS = {
  view: 'aps.manual_capacity.view',
  edit: 'aps.manual_capacity.edit',
  delete: 'aps.manual_capacity.delete'
} as const

/** 铺窗天数上界（后端 ResourceCalendarService.cs:40 / ManualCapacityService.cs:113 同为 1..370） */
export const BULK_DAYS_MIN = 1
export const BULK_DAYS_MAX = 370

/**
 * 铺窗草稿（设备 / 人工两模块共用；不含对象键）
 *  - 调用页面按模块补 `resourceId`（设备）或 `manualSlotId`（人工）后组装为完整请求
 *  - 时间格式：startTime/endTime = `HH:mm:ss`（后端 TimeSpan）；startDate = `YYYY-MM-DD`（后端 DateTime）
 */
export interface BulkWindowDraft {
  startDate: string
  /** 1..370 */
  days: number
  startTime: string
  /** 须 > startTime（后端 400 校验；V1 单窗口不得跨天） */
  endTime: string
  availableFlag: boolean
  remark?: string
}

/* ==================== 模块 A：设备资源日历 ==================== */

/**
 * 设备日历批量铺窗请求（POST /api/resource-calendar/slots）
 *  - days=1 即"逐条加一天"，覆盖逐条场景
 *  - endTime 须 > startTime（后端 400）
 *  - ⚠️ 后端 BulkCreateAsync 只 INSERT 不删旧窗口（注释与代码不一致，已发函 D2 确认）
 *    → 重复铺窗会累积重叠窗口，前端须提示"追加"而非"覆盖"
 */
export interface ResourceCalendarBulkRequest {
  resourceId: number
  /** 起始日期（含） */
  startDate: string
  /** 连续天数 1..370 */
  days: number
  /** 每天开始时间 HH:mm:ss */
  startTime: string
  /** 每天结束时间 HH:mm:ss */
  endTime: string
  availableFlag: boolean
  remark?: string
}

/** 设备日历窗口（GET /api/resource-calendar/{resourceId}） */
export interface ResourceCalendarEntryDto {
  /** dbo.ResourceCalendarSlot.Id（long） */
  id: number
  resourceId: number
  resourceCode?: string
  resourceName?: string
  startTime: string
  endTime: string
  /** true=可用 / false=禁用 */
  availableFlag: boolean
  remark?: string
}

/* ==================== 模块 B：人工能力槽 ==================== */

/**
 * 人工能力槽主档（POST/GET /api/manual-capacity/slots）
 *  - 唯一键 (productionDepartmentId, operationName, slotCode)，重复 → 后端 400
 *  - ⚠️ 后端 DTO 无 operationCode（v1.3 §五 模型含之）；4号位 原 P0-1 仍未答，
 *    前端暂按 operationName 自由文本实现（已发函 G3）
 *  - 新增时后端回包不含 departmentName（ManualCapacityService.cs:71-78），需前端补齐展示
 */
export interface ManualCapacitySlotDto {
  /** 库自增主键；新增时传 0（后端忽略，回包带出真实值） */
  manualSlotId: number
  productionDepartmentId: number
  departmentName?: string
  operationName: string
  slotCode: string
  /** 软删标记；false = 已停用（Solver 不再装载） */
  isActive: boolean
}

/** 人工槽主档查询入参（GET /api/manual-capacity/slots，5号位 10-12 加分页） */
export interface ManualSlotQuery {
  departmentId?: number
  operationName?: string
  /** 默认 false（不含软删） */
  includeInactive?: boolean
  /** 分页：5号位 10-12 函确认 pageSize 默认 20、上限 200（越界回落 20） */
  pageIndex?: number
  pageSize?: number
}

/** 人工槽主档分页响应（10-12 收：与后端 PageResult<Page> 字段对齐，模块级本地包装）
 *  - 字段名用 `page`（不是 pageIndex），与 TaskSplitRuleConfig / ManualETA / business-fact-issues 同款
 *  - 替代此前通用 PageResult<T>（types/common.ts:62-67 已删，@see 4号位-2026-10-12-三函闭环RC22-ME13-E24-给5号位.md）
 */
export interface ManualCapacitySlotPageResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/**
 * 新增人工槽提交载荷（POST /api/manual-capacity/slots）
 *  - 不含 `manualSlotId`（库自增）与 `departmentName`（后端回包才带，且新增回包不含）
 */
export interface ManualSlotDraft {
  productionDepartmentId: number
  operationName: string
  slotCode: string
}

/** 人工槽日历窗口（GET /api/manual-capacity/calendar/{manualSlotId}） */
export interface ManualSlotCalendarDto {
  /** dbo.ManualCapacitySlotCalendar.Id（long） */
  id: number
  manualSlotId: number
  startTime: string
  endTime: string
  availableFlag: boolean
  remark?: string
}

/** 人工槽日历批量铺窗请求（POST /api/manual-capacity/calendar，与设备同构，键为 manualSlotId） */
export interface ManualSlotCalendarBulkRequest {
  manualSlotId: number
  startDate: string
  days: number
  startTime: string
  endTime: string
  availableFlag: boolean
  remark?: string
}

/* ==================== 展示用派生类型 ==================== */

/**
 * Calendar 配置状态（**本级**窗口有无；不是三级合并后的可排判定）
 *  - CONFIGURED   已配（本级窗口数 > 0）
 *  - NOT_CONFIGURED 未配（本级窗口数组为空）
 *  - UNKNOWN      未查证（本级尚未查询）
 *
 * ⚠️ v1.4 §23 + 废止清单 O-024 已废止「本级无窗口 = 不可排」：
 *    本级 MISSING 允许继承上级已认证窗口，仅三级计算后仍无权威合法窗口才判不可排。
 *    故 NOT_CONFIGURED 的 description 只陈述本级事实，**不得断言不可用**（10-14 RC25 收）
 */
export type CalendarConfigStatus = 'CONFIGURED' | 'NOT_CONFIGURED' | 'UNKNOWN'

export const CALENDAR_CONFIG_META: Record<
  CalendarConfigStatus,
  { label: string; tagType: 'success' | 'warning' | 'danger' | 'info'; description: string }
> = {
  CONFIGURED: {
    label: '已配',
    tagType: 'success',
    description: '本级已单独设过可用时段'
  },
  NOT_CONFIGURED: {
    label: '未配',
    tagType: 'warning',
    description: '本级没单独登记；所属工厂或部门开放了，它照样能排产'
  },
  UNKNOWN: {
    label: '未查证',
    tagType: 'info',
    description: '尚未查询该对象的本级窗口'
  }
}

/**
 * 资源（设备）主表行（GET /api/resource）—— 10-12 收口：原过渡 `ResourceCandidate` 已撤
 *  - 真源：`dbo.Resource` 镜像表（@see lps/LPS.APS.Core/Entities/Aps/Resource.cs）
 *  - 端点：5号位 ResourceController.cs:34，权限码 ResourceCalendarView
 *  - 字段对齐后端 ResourceListItemDto（@see lps/LPS.APS.Application/Services/Dto/ResourceCalendarDtos.cs:106-118）
 *  - 含 hasCalendar：v1.3 §九 无有效日历=不可用的"已配/未配"状态在列表阶段即可判定（无须逐行再查）
 */
export interface Resource {
  resourceId: number
  resourceCode: string
  resourceName: string
  /** MACHINE / LINE / MANUAL_STATION */
  resourceType?: string
  productionDepartmentId: number
  productionDepartmentName?: string
  isActive?: boolean
  /** 已配日历窗口 = ResourceCalendarSlot 有任一记录；空 = 不可排（v1.3 §九） */
  hasCalendar?: boolean
}
