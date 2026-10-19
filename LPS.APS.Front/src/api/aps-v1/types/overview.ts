/**
 * 排产总览 DTO —— 对齐 5号位 OverviewController 真实端点（2026-10-16）
 *
 * Overview **没有聚合端点**（5号位 2026-10-15 定案），四个数据面各自独立调：
 *  - GET /api/overview/active-plan               → ActiveVersionPanel[]（每 Domain 一行；planVersionId 唯一来源）
 *  - GET /api/order-query/summary?planVersionId= → 订单摘要（总数 + 4 类计数）
 *  - GET /api/overview/resource-bottleneck?planVersionId= → 资源负荷 + 不可用窗口
 *  - GET /api/overview/candidate-summary         → 待确认 Candidate 数
 *  - GET /api/domain-status/{scheduleRunId}      → 排程域状态（6 值）
 *
 * 已撤：`PlanOverviewDto` 复合体（mock 时代设计，后端从未实现该聚合端点）。
 * 已删：`estimatedOnlyOrderCount` / 「仅估算」订单卡 ——
 *       冻结 07 §6.6a + 规则 P-009：规划性采购占位**仅内存、不建表**，
 *       Overview 是事后读模型，物理上给不出（5号位 2026-10-15 §十 结论，4号位 10-16 回 (A) 删除）。
 */

import type { DomainKey, IsoDateTime, PlanVersionStatus } from './common'

/** 当前 ACTIVE 版本（/api/overview/active-plan 数组元素）
 *  - 每个 Domain 至多一行（库级唯一索引 UQ_PlanVersion_OneActivePerDomain，前端无需去重）
 *  - 无 ACTIVE / 无授权域 → 后端返**空数组**（旧版返 null，前端两种都要兼容）
 */
export interface ActiveVersionPanel {
  planVersionId: number
  versionCode: string
  domainKey: DomainKey | null
  /** 版本生命周期状态；下钻为 ACTIVE 行时一般为 'ACTIVE' */
  status: PlanVersionStatus
  planHorizonStart: IsoDateTime
  planHorizonEnd: IsoDateTime
  /** 后端 WhenWritingNull ⇒ 该字段可能整个缺失，渲染需容缺 */
  activatedAt?: IsoDateTime | null
  sourceScheduleRunId?: number | null
  /** 以下为后端附加字段（可选消费） */
  totalTasks?: number
  totalOrders?: number
  createdAt?: IsoDateTime
}

/** 订单摘要（/api/order-query/summary）
 *  注意：**没有** estimatedOnlyCount —— 后端 DTO 无该字段（见文件头「已删」说明）
 */
export interface OrderSummaryCounts {
  planVersionId: number
  totalCount: number
  onTimeCount: number
  delayedCount: number
  riskCount: number
  unscheduledCount: number
}

/** 资源负荷（/api/overview/resource-bottleneck 数组元素） */
export interface OverviewResourceBottleneck {
  resourceId: number
  resourceCode: string
  resourceName: string
  resourceType: string
  taskCount: number
  totalPlannedHours: number
  /** 负荷率 = 实际工时 / 可用工时，>1 为过载；后端可空 */
  utilizationRate?: number | null
  isBottleneck: boolean
  /** 资源级「明确停机」窗口（ResourceCalendarSlot.AvailableFlag = 0），
   *  已限定在该 planVersionId 的计划范围内；无 OFF 时为空数组（非 null）
   */
  unavailableWindows: UnavailableWindow[]
}

export interface UnavailableWindow {
  from: IsoDateTime
  to: IsoDateTime
  /** 取自窗口 Remark，可空（后端 WhenWritingNull 会省略该字段） */
  reason?: string | null
}

/** 排程域状态值域（5号位 2026-10-15 §7.2 定的 6 值；3号位 RunLifecycleService 由 PlanVersion.Status 派生）
 *  历史：前端曾有 4 分类 FAILED / BLOCKED_BY_UPSTREAM / PI_POSITION_ISSUE / ODS_CONTRACT_FAILED，
 *        其中后 3 个后端**无此状态值** ⇒ 收敛（细分说明走 reason 字段，不进状态枚举）
 */
export const RUN_DOMAIN_STATUSES = [
  'COMPLETED',
  'CANDIDATE',
  'RUNNING',
  'FAILED',
  'BLOCKED',
  'NOT_STARTED'
] as const

export type RunDomainStatus = (typeof RUN_DOMAIN_STATUSES)[number]

export const RUN_DOMAIN_STATUS_LABELS: Record<RunDomainStatus, string> = {
  COMPLETED: '已完成',
  CANDIDATE: '待确认',
  RUNNING: '进行中',
  FAILED: '失败',
  BLOCKED: '被阻断',
  NOT_STARTED: '未开始'
}

export const RUN_DOMAIN_STATUS_TAG: Record<
  RunDomainStatus,
  'success' | 'primary' | 'warning' | 'info' | 'danger'
> = {
  COMPLETED: 'success',
  CANDIDATE: 'warning',
  RUNNING: 'primary',
  FAILED: 'danger',
  BLOCKED: 'danger',
  NOT_STARTED: 'info'
}

/** 需要报为「异常」的域状态（其余为正常/进行中） */
export const RUN_DOMAIN_ISSUE_STATUSES: RunDomainStatus[] = ['FAILED', 'BLOCKED']

/** 排程域状态（/api/domain-status/{scheduleRunId} 数组元素） */
export interface RunDomainStatusItem {
  domainKey: DomainKey
  status: RunDomainStatus | string
  /** FAILED = 失败信息；BLOCKED = 被阻断说明；其余可为空 */
  reason?: string | null
  planVersionId?: number | null
  planVersionCode?: string | null
  startedAt: IsoDateTime
  computedAt?: IsoDateTime | null
  activatedAt?: IsoDateTime | null
}

/** 待处理 Candidate 摘要（/api/overview/candidate-summary） */
export interface OverviewCandidateSummary {
  pendingCount: number
  candidates: OverviewCandidateBrief[]
}

export interface OverviewCandidateBrief {
  planVersionId: number
  versionCode: string
  domainKey?: DomainKey | null
  createdAt: IsoDateTime
  createdByUserName?: string | null
  status?: string | null
  sourceScheduleRunId?: number | null
  runType?: string | null
  basePlanVersionId?: number | null
  canActivate: boolean
}
