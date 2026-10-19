/**
 * Candidate 列表 / 对比 / 确认 DTO —— 2026-10-16 按后端**已部署** DTO 逐字段重写
 *
 * 本文件此前的类型是 4号位 依据 §十二「17 字段」自拟的富对比契约，后端从未实现；
 * 页面据此渲染的订单级差异表、根因列表、共享资源占用、数量-时间切片四块均无端点供给，
 * 属假数据，已整体下线。以下字段全部可在 dev Swagger 与 lps/ 源文件中核对：
 *  - GET  /api/overview/candidate-summary → OverviewCandidateSummaryDto
 *      源：lps/LPS.APS.Core/Dto/OverviewDtos.cs:100-153
 *      列表**刻意不含** diff 统计（DTO 注释明写「不在列表项内承载 diff/平均完成时间等统计字段」），
 *      所以页面列表不再显示「影响订单/新增延期」，那两项只在对比详情里出现。
 *  - GET  /api/governance/plan-version/{cid}/compare-with/{bid} → CandidateComparisonDto
 *      源：lps/LPS.APS.Application/Services/Query/Dto/CandidateComparisonDto.cs
 *  - POST .../confirm-candidate、POST .../activate-candidate
 *      源：lps/LPS.APS.Web/Controllers/GovernanceController.cs:544-588
 *      两个写端点都返回 `ApiResponse.Ok(提示语)`，**data 恒为 null**，没有 actor/时间等回执字段。
 */

import type { IsoDateTime } from './common'

/* ===== 列表：GET /api/overview/candidate-summary ===== */

/** Candidate 简要信息（5号位 Overview 列表项） */
export interface CandidateBriefDto {
  planVersionId: number
  versionCode: string | null
  domainKey: string | null
  createdAt: IsoDateTime
  createdByUserName: string | null
  /** PlanVersion.Status（CANDIDATE / ACTIVE / …） */
  status: string | null
  /** 候选来源 ScheduleRunId */
  sourceScheduleRunId: number | null
  /** 候选来源 RunType（FULL_SCHEDULE / LOCAL_RESCHEDULE / MANUAL_RESCHEDULE / INSERT_ORDER_WHATIF） */
  runType: string | null
  /** 候选冻结的 Base 计划版本；compare-with 端点的第二个路径参数 */
  basePlanVersionId: number | null
  /** Base 是否仍为该 Domain 当前 ACTIVE（后端 §12.3 激活红线前置校验结果） */
  canActivate: boolean
}

/** Candidate 待处理摘要 */
export interface OverviewCandidateSummaryDto {
  pendingCount: number
  candidates: CandidateBriefDto[] | null
}

/* ===== 对比：GET /api/governance/plan-version/{cid}/compare-with/{bid} ===== */

export interface TaskChangeSummaryDto {
  /** 候选有、基础无的 Task 数 */
  added: number
  /** 基础有、候选无的 Task 数 */
  removed: number
  /** 计划起止时间变化的 Task 数（与 resourceChanged 可重叠） */
  timeShifted: number
  /** 资源变化的 Task 数（与 timeShifted 可重叠） */
  resourceChanged: number
  /** 跨域阻挡 Task 数 —— 后端当前保守返 0（任务级跨域判定未落盘，待 2号位） */
  crossDomainBlocked: number
}

export interface ImpactSummaryDto {
  /** 发生新增/移除/时间平移/资源变更的去重订单数 */
  impactedOrderCount: number
  /** 候选延期且基础同主键任务未延期的 Task 数 */
  newDelayCount: number
  /** 跨域影响数 —— 后端当前保守返 0（同 crossDomainBlocked） */
  crossDomainImpacted: number
  /** 订单级估算数 —— 归 2号位 边界，后端返 null 时前端显示 '-'，不按 0 处理 */
  estimatedOnlyCount: number | null
}

/** 计划版本概要（对比双方各一份） */
export interface PlanVersionBriefDto {
  planVersionId: number
  versionCode: string
  /** CANDIDATE / ACTIVE / …（PlanVersion.VersionCategory） */
  versionCategory: string
  status: string
  computedAt: IsoDateTime | null
}

/** 后端 compare-with 原样返回体 */
export interface CandidateComparisonDto {
  taskChangeSummary: TaskChangeSummaryDto
  impactSummary: ImpactSummaryDto
  baseSummary: PlanVersionBriefDto
  candidateSummary: PlanVersionBriefDto
}

/**
 * 页面视图模型 = 列表项（CandidateBriefDto）+ 对比结果 合并
 *  - header 的 domainKey / createdAt / runType / canActivate 只有列表端点给；
 *    对比端点只给双方版本概要，故必须由 store 合并，不能各自单读。
 */
export interface CandidateComparisonView {
  header: {
    candidatePlanVersionId: number
    candidateVersionCode: string
    basePlanVersionId: number | null
    baseVersionCode: string
    domainKey: string | null
    status: string
    createdAt: IsoDateTime
    createdByUserName: string | null
    sourceScheduleRunId: number | null
    runType: string | null
    /** 后端判定的可激活性，页面不自行推断 */
    canActivate: boolean
    /** 候选版本计算完成时间 */
    computedAt: IsoDateTime | null
  }
  taskChangeSummary: TaskChangeSummaryDto
  impactSummary: ImpactSummaryDto
}

/* ===== 写端点回执 ===== */

/** confirm-candidate / activate-candidate 的入参（后端只取 path 上的 planVersionId） */
export interface CandidateConfirmInput {
  candidatePlanVersionId: number
  /** 确认备注；后端 ConfirmCandidateRequest 仅此一个字段 */
  remark?: string
}

export interface CandidateActivateInput {
  candidatePlanVersionId: number
}

/**
 * 写端点回执：后端返回 `ApiResponse.Ok("候选版本 N 已确认（待激活）")`，
 * data 恒为 null —— 能拿到的真实信息只有 message 与服务端 timestamp，
 * 拿不到 actor / activatedAt / replacedActivePlanVersionId，页面不得补。
 */
export interface CandidateActionAck {
  message: string
  /** ApiResponse.Timestamp（服务端时间，用于确认动作留痕） */
  serverTime: IsoDateTime | null
}
