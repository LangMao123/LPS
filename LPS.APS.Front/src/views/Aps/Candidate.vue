<script setup lang="ts">
/**
 * APS V1 4号位 — 候选版本确认页
 *
 * 2026-10-16 代码级审计后按后端**已部署** DTO 重做：
 *  - 列表：5号位 `/api/overview/candidate-summary`（CandidateBriefDto）
 *  - 对比：3号位 `compare-with` 双参端点（CandidateComparisonDto）
 *  - 确认/激活：3号位，返回 message 回执
 *
 * 已下线的区块（后端无端点供给，页面不得造数）：
 *  - 顶部「使用说明」整块
 *  - 新订单完成情况表 / 既有订单影响表（对比端点只给汇总计数，无订单级明细）
 *  - 根因列表（reasons 数组不存在；跨域阻挡原因待后端补端点）
 *  - 共享资源占用表 / 数量-时间切片表（后端无对应字段）
 *  - 确认/激活结果里的操作者与时间（后端只回 message + 服务端时间戳）
 */

import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { formatUtcDateTimeSec, formatUtcMonthDayHM } from '@/utils/datetime'
import { storeToRefs } from 'pinia'
import { useCandidateStore, type CandidateListItem } from '@/store/modules/aps/candidate'
import { RUN_TYPE_LABEL } from '@/store/modules/aps/run'
import { PLAN_VERSION_STATUS_LABELS } from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElDialog,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElMessageBox,
  ElTag,
  ElTooltip
} from 'element-plus'

const candidateStore = useCandidateStore()
const route = useRoute()
const {
  list,
  pendingCount,
  currentComparison,
  lastAck,
  loading,
  listLoading,
  confirming,
  activating,
  error,
  currentCandidateId,
  currentHeader,
  canConfirm,
  canActivate,
  isWhatif,
  cannotConfirmReason,
  cannotActivateReason
} = storeToRefs(candidateStore)

/**
 * 跳转来源（route.query.source）
 *  - 'gantt'          → Gantt 任务 Drawer 的「发起人工重排」
 *  - 'resource-down'  → Gantt 任务 Drawer 的「基于设备故障建议重排」
 *  - 其他 / 无        → 直接访问本页
 */
type TriggerSource = 'gantt' | 'resource-down' | 'unknown'
const triggerSource = computed<TriggerSource>(() => {
  const s = route.query.source
  if (s === 'gantt') return 'gantt'
  if (s === 'resource-down') return 'resource-down'
  return 'unknown'
})

/** query 中的候选 ID（从甘特图抽屉跳转过来时携带） */
const targetCandidateId = computed<number | null>(() => {
  const raw = route.query.candidatePlanVersionId
  if (raw === undefined || raw === null || raw === '') return null
  const n = Number(raw)
  return Number.isFinite(n) ? n : null
})

/** query 中的对比基线 ID（Order 页「已有订单提前」跳转时携带；
 *  正式端点 CandidateBriefDto 已自带 basePlanVersionId，这里只作为兜底 */
const queryBasePlanVersionId = computed<number | null>(() => {
  const raw = route.query.basePlanVersionId
  if (raw === undefined || raw === null || raw === '') return null
  const n = Number(raw)
  return Number.isFinite(n) && n > 0 ? n : null
})

/** Non-null 在 v-if 块内用 */
const compNN = computed(() => currentComparison.value!)
/** Header 同上（currentHeader 是 header|null；template v-if 不传播窄化） */
const headerNN = computed(() => compNN.value.header)

/* ===== Confirm Modal ===== */
const dialogVisible = ref(false)
const confirmForm = ref({ remark: '' })

/* ===== 操作 ===== */
async function selectCandidate(item: CandidateListItem): Promise<void> {
  candidateStore.reset()
  await candidateStore.loadComparison(item.candidatePlanVersionId, item.basePlanVersionId)
}

function openConfirmDialog(): void {
  confirmForm.value = { remark: '' }
  dialogVisible.value = true
}
async function onConfirm(): Promise<void> {
  const h = currentHeader.value
  if (!h) return
  await candidateStore.confirm({
    candidatePlanVersionId: h.candidatePlanVersionId,
    remark: confirmForm.value.remark || undefined
  })
  if (!error.value) {
    dialogVisible.value = false
  }
}

