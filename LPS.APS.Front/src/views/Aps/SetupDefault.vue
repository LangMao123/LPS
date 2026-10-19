<!--
  APS V1 4号位 — 默认换型（DEFAULT）规则维护
  依据：frontNew/docs/APS_V1_Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916.md
   - §四 正式默认规则粒度：生产部门 + 大工艺 + 当前工序 + 设备 → DefaultSetupMinutes
   - §五 第二优先：明确产品对规则不存在时命中本规则
   - §19.2 4号位「默认规则维护」

  职责：DEFAULT 规则 CRUD（5 元组：部门/大工艺/工序/设备 + RuleSetVersionId）
  入口联动：从「缺失规则查询」跳转带入 query 预填 4 字段，自动打开新增 Dialog

  页面内已落实的校验：
   - DEFAULT 不带前后产品（表单不渲染产品字段，与 EXACT 互斥）
   - 换型分钟 > 0（Dialog 内拦截）
   - 仅 DRAFT 状态的规则集版本可编辑（其余状态 → 按钮 disabled + Tooltip 说明原因）
   - 同 5 元组重复（EXACT+DEFAULT / DEFAULT+DEFAULT）→ 后端 422 原文透传

  权限：aps.setup.view（查看）+ aps.setup.edit（CRUD）
-->

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { storeToRefs } from 'pinia'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElCol,
  ElEmpty,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElRow,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'
import { useSetupDefaultStore } from '@/store/modules/aps/setupDefault'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import {
  SETUP_RULE_STATUS_META,
  SETUP_VERSION_STATUS_META,
  roleLabelOf,
  type SetupDialogPrefill,
  type SetupRuleDto
} from '@/api/aps-v1'
import { formatUtcDateTimeSec } from '@/utils/datetime'
import SetupDefaultDialog from './components/SetupDefaultDialog.vue'
import { useSetupMasterData } from './composables/useSetupMasterData'

defineOptions({ name: 'ApsSetupDefault' })

const route = useRoute()
const defaultStore = useSetupDefaultStore()
const apsAuth = useApsAuthStore()
const masterData = useSetupMasterData()

const {
  rules,
  selectedRuleSetVersionId,
  ruleSetVersions,
  listLoading,
  versionsLoading,
  actionRunning,
  error,
  currentRuleSetVersion,
  canEdit,
  canWrite,
  ruleCount
} = storeToRefs(defaultStore)

// ===== 过滤 =====
// 10-15 4号位：同 SetupExact —— 部门/大工艺选项改取「当前版本规则里真实出现过」的，
// 不再列 lookups 全量主数据（否则选一个没有规则的部门必然 0 条且无从预判）。
const filterDepartment = ref<string>('')
const filterStage = ref<string>('')

const departmentOptions = computed(() => {
  const used = new Set(rules.value.map((r) => r.departmentCode).filter(Boolean))
  return masterData.departments.value.filter((d) => used.has(d.value))
})

/** 大工艺选项随部门级联收窄 */
const stageOptions = computed(() => {
  const dept = filterDepartment.value
  const used = new Set(
    rules.value
      .filter((r) => !dept || r.departmentCode === dept)
      .map((r) => r.stageCode)
      .filter(Boolean)
  )
  return masterData.stages.value.filter((s) => used.has(s.value))
})

const filteredRules = computed<SetupRuleDto[]>(() => {
  return rules.value.filter((r) => {
    if (filterDepartment.value && r.departmentCode !== filterDepartment.value) return false
    if (filterStage.value && r.stageCode !== filterStage.value) return false
    return true
  })
})

watch(filterDepartment, () => {
  if (filterStage.value && !stageOptions.value.some((s) => s.value === filterStage.value)) {
    filterStage.value = ''
  }
})

watch(selectedRuleSetVersionId, () => {
  filterDepartment.value = ''
  filterStage.value = ''
})

// ===== Dialog 状态 =====
const dialogVisible = ref(false)
const dialogMode = ref<'create' | 'edit'>('create')
const editingRule = ref<SetupRuleDto | null>(null)
const dialogPrefill = ref<SetupDialogPrefill | null>(null)

// ===== 加载 =====
async function loadAll() {
  await defaultStore.loadRuleSetVersions()
  // 默认选中第一个 DRAFT 版本（无 DRAFT 时选第一个）
  const draft =
    ruleSetVersions.value.find((v) => v.status === 'DRAFT') ?? ruleSetVersions.value[0] ?? null
  if (draft) {
    defaultStore.selectRuleSetVersion(draft.id)
    await defaultStore.loadRules(draft.id)
  }
}

