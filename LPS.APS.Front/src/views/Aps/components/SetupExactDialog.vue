<!--
  APS V1 4号位 — 明确产品转换（EXACT）规则创建/编辑 Dialog
  依据：Setup 专项指导 v1.2 §五 第一优先 / §19.2「明确产品转换维护」

  职责：
   - 7 元组表单：Department/Stage/Operation/Resource/FromMaterial/ToMaterial + SetupMinutes（+ RuleSetVersionId）
   - 共同合法设备推荐算法 UI 流程：
       选定 OperationCode + FromMaterialCode + ToMaterialCode
       → 调 GET /api/governance/operation-resource-eligibility
       → 设备下拉只展示共同合法设备（非共同设备不可选）
   - UI 校验：
       FromMaterial == ToMaterial → 提交按钮 disabled + 提示（同产品连续 = 0 分钟，不存规则）
       SetupMinutes <= 0 → 提交按钮 disabled + 提示
       FromMaterial/ToMaterial 任一空 → 提交按钮 disabled（EXACT 必填）
       设备不在共同合法设备列表 → 提交按钮 disabled
       重复 7 元组 → 后端 422，前端 ElMessage.error 原文透传

  复用：StrategyDraftDialog.vue 范式（props/emits 契约 + validate().catch(()=>false)）
  权限：仅 aps.setup.edit 持有者可提交（父页面已做按钮显隐，此处双保险）

  09-20 3号位 交付回执偏差适配：
   - 偏差 #1：remark 撤除（表单/载荷不含备注）
   - 偏差 #5：eligibility 传 Material.Id（内部主键非 Code）；编辑态优先取 DTO 双返回 fromMaterialId/toMaterialId

  09-20 lps 代码同步核实（Id-口径终定 v1.1，SetupRuleDtos.cs）：
   - 写输入提交 Id（productionDepartmentId/resourceId/fromMaterialId/toMaterialId），大工艺/工序传 Code
   - UI 层下拉/红线仍按 Code（双返回回带展示）；提交时 resolve*Id() 转换

  10-14 下拉接后端主数据（此前部门/大工艺/工序/设备/产品全为写死的假占位值）：
   - 部门/设备/产品  GET /api/governance/lookups/{departments,resources,materials}（含 masterId）
   - 大工艺          无独立端点，由部门列表的 stageCode 去重派生，并按所选部门收窄
   - 工序            GET /api/governance/routing-operations?materialId=（工序归属产品工艺路线，
                     故表单顺序为 前产品 → 工序；换前产品会清空已选工序）
   - 编辑态 Id 仍优先取 SetupRuleDto 双返回（权威）；解析不到时禁止提交，不发 Code 伪载荷

  10-15 4号位 生产可用化（Setup 专项指导 v1.2 §19.2「选择一台或多台设备」）：
  - 设备下拉改由 GET /operation-resource-eligibility 的 resources 明细驱动。该集合**不受
    lookups/resources 的 TOP 200 截断**且已按 Department Scope 过滤，是设备下拉的权威源；
    改前从 masterData.resources（字母序前 200）取，用户配不出 200 之外的设备，
    且点保存只会得到「无法解析主数据 Id」而无从预判。
  - 部门 / 前产品 / 后产品 三个下拉改远程搜索（后端三个 lookup 均 TOP 200 写死，
    物料实测每个字母档都恰好 200 条），空关键词回落首屏。
  - 大工艺由所选部门自动带出（部门→大工艺 1:1，stageCode 由后端给出），仍可手动改。
  - create 模式设备可多选，提交**串行**逐台建单（后端 create 是读 ContentSnapshotJson →
    加一条 → 整列写回的 read-modify-write，并发必丢规则）；部分失败如实报、不吞。
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
  ElTag,
  ElTooltip
} from 'element-plus'
import {
  opmApi,
  setupApi,
  type OperationResourceEligibilityDto,
  type SetupDialogPrefill,
  type SetupRuleDto,
  type SetupRuleExactInput
} from '@/api/aps-v1'
import { useSetupExactStore } from '@/store/modules/aps/setupExact'
import { useSetupMasterData, type SetupLookupOption } from '../composables/useSetupMasterData'

