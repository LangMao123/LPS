/**
 * APS V1 4号位 — Candidate 列表与确认 Pinia store（页面：候选版本确认）
 *
 * 2026-10-16 按后端已部署 DTO 重排：
 *  - 列表改走 5号位 `/api/overview/candidate-summary`（此前绕道 governance/runs，字段全不匹配）
 *  - 对比走 3号位 `compare-with` 双参端点；basePlanVersionId 由列表项提供
 *  - header（domainKey / createdAt / runType / canActivate）只有列表端点给，对比端点不给，
 *    故 currentComparison 是「列表项 + 对比结果」合并出的视图模型，不是后端原样透传
 *  - confirm / activate 后端只回 message + 服务端时间，没有 actor/时间戳等回执字段，
 *    页面展示的就是这两项，不补任何前端自造字段
 *
 * 权限码门控（v1.2 §23.1）：aps.candidate.confirm / aps.candidate.activate，
 * 可激活性一律以后端 CandidateBriefDto.canActivate 为准，前端不自行推断。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  candidateApi,
  ApiError,
  type CandidateActionAck,
  type CandidateActivateInput,
  type CandidateBriefDto,
  type CandidateComparisonView,
  type CandidateConfirmInput,
  type DomainKey
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'
import { useDomainStore } from './domain'
import { assertScope, ScopeViolationError, filterByScope } from './scope'

/** 列表卡片用（已由 store 从 CandidateBriefDto 归一，模板不必处理 null 字段名） */
export interface CandidateListItem {
  candidatePlanVersionId: number
  candidateVersionCode: string
  domainKey: string | null
  status: string
  createdAt: string
  createdByUserName: string | null
  runType: string | null
  /** compare-with 端点的第二个路径参数；后端未给时列表仍展示但不可点开对比 */
  basePlanVersionId: number | null
  /** 后端 §12.3 激活红线前置校验结果 */
  canActivate: boolean
}

/** 待确认候选数（Overview 底部条与本页共用同一端点） */
function toListItem(b: CandidateBriefDto): CandidateListItem {
  return {
    candidatePlanVersionId: b.planVersionId,
    candidateVersionCode: b.versionCode ?? `#${b.planVersionId}`,
    domainKey: b.domainKey,
    status: b.status ?? '',
    createdAt: b.createdAt,
    createdByUserName: b.createdByUserName,
    runType: b.runType,
    basePlanVersionId: b.basePlanVersionId,
    canActivate: b.canActivate
  }
}

