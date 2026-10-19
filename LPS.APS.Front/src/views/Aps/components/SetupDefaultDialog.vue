<!--
  APS V1 4号位 — 默认换型（DEFAULT）规则创建/编辑 Dialog
  依据：Setup 专项指导 v1.2 §四 / §19.2「默认规则维护」

  职责：
   - 5 元组表单：Department/Stage/Operation/Resource + SetupMinutes（+ RuleSetVersionId）
   - UI 校验：
       表单不渲染 FromMaterial/ToMaterial 字段（DEFAULT 必须都空，与 EXACT 互斥）
       SetupMinutes <= 0 → 提交按钮 disabled + 提示
       同 5 元组重复（EXACT+DEFAULT / DEFAULT+DEFAULT）→ 后端 422，前端 ElMessage.error 原文透传

  复用：StrategyDraftDialog.vue 范式（props/emits 契约 + validate().catch(()=>false)）
  权限：仅 aps.setup.edit 持有者可提交（父页面已做按钮显隐，此处双保险）

  09-20 3号位 交付回执偏差适配：偏差 #1 remark 撤除（表单/载荷不含备注）

  09-20 lps 代码同步核实（Id-口径终定 v1.1，SetupRuleDtos.cs）：
   - 写输入提交 Id（productionDepartmentId/resourceId），大工艺/工序传 Code；UI 层仍按 Code 展示
   - 后端 [JsonExtensionData]：DEFAULT 载荷出现含 "material" 键 → 422（前端结构上不可能发出）

  10-14 下拉接后端主数据（此前部门/大工艺/工序/设备全为写死的假占位值）：
   - 部门/设备  GET /api/governance/lookups/{departments,resources}（含 masterId）
   - 大工艺      无独立端点，由部门列表的 stageCode 去重派生，并按所选部门收窄
   - 工序        DEFAULT 无产品维度，后端也无全量工序端点 → 取该部门若干产品的工艺路线并集，
                 下拉 allow-create 允许手工输入兜底
   - 编辑态 Id 仍优先取 SetupRuleDto 双返回（权威）；解析不到时禁止提交

  10-15 4号位 生产可用化（Setup 专项指导 v1.2 §19.2「一台或多台设备」）：
  - 部门 / 设备 下拉改远程搜索。后端 lookups/departments 与 lookups/resources 都是
    `SELECT TOP (200) ... ORDER BY [Code]`、limit 写死且 Controller 不透传
    （MasterDataLookupRepository.cs:87/98 + SetupRuleController.cs:299-347），
    不带 search 时永远只有字母序前 200 条，用户配不出后面的设备。
  - 大工艺由所选部门自动带出（部门→大工艺 1:1，stageCode 由后端给出），仍可手动改。
  - create 模式设备可多选，提交**串行**逐台建单（后端 create 是读 ContentSnapshotJson →
    加一条 → 整列写回的 read-modify-write，并发必丢规则）；部分失败如实报、不吞。
  - DEFAULT 无 eligibility 交集可用（§19.2 默认规则段未定义资格过滤），
    故设备 label 只追加后端真实给的所属部门，**不据此收窄**（后端不校验 dept↔设备部门一致）。
-->

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElForm,
  ElFormItem,
  ElInputNumber,
  ElMessage,
  ElOption,
  ElSelect,
  ElTooltip
} from 'element-plus'
import type { SetupDialogPrefill, SetupRuleDto, SetupRuleDefaultInput } from '@/api/aps-v1'
import { opmApi } from '@/api/aps-v1'
import { useSetupDefaultStore } from '@/store/modules/aps/setupDefault'
import { useSetupMasterData, type SetupLookupOption } from '../composables/useSetupMasterData'

defineOptions({ name: 'SetupDefaultDialog' })

interface Props {
  /** v-model 显隐 */
  modelValue: boolean
  /** create = 新增 / edit = 编辑 */
  mode: 'create' | 'edit'
  /** 编辑对象（mode=edit 时必填） */
  rule?: SetupRuleDto | null
  /** 预填字段（从「缺失规则查询」跳转带入，Code 形态） */
  prefill?: SetupDialogPrefill | null
}

const props = withDefaults(defineProps<Props>(), {
  rule: null,
  prefill: null
})

const emit = defineEmits<{
  (e: 'update:modelValue', value: boolean): void
  (e: 'saved'): void
}>()

const defaultStore = useSetupDefaultStore()
const masterData = useSetupMasterData()

/** create 模式设备可多选（§19.2「一台或多台设备」）；edit 模式一条规则物理上只对应一台设备 */
const isCreate = computed(() => props.mode === 'create')

