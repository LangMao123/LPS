<!--
  APS V1 4号位 — 缺失规则查询
  依据：frontNew/docs/APS_V1_Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916.md
   - §五 第三优先：无明确规则也无默认规则 → Setup = 0 分钟，并记录解释/数据质量事实
   - §18 缺失规则数据治理
   - §19.2 4号位「缺失规则查询」+「0 分钟兜底查询」

  职责：统计排产批次中所有未命中 EXACT / DEFAULT 规则的产品转换对（0 分钟兜底）
  用途：
   - 排查 0 分钟兜底是否合理
   - 指导 EXACT / DEFAULT 规则补全
   - 评估换型规则覆盖率

  输入：排产批次（必填）+ 部门/大工艺/设备（可选筛选）
  输出：部门/大工艺/工序/设备/前产品/后产品/出现次数/最近出现
  出口：去补明确产品转换 / 去补默认换型规则 / 导出 CSV
  统计口径：走后端聚合端点，不在前端二次聚合

  权限：aps.setup.view（只读页面，无写操作）
-->

<script setup lang="ts">
import { computed, onMounted, reactive } from 'vue'
import { useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import dayjs from 'dayjs'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElCol,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInputNumber,
  ElMessage,
  ElOption,
  ElRow,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'
import { useSetupUncoveredStore } from '@/store/modules/aps/setupUncovered'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import type { SetupUncoveredQuery } from '@/api/aps-v1'
import { roleLabelOf, SETUP_SOURCES, SETUP_SOURCE_META } from '@/api/aps-v1'
import { formatUtcDateTimeSec } from '@/utils/datetime'
import { useSetupMasterData } from './composables/useSetupMasterData'

defineOptions({ name: 'ApsSetupUncovered' })

const router = useRouter()
const uncoveredStore = useSetupUncoveredStore()
const apsAuth = useApsAuthStore()
const masterData = useSetupMasterData()

const { stats, listLoading, error, statCount } = storeToRefs(uncoveredStore)

// ===== 查询表单 =====
const queryForm = reactive<SetupUncoveredQuery>({
  runId: 1, // 默认 RunId=1（dev 演示用）
  departmentCode: undefined,
  stageCode: undefined,
  resourceCode: undefined
})

// ===== 下拉源（后端主数据 lookup：大工艺由部门列表的 stageCode 派生） =====
// 10-15 4号位：三个下拉改远程搜索。后端 lookups/{departments,resources} 的 SQL 都是
// `SELECT TOP (200) ... ORDER BY [Code]`、limit 写死且 Controller 不透传
// （MasterDataLookupRepository.cs:87/98 + SetupRuleController.cs:299-347）
// → 不带 search 时只有字母序前 200 条，用户筛不到后面的部门/设备。
const departmentOptions = masterData.departments
const resourceOptions = masterData.resources

const searching = reactive({ department: false, resource: false })

/** 300ms 防抖：连续打字只发最后一次 */
function debounce<A extends unknown[]>(fn: (...args: A) => void) {
  let timer: ReturnType<typeof setTimeout> | undefined
  return (...args: A) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => fn(...args), 300)
  }
}

const onSearchDepartment = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.department = false
    return
  }
  searching.department = true
  void masterData.searchDepartments(kw).finally(() => (searching.department = false))
})
const onSearchResource = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.resource = false
    return
  }
  searching.resource = true
  void masterData.searchResources(kw).finally(() => (searching.resource = false))
})

/** 大工艺按所选部门收窄（部门→大工艺 1:1，stageCode 由后端给） */
const stageOptionsForDepartment = computed(() => {
  const all = masterData.stages.value
  const stageOfDept = masterData.stageOfDepartment(queryForm.departmentCode ?? '')
  if (!stageOfDept) return all
  const narrowed = all.filter((s) => s.value === stageOfDept)
  return narrowed.length ? narrowed : all
})

// ===== 加载 =====
async function loadStats() {
  if (!queryForm.runId) {
    ElMessage.warning('请填写 RunId（必填）')
    return
  }
  await uncoveredStore.loadStats({ ...queryForm })
}

