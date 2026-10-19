/**
 * APS V1 4号位 — 排产总览 Pinia store
 *
 * 加载编排（5 个独立端点，无聚合端点）：
 *   1. active-plan          → 各 Domain 生效版本 + **planVersionId 唯一来源**
 *   2. order-query/summary  ┐
 *   3. resource-bottleneck  ┘ 依赖 1 的 planVersionId（无生效版本则跳过，不拿假数）
 *   4. candidate-summary    → 与 planVersionId 无关，始终调
 *   5. domain-status/{runId} → 依赖 1 的 sourceScheduleRunId（null 则**不发请求**）
 *
 * 各端点独立降级：单个失败只记录该端点错误，不清空其它已取到的数据
 * （避免「资源表挂了 ⇒ 订单 KPI 也变 0」这种假象）。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  orderApi,
  overviewApi,
  type ActiveVersionPanel,
  type OrderSummaryCounts,
  type OverviewResourceBottleneck,
  type RunDomainStatusItem
} from '@/api/aps-v1'

/** 单个端点的失败信息（用于页面按块提示，而不是整页报错） */
export interface OverviewSectionError {
  /** 中文块名，直接可展示 */
  section: string
  message: string
}

const toMessage = (err: unknown, fallback: string): string => {
  if (err instanceof ApiError) return `[${err.code}] ${err.message}`
  return (err as Error)?.message ?? fallback
}

export const useOverviewStore = defineStore('aps.overview', () => {
  const loading = ref(false)
  const errors = ref<OverviewSectionError[]>([])

  /** 各 Domain 生效版本（active-plan） */
  const activeVersions = ref<ActiveVersionPanel[]>([])
  /** 订单摘要（order-query/summary）—— null 表示「取不到」而非「为 0」 */
  const orderSummary = ref<OrderSummaryCounts | null>(null)
  /** 资源负荷（resource-bottleneck） */
  const resources = ref<OverviewResourceBottleneck[]>([])
  /** 待确认 Candidate 数（candidate-summary） */
  const pendingCandidateCount = ref<number | null>(null)
  /** 排程域状态（domain-status） */
  const domainStatuses = ref<RunDomainStatusItem[]>([])
  /** 是否存在 ACTIVE 版本 —— 决定 2/3/5 是否可调 */
  const hasActivePlan = computed(() => activeVersions.value.length > 0)

  async function load(): Promise<void> {
    loading.value = true
    errors.value = []

    const failed = (section: string, err: unknown, fallback: string) =>
      errors.value.push({ section, message: toMessage(err, fallback) })

    // ---- 1. active-plan：其余两个数据面的入参来源，必须先拿 ----
    let plans: ActiveVersionPanel[] = []
    try {
      plans = await overviewApi.getActivePlans()
      activeVersions.value = plans
    } catch (err) {
      activeVersions.value = []
      failed('生效版本', err, '加载生效版本失败')
    }

    // 单条场景（订单 / 资源 / 域状态）取第一条 —— 与 5号位 §四.2 提示一致
    const primary = plans[0]
    const planVersionId = primary?.planVersionId
    const scheduleRunId = primary?.sourceScheduleRunId

    // ---- 2/3/4/5 并行，各自降级 ----
    await Promise.all([
      (async () => {
        if (!planVersionId) {
          orderSummary.value = null
          return
        }
        try {
          orderSummary.value = await orderApi.getSummaryCounts(planVersionId)
        } catch (err) {
          orderSummary.value = null
          failed('订单摘要', err, '加载订单摘要失败')
        }
      })(),
      (async () => {
        if (!planVersionId) {
          resources.value = []
          return
        }
        try {
          resources.value = await overviewApi.getResourceBottleneck(planVersionId)
        } catch (err) {
          resources.value = []
          failed('资源负荷', err, '加载资源负荷失败')
        }
      })(),
      (async () => {
        try {
          pendingCandidateCount.value = await overviewApi
            .getCandidateSummary()
            .then((d) => d.pendingCount)
        } catch (err) {
          pendingCandidateCount.value = null
          failed('待确认候选', err, '加载待确认候选失败')
        }
      })(),
      (async () => {
        // §7.3：sourceScheduleRunId 为 null → 不发请求（路由是 {scheduleRunId:int}）
        if (!scheduleRunId) {
          domainStatuses.value = []
          return
        }
        try {
          domainStatuses.value = await overviewApi.getRunDomainStatus(scheduleRunId)
        } catch (err) {
          domainStatuses.value = []
          failed('排程域状态', err, '加载排程域状态失败')
        }
      })()
    ])

    loading.value = false
  }

  return {
    loading,
    errors,
    activeVersions,
    orderSummary,
    resources,
    pendingCandidateCount,
    domainStatuses,
    hasActivePlan,
    load
  }
})