defineOptions({ name: 'SetupExactDialog' })

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

const exactStore = useSetupExactStore()
const masterData = useSetupMasterData()

/** create 模式设备可多选（§19.2「一台或多台设备」）；edit 模式一条规则物理上只对应一台设备 */
const isCreate = computed(() => props.mode === 'create')

// ===== 表单（UI 层 Code-口径；提交时转 Id-口径 — v1.1 请求提交 Id / 响应回带 Code） =====
const formRef = ref<InstanceType<typeof ElForm>>()
const form = reactive({
  ruleSetVersionId: 0,
  departmentCode: '',
  stageCode: '',
  operationCode: '',
  /** 选中的设备编码；create 可多值，edit 恒为单元素数组 */
  resourceCodes: [] as string[],
  fromMaterialCode: '',
  toMaterialCode: '',
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
  operationCode: [{ required: true, message: '当前工序必填', trigger: 'change' }],
  fromMaterialCode: [
    { required: true, message: '前产品必填（明确转换规则 7 元组）', trigger: 'change' }
  ],
  toMaterialCode: [
    { required: true, message: '后产品必填（明确转换规则 7 元组）', trigger: 'change' }
  ]
}

/** 设备必填挂在数组上：create 要至少一台，edit 要恰好一台 */
const deviceRules = computed(() =>
  isCreate.value
    ? [{ required: true, message: '设备必填（可多选）', trigger: 'change' }]
    : [{ required: true, message: '设备必填', trigger: 'change' }]
)

// ===== 远程搜索（后端三个 lookup 均 TOP 200 写死，见 useSetupMasterData 头注释） =====
const searching = reactive({ department: false, material: false, resource: false })

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
const onSearchMaterial = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.material = false
    return
  }
  searching.material = true
  void masterData.searchMaterials(kw).finally(() => (searching.material = false))
})

// ===== 下拉源（后端主数据 lookup；Id-口径 v1.1 选项带 id 供写输入提交） =====
const departmentOptions = masterData.departments
const materialOptions = masterData.materials

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
 * 仍保留手动改的余地：后端 ValidateCommon 不校验 dept↔stage 一致，前端不越权锁死。
 */
watch(
  () => form.departmentCode,
  (code) => {
    const stage = masterData.stageOfDepartment(code)
    if (stage) form.stageCode = stage
  }
)

/** 工序按前产品的工艺路线派生（GET /api/governance/routing-operations?materialId=） */
const operationOptions = ref<{ label: string; value: string }[]>([])
const operationsLoading = ref(false)

async function loadOperations() {
  const fromMaterialId = masterData.materialIdByCode(form.fromMaterialCode)
  if (fromMaterialId == null) {
    operationOptions.value = []
    return
  }
  operationsLoading.value = true
  try {
    const ops = await opmApi.listOperations(fromMaterialId)
    operationOptions.value = ops.map((op) => ({
      label: op.operationName ? `${op.operationCode}（${op.operationName}）` : op.operationCode,
      value: op.operationCode
    }))
  } catch {
    operationOptions.value = []
  } finally {
    operationsLoading.value = false
  }
}

/** 用户改前产品 → 原工序属于旧产品的工艺路线，清空重选 */
async function onFromMaterialChange() {
  form.operationCode = ''
  await loadOperations()
  await loadEligibility()
}

// ===== 共同合法设备推荐 =====
/** 后端返回的共同合法设备明细（权威源：不限 TOP 200、已按 Department Scope 过滤） */
const eligibilityResources = ref<NonNullable<OperationResourceEligibilityDto['resources']> | null>(
  null
)
const eligibilityLoading = ref(false)
const eligibilityError = ref<string | null>(null)