onMounted(() => {
  void masterData.loadAll()
  // 默认加载一次（dev 演示用）
  void loadStats()
})

// ===== 出口操作 =====

/** 去补明确产品转换 → 跳明确产品转换页，预填 6 字段 */
function goToExact(row: (typeof stats.value)[number]) {
  void router.push({
    path: '/aps/setup/exact',
    query: {
      departmentCode: row.departmentCode,
      stageCode: row.stageCode,
      operationCode: row.operationCode,
      resourceCode: row.resourceCode,
      fromMaterialCode: row.fromMaterialCode,
      toMaterialCode: row.toMaterialCode
    }
  })
}

/** 去补默认换型规则 → 跳默认换型规则页，预填 4 字段 */
function goToDefault(row: (typeof stats.value)[number]) {
  void router.push({
    path: '/aps/setup/default',
    query: {
      departmentCode: row.departmentCode,
      stageCode: row.stageCode,
      operationCode: row.operationCode,
      resourceCode: row.resourceCode
    }
  })
}

/** 导出 CSV */
function handleExportCsv() {
  const csv = uncoveredStore.exportCsv()
  const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' })
  const link = document.createElement('a')
  link.href = URL.createObjectURL(blob)
  link.download = `setup-uncovered-${dayjs().format('YYYYMMDD-HHmmss')}.csv`
  link.click()
  URL.revokeObjectURL(link.href)
  ElMessage.success(`已导出 ${statCount.value} 条记录`)
}
</script>