onMounted(async () => {
  void masterData.loadAll()
  await loadAll()
  // 「缺失规则查询」跳转预填（4 字段）
  const q = route.query
  if (q.operationCode && canWrite.value && canEdit.value) {
    dialogPrefill.value = {
      departmentCode: String(q.departmentCode ?? ''),
      stageCode: String(q.stageCode ?? ''),
      operationCode: String(q.operationCode),
      resourceCode: String(q.resourceCode ?? '')
    }
    openCreateDialog()
  }
})

async function handleVersionChange(versionId: number | null) {
  defaultStore.selectRuleSetVersion(versionId)
  if (versionId !== null) {
    await defaultStore.loadRules(versionId)
  }
}

// ===== CRUD 操作（仅 DRAFT 可编辑，按钮 disabled + store 双保险） =====
function openCreateDialog() {
  if (!canWrite.value) {
    ElMessage.warning('当前操作者无编辑权限，请联系系统管理员授予')
    return
  }
  if (!canEdit.value) {
    ElMessage.warning('只能编辑草稿（DRAFT）状态的版本')
    return
  }
  dialogMode.value = 'create'
  editingRule.value = null
  dialogVisible.value = true
}

function openEditDialog(rule: SetupRuleDto) {
  if (!canWrite.value || !canEdit.value) return
  dialogMode.value = 'edit'
  editingRule.value = rule
  dialogPrefill.value = null
  dialogVisible.value = true
}

async function handleDelete(rule: SetupRuleDto) {
  if (!canWrite.value || !canEdit.value) return
  let confirmed = false
  try {
    await ElMessageBox.confirm(
      `即将删除默认换型规则：${rule.departmentCode} / ${rule.stageCode} / ${rule.operationCode} / ${rule.resourceCode}（${rule.setupMinutes} 分钟）。删除后该 5 元组的转换将回退 0 分钟兜底。是否继续？`,
      '删除确认',
      {
        type: 'warning',
        confirmButtonText: '确认删除',
        cancelButtonText: '取消',
        confirmButtonClass: 'el-button--danger'
      }
    )
    confirmed = true
  } catch {
    confirmed = false
  }
  if (!confirmed) return

  const ok = await defaultStore.deleteRule(rule.id)
  if (ok) {
    ElMessage.success('默认换型规则删除成功')
  } else if (defaultStore.error) {
    ElMessage.error(defaultStore.error)
  }
}

function handleSaved() {
  dialogPrefill.value = null
}

// ===== 格式化 =====
function getStatusTag(status: string) {
  // 版本选择器传治理六态、规则行传派生三态；两表 DRAFT 同义，其余键不重叠，合并回退安全
  const meta =
    SETUP_RULE_STATUS_META[status as keyof typeof SETUP_RULE_STATUS_META] ??
    SETUP_VERSION_STATUS_META[status as keyof typeof SETUP_VERSION_STATUS_META]
  return meta ?? { label: status, tag: 'info' as const }
}

/** 编辑按钮不可用原因（Tooltip 文案） */
const editDisabledReason = computed<string>(() => {
  if (!canWrite.value) return '当前操作者无编辑权限，请联系系统管理员授予'
  if (!canEdit.value) return '只能编辑草稿（DRAFT）状态的版本'
  return ''
})
</script>