/** 共同合法设备 Code 集合（红线校验用；null = 尚未算出或查询失败） */
const eligibleResourceCodes = computed<string[] | null>(() =>
  eligibilityResources.value ? eligibilityResources.value.map((r) => r.resourceCode) : null
)

/**
 * 设备下拉源：已算出共同合法设备时**直接用后端明细**（含真实名称 + 所属部门/大工艺 + Id）。
 * 改前是从 masterData.resources（lookups/resources 的字母序前 200）里过滤，
 * 于是交集里的设备若不在那 200 条内，下拉根本列不出来，用户配不出规则。
 * 未算出时回落到 masterData 全量（尚未选工序/前后产品，属正常中间态）。
 */
const resourceOptions = computed<SetupLookupOption[]>(() => {
  if (!eligibilityResources.value) return masterData.resources.value
  return eligibilityResources.value.map((r) => ({
    label: [
      r.resourceName ? `${r.resourceCode}（${r.resourceName}）` : r.resourceCode,
      // 设备所属部门/大工艺是后端给的真实值，只展示不据此收窄——
      // 后端 ValidateCommon 不校验「规则部门 vs 设备所属部门」是否一致，
      // 前端擅自收窄会挡住后端允许的合法配置。
      [r.departmentCode, r.stageCode].filter(Boolean).join(' / ')
    ]
      .filter(Boolean)
      .join(' · '),
    value: r.resourceCode,
    id: r.resourceId
  }))
})

/**
 * Code→Id 解析（Id-口径 v1.1：写输入提交 productionDepartmentId/resourceId）
 * 编辑态优先取 SetupRuleDto 双返回 Id（权威）；新建态查后端主数据 lookup。
 */
function resolveDepartmentId(code: string): number | undefined {
  if (props.mode === 'edit' && props.rule && props.rule.departmentCode === code) {
    return props.rule.productionDepartmentId
  }
  return masterData.departmentIdByCode(code)
}

/**
 * Code → ResourceId（同步三查，不发请求）
 * 顺序：编辑态 DTO 双返回 → 共同合法设备明细 → 主数据 lookup。
 * 第 2 步依赖后端 Q1 投影 resourceId（3号位 10-15 已落码，S-11-d，dev 实测已生效）；
 * 三处都落空时由 resolveResourceIds 走第 4 步按编码向后端补拉（后端回退部署时的降级兜底）。
 */
function resolveResourceId(code: string): number | undefined {
  if (props.mode === 'edit' && props.rule && props.rule.resourceCode === code) {
    return props.rule.resourceId
  }
  const fromEligibility = eligibilityResources.value?.find(
    (r) => r.resourceCode === code
  )?.resourceId
  if (fromEligibility != null) return fromEligibility
  return masterData.resourceIdByCode(code)
}

/**
 * Code → Material.Id（eligibility 传内部主键非 Code）
 * 优先取编辑对象的双返回字段（fromMaterialId/toMaterialId），其次查后端物料 lookup。
 */
function resolveMaterialId(code: string, side: 'from' | 'to'): number | undefined {
  if (props.mode === 'edit' && props.rule) {
    const dualId = side === 'from' ? props.rule.fromMaterialId : props.rule.toMaterialId
    const dualCode = side === 'from' ? props.rule.fromMaterialCode : props.rule.toMaterialCode
    if (dualCode === code && dualId != null) return dualId
  }
  return masterData.materialIdByCode(code)
}