// ===== 表单（UI 层 Code-口径，提交时转 Id-口径 v1.1；无 FromMaterial/ToMaterial 字段 —
// 红线：DEFAULT 必须都空，后端 JsonExtensionData 对含 "material" 键直接 422） =====
const formRef = ref<InstanceType<typeof ElForm>>()
const form = reactive({
  ruleSetVersionId: 0,
  departmentCode: '',
  stageCode: '',
  operationCode: '',
  /** 选中的设备编码；create 可多值，edit 恒为单元素数组 */
  resourceCodes: [] as string[],
  setupMinutes: 30
})

/**
 * ElSelect 在 multiple 切换时 v-model 语义不同（数组 vs 标量），用计算属性桥接，
 * 保证表单内只有 form.resourceCodes 一个真源。
 */
const resourceSelection = computed<string | string[]>({
  get: () => (isCreate.value ? form.resourceCodes : (form.resourceCodes[0] ?? '')),
  set: (v) => {
    form.resourceCodes = isCreate.value
      ? Array.isArray(v)
        ? v
        : [v]
      : [String(v ?? '')].filter(Boolean)
  }
})

const formRules = {
  departmentCode: [{ required: true, message: '部门必填', trigger: 'change' }],
  stageCode: [{ required: true, message: '大工艺必填', trigger: 'change' }],
  operationCode: [{ required: true, message: '当前工序必填', trigger: 'change' }]
}

/** 设备必填挂在数组上：create 要至少一台，edit 要恰好一台 */
const deviceRules = computed(() =>
  isCreate.value
    ? [{ required: true, message: '设备必填（可多选）', trigger: 'change' }]
    : [{ required: true, message: '设备必填', trigger: 'change' }]
)

// ===== 远程搜索（后端 lookups 均 TOP 200 写死，见 useSetupMasterData 头注释） =====
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

// ===== 下拉源（后端主数据 lookup；Id-口径 v1.1 选项带 id 供写输入提交） =====
const departmentOptions = masterData.departments

/** 设备选项追加后端给的真实所属部门（DEFAULT 无 eligibility 交集可用，只展示不据此收窄） */
const resourceOptions = computed<SetupLookupOption[]>(() =>
  masterData.resources.value.map((r) => {
    const deptCode = masterData.departmentCodeOfResource(r.value)
    const base = r.label
    return deptCode ? { ...r, label: `${base} · ${deptCode}` } : r
  })
)

/** 大工艺按所选部门收窄（部门选项自带 stageCode）；未选部门时给全部 */
const stageOptions = computed(() => {
  const all = masterData.stages.value
  const stageOfDept = masterData.stageOfDepartment(form.departmentCode)
  if (!stageOfDept) return all
  const narrowed = all.filter((s) => s.value === stageOfDept)
  return narrowed.length ? narrowed : all
})

/**
 * 选部门 → 自动带出大工艺。
 * 部门→大工艺在后端是 1:1（DepartmentLookupItem.stageCode 即该部门唯一大工艺），
 * 手工让用户再选一次只能选到同一个值，或选到不属于该部门的错值。
 * 仍保留手动改的余地：后端不强制 dept↔stage 一致，前端不越权锁死。
 */
watch(
  () => form.departmentCode,
  (code) => {
    const stage = masterData.stageOfDepartment(code)
    if (stage) form.stageCode = stage
  }
)

/**
 * 工序候选：DEFAULT 规则不含产品维度，后端也无「全部工序」列表端点
 * （routing-operations 只接受单个 materialId）→ 取该部门下若干产品的工艺路线并集。
 * 并集不完整，故下拉允许手工输入工序编码兜底。
 */
const OPERATION_SAMPLE_SIZE = 5
const operationOptions = ref<{ label: string; value: string }[]>([])
const operationsLoading = ref(false)

async function loadOperations() {
  const deptId = masterData.departmentIdByCode(form.departmentCode)
  const sample = masterData.materials.value
    .map((m) => m.id)
    .filter((id): id is number => id != null)
    .slice(0, OPERATION_SAMPLE_SIZE)
  if (!sample.length) {
    operationOptions.value = []
    return
  }
  operationsLoading.value = true
  try {
    const results = await Promise.all(sample.map((id) => opmApi.listOperations(id).catch(() => [])))
    const merged = new Map<string, string>()
    for (const ops of results) {
      for (const op of ops) {
        if (deptId != null && op.productionDepartmentId !== deptId) continue
        if (merged.has(op.operationCode)) continue
        merged.set(
          op.operationCode,
          op.operationName ? `${op.operationCode}（${op.operationName}）` : op.operationCode
        )
      }
    }
    operationOptions.value = [...merged].map(([value, label]) => ({ label, value }))
  } catch {
    operationOptions.value = []
  } finally {
    operationsLoading.value = false
  }
}

/** Code→Id 解析（Id-口径 v1.1：写输入提交 productionDepartmentId/resourceId）
 *  编辑态优先取 DTO 双返回 Id（权威）；新建态查后端主数据 lookup。 */