export const useCandidateStore = defineStore('aps.candidate', () => {
  // ===== state =====
  const list = ref<CandidateListItem[]>([])
  const pendingCount = ref<number | null>(null)
  const currentComparison = ref<CandidateComparisonView | null>(null)
  const loading = ref(false)
  const listLoading = ref(false)
  const confirming = ref(false)
  const activating = ref(false)
  /** 最近一次写端点回执（只有后端给的 message + 服务端时间） */
  const lastAck = ref<{ action: 'confirm' | 'activate'; ack: CandidateActionAck } | null>(null)
  const error = ref<string | null>(null)

  // ===== getters =====
  const currentCandidateId = computed(
    () => currentComparison.value?.header.candidatePlanVersionId ?? null
  )
  /** 当前选中的 Header（用于确认按钮处展示） */
  const currentHeader = computed(() => currentComparison.value?.header ?? null)

  const apsAuth = useApsAuthStore()
  const roleAllowedToConfirm = computed(() => apsAuth.has('aps.candidate.confirm'))
  const roleAllowedToActivate = computed(() => apsAuth.has('aps.candidate.activate'))

  /** 是否可"确认采用"：后端 canActivate + 状态为 CANDIDATE + 角色持确认码 */
  const canConfirm = computed(() => {
    const h = currentHeader.value
    if (!h) return false
    if (!roleAllowedToConfirm.value) return false
    return h.canActivate && h.status === 'CANDIDATE'
  })

  /** 是否可"激活"（CANDIDATE → ACTIVE）；与确认分开判权限码，便于将来拆边界 */
  const canActivate = computed(() => {
    const h = currentHeader.value
    if (!h) return false
    if (!roleAllowedToActivate.value) return false
    return h.canActivate && h.status === 'CANDIDATE'
  })

  /** 试算只读：后端判不可激活、且来源是插单试算运行 */
  const isWhatif = computed(() => {
    const h = currentHeader.value
    return !!h && !h.canActivate && h.runType === 'INSERT_ORDER_WHATIF'
  })

  function cannotReason(kind: 'confirm' | 'activate'): string | null {
    const h = currentHeader.value
    if (!h) return null
    const hasRole = kind === 'confirm' ? roleAllowedToConfirm.value : roleAllowedToActivate.value
    if (!hasRole) return '当前账号没有该操作权限'
    if (h.status !== 'CANDIDATE') return `当前状态为「${h.status}」，只有待确认的候选版本可以操作`
    if (!h.canActivate) {
      return isWhatif.value
        ? '插单试算产生的候选版本只能查看，不能采用'
        : '后端判定该候选版本当前不可激活'
    }
    return null
  }

  const cannotConfirmReason = computed(() => cannotReason('confirm'))
  const cannotActivateReason = computed(() => cannotReason('activate'))

  const domainStore = useDomainStore()
  /** domainKey → 中文名；后端未给或字典里没有时回退原值 */
  const humanLabel = (domainKey: string | undefined | null): string =>
    domainStore.label(domainKey as DomainKey | undefined | null)

  // ===== actions =====
  /**
   * 加载 Candidate 列表（5号位 candidate-summary）
   *  - 按 dataScope.domainKeys 预过滤（前端防御；后端亦有二次校验）
   *  - 端点不返回 diff 统计，故列表不展示影响订单/新增延期
   */
  async function loadList(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      const summary = await candidateApi.list()
      pendingCount.value = summary.pendingCount
      const items = (summary.candidates ?? []).map(toListItem)
      list.value = filterByScope(items, apsAuth.dataScope, (c) => ({
        domainKey: c.domainKey ?? undefined
      }))
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  /**
   * 加载候选 vs 基准 对比（双参：candidateId + baseId）
   *  - basePlanVersionId 来自列表项；缺失时不发请求，给出可读原因
   *  - 返回值是「列表项 + 对比结果」合并的视图模型（header 的域/时间/运行类型只有列表端点给）
   */
  async function loadComparison(
    candidatePlanVersionId: number,
    basePlanVersionId?: number | null
  ): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const brief = list.value.find((c) => c.candidatePlanVersionId === candidatePlanVersionId)
      const baseId = basePlanVersionId ?? brief?.basePlanVersionId
      if (!brief) {
        throw new Error('该候选版本已不在待确认列表中，请刷新后重试')
      }
      if (!baseId) {
        throw new Error('该候选版本没有关联的基准计划版本，暂时无法对比')
      }
      const cmp = await candidateApi.getComparison(candidatePlanVersionId, baseId)
      currentComparison.value = {
        header: {
          candidatePlanVersionId: cmp.candidateSummary.planVersionId,
          candidateVersionCode: cmp.candidateSummary.versionCode || brief.candidateVersionCode,
          basePlanVersionId: cmp.baseSummary.planVersionId,
          baseVersionCode: cmp.baseSummary.versionCode,
          domainKey: brief.domainKey,
          status: cmp.candidateSummary.status || brief.status,
          createdAt: brief.createdAt,
          createdByUserName: brief.createdByUserName,
          sourceScheduleRunId: null,
          runType: brief.runType,
          canActivate: brief.canActivate,
          computedAt: cmp.candidateSummary.computedAt
        },
        taskChangeSummary: cmp.taskChangeSummary,
        impactSummary: cmp.impactSummary
      }
    } catch (err) {
      handleError(err)
      currentComparison.value = null
    } finally {
      loading.value = false
    }
  }

  /** 人工确认：仅 canActivate=true 且状态 CANDIDATE 的候选可确认 */
  async function confirm(input: CandidateConfirmInput): Promise<void> {
    if (!canConfirm.value) {
      error.value = cannotConfirmReason.value ?? '当前候选版本不允许确认'
      return
    }
    const header = currentHeader.value
    if (header?.domainKey) {
      try {
        assertScope(apsAuth.dataScope, { domainKey: header.domainKey })
      } catch (err) {
        if (err instanceof ScopeViolationError) {
          error.value = err.message
          return
        }
        throw err
      }
    }
    confirming.value = true
    error.value = null
    try {
      lastAck.value = { action: 'confirm', ack: await candidateApi.confirm(input) }
    } catch (err) {
      handleError(err)
    } finally {
      confirming.value = false
    }
  }

  /**
   * 激活候选版本（CANDIDATE → ACTIVE）
   *  - 不强制先确认：确认是留痕，激活是落地，两者独立
   *  - 成功后刷新列表并按当前候选的基准版本重拉对比
   */
  async function activate(input: CandidateActivateInput): Promise<void> {
    if (!canActivate.value) {
      error.value = cannotActivateReason.value ?? '当前候选版本不允许激活'
      return
    }
    const header = currentHeader.value
    if (header?.domainKey) {
      try {
        assertScope(apsAuth.dataScope, { domainKey: header.domainKey })
      } catch (err) {
        if (err instanceof ScopeViolationError) {
          error.value = err.message
          return
        }
        throw err
      }
    }
    activating.value = true
    error.value = null
    try {
      lastAck.value = { action: 'activate', ack: await candidateApi.activate(input) }
      const baseId = currentComparison.value?.header.basePlanVersionId
      await Promise.all([
        loadList(),
        baseId ? loadComparison(input.candidatePlanVersionId, baseId) : Promise.resolve()
      ])
    } catch (err) {
      handleError(err)
    } finally {
      activating.value = false
    }
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    currentComparison.value = null
    lastAck.value = null
    error.value = null
  }

  return {
    list,
    pendingCount,
    currentComparison,
    loading,
    listLoading,
    confirming,
    activating,
    lastAck,
    error,
    currentCandidateId,
    currentHeader,
    canConfirm,
    canActivate,
    isWhatif,
    cannotConfirmReason,
    cannotActivateReason,
    roleAllowedToConfirm,
    roleAllowedToActivate,
    humanLabel,
    loadList,
    loadComparison,
    confirm,
    activate,
    reset,
    handleError
  }
})