/** 选定 Operation + From/To Material → 调共同合法设备推荐端点 */
async function loadEligibility() {
  if (!form.operationCode || !form.fromMaterialCode || !form.toMaterialCode) {
    eligibilityResources.value = null
    return
  }
  if (form.fromMaterialCode === form.toMaterialCode) {
    eligibilityResources.value = null
    return
  }
  const fromMaterialId = resolveMaterialId(form.fromMaterialCode, 'from')
  const toMaterialId = resolveMaterialId(form.toMaterialCode, 'to')
  if (fromMaterialId == null || toMaterialId == null) {
    eligibilityError.value = '无法解析物料 Id'
    eligibilityResources.value = null
    return
  }
  eligibilityLoading.value = true
  eligibilityError.value = null
  try {
    const dto = await setupApi.getOperationResourceEligibility({
      operationCode: form.operationCode,
      materialId: fromMaterialId,
      toMaterialId
    })
    eligibilityResources.value = dto.resources ?? []
    // 当前选中设备不在交集内 → 剔掉重选（红线：共同合法设备外不允许保存）
    const eligible = dto.resourceCodes
    const kept = form.resourceCodes.filter((c) => eligible.includes(c))
    if (kept.length !== form.resourceCodes.length) {
      form.resourceCodes = kept
      ElMessage.warning('原选设备不在共同合法设备列表内，已清空，请重新选择')
    }
  } catch (err) {
    eligibilityError.value = (err as Error)?.message ?? '共同合法设备查询失败'
    eligibilityResources.value = null
  } finally {
    eligibilityLoading.value = false
  }
}

watch(
  () => [form.operationCode, form.fromMaterialCode, form.toMaterialCode],
  () => {
    if (props.modelValue) void loadEligibility()
  }
)

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
  if (!form.fromMaterialCode || !form.toMaterialCode) {
    return '明确转换规则必须填前产品 + 后产品'
  }
  if (form.fromMaterialCode === form.toMaterialCode) {
    return '同产品连续 = 0 分钟，不存规则'
  }
  if (form.setupMinutes <= 0) {
    return '换型分钟数必须大于 0（0 分钟由兜底自动命中，不需建规则）'
  }
  if (eligibleResourceCodes.value?.some((c) => !form.resourceCodes.includes(c))) {
    return '设备不在共同合法设备列表内，不允许保存'
  }
  return ''
})

// ===== 打开时初始化 =====
watch(
  () => props.modelValue,
  async (visible) => {
    if (!visible) return
    eligibilityError.value = null
    eligibilityResources.value = null
    if (props.mode === 'edit' && props.rule) {
      form.ruleSetVersionId = props.rule.ruleSetVersionId
      form.departmentCode = props.rule.departmentCode ?? ''
      form.stageCode = props.rule.stageCode
      form.operationCode = props.rule.operationCode
      form.resourceCodes = props.rule.resourceCode ? [props.rule.resourceCode] : []
      form.fromMaterialCode = props.rule.fromMaterialCode ?? ''
      form.toMaterialCode = props.rule.toMaterialCode ?? ''
      form.setupMinutes = props.rule.setupMinutes
    } else {
      form.ruleSetVersionId = exactStore.selectedRuleSetVersionId ?? 0
      form.departmentCode = props.prefill?.departmentCode ?? ''
      form.stageCode = props.prefill?.stageCode ?? ''
      form.operationCode = props.prefill?.operationCode ?? ''
      form.resourceCodes = props.prefill?.resourceCode ? [props.prefill.resourceCode] : []
      form.fromMaterialCode = props.prefill?.fromMaterialCode ?? ''
      form.toMaterialCode = props.prefill?.toMaterialCode ?? ''
      form.setupMinutes = 30
    }
    // 主数据到位后再解析 Code→Id 与工序/设备候选（预填值可能已含 Code）
    await masterData.loadAll()
    // 大工艺：部门已定但因主数据晚到才拿到 stageCode 时补带一次
    if (form.departmentCode && !form.stageCode) {
      form.stageCode = masterData.stageOfDepartment(form.departmentCode) ?? ''
    }
    await loadOperations()
    await loadEligibility()
    formRef.value?.clearValidate()
  }
)

