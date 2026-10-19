<script setup lang="ts">
/**
 * APS V1 4号位 — 排产总览（页面 1 / U01 / U02）
 *
 * 数据源（5号位真实端点，5 个数据面各自独立调，无聚合端点）：
 *  - /api/overview/active-plan             各 Domain 生效版本（→ store.planVersionId）
 *  - /api/order-query/summary?planVersionId= 订单摘要 4 类计数
 *  - /api/overview/resource-bottleneck?planVersionId= 资源负荷 + 不可用窗口
 *  - /api/overview/candidate-summary       待确认 Candidate 数
 *  - /api/domain-status/{runId}            排程域状态（6 值）
 *
 * 保留的 4号位文档约束：
 *  - 只展示，不触发任何重算/触发接口（第 2.1 节）
 *  - 多 Domain 同时展示生效版本（第 4 节）
 *  - 排程域异常区分根因（FAILED）与非根因（BLOCKED），细分说明走 reason
 *
 * 已删：底部「仅估算订单」卡片 —— 冻结 07 §6.6a + 规则 P-009 规划性采购占位**仅内存不建表**，
 *       总览是事后读模型物理上给不出（5号位 2026-10-15 §十；4号位 10-16 回执选 (A) 删除）。
 *
 * 10-16 版式精简：页面大标题 + 副标题 + 刷新按钮整块下线（标题由面包屑/标签页承担，页面只留数据）；
 *   「无生效版本」说明从顶部横幅改为写进「当前生效版本」卡片的空状态，订单摘要取不到时给整块空态
 *   而不是摆 5 张含 0 的 KPI 卡。
 */

import { computed, onMounted } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { useOverviewStore } from '@/store/modules/aps/overview'
import { useDomainStore } from '@/store/modules/aps/domain'
import {
  RUN_DOMAIN_ISSUE_STATUSES,
  RUN_DOMAIN_STATUS_LABELS,
  RUN_DOMAIN_STATUS_TAG,
  planVersionStatusLabel,
  planVersionStatusTag,
  type OrderSummaryStatus,
  type RunDomainStatus
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElEmpty,
  ElIcon,
  ElTag
} from 'element-plus'

const overviewStore = useOverviewStore()
const { errors, activeVersions, orderSummary, resources, pendingCandidateCount, domainStatuses } =
  storeToRefs(overviewStore)

const domainStore = useDomainStore()
function humanLabel(domainKey: string | undefined | null): string {
  return domainStore.label(domainKey)
}

/** 时间容缺：后端 WhenWritingNull 会整段省略字段 */
function fmtTime(v: string | null | undefined, pattern = 'YYYY-MM-DD HH:mm'): string {
  return v ? dayjs(v).format(pattern) : '-'
}

/* ===== 订单摘要（4 类 + 合计；后端不提供「仅估算」，故不展示该卡） ===== */
const ORDER_STATUS_COLORS: Record<OrderSummaryStatus, string> = {
  ON_TIME: '#059669',
  AT_RISK: '#f59e0b',
  DELAYED: '#ef4444',
  UNSCHEDULED: '#94a3b8',
  ESTIMATED_ONLY: '#8b5cf6'
}

const orderKpis = computed(() => {
  const s = orderSummary.value
  if (!s) return []
  return [
    { status: 'ON_TIME' as const, label: '按期', count: s.onTimeCount },
    { status: 'DELAYED' as const, label: '延期', count: s.delayedCount },
    { status: 'AT_RISK' as const, label: '风险', count: s.riskCount },
    { status: 'UNSCHEDULED' as const, label: '未排', count: s.unscheduledCount }
  ].map((it) => ({ ...it, color: ORDER_STATUS_COLORS[it.status] }))
})

const totalOrders = computed(() => orderSummary.value?.totalCount ?? null)

const orderShare = (count: number): number =>
  totalOrders.value && totalOrders.value > 0 ? Math.round((count / totalOrders.value) * 100) : 0

/* ===== 排程域状态 ===== */
const issueStatuses = new Set<string>(RUN_DOMAIN_ISSUE_STATUSES)
const issueCount = computed(
  () => domainStatuses.value.filter((d) => issueStatuses.has(d.status)).length
)

const domainLabel = (status: string): string =>
  RUN_DOMAIN_STATUS_LABELS[status as RunDomainStatus] ?? status
const domainTag = (status: string) => RUN_DOMAIN_STATUS_TAG[status as RunDomainStatus] ?? 'info'

/* ===== 资源 ===== */
const bottleneckResources = computed(() => resources.value.filter((r) => r.isBottleneck))

const utilPercent = (r: { utilizationRate?: number | null }): number | null =>
  r.utilizationRate === null || r.utilizationRate === undefined
    ? null
    : Math.round(r.utilizationRate * 100)

function refresh() {
  overviewStore.load()
}

onMounted(refresh)
</script>

