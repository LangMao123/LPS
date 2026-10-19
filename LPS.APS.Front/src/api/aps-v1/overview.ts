/**
 * APS V1 4号位 — 排产总览 API（5号位 OverviewController 真实端点）
 *
 * @owner 5号位（只读查询，直接读 APS 事实表，不重算业务结果）
 * 端点一览（`lps/LPS.APS.Web/Controllers/OverviewController.cs`）：
 *  - GET /api/overview/active-plan             当前 ACTIVE 计划版本（按 Domain 的列表）
 *  - GET /api/overview/resource-bottleneck     资源负荷 / Bottleneck（含不可用窗口）
 *  - GET /api/overview/candidate-summary       Candidate 待处理摘要
 *  - GET /api/domain-status/{scheduleRunId}    排程域状态（6 值，5号位 业务接入层）
 * 订单摘要走 OrderQuery 的 /api/order-query/summary（见 orderApi.getSummary）。
 *
 * 4号位文档第 4 节约束：本页面只读，不触发任何重算/触发接口。
 */

import { apsHttp } from './http'
import type {
  ActiveVersionPanel,
  OverviewCandidateSummary,
  OverviewResourceBottleneck,
  RunDomainStatusItem
} from './types'

/**
 * active-plan 响应归一化。
 *
 * 5号位 2026-10-15 §四 定案：该端点由「单对象（TOP 1）/ null」**改为裸数组**，
 * `data: []` 表示无 ACTIVE。但代码改动与 dev 部署不同步（实测 dev 仍返无 data 键 = null），
 * 故这里同时接受三种形状：数组 / 单对象 / null —— 不为它造任何数据，只是不白屏。
 * 后端部署到位后自然只走数组分支。
 */
export function normalizeActivePlans(raw: unknown): ActiveVersionPanel[] {
  if (!raw) return []
  if (Array.isArray(raw)) return raw as ActiveVersionPanel[]
  if (typeof raw === 'object' && 'planVersionId' in (raw as Record<string, unknown>)) {
    return [raw as ActiveVersionPanel]
  }
  return []
}

/** 后端 `unavailableWindows` 容缺：老 DTO 无该字段 ⇒ 补空数组，避免模板 `.length` 崩 */
const withWindows = (rows: OverviewResourceBottleneck[]): OverviewResourceBottleneck[] =>
  rows.map((r) => ({ ...r, unavailableWindows: r.unavailableWindows ?? [] }))

export const overviewApi = {
  /** 各 Domain 当前生效（ACTIVE）版本；不传 domainKey = 授权范围内全部
   *  - 越界 domainKey → 后端 403
   *  - 返回值即 planVersionId 的唯一来源（订单 / 资源两张卡都依赖它）
   */
  async getActivePlans(domainKey?: string): Promise<ActiveVersionPanel[]> {
    const res = await apsHttp.get<unknown>({
      url: '/api/overview/active-plan',
      params: domainKey ? { domainKey } : undefined
    })
    return normalizeActivePlans(res)
  },

  /** 资源负荷 / Bottleneck（topN 默认 10） */
  async getResourceBottleneck(
    planVersionId: number,
    topN = 10
  ): Promise<OverviewResourceBottleneck[]> {
    const rows = await apsHttp.get<OverviewResourceBottleneck[]>({
      url: '/api/overview/resource-bottleneck',
      params: { planVersionId, topN }
    })
    return withWindows(rows ?? [])
  },

  /** Candidate 待处理摘要（无入参，后端按授权范围统计） */
  async getCandidateSummary(): Promise<OverviewCandidateSummary> {
    const dto = await apsHttp.get<OverviewCandidateSummary>({
      url: '/api/overview/candidate-summary'
    })
    return { pendingCount: dto?.pendingCount ?? 0, candidates: dto?.candidates ?? [] }
  },

  /** 排程域状态（6 值）。runId 为 null 时**调用方不要发请求**（路由是 {scheduleRunId:int}） */
  async getRunDomainStatus(scheduleRunId: number): Promise<RunDomainStatusItem[]> {
    const rows = await apsHttp.get<RunDomainStatusItem[]>({
      url: `/api/domain-status/${scheduleRunId}`
    })
    return rows ?? []
  }
}