// ===== 提交 =====
/**
 * 逐台解析设备 Id；解析不到的那台向后端按编码补拉一次仍拿不到才报出来。
 *
 * 10-15：原先只做三处本地解析就报错，提示还让用户「换个关键词重新搜索该设备」——
 * 但本 Dialog 的设备下拉是「共同合法设备」交集驱动、只有本地 filterable（不能用远程搜索，
 * 远程会把不合法设备也塞进选项、破坏资格语义），用户根本无从照做。
 * dev 实测：工序 M102 + 前产品 FINAL_FG-CQ2KB25-40D + 后产品 FINAL_FG-CQSB25-5DM 时，
 * 资格集合只返回设备 763-2，而 lookups/resources 首屏 200 条字母序范围是「#22年CUJ1-1 ~ 0-183」，
 * 不含 763-2 —— 三处本地解析全落空 ⇒ 必然卡死。
 *
 * 补拉：按该设备编码调 searchResources（同一 lookup 端点，只多带一个 search，
 * 所以不受 TOP 200 截断），后端返回 masterId（dev 实测 763-2 → 13394，与 uncovered-stats 一致）。
 * 3号位 Q1 的 resourceId 一旦在 dev 生效，第 2 步就命中，这段补拉不会再触发。
 */
async function resolveResourceIds(
  codes: string[]
): Promise<{ ids: number[]; unresolved: string[] }> {
  const ids: (number | undefined)[] = codes.map((code) => resolveResourceId(code))
  const pending = codes.filter((_, i) => ids[i] == null)
  for (const code of pending) {
    await masterData.searchResources(code)
    ids[codes.indexOf(code)] = masterData.resourceIdByCode(code)
  }
  return {
    ids: ids.filter((id): id is number => id != null),
    unresolved: codes.filter((_, i) => ids[i] == null)
  }
}