<template>
  <div class="aps-overview">
    <!-- ===== 分块错误（单端点失败不清空其它数据） ===== -->
    <ElAlert
      v-for="e in errors"
      :key="e.section"
      type="error"
      :closable="false"
      show-icon
      :title="`${e.section}加载失败：${e.message}`"
    />

    <!-- ===== 订单摘要：合计 + 4 类。取不到时给整块空态，不摆 0 ===== -->
    <div v-if="orderSummary" class="kpi-row">
      <div class="kpi-card total">
        <div class="kpi-label">订单合计</div>
        <div class="kpi-value">{{ totalOrders ?? '-' }}</div>
        <div class="kpi-ratio">本版本全部订单</div>
      </div>
      <div
        v-for="item in orderKpis"
        :key="item.status"
        class="kpi-card"
        :style="{ borderTopColor: item.color }"
      >
        <div class="kpi-label">{{ item.label }}</div>
        <div class="kpi-value" :style="{ color: item.color }">{{ item.count }}</div>
        <div class="kpi-ratio">占 {{ orderShare(item.count) }}%</div>
      </div>
    </div>
    <ElCard v-else class="panel">
      <ElEmpty description="暂无订单摘要，需先完成一次排产运行并正式采用生效版本" />
    </ElCard>

    <!-- ===== 中部两列 ===== -->
    <div class="content-row">
      <!-- 当前生效版本（多排程域） -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>当前生效版本</span>
            <ElTag v-if="activeVersions.length" type="success" size="small">
              {{ activeVersions.length }} 排程域
            </ElTag>
          </div>
        </template>
        <div v-if="!activeVersions.length" class="panel-empty">
          <ElEmpty description="当前没有排程域处于生效（ACTIVE）状态">
            <p class="empty-hint">
              订单摘要、资源负荷、排程域状态都以生效版本为入参。<br />
              需先完成一次排产运行并被正式采用。
            </p>
          </ElEmpty>
        </div>
        <div v-else class="version-list">
          <div v-for="v in activeVersions" :key="v.planVersionId" class="version-card">
            <div class="version-card-head">
              <div class="version-code">{{ v.versionCode }}</div>
              <ElTag :type="planVersionStatusTag(v.status)" size="small">
                {{ planVersionStatusLabel(v.status) }}
              </ElTag>
            </div>
            <ElDescriptions :column="2" size="small" class="version-desc">
              <ElDescriptionsItem label="排程域">{{ humanLabel(v.domainKey) }}</ElDescriptionsItem>
              <ElDescriptionsItem label="激活时间">
                {{ fmtTime(v.activatedAt) }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="计划区间">
                {{ fmtTime(v.planHorizonStart, 'YYYY-MM-DD') }}
                ~
                {{ fmtTime(v.planHorizonEnd, 'YYYY-MM-DD') }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="来源 Run">
                {{ v.sourceScheduleRunId ?? '-' }}
              </ElDescriptionsItem>
            </ElDescriptions>
          </div>
        </div>
      </ElCard>

      <!-- 排程域状态 -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>排程域状态</span>
            <ElTag v-if="issueCount" type="danger" size="small"> {{ issueCount }} 项异常 </ElTag>
          </div>
        </template>
        <div v-if="!domainStatuses.length" class="panel-empty">
          <ElEmpty description="暂无排程域状态" />
        </div>
        <div v-else class="issue-list">
          <div
            v-for="d in domainStatuses"
            :key="d.domainKey"
            class="issue-item"
            :class="{ abnormal: issueStatuses.has(d.status) }"
          >
            <ElTag :type="domainTag(d.status)" size="small">{{ domainLabel(d.status) }}</ElTag>
            <div class="issue-body">
              <div class="issue-domain">{{ humanLabel(d.domainKey) }}</div>
              <div v-if="d.reason" class="issue-msg">{{ d.reason }}</div>
              <div class="issue-time">{{ fmtTime(d.startedAt) }}</div>
            </div>
          </div>
        </div>
      </ElCard>
    </div>

    <!-- ===== 资源利用率 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>资源利用率</span>
          <span v-if="bottleneckResources.length" class="hint-text">
            <ElIcon><Icon icon="vi-mdi:alert" /></ElIcon>
            <strong>{{ bottleneckResources.length }} 个瓶颈资源</strong>
          </span>
        </div>
      </template>

      <div v-if="!resources.length" class="panel-empty">
        <ElEmpty description="暂无资源数据" />
      </div>
      <table v-else class="resource-table">
        <thead>
          <tr>
            <th>资源编码</th>
            <th>名称</th>
            <th>负荷率</th>
            <th>状态</th>
            <th>不可用窗口</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in resources" :key="r.resourceId" :class="{ bottleneck: r.isBottleneck }">
            <td>{{ r.resourceCode }}</td>
            <td>{{ r.resourceName }}</td>
            <td>
              <span v-if="utilPercent(r) === null" class="muted">-</span>
              <div v-else class="util-bar">
                <div
                  class="util-fill"
                  :style="{
                    width: Math.min(utilPercent(r)!, 100) + '%',
                    background:
                      utilPercent(r)! >= 90
                        ? '#ef4444'
                        : utilPercent(r)! >= 70
                          ? '#f59e0b'
                          : '#10b981'
                  }"
                ></div>
                <span class="util-text">{{ utilPercent(r) }}%</span>
              </div>
            </td>
            <td>
              <ElTag v-if="r.isBottleneck" type="danger" size="small">瓶颈</ElTag>
              <ElTag v-else type="success" size="small">正常</ElTag>
            </td>
            <td>
              <span v-if="!r.unavailableWindows.length" class="muted">无</span>
              <div v-else class="window-list">
                <div v-for="(w, wi) in r.unavailableWindows" :key="wi" class="window-item">
                  {{ fmtTime(w.from) }} ~ {{ fmtTime(w.to) }}
                  <span v-if="w.reason" class="window-reason">{{ w.reason }}</span>
                </div>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </ElCard>

    <!-- ===== 底部：待确认候选（整行窄条，不占半屏留白） ===== -->
    <div class="candidate-bar">
      <ElIcon class="candidate-icon"><Icon icon="vi-mdi:clock-alert-outline" /></ElIcon>
      <span class="candidate-label">待确认 Candidate</span>
      <span class="candidate-value">{{ pendingCandidateCount ?? '-' }}</span>
      <span class="candidate-sub">去「候选版本确认」页处理</span>
    </div>
  </div>
</template>

<style lang="less" scoped>
.aps-overview {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

/* 顶部（页面大标题区已按 10-16 要求下线，标题由面包屑与标签页承担） */
.hint-text {
  font-size: 13px;
  color: #475569;
}

.empty-hint {
  margin: 0;
  font-size: 13px;
  line-height: 1.7;
  color: #94a3b8;
}

/* KPI 卡片 */
.kpi-row {
  display: grid;
  grid-template-columns: repeat(5, 1fr);
  gap: 12px;
}

.kpi-card {
  padding: 14px 16px;
  background: #fff;
  border-top: 3px solid #94a3b8;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);

  .kpi-label {
    font-size: 12px;
    color: #94a3b8;
  }

  .kpi-value {
    margin-top: 4px;
    font-size: 24px;
    font-weight: 600;
    color: #1e293b;
  }

  .kpi-ratio {
    margin-top: 2px;
    font-size: 11px;
    color: #cbd5e1;
  }

  &.total {
    background: #f1f5f9;

    .kpi-value {
      color: #475569;
    }
  }
}

/* 中部两列 */
.content-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

/* 卡片 */
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

/* ACTIVE 版本卡片 */
.version-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.version-card {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  &-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 8px;
  }

  .version-code {
    font-weight: 600;
    color: #1e293b;
  }

  .version-desc {
    :deep(.el-descriptions__label) {
      width: 80px;
      color: #94a3b8;
    }

    :deep(.el-descriptions__content) {
      color: #475569;
    }
  }
}

/* 排程域状态 */
.issue-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.issue-item {
  display: flex;
  gap: 12px;
  padding: 10px 12px;
  background: #f8fafc;
  border-left: 3px solid #cbd5e1;
  border-radius: 4px;

  &.abnormal {
    background: #fef2f2;
    border-left-color: #ef4444;
  }
}

.issue-body {
  flex: 1;
}

.issue-domain {
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
}

.issue-msg {
  margin-top: 2px;
  font-size: 13px;
  color: #475569;
}

.issue-time {
  margin-top: 4px;
  font-size: 11px;
  color: #94a3b8;
}

/* 资源表 */
.resource-table {
  width: 100%;
  border-collapse: collapse;

  th,
  td {
    padding: 10px 12px;
    font-size: 13px;
    text-align: left;
    border-bottom: 1px solid #f1f5f9;
  }

  th {
    font-weight: 500;
    color: #64748b;
    background: #f8fafc;
  }

  tbody tr.bottleneck {
    background: #fef2f2;
  }

  tbody tr:hover {
    background: #f8fafc;
  }
}

.util-bar {
  position: relative;
  width: 100%;
  height: 18px;
  overflow: hidden;
  background: #f1f5f9;
  border-radius: 9px;

  .util-fill {
    position: absolute;
    top: 0;
    left: 0;
    height: 100%;
    border-radius: 9px;
    transition: width 0.3s;
  }

  .util-text {
    position: absolute;
    top: 50%;
    left: 50%;
    z-index: 1;
    font-size: 11px;
    font-weight: 500;
    color: #1e293b;
    transform: translate(-50%, -50%);
  }
}

.window-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.window-item {
  font-size: 12px;
  color: #475569;
}

.window-reason {
  margin-left: 6px;
  color: #94a3b8;
}

.muted {
  color: #cbd5e1;
}

/* 底部：待确认候选窄条 */
.candidate-bar {
  display: flex;
  align-items: center;
  padding: 12px 18px;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
  gap: 12px;

  .candidate-icon {
    font-size: 20px;
    color: #3b82f6;
  }

  .candidate-label {
    font-size: 14px;
    color: #475569;
  }

  .candidate-value {
    font-size: 20px;
    font-weight: 600;
    color: #1e293b;
  }

  .candidate-sub {
    margin-left: auto;
    font-size: 12px;
    color: #94a3b8;
  }
}
</style>