<template>
  <div class="setup-uncovered-page">
    <!-- 页面头部 -->
    <div class="page-header">
      <div class="header-left">
        <h2>缺失规则查询</h2>
        <p class="subtitle">查询未命中换型规则的产品转换（按 0 分钟兜底）</p>
      </div>
      <div class="header-right">
        <ElTooltip
          :content="`当前操作者：${apsAuth.userInfo?.userCode ?? '—'}（${apsAuth.roles.map(roleLabelOf).join('、')}）`"
        >
          <ElTag type="info" size="small">{{ apsAuth.userInfo?.userCode ?? '—' }}</ElTag>
        </ElTooltip>
      </div>
    </div>

    <!-- 错误提示 -->
    <ElAlert
      v-if="error"
      type="error"
      :title="error"
      :closable="false"
      show-icon
      class="error-alert"
    />

    <!-- 查询表单 -->
    <ElCard class="query-card" shadow="never">
      <ElForm :model="queryForm" inline>
        <ElFormItem label="排产批次" required>
          <ElInputNumber
            v-model="queryForm.runId"
            :min="1"
            :step="1"
            controls-position="right"
            style="width: 120px"
          />
        </ElFormItem>
        <ElFormItem label="部门">
          <ElSelect
            v-model="queryForm.departmentCode"
            placeholder="全部（可输入编码搜索）"
            clearable
            filterable
            remote
            :remote-method="onSearchDepartment"
            :loading="searching.department"
            style="width: 180px"
          >
            <ElOption
              v-for="opt in departmentOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="大工艺">
          <ElSelect
            v-model="queryForm.stageCode"
            placeholder="全部"
            clearable
            filterable
            style="width: 180px"
          >
            <ElOption
              v-for="opt in stageOptionsForDepartment"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="设备">
          <ElSelect
            v-model="queryForm.resourceCode"
            placeholder="全部（可输入编码搜索）"
            clearable
            filterable
            remote
            :remote-method="onSearchResource"
            :loading="searching.resource"
            style="width: 180px"
          >
            <ElOption
              v-for="opt in resourceOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem>
          <ElButton type="primary" :loading="listLoading" @click="loadStats">
            <Icon icon="vi-ep:search" class="mr-4" />
            查询
          </ElButton>
          <ElButton @click="uncoveredStore.reset()">重置</ElButton>
        </ElFormItem>
      </ElForm>
    </ElCard>

    <!-- KPI 条 -->
    <ElRow :gutter="16" class="kpi-row">
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ statCount }}</div>
          <div class="kpi-label">未覆盖转换对</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ Object.keys(uncoveredStore.statsByDepartment).length }}</div>
          <div class="kpi-label">涉及部门</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ Object.keys(uncoveredStore.statsByStage).length }}</div>
          <div class="kpi-label">涉及工艺</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value danger">0 分钟</div>
          <div class="kpi-label">兜底换型时间</div>
        </ElCard>
      </ElCol>
    </ElRow>

    <!-- 换型来源 5 值说明（标签取自 SETUP_SOURCE_META，与 Gantt.vue Drawer、总览页一致） -->
    <ElAlert type="info" :closable="false" show-icon class="legend-alert">
      <template #title>
        换型来源五值说明（本页统计仅含「无规则兜底」：有前后产品但无规则 → 0 分钟兜底；
        「冷启动首单」：班头首单 / 冷启动，属正常生产事实，<strong>不进</strong>未覆盖统计）
      </template>
      <div class="legend-row">
        <ElTag
          v-for="s in SETUP_SOURCES"
          :key="s"
          size="small"
          :type="SETUP_SOURCE_META[s].tag"
          effect="dark"
        >
          {{ SETUP_SOURCE_META[s].label }}
        </ElTag>
      </div>
    </ElAlert>

    <!-- 统计列表 -->
    <ElCard class="table-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span>未覆盖的产品转换</span>
          <ElButton
            type="success"
            size="small"
            :disabled="statCount === 0"
            @click="handleExportCsv"
          >
            <Icon icon="vi-ep:download" class="mr-4" />
            导出 CSV
          </ElButton>
        </div>
      </template>

      <ElTable
        v-loading="listLoading"
        :data="stats"
        border
        stripe
        empty-text="无未覆盖的产品转换对（所有转换均已命中明确产品转换规则或默认换型规则）"
      >
        <ElTableColumn type="index" label="#" width="50" />
        <ElTableColumn prop="departmentCode" label="部门" width="120" />
        <ElTableColumn prop="stageCode" label="大工艺" width="120" />
        <ElTableColumn prop="operationCode" label="当前工序" width="120" />
        <ElTableColumn prop="resourceCode" label="设备" width="120" />
        <ElTableColumn prop="fromMaterialCode" label="前产品" width="140" />
        <ElTableColumn prop="toMaterialCode" label="后产品" width="140" />
        <ElTableColumn prop="count" label="出现次数" width="100" align="right">
          <template #default="{ row }">
            <ElTag
              :type="row.count > 10 ? 'danger' : row.count > 5 ? 'warning' : 'info'"
              size="small"
            >
              {{ row.count }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="lastOccurrence" label="最近出现" width="180">
          <template #default="{ row }">
            {{ formatUtcDateTimeSec(row.lastOccurrence) }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="200" fixed="right">
          <template #default="{ row }">
            <ElButton type="primary" size="small" link @click="goToExact(row)">
              去补明确产品转换
            </ElButton>
            <ElButton type="warning" size="small" link @click="goToDefault(row)">
              去补默认换型规则
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>

      <ElEmpty v-if="!listLoading && statCount === 0" description="无未覆盖的产品转换对" />
    </ElCard>
  </div>
</template>

<style scoped>
.setup-uncovered-page {
  padding: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
}

.header-left h2 {
  margin: 0 0 4px 0;
  font-size: 20px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.header-left .subtitle {
  margin: 0;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.error-alert {
  margin-bottom: 16px;
}

.query-card {
  margin-bottom: 16px;
}

.kpi-row {
  margin-bottom: 16px;
}

.kpi-card {
  text-align: center;
}

.kpi-value {
  font-size: 28px;
  font-weight: 700;
  color: var(--el-color-primary);
  margin-bottom: 4px;
}

.kpi-value.danger {
  color: var(--el-color-danger);
}

.kpi-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.table-card {
  margin-bottom: 16px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.mr-4 {
  margin-right: 4px;
}
</style>