async function onActivate(): Promise<void> {
  const h = currentHeader.value
  if (!h) return
  if (!canActivate.value) {
    ElMessageBox.alert(cannotActivateReason.value ?? '当前候选版本不允许激活', '无法激活', {
      type: 'warning'
    })
    return
  }
  try {
    await ElMessageBox.confirm(
      `确认将「${h.candidateVersionCode}」正式采用为该排程域的生效版本？原生效版本会自动归档。`,
      '采用候选版本',
      {
        type: 'warning',
        confirmButtonText: '确认采用',
        cancelButtonText: '取消',
        confirmButtonClass: 'el-button--warning'
      }
    )
  } catch {
    return
  }
  await candidateStore.activate({ candidatePlanVersionId: h.candidatePlanVersionId })
}

onMounted(async () => {
  await candidateStore.loadList()
  if (targetCandidateId.value !== null) {
    const found = list.value.find(
      (c: CandidateListItem) => c.candidatePlanVersionId === targetCandidateId.value
    )
    await candidateStore.loadComparison(
      targetCandidateId.value,
      queryBasePlanVersionId.value ?? found?.basePlanVersionId
    )
  }
})

/** 监听 route.query 变化：在甘特图抽屉连续触发不同 Task 时，刷新对比 */
watch(
  () => route.query.candidatePlanVersionId,
  async (newId) => {
    if (newId === undefined || newId === null || newId === '') return
    const n = Number(newId)
    if (!Number.isFinite(n)) return
    if (candidateStore.currentCandidateId === n) return
    await candidateStore.loadList()
    const found = list.value.find((c: CandidateListItem) => c.candidatePlanVersionId === n)
    await candidateStore.loadComparison(n, queryBasePlanVersionId.value ?? found?.basePlanVersionId)
  }
)
</script>