<template>
  <div class="setup-default-page">
    <!-- 页面头部 -->
    <div class="page-header">
      <div class="header-left">
        <h2>默认换型规则</h2>
        <p class="subtitle"
          >无前后产品的兜底换型规则；命中优先级：明确产品转换规则 &gt; 默认换型规则 &gt; 0 分钟</p
        >
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

    <!-- 版本选择 + 过滤 -->
    <ElCard class="selector-card" shadow="never">
      <ElRow :gutter="16" align="middle">
        <ElCol :span="8">
          <div class="selector-label">规则集版本（仅草稿可编辑）</div>
          <ElSelect
            :model-value="selectedRuleSetVersionId ?? undefined"
            placeholder="选择规则集版本"
            :loading="versionsLoading"
            style="width: 100%"
            @change="handleVersionChange"
          >
            <ElOption
              v-for="v in ruleSetVersions"
              :key="v.id"
              :label="`${v.versionCode}（${getStatusTag(v.status).label}）`"
              :value="v.id"
            >
              <span>{{ v.versionCode }}</span>
              <ElTag :type="getStatusTag(v.status).tag" size="small" style="margin-left: 8px">
                {{ getStatusTag(v.status).label }}
              </ElTag>
            </ElOption>
          </ElSelect>
        </ElCol>
        <ElCol :span="5">
          <div class="selector-label">部门筛选</div>
          <ElSelect
            v-model="filterDepartment"
            placeholder="全部"
            no-data-text="当前版本没有配规则的部门"
            clearable
            filterable
            :loading="masterData.loading.value"
            style="width: 100%"
          >
            <ElOption
              v-for="opt in departmentOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </ElCol>
        <ElCol :span="5">
          <div class="selector-label">大工艺筛选</div>
          <ElSelect
            v-model="filterStage"
            placeholder="全部"
            no-data-text="该部门下没有配规则的大工艺"
            clearable
            style="width: 100%"
          >
            <ElOption
              v-for="opt in stageOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </ElCol>
        <ElCol :span="6">
          <div class="selector-label">&nbsp;</div>
          <ElTooltip :content="editDisabledReason" :disabled="!editDisabledReason" placement="top">
            <span>
              <ElButton type="primary" :disabled="!!editDisabledReason" @click="openCreateDialog">
                <Icon icon="vi-ep:plus" class="mr-4" />
                新增默认换型规则
              </ElButton>
            </span>
          </ElTooltip>
        </ElCol>
      </ElRow>
      <div v-if="currentRuleSetVersion && !canEdit" class="version-hint">
        当前版本 {{ currentRuleSetVersion.versionCode }} 状态为
        {{ getStatusTag(currentRuleSetVersion.status).label }}，不可编辑（仅草稿可编辑）
      </div>
    </ElCard>

    <!-- 规则表格 -->
    <ElCard class="table-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span>
            默认换型规则列表
            <ElTag size="small" style="margin-left: 8px">{{ ruleCount }} 条</ElTag>
            <ElTag
              v-if="filteredRules.length !== ruleCount"
              size="small"
              type="info"
              style="margin-left: 4px"
            >
              过滤后 {{ filteredRules.length }} 条
            </ElTag>
          </span>
        </div>
      </template>

      <ElTable
        v-loading="listLoading || actionRunning"
        :data="filteredRules"
        border
        stripe
        empty-text="该版本下暂无默认换型规则（未命中明确产品转换规则的转换将回退 0 分钟兜底）"
      >
        <ElTableColumn type="index" label="#" width="50" />
        <ElTableColumn prop="departmentCode" label="部门" width="110" />
        <ElTableColumn prop="stageCode" label="大工艺" width="110" />
        <ElTableColumn prop="operationCode" label="当前工序" width="110" />
        <ElTableColumn prop="resourceCode" label="设备" width="110" />
        <ElTableColumn prop="setupMinutes" label="换型分钟" width="110" align="right">
          <template #default="{ row }">
            <ElTag
              :type="
                row.setupMinutes > 60 ? 'danger' : row.setupMinutes > 30 ? 'warning' : 'success'
              "
              size="small"
            >
              {{ row.setupMinutes }} 分钟
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="status" label="状态" width="90">
          <template #default="{ row }">
            <ElTag :type="getStatusTag(row.status).tag" size="small">
              {{ getStatusTag(row.status).label }}
            </ElTag>
          </template>
        </ElTableColumn>
        <!-- 09-20 交付回执偏差 #1：remark 字段撤除（备注列移除） -->
        <ElTableColumn prop="updatedAt" label="更新时间" width="170">
          <template #default="{ row }">
            {{ formatUtcDateTimeSec(row.updatedAt ?? row.createdAt) }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="140" fixed="right">
          <template #default="{ row }">
            <ElTooltip
              :content="editDisabledReason"
              :disabled="!editDisabledReason"
              placement="top"
            >
              <span>
                <ElButton
                  type="primary"
                  size="small"
                  link
                  :disabled="!!editDisabledReason"
                  @click="openEditDialog(row)"
                >
                  编辑
                </ElButton>
                <ElButton
                  type="danger"
                  size="small"
                  link
                  :disabled="!!editDisabledReason"
                  @click="handleDelete(row)"
                >
                  删除
                </ElButton>
              </span>
            </ElTooltip>
          </template>
        </ElTableColumn>
      </ElTable>

      <ElEmpty
        v-if="!listLoading && selectedRuleSetVersionId === null"
        description="请先选择规则集版本"
      />
    </ElCard>

    <!-- 创建/编辑 Dialog -->
    <SetupDefaultDialog
      v-model="dialogVisible"
      :mode="dialogMode"
      :rule="editingRule"
      :prefill="dialogPrefill"
      @saved="handleSaved"
    />
  </div>
</template>

<style scoped>
.setup-default-page {
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

.selector-card {
  margin-bottom: 16px;
}

.selector-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.version-hint {
  margin-top: 12px;
  font-size: 12px;
  color: var(--el-color-warning);
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
