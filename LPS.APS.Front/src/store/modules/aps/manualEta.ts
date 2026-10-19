/**
 * APS V1 4号位 — 人工到货时间（Manual ETA）Pinia store（页面 8）
 *
 * @see 4号位文档第 11 节 / 审核报告 P1-7
 *
 * 状态：
 *  - list：ManualEtaDto[]
 *  - currentDetail：当前 Drawer 编辑对象（新增时为 null）
 *  - loading / saving / error：UI 态
 *  - lastFilter：最近一次查询的筛选条件
 *
 * 4号位不计算 Effective ETA / AvailableTime
 *
 * ❌ 2026-09-15 撤销 DepartmentCode 维度（@see 4号位-2026-09-13-裁定回退清单.md）：
 *  - 9月13日 `未命名的Markdown文件.md` 实际是 0号位 出的业务裁决（程序有效）
 *  - DepartmentCode 整条撤销：store getter distinctDepartmentCodes 删除
 *  - 当前状态：与 9月13日 撤销状态一致
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  manualEtaApi,
  type ManualEtaDto,
  type ManualEtaListFilter,
  type ManualEtaUpsertInput,
  type ManualEtaCancelInput
} from '@/api/aps-v1'

export const useManualEtaStore = defineStore('aps.manualEta', () => {
  // ===== state =====
  const list = ref<ManualEtaDto[]>([])
  const currentDetail = ref<ManualEtaDto | null>(null)
  const loading = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)
  /** 最近一次查询的筛选条件（分页/筛选 UI 还原用） */
  const lastFilter = ref<ManualEtaListFilter>({})
  /** 分页：当前页（1 基） */
  const pageIndex = ref(1)
  /** 分页：每页条数（默认 20，与 5号位 端点一致） */
  const pageSize = ref(20)
  /** 分页：后端返回的 total（满足筛选条件全量条数） */
  const total = ref(0)

  /* ===== 10-12 ME13 收口：KPI 聚合不再读 list.length（翻页会跳） =====
   *  - 后端未落 summary 聚合端点（5号位 10-12 函未明确），store 端维护 cumulative 计数
   *  - 筛选变化 → 重置 cumulative + visitedPages；翻页 → 已访问页跳过，未访问页累加
   *  - upsert/cancel → 同步累计（保证新增/取消后 KPI 即时刷新）
   *  - 后端 R1 summary 端点落地后可改为单一 source-of-truth，cumulative 退化为回填
   */
  const totalActive = ref(0)
  const totalCancelled = ref(0)
  /** 已访问页标记（key = pageIndex），filter 变化时清空 */
  const visitedPages = new Set<number>()

  // ===== getters =====
  /** Active 行数（当前页；KPI 用 totalActive 全集，activeCount 仅供调试/兼容） */
  const activeCount = computed(() => list.value.filter((x) => x.isActive).length)
  /** 已取消行数（当前页；KPI 用 totalCancelled 全集，cancelledCount 仅供调试/兼容） */
  const cancelledCount = computed(() => list.value.filter((x) => !x.isActive).length)
  /** 采购订单号去重集合（供筛选下拉用） */
  const distinctPoNos = computed(() => Array.from(new Set(list.value.map((x) => x.poNo))).sort())
  /** 物料编码去重集合（供筛选下拉用） */
  const distinctMaterialCodes = computed(() =>
    Array.from(new Set(list.value.map((x) => x.materialCode))).sort()
  )

  // ===== actions =====
  /** 重置 cumulative KPI（筛选条件变化时调用） */
  function resetKpiAggregate(): void {
    totalActive.value = 0
    totalCancelled.value = 0
    visitedPages.clear()
  }

  /** 累加当前页到 cumulative KPI（已访问页跳过） */
  function accumulateKpi(page: number): void {
    if (visitedPages.has(page)) return
    visitedPages.add(page)
    for (const item of list.value) {
      if (item.isActive) totalActive.value += 1
      else totalCancelled.value += 1
    }
  }

  async function load(filter?: ManualEtaListFilter): Promise<void> {
    loading.value = true
    error.value = null
    try {
      // 默认带当前分页参数；filter 内未传则用 store 现行值
      const next: ManualEtaListFilter = {
        pageIndex: filter?.pageIndex ?? pageIndex.value,
        pageSize: filter?.pageSize ?? pageSize.value,
        ...(filter ?? {})
      }
      // 筛选条件变化（不含 pageIndex/pageSize）→ 重置 cumulative KPI
      const prev = lastFilter.value
      const filterKeyChanged =
        prev.materialIds?.join(',') !== next.materialIds?.join(',') ||
        prev.poNos?.join(',') !== next.poNos?.join(',') ||
        prev.materialCodes?.join(',') !== next.materialCodes?.join(',') ||
        prev.activeOnly !== next.activeOnly
      if (filterKeyChanged) resetKpiAggregate()
      // 5号位 2026-10-08 端点已切分页：返回 { items, total, page, pageSize }；旧 mock / 老版本直接返回数组
      //  - 老形态：直接当数组用；total 取数组长度
      //  - 新形态：取 .items + .total 还原
      const resp = await manualEtaApi.list(next)
      let currentPage = next.pageIndex ?? pageIndex.value
      if (Array.isArray(resp)) {
        list.value = resp
        total.value = resp.length
      } else {
        list.value = resp?.items ?? []
        total.value = Number(resp?.total ?? 0)
        if (typeof resp?.page === 'number') pageIndex.value = resp.page
        if (typeof resp?.pageSize === 'number') pageSize.value = resp.pageSize
        currentPage = pageIndex.value
      }
      // 累加当前页到 cumulative KPI
      accumulateKpi(currentPage)
      lastFilter.value = next
    } catch (err) {
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  /** 业务键（与 5号位 `ProcurementManualEtaRepository.MERGE ON` 一致：4 字段复合主键）
   *  - @see lps/LPS.APS.BusinessRules/Repositories/ProcurementManualEtaRepository.cs:202-205
   *  - receivingWarehouse 可选字段，统一按空串处理（避免 undefined/null/'' 三态比较漏匹配）
   */
  function sameBusinessKey(a: ManualEtaDto, b: ManualEtaDto): boolean {
    return (
      a.poNo === b.poNo &&
      a.lineNo === b.lineNo &&
      a.materialId === b.materialId &&
      (a.receivingWarehouse ?? '') === (b.receivingWarehouse ?? '')
    )
  }

  /**
   * Upsert：新增或更新
   *  - 成功后用 result 局部回写（避免无谓的全量 reload）
   *  - 列表内查找用 4 字段复合主键（与后端 MERGE ON 对齐；避免同 poNo+lineNo 多物料 / 多仓库时找错行）
   *  - 新增时 total +1（兜底分页总数；后续 @see R1 summary 端点落地后改为后端聚合口径）
   *  - 列表写回用新数组引用（map / spread），不直接下标赋值 / unshift —— ElTable :data
   *    依赖引用变化触发重渲，原地变更会出现"新增后不显示，刷新后显示"的问题
   *  - currentDetail 同步刷新
   *  - 10-12 ME13：同步刷新 cumulative KPI（新增 → totalActive+1；更新 → 状态翻转也翻转累计）
   */
  async function upsert(input: ManualEtaUpsertInput): Promise<ManualEtaDto> {
    saving.value = true
    error.value = null
    try {
      const result = await manualEtaApi.upsert(input)
      const idx = list.value.findIndex((x) => sameBusinessKey(x, result))
      if (idx >= 0) {
        const prevActive = list.value[idx].isActive
        list.value = list.value.map((x, i) => (i === idx ? result : x))
        // 状态翻转：active↔cancelled，cumulative KPI 同步迁移
        if (prevActive !== result.isActive) {
          if (result.isActive) {
            totalActive.value += 1
            totalCancelled.value = Math.max(0, totalCancelled.value - 1)
          } else {
            totalCancelled.value += 1
            totalActive.value = Math.max(0, totalActive.value - 1)
          }
        }
      } else {
        list.value = [result, ...list.value]
        // 新增：list 长度 +1，total 兜底 +1（避免「共 X 条」与「当前页」长期不一致）
        total.value = total.value + 1
        // 新增默认 isActive=true（后端 ManualEtaService 默认），cumulative KPI 累加
        if (result.isActive) totalActive.value += 1
        else totalCancelled.value += 1
      }
      currentDetail.value = result
      return result
    } catch (err) {
      handleError(err)
      throw err
    } finally {
      saving.value = false
    }
  }

  /**
   * 取消（isActive=false）
   *  - 软删除，保留记录
   *  - 列表内查找 / currentDetail 同步均用 4 字段复合主键（同 upsert）
   *  - 列表写回用新数组引用（map），不直接下标赋值（ElTable :data 重渲依赖，@see upsert）
   *  - 10-12 ME13：同步刷新 cumulative KPI（active → cancelled，totalActive-1/totalCancelled+1）
   */
  async function cancel(input: ManualEtaCancelInput): Promise<ManualEtaDto> {
    saving.value = true
    error.value = null
    try {
      const result = await manualEtaApi.cancel(input)
      const idx = list.value.findIndex((x) => sameBusinessKey(x, result))
      if (idx >= 0) {
        const prevActive = list.value[idx].isActive
        list.value = list.value.map((x, i) => (i === idx ? result : x))
        // active → cancelled：cumulative 翻转
        if (prevActive && !result.isActive) {
          totalActive.value = Math.max(0, totalActive.value - 1)
          totalCancelled.value += 1
        }
      }
      if (currentDetail.value && sameBusinessKey(currentDetail.value as ManualEtaDto, result)) {
        currentDetail.value = result
      }
      return result
    } catch (err) {
      handleError(err)
      throw err
    } finally {
      saving.value = false
    }
  }

  function openDetail(dto: ManualEtaDto | null): void {
    currentDetail.value = dto
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
    list.value = []
    currentDetail.value = null
    error.value = null
    lastFilter.value = {}
    resetKpiAggregate()
  }

  return {
    list,
    currentDetail,
    loading,
    saving,
    error,
    lastFilter,
    pageIndex,
    pageSize,
    total,
    totalActive,
    totalCancelled,
    activeCount,
    cancelledCount,
    distinctPoNos,
    distinctMaterialCodes,
    load,
    upsert,
    cancel,
    openDetail,
    reset,
    resetKpiAggregate
  }
})