async function handleSubmit() {
  if (submitBlockReason.value) {
    ElMessage.warning(submitBlockReason.value)
    return
  }
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  // Id-口径转换（v1.1：请求提交 Id；解析失败 = 主数据缺口，禁止发 Code 伪载荷）
  const productionDepartmentId = resolveDepartmentId(form.departmentCode)
  const fromMaterialId = resolveMaterialId(form.fromMaterialCode, 'from')
  const toMaterialId = resolveMaterialId(form.toMaterialCode, 'to')
  if (productionDepartmentId == null || fromMaterialId == null || toMaterialId == null) {
    ElMessage.error('无法解析主数据 Id，请检查部门 / 产品输入')
    return
  }
  const { ids: resourceIds, unresolved } = await resolveResourceIds(form.resourceCodes)
  if (unresolved.length) {
    // 已按编码向后端补拉过仍查不到 ⇒ 主数据里确实没有这台设备
    ElMessage.error(`主数据里查不到设备 ${unresolved.join('、')}，请确认设备编码是否正确`)
    return
  }

  const buildInput = (resourceId: number): SetupRuleExactInput => ({
    ruleSetVersionId: form.ruleSetVersionId,
    productionDepartmentId,
    stageCode: form.stageCode,
    operationCode: form.operationCode,
    resourceId,
    fromMaterialId,
    toMaterialId,
    setupMinutes: form.setupMinutes
  })

  if (props.mode === 'edit' && props.rule) {
    const ok = await exactStore.updateRule(props.rule.id, buildInput(resourceIds[0]))
    if (ok) {
      ElMessage.success('明确产品转换规则更新成功')
      emit('update:modelValue', false)
      emit('saved')
    } else if (exactStore.error) {
      // 后端 422（7 元组重复）等错误原文透传
      ElMessage.error(exactStore.error)
    }
    return
  }

  // 多设备：必须串行。后端 create 是「读 ContentSnapshotJson → 加一条 → 整列写回」的
  // read-modify-write，并发请求后写的会覆盖先写的规则（换型规则只存那一列）。
  let okCount = 0
  let firstError = ''
  for (let i = 0; i < resourceIds.length; i++) {
    const created = await exactStore.createRule(buildInput(resourceIds[i]))
    if (created) {
      okCount++
    } else {
      firstError = exactStore.error ?? '未知错误'
      // 保留失败及未尝试的设备，让用户调整后能直接重试，不必重填整张表
      form.resourceCodes = form.resourceCodes.slice(i)
      break
    }
  }
  if (okCount === resourceIds.length) {
    ElMessage.success(`明确产品转换规则创建成功（${okCount} 条）`)
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
  props.mode === 'edit' ? '编辑明确产品转换规则' : '新增明确产品转换规则'
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
    <!-- 规则提示（明确转换规则专项） -->
    <ElAlert type="warning" :closable="false" show-icon style="margin-bottom: 16px">
      <template #title>明确转换规则要求</template>
      前产品 ≠ 后产品（同产品连续 = 0 分钟，不存规则）；换型分钟数必须大于 0；
      设备必须在共同合法设备列表内。
    </ElAlert>
    <ElAlert
      v-if="masterData.truncated.value"
      type="info"
      :closable="false"
      show-icon
      style="margin-bottom: 16px"
    >
      <template #title>主数据较多，请用编码搜索</template>
      部门和产品一次只带出前一批，请直接输入编码或名称片段搜索后再选。
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
      <ElFormItem label="前产品" prop="fromMaterialCode">
        <ElSelect
          v-model="form.fromMaterialCode"
          placeholder="输入编码搜索前产品"
          filterable
          remote
          :remote-method="onSearchMaterial"
          :loading="searching.material || masterData.loading.value"
          style="width: 100%"
          @change="onFromMaterialChange"
        >
          <ElOption
            v-for="opt in materialOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
      </ElFormItem>
      <ElFormItem label="后产品" prop="toMaterialCode">
        <ElSelect
          v-model="form.toMaterialCode"
          placeholder="输入编码搜索后产品"
          filterable
          remote
          :remote-method="onSearchMaterial"
          :loading="searching.material || masterData.loading.value"
          style="width: 100%"
        >
          <ElOption
            v-for="opt in materialOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
            :disabled="opt.value === form.fromMaterialCode"
          >
            <span>{{ opt.label }}</span>
            <ElTag
              v-if="opt.value === form.fromMaterialCode"
              type="danger"
              size="small"
              style="margin-left: 8px"
            >
              同产品 = 0 分钟
            </ElTag>
          </ElOption>
        </ElSelect>
        <div
          v-if="form.fromMaterialCode && form.toMaterialCode === form.fromMaterialCode"
          class="redline-hint"
        >
          同产品连续 = 0 分钟，不存规则
        </div>
      </ElFormItem>

      <ElFormItem label="当前工序" prop="operationCode">
        <ElSelect
          v-model="form.operationCode"
          placeholder="先选前产品，再选当前工序"
          filterable
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
        <div v-if="!form.fromMaterialCode" class="eligibility-hint">
          工序按前产品的工艺路线给出，请先选前产品
        </div>
      </ElFormItem>

      <!-- 共同合法设备推荐 -->
      <ElFormItem label="设备" prop="resourceCodes" :rules="deviceRules">
        <ElSelect
          v-model="resourceSelection"
          :multiple="isCreate"
          collapse-tags
          collapse-tags-tooltip
          :max-collapse-tags="5"
          :placeholder="isCreate ? '可多选设备' : '选择设备（共同合法设备）'"
          filterable
          :loading="eligibilityLoading"
          style="width: 100%"
        >
          <ElOption
            v-for="opt in resourceOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>
        <div v-if="eligibilityLoading" class="eligibility-hint">共同合法设备查询中…</div>
        <div v-else-if="eligibilityError" class="redline-hint"
          >共同合法设备查询失败：{{ eligibilityError }}</div
        >
        <div v-else-if="eligibleResourceCodes" class="eligibility-hint">
          共同合法设备 {{ eligibleResourceCodes.length }} 台（当前工序与前后产品的交集）<template
            v-if="isCreate"
            >，可一次多选，每台各建一条规则</template
          >
        </div>
        <div v-else class="eligibility-hint">
          选定前产品 + 工序 + 后产品后自动查询共同合法设备
        </div>
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
            :loading="exactStore.actionRunning"
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

.eligibility-hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  line-height: 1.5;
  margin-top: 4px;
}

.unit-label {
  margin-left: 8px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>