function resolveDepartmentId(code: string): number | undefined {
  if (props.mode === 'edit' && props.rule && props.rule.departmentCode === code) {
    return props.rule.productionDepartmentId
  }
  return masterData.departmentIdByCode(code)
}

function resolveResourceId(code: string): number | undefined {
  if (props.mode === 'edit' && props.rule && props.rule.resourceCode === code) {
    return props.rule.resourceId
  }
  return masterData.resourceIdByCode(code)
}

// ===== 红线拦截 =====
/** 提交拦截原因（空串 = 可提交） */
const submitBlockReason = computed<string>(() => {
  if (
    !form.departmentCode ||
    !form.stageCode ||
    !form.operationCode ||
    !form.resourceCodes.length
  ) {
    return '请填写完整 5 元组基础字段（部门/大工艺/工序/设备）'
  }
  if (form.setupMinutes <= 0) {
    return '换型分钟数必须大于 0（0 分钟由兜底自动命中，不需建规则）'
  }
  return ''
})

// ===== 打开时初始化 =====
watch(
  () => props.modelValue,
  async (visible) => {
    if (!visible) return
    if (props.mode === 'edit' && props.rule) {
      form.ruleSetVersionId = props.rule.ruleSetVersionId
      form.departmentCode = props.rule.departmentCode ?? ''
      form.stageCode = props.rule.stageCode
      form.operationCode = props.rule.operationCode
      form.resourceCodes = props.rule.resourceCode ? [props.rule.resourceCode] : []
      form.setupMinutes = props.rule.setupMinutes
    } else {
      form.ruleSetVersionId = defaultStore.selectedRuleSetVersionId ?? 0
      form.departmentCode = props.prefill?.departmentCode ?? ''
      form.stageCode = props.prefill?.stageCode ?? ''
      form.operationCode = props.prefill?.operationCode ?? ''
      form.resourceCodes = props.prefill?.resourceCode ? [props.prefill.resourceCode] : []
      form.setupMinutes = 30
    }
    await masterData.loadAll()
    // 大工艺：部门已定但因主数据晚到才拿到 stageCode 时补带一次
    if (form.departmentCode && !form.stageCode) {
      form.stageCode = masterData.stageOfDepartment(form.departmentCode) ?? ''
    }
    await loadOperations()
    formRef.value?.clearValidate()
  }
)

/** 用户改部门 → 原工序候选属于旧部门，清空重选 */
async function onDepartmentChange() {
  form.operationCode = ''
  await loadOperations()
}

// ===== 提交 =====
async function handleSubmit() {
  if (submitBlockReason.value) {
    ElMessage.warning(submitBlockReason.value)
    return
  }
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  // Id-口径转换（v1.1：请求提交 Id；解析失败 = 主数据缺口，禁止发 Code 伪载荷）
  const productionDepartmentId = resolveDepartmentId(form.departmentCode)
  if (productionDepartmentId == null) {
    ElMessage.error('无法解析部门 Id，请检查部门输入')
    return
  }
  const resourceIds: number[] = []
  const unresolved: string[] = []
  for (const code of form.resourceCodes) {
    const id = resolveResourceId(code)
    if (id == null) unresolved.push(code)
    else resourceIds.push(id)
  }
  if (unresolved.length) {
    // 设备在列表里但主数据查不到 Id —— 多半是 lookups 的 TOP 200 截断
    ElMessage.error(`无法解析设备 Id：${unresolved.join('、')}。请换个关键词重新搜索该设备后再保存`)
    return
  }

  // ⚠️ 载荷严禁出现含 "material" 的键（后端 JsonExtensionData 拦截 → 422）
  const buildInput = (resourceId: number): SetupRuleDefaultInput => ({
    ruleSetVersionId: form.ruleSetVersionId,
    productionDepartmentId,
    stageCode: form.stageCode,
    operationCode: form.operationCode,
    resourceId,
    setupMinutes: form.setupMinutes
  })

  if (props.mode === 'edit' && props.rule) {
    const ok = await defaultStore.updateRule(props.rule.id, buildInput(resourceIds[0]))
    if (ok) {
      ElMessage.success('默认换型规则更新成功')
      emit('update:modelValue', false)
      emit('saved')
    } else if (defaultStore.error) {
      // 后端 422（5 元组重复）等错误原文透传
      ElMessage.error(defaultStore.error)
    }
    return
  }

  // 多设备：必须串行。后端 create 是「读 ContentSnapshotJson → 加一条 → 整列写回」的
  // read-modify-write，并发请求后写的会覆盖先写的规则（换型规则只存那一列）。
  let okCount = 0
  let firstError = ''
  for (let i = 0; i < resourceIds.length; i++) {
    const created = await defaultStore.createRule(buildInput(resourceIds[i]))
    if (created) {
      okCount++
    } else {
      firstError = defaultStore.error ?? '未知错误'
      // 保留失败及未尝试的设备，让用户调整后能直接重试，不必重填整张表
      form.resourceCodes = form.resourceCodes.slice(i)
      break
    }
  }
  if (okCount === resourceIds.length) {
    ElMessage.success(`默认换型规则创建成功（${okCount} 条）`)
    emit('update:modelValue', false)
    emit('saved')
  } else {
    ElMessage.error(
      `已创建 ${okCount} 条，第 ${okCount + 1} 台设备失败：${firstError}。剩余未创建，请调整后重试`
    )
  }
}