<template>
  <div class="aps-candidate">
    <!-- ===== 顶部 ===== -->
    <div class="page-header">
      <div>
        <h2 class="page-title">候选版本对比与确认</h2>
        <p class="page-sub">选择候选版本 → 对比基准版本差异 → 人工确认采用</p>
      </div>
      <ElButton :loading="listLoading" @click="candidateStore.loadList">
        <Icon icon="vi-ep:refresh" /> 刷新列表
      </ElButton>
    </div>

    <!-- ===== 来源提示（Gantt 抽屉跳转过来时，按 source 分文案） ===== -->
    <ElAlert
      v-if="triggerSource === 'gantt'"
      type="success"
      :closable="false"
      show-icon
      class="from-gantt-bar"
      title="本次候选版本由甘特图任务详情的「发起人工重排」产生，已自动加载对比"
    />
    <ElAlert
      v-else-if="triggerSource === 'resource-down'"
      type="warning"
      :closable="false"
      show-icon
      class="from-resource-bar"
      title="本次候选版本由甘特图任务详情中的「基于设备故障建议重排」产生，已自动加载对比"
    />

    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="error" />

    <!-- ===== Candidate 列表 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>候选版本列表</span>
          <ElTag v-if="pendingCount" type="warning" size="small">
            待确认 {{ pendingCount }} 个
          </ElTag>
        </div>
      </template>
      <div v-if="!list.length && !listLoading" class="panel-empty">
        <ElEmpty description="当前无候选版本" />
      </div>
      <div v-else class="candidate-grid">
        <div
          v-for="item in list"
          :key="item.candidatePlanVersionId"
          class="candidate-card"
          :class="{ active: currentCandidateId === item.candidatePlanVersionId }"
          @click="selectCandidate(item)"
        >
          <div class="card-head">
            <span class="card-code">{{ item.candidateVersionCode }}</span>
            <div class="card-head-tags">
              <ElTag size="small" effect="plain">{{
                PLAN_VERSION_STATUS_LABELS[item.status] ?? item.status
              }}</ElTag>
              <ElTag
                v-if="item.runType === 'INSERT_ORDER_WHATIF'"
                size="small"
                type="info"
                effect="plain"
              >
                试算
              </ElTag>
              <ElTag v-else-if="!item.canActivate" size="small" type="danger" effect="plain">
                不可采用
              </ElTag>
              <ElTag v-else size="small" type="success" effect="plain"> 可采用 </ElTag>
            </div>
          </div>
          <div class="card-meta">
            <div class="meta-row">
              <span class="meta-label">排程域</span>
              <ElTag size="small" effect="plain">{{
                candidateStore.humanLabel(item.domainKey)
              }}</ElTag>
            </div>
            <div class="meta-row">
              <span class="meta-label">基准版本</span>
              <span>{{
                item.basePlanVersionId === null ? '未关联' : `#${item.basePlanVersionId}`
              }}</span>
            </div>
            <div class="meta-row">
              <span class="meta-label">创建</span>
              <span>{{ formatUtcMonthDayHM(item.createdAt) }}</span>
            </div>
            <div class="meta-row">
              <span class="meta-label">创建人</span>
              <span>{{ item.createdByUserName || '—' }}</span>
            </div>
            <div class="meta-row">
              <span class="meta-label">运行类型</span>
              <span class="run-type">{{
                item.runType ? (RUN_TYPE_LABEL[item.runType] ?? item.runType) : '—'
              }}</span>
            </div>
          </div>
        </div>
      </div>
    </ElCard>

    <!-- ===== 加载态 ===== -->
    <div v-if="loading" class="loading-tip">加载候选版本对比中…</div>

    <!-- ===== 对比详情 ===== -->
    <template v-if="currentComparison">
      <!-- Header 条 -->
      <ElCard class="panel header-card">
        <template #header>
          <div class="panel-header">
            <span>
              <ElTag type="warning" effect="dark">候选版本</ElTag>
              {{ headerNN.candidateVersionCode }}
              <span class="muted-tag">对比</span>
              <ElTag type="success" effect="dark">基准 {{ headerNN.baseVersionCode }}</ElTag>
              <!-- 插单试算产生的候选：头部显式标识只读 -->
              <ElTag v-if="isWhatif" type="info" effect="dark" style="margin-left: 8px">
                试算只读
              </ElTag>
            </span>
            <div class="header-actions">
              <ElButton v-if="canConfirm" type="primary" @click="openConfirmDialog">
                <Icon icon="vi-ep:check" /> 确认
              </ElButton>
              <ElTooltip
                v-else-if="cannotConfirmReason"
                :content="cannotConfirmReason"
                placement="top"
              >
                <ElTag v-if="isWhatif" type="info" size="small" effect="plain">
                  <Icon icon="vi-ep:lock" /> 试算只读
                </ElTag>
                <ElTag v-else type="danger" size="small" effect="plain">
                  <Icon icon="vi-ep:lock" /> 不可采用
                </ElTag>
              </ElTooltip>
              <ElButton v-if="canActivate" type="warning" :loading="activating" @click="onActivate">
                <Icon icon="vi-ep:promotion" /> 采用
              </ElButton>
            </div>
          </div>
        </template>
        <ElDescriptions :column="4" size="small" border>
          <ElDescriptionsItem label="候选版本">
            {{ headerNN.candidateVersionCode }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="基准版本">{{ headerNN.baseVersionCode }}</ElDescriptionsItem>
          <ElDescriptionsItem label="排程域">
            {{ candidateStore.humanLabel(headerNN.domainKey) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="状态">
            <ElTag type="warning" size="small">{{
              PLAN_VERSION_STATUS_LABELS[headerNN.status] ?? headerNN.status
            }}</ElTag>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="创建时间">
            {{ formatUtcDateTimeSec(headerNN.createdAt) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="创建人">
            {{ headerNN.createdByUserName || '—' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="运行类型">
            {{ headerNN.runType ? (RUN_TYPE_LABEL[headerNN.runType] ?? headerNN.runType) : '—' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="可采用性">
            <ElTag v-if="canConfirm" type="success" size="small">可采用</ElTag>
            <ElTag v-else-if="isWhatif" type="info" size="small">试算只读</ElTag>
            <ElTag v-else type="danger" size="small">不可采用</ElTag>
            <span class="muted-ref"> 由后端判定 </span>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="计算时间">
            {{ headerNN.computedAt ? formatUtcDateTimeSec(headerNN.computedAt) : '—' }}
          </ElDescriptionsItem>
        </ElDescriptions>

        <!-- 任务变化汇总：后端 compare-with 的 TaskChangeSummary 五项 -->
        <div class="kpi-strip">
          <div class="kpi-pill">
            <span class="pill-label">新增任务</span>
            <span class="pill-val ok">+{{ compNN.taskChangeSummary.added }}</span>
          </div>
          <div class="kpi-pill">
            <span class="pill-label">删除任务</span>
            <span class="pill-val bad">-{{ compNN.taskChangeSummary.removed }}</span>
          </div>
          <div class="kpi-pill">
            <span class="pill-label">时间移动</span>
            <span class="pill-val">{{ compNN.taskChangeSummary.timeShifted }}</span>
          </div>
          <div class="kpi-pill">
            <span class="pill-label">资源变更</span>
            <span class="pill-val">{{ compNN.taskChangeSummary.resourceChanged }}</span>
          </div>
          <div class="kpi-pill" :class="{ warn: compNN.taskChangeSummary.crossDomainBlocked > 0 }">
            <span class="pill-label">跨域阻挡任务</span>
            <span
              class="pill-val"
              :class="{ danger: compNN.taskChangeSummary.crossDomainBlocked > 0 }"
            >
              {{ compNN.taskChangeSummary.crossDomainBlocked }}
            </span>
          </div>
        </div>
      </ElCard>

      <!-- 影响摘要：后端 ImpactSummary 四项；estimatedOnlyCount 后端返 null 时显示 '-'，不按 0 处理 -->
      <div class="summary-row">
        <div class="sum-pill" :class="{ hot: compNN.impactSummary.impactedOrderCount > 0 }">
          <span class="sum-label">受影响订单数</span>
          <span class="sum-val">{{ compNN.impactSummary.impactedOrderCount }}</span>
        </div>
        <div class="sum-pill" :class="{ hot: compNN.impactSummary.newDelayCount > 0 }">
          <span class="sum-label">新增延期</span>
          <span class="sum-val">{{ compNN.impactSummary.newDelayCount }}</span>
        </div>
        <div class="sum-pill" :class="{ hot: (compNN.impactSummary.estimatedOnlyCount ?? 0) > 0 }">
          <span class="sum-label">仅估算订单</span>
          <span class="sum-val">{{ compNN.impactSummary.estimatedOnlyCount ?? '-' }}</span>
        </div>
        <div class="sum-pill" :class="{ hot: compNN.impactSummary.crossDomainImpacted > 0 }">
          <span class="sum-label">跨排程域影响</span>
          <span class="sum-val">{{ compNN.impactSummary.crossDomainImpacted }}</span>
        </div>
      </div>

      <!-- 操作回执：后端写端点只返回提示语与服务端时间，没有操作者等字段，这里如实展示 -->
      <ElCard v-if="lastAck" class="panel activate-card">
        <template #header>
          <span>
            <Icon icon="vi-mdi:check-decagram" />
            {{ lastAck.action === 'activate' ? '本次采用结果' : '本次确认结果' }}
          </span>
        </template>
        <ElAlert type="success" :closable="false" show-icon :title="lastAck.ack.message" />
        <div class="ack-time">服务端时间：{{ formatUtcDateTimeSec(lastAck.ack.serverTime) }}</div>
      </ElCard>
    </template>

    <!-- ===== 确认 Dialog ===== -->
    <ElDialog
      v-model="dialogVisible"
      title="确认采用候选版本"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="候选版本">
          <span class="d-val">{{ currentHeader?.candidateVersionCode }}</span>
        </ElFormItem>
        <ElFormItem label="基准版本">
          <span class="d-val">{{ currentHeader?.baseVersionCode }}</span>
        </ElFormItem>
        <ElFormItem label="备注">
          <ElInput v-model="confirmForm.remark" type="textarea" :rows="3" placeholder="选填" />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>确认和采用的区别</template>
          确认只做留痕，版本不变；采用之后该排程域才以这个版本为准，原生效版本自动归档。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="dialogVisible = false">取消</ElButton>
        <ElButton v-if="canConfirm" type="primary" :loading="confirming" @click="onConfirm">
          确认
        </ElButton>
      </template>
    </ElDialog>
  </div>
</template>

<style lang="less" scoped>
.aps-candidate {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-end;

  .page-title {
    margin: 0;
    font-size: 20px;
    font-weight: 600;
    color: #1e293b;
  }

  .page-sub {
    margin: 4px 0 0;
    font-size: 12px;
    color: #94a3b8;
  }
}

.hint-bar {
  :deep(.el-alert__title) {
    font-weight: 600;
  }

  .hint-text {
    margin-left: 8px;
    font-size: 13px;
    line-height: 1.8;
    color: #475569;
  }

  .hint-text-v12 {
    display: block;
    margin-top: 4px;
    margin-left: 8px;
    padding-top: 4px;
    font-size: 12px;
    line-height: 1.7;
    color: #64748b;
    border-top: 1px dashed #cbd5e1;
  }
}

/* Gantt 抽屉跳转过来时的来源条（绿色，区别于普通约束提示） */
.from-gantt-bar {
  :deep(.el-alert__title) {
    font-weight: 600;
  }
}

/* P1：设备故障来源条 - 黄色（与设备故障视觉一致） */
.from-resource-bar {
  :deep(.el-alert__title) {
    font-weight: 600;
  }
}

.panel {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
}

.muted-tag {
  margin: 0 6px;
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
}

.muted-ref {
  margin-left: 6px;
  font-size: 11px;
  font-weight: 400;
  color: #94a3b8;
}

.loading-tip {
  padding: 30px;
  font-size: 14px;
  color: #94a3b8;
  text-align: center;
}

/* 候选版本卡 */
.candidate-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 12px;
}

.candidate-card {
  padding: 14px 16px;
  cursor: pointer;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  transition: all 0.2s;

  &:hover {
    border-color: #3b82f6;
  }

  &.active {
    background: #eff6ff;
    border-color: #3b82f6;
    box-shadow: 0 0 0 2px rgb(59 130 246 / 12%);
  }

  .card-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 10px;

    .card-code {
      font-family: 'Courier New', monospace;
      font-weight: 600;
      color: #1e293b;
    }
  }

  .card-meta {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 6px 12px;
    font-size: 12px;

    .meta-row {
      display: flex;
      gap: 6px;

      .meta-label {
        color: #94a3b8;
      }

      .warn {
        font-weight: 600;
        color: #f59e0b;
      }

      .danger {
        font-weight: 600;
        color: #ef4444;
      }
    }
  }
}

/* Header 卡 */
.header-card .header-actions {
  display: flex;
  gap: 8px;
}

/* KPI 条 */
.kpi-strip {
  display: flex;
  gap: 10px;
  margin-top: 12px;
  flex-wrap: wrap;
}

.kpi-pill {
  display: inline-flex;
  padding: 6px 12px;
  font-size: 12px;
  background: #f8fafc;
  border-radius: 16px;
  align-items: center;
  gap: 6px;

  &.warn {
    background: #fef3c7;
  }

  .pill-label {
    color: #64748b;
  }

  .pill-val {
    font-weight: 600;
    color: #1e293b;

    &.ok {
      color: #059669;
    }

    &.bad {
      color: #ef4444;
    }

    &.danger {
      color: #ef4444;
    }

    &.purple {
      color: #7c3aed;
    }

    &.green {
      color: #059669;
    }
  }
}

/* 影响摘要 */
.summary-row {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}

.sum-pill {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 16px;
  background: #f8fafc;
  border-radius: 6px;

  &.hot {
    background: #fef3c7;

    .sum-val {
      color: #b45309;
    }
  }

  .sum-label {
    font-size: 12px;
    color: #64748b;
  }

  .sum-val {
    font-size: 18px;
    font-weight: 600;
    color: #1e293b;
  }
}

.content-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

/* Diff 颜色 */
.ok {
  font-weight: 600;
  color: #059669;
}

.danger {
  font-weight: 600;
  color: #ef4444;
}

.ack-time {
  margin-top: 8px;
  font-size: 12px;
  color: #64748b;
}

/* Dialog */
.d-val {
  font-family: 'Courier New', monospace;
  color: #1e293b;
}

.d-alert {
  margin-bottom: -10px;
}
</style>