function handleClose() {
  emit('update:modelValue', false)
}

const dialogTitle = computed(() =>
  props.mode === 'edit' ? '编辑默认换型规则' : '新增默认换型规则'
)
</script>

<template>
  <ElDialog
    :model-value="modelValue"
    :title="dialogTitle"
    width="640px"
    :close-on-click-modal="false"
    @update:model-value="handleClose"
  >
    <!-- 规则提示（DEFAULT 专项） -->
    <ElAlert type="warning" :closable="false" show-icon style="margin-bottom: 16px">
      <template #title>默认换型规则要求</template>
      默认换型规则不填前后产品（前产品与后产品必须都为空，与明确转换规则互斥）；换型分钟数必须大于
      0。
    </ElAlert>

    <ElAlert
      v-if="masterData.truncated.value"
      type="info"
      :closable="false"
      show-icon
      style="margin-bottom: 16px"
    >
      <template #title>主数据较多，请用编码搜索</template>
      部门和设备一次只带出前一批，请直接输入编码或名称片段搜索后再选。
    </ElAlert>

    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px">
      <ElFormItem label="部门" prop="departmentCode">
        <ElSelect
          v-model="form.departmentCode"
          placeholder="输入编码搜索部门"
          filterable
          remote
          :remote-method="onSearchDepartment"
          :loading="searching.department || masterData.loading.value"
          style="width: 100%"
          @change="onDepartmentChange"
        >
          <ElOption
            v-for="opt in departmentOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
      </ElFormItem>
      <ElFormItem label="大工艺" prop="stageCode">
        <ElSelect
          v-model="form.stageCode"
          placeholder="选部门后自动带出，可改"
          filterable
          style="width: 100%"
        >
          <ElOption
            v-for="opt in stageOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
      </ElFormItem>
      <ElFormItem label="当前工序" prop="operationCode">
        <ElSelect
          v-model="form.operationCode"
          placeholder="选择当前工序（唯一工序维度）"
          filterable
          allow-create
          default-first-option
          :loading="operationsLoading"
          style="width: 100%"
        >
          <ElOption
            v-for="opt in operationOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
        <div class="eligibility-hint">
          候选来自该部门产品的工艺路线；找不到的工序可直接输入编码
        </div>
      </ElFormItem>
      <ElFormItem label="设备" prop="resourceCodes" :rules="deviceRules">
        <ElSelect
          v-model="resourceSelection"
          :multiple="isCreate"
          collapse-tags
          collapse-tags-tooltip
          :max-collapse-tags="5"
          :placeholder="isCreate ? '输入编码搜索设备，可多选' : '输入编码搜索设备'"
          filterable
          remote
          :remote-method="onSearchResource"
          :loading="searching.resource || masterData.loading.value"
          style="width: 100%"
        >
          <ElOption
            v-for="opt in resourceOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
        <div v-if="isCreate" class="eligibility-hint"> 可一次多选，每台设备各建一条默认规则 </div>
      </ElFormItem>

      <ElFormItem label="换型分钟数" required>
        <ElInputNumber
          v-model="form.setupMinutes"
          :min="0"
          :step="5"
          controls-position="right"
          style="width: 180px"
        />
        <span class="unit-label">分钟</span>
        <div v-if="form.setupMinutes <= 0" class="redline-hint"> 换型分钟数必须大于 0 </div>
      </ElFormItem>
    </ElForm>

    <template #footer>
      <ElButton @click="handleClose">取消</ElButton>
      <ElTooltip :content="submitBlockReason" :disabled="!submitBlockReason" placement="top">
        <span>
          <ElButton
            type="primary"
            :loading="defaultStore.actionRunning"
            :disabled="!!submitBlockReason"
            @click="handleSubmit"
          >
            {{ mode === 'edit' ? '保存修改' : '创建规则' }}
          </ElButton>
        </span>
      </ElTooltip>
    </template>
  </ElDialog>
</template>

<style scoped>
.redline-hint {
  font-size: 12px;
  color: var(--el-color-danger);
  line-height: 1.5;
  margin-top: 4px;
}

.unit-label {
  margin-left: 8px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>
