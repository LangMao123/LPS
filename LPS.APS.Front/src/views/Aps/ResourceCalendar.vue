<!--
  APS V1 4号位 — 资源日历维护页（单页 3 Tab）

  依据：
    - 冻结文档 v1.3《APS_V1_Resource_Calendar资源日历能力补充冻结方案_v1.3_人工能力槽模型与职责边界修订版》
        §八 4号位 = 资源能力维护页面 Owner，维护 3 类对象：
          ① 设备资源日历（Resource + ResourceCalendarSlot）
          ② 人工能力槽（ManualCapacitySlot）
          ③ 人工能力槽日历（ManualCapacitySlotCalendar）
    - 冻结文档 v1.4（2026-10-12 三级继承）§17/§19/§23 **定向覆盖 v1.3 §九**：
        · 本页 = 三级继承的第 3 级（工厂 → 部门 → 设备/人工槽），v1.4 line 19「保留设备及人工槽已有页面资产」
        · 「本级无窗口 = 不可排」**已废止**（废止清单 O-024）：本级 MISSING 允许继承上级已认证窗口，
          仅当**三级计算后仍无权威合法窗口**才判不可排
        · 因此本页「未铺窗口」提示只能陈述本级事实，不得断言该资源不可排
        · ⚠️ 合并后真实状态暂无查询端点（ResourceCalendarResolver 未挂 Service/Controller）——
          已发函 5号位（4号位-2026-10-14）；落地前页面按本级事实表述，不得伪造三态
    - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》（9 端点 / 6 权限码）

  页面结构：
    Tab1 设备资源日历：资源选择（资源主表 /api/resource 正式端点）→ 窗口列表 → 批量铺窗 / 删窗
    Tab2 人工能力槽  ：部门/工序过滤 + 含软删开关 → 主档列表 → 新增 / 查看窗口 / 铺窗 / 软删
    Tab3 人工槽日历  ：由 Tab2 选中行驱动 → 窗口列表 → 批量铺窗 / 删窗

  RBAC（v1.2 §23.1）：
    - 路由级 apsRequiredPermissions = [aps.resource_calendar.view, aps.manual_capacity.view]（OR 语义）
    - 按钮级：canEditDevice / canDeleteDevice / canEditManual / canDeleteManual

  10-12 RC23 收（G1 已闭环 + 撤过渡）：
    - 旧「计划版本甘特资源池」候选 → 撤；改走 GET /api/resource（ResourceController.cs:34）
    - 旧「手填资源 ID」 → 撤；el-select-v2 虚拟滚动满足 7773+ records 性能
    - 旧「过渡数据源」用户提示 → 撤；当前实现已无"过渡/兜底"概念

  2026-10-14 RC27 收（撤 RC19）：
   - 撤顶部版本选择器（version-bar）—— 原为绕过"资源池取自甘特图、ACTIVE 版无甘特数据"的排查障碍而加，
     同日 RC23 已把资源池改走 GET /api/resource（基础主数据，与计划版本无关），障碍不复存在
   - 且本页从不读 gantt / summary，切版本只是触发一次本页用不上的取数；留着会误导用户
     以为资源日历按计划版本分版本（上级工作日历同为基础数据，两页口径现已一致）
   - 冻结 v1.4 §「4号位：资源能力维护页面Owner」只列 3 项维护对象，未要求计划版本锚点
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { storeToRefs } from 'pinia'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElOption,
  ElPagination,
  ElSelect,
  ElSelectV2,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTabPane,
  ElTabs,
  ElTag,
  ElTooltip
} from 'element-plus'
import {
  CALENDAR_CONFIG_META,
  type BulkWindowDraft,
  type ManualCapacitySlotDto,
  type ManualSlotCalendarDto,
  type ManualSlotDraft,
  type Resource,
  type ResourceCalendarEntryDto
} from '@/api/aps-v1'
import type { RoleKey } from '@/api/aps-v1/types'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { useResourceCalendarStore } from '@/store/modules/aps/resourceCalendar'
import CalendarBulkDialog from './components/CalendarBulkDialog.vue'
import ManualSlotCreateDialog from './components/ManualSlotCreateDialog.vue'

const apsAuth = useApsAuthStore()
const store = useResourceCalendarStore()
const {
  resources,
  resourcesLoading,
  currentResourceId,
  deviceWindows,
  deviceWindowsLoaded,
  deviceWindowTotal,
  deviceWindowPageRows,
  deviceWindowPage,
  deviceWindowPageSize,
  deviceCalendarStatus,
  slots,
  slotTotal,
  slotPage,
  slotPageSize,
  slotsLoading,
  departmentCandidates,
  currentSlotId,
  slotWindows,
  currentSlot,
  loading,
  saving,
  error,
  canViewDevice,
  canEditDevice,
  canDeleteDevice,
  canViewManual,
  canEditManual,
  canDeleteManual
} = storeToRefs(store)

/* ===== Tab 控制 ===== */
type TabName = 'device' | 'manual' | 'manual-calendar'
const activeTab = ref<TabName>('device')
const loadedTabs = ref<Set<TabName>>(new Set())

async function onTabChange(name: TabName): Promise<void> {
  if (loadedTabs.value.has(name)) return
  loadedTabs.value.add(name)
  // 10-12 RC20：fire-and-forget，不阻塞 UI 切 Tab
  //  - 原顶层 wait 在 onTabChange 内，会让 ElTabPane 切换视觉"卡"（慢响应端点会拖住 UI 切回）
  //  - store 内部已有 loading / error / ElMessage，错误由 store 处理
  //  - RC20 探针锁契约：onTabChange 内不在函数顶层 wait 数据加载
  void (async () => {
    try {
      if (name === 'device') {
        await store.loadResources()
      } else if (name === 'manual') {
        await Promise.all([store.loadSlots(), store.loadDepartmentCandidates()])
      }
    } catch {
      // 错误已由 store handleError 写入 error ref + ElMessage；此处不重复
    }
  })()
}

/* ===== Tab1：设备资源日历 ===== */
// 10-12 RC23：资源主表改走正式端点 GET /api/resource（含 hasCalendar，列表级判定日历状态）
//  - 旧「手填资源 ID」已撤（@see RC23 老实现 + 原 G1 过渡说明）
//  - dev 实测 7773 records → 用 el-select-v2 虚拟滚动渲染
const deviceDialogVisible = ref(false)

/** ElSelectV2 需要的 {value,label} 形态；按 resourceCode 升序便于定位 */
const resourceOptions = computed<Array<{ value: number; label: string; resource: Resource }>>(() =>
  [...resources.value]
    .sort((a, b) => (a.resourceCode ?? '').localeCompare(b.resourceCode ?? ''))
    .map((r) => ({
      value: r.resourceId,
      label: labelOfResource(r),
      resource: r
    }))
)

/** 冻结 v1.4 line 17：「资源归部门」是三级继承的前提 —— 用户须先知道设备属于哪个车间，
 *  才能去判断「上级（工厂/部门）开没开」。后端 ResourceListItemDto 已带 productionDepartmentName，
 *  10-14 起展示（此前 DTO 有值但页面未用，dev 7773 条同名设备无法区分）。 */
function labelOfResource(r: Resource): string {
  const dept = r.productionDepartmentName?.trim()
  return `${r.resourceCode ?? '-'} ${r.resourceName ?? ''}${dept ? `（${dept}）` : ''}`.trim()
}

async function pickResource(resourceId: number): Promise<void> {
  await store.loadDeviceWindows(resourceId)
}

function openDeviceDialog(): void {
  if (currentResourceId.value == null) {
    ElMessage.warning('请先选择资源并加载窗口')
    return
  }
  deviceDialogVisible.value = true
}

async function submitDeviceBulk(draft: BulkWindowDraft): Promise<void> {
  if (currentResourceId.value == null) return
  const inserted = await store.createDeviceWindows({
    resourceId: currentResourceId.value,
    startDate: `${draft.startDate}T00:00:00`,
    days: draft.days,
    startTime: draft.startTime,
    endTime: draft.endTime,
    availableFlag: draft.availableFlag,
    remark: draft.remark
  })
  if (inserted != null) {
    ElMessage.success(`已生成 ${inserted} 条设备窗口`)
    deviceDialogVisible.value = false
  }
}

async function removeDeviceWindow(row: ResourceCalendarEntryDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将物理删除设备窗口 #${row.id}（${fmt(row.startTime)} ~ ${fmt(row.endTime)}）。\n` +
      '物理删除后行消失、不可恢复，排产追溯将不再含该窗口。确认删除？',
    '删除设备窗口',
    { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.removeDeviceWindow(row.id, row.resourceId)
  if (done) ElMessage.success('设备窗口已删除')
}

/** 当前资源的展示名 */
const currentResourceLabel = computed<string>(() => {
  const id = currentResourceId.value
  if (id == null) return ''
  const hit = resources.value.find((r) => r.resourceId === id)
  if (!hit) return `资源 #${id}`
  return labelOfResource(hit)
})

/** 当前资源的归属部门（冻结 v1.4 line 17 资源归部门是三级继承前提；status 行展示供用户核对上级） */
const currentResourceDept = computed<string>(
  () =>
    resources.value.find((r) => r.resourceId === currentResourceId.value)
      ?.productionDepartmentName ?? ''
)

/* 10-14 RC26 收：设备窗口分页（过渡期前端切片；后端 PageResult 待 5号位 落地）
 *  - 切页码 → 改 store 游标；切每页条数 → 回第 1 页防越界
 *  - 后端改 PageResult 后这两个函数签名不变，只换 store 内部取数方式
 */
function onDeviceWindowPageChange(nextPage: number): void {
  store.deviceWindowPage = nextPage
}
function onDeviceWindowSizeChange(nextSize: number): void {
  store.deviceWindowPageSize = nextSize
  store.deviceWindowPage = 1
}

/* ===== Tab2：人工能力槽 ===== */
const slotDialogVisible = ref(false)
const filterDepartmentId = ref<number | undefined>(undefined)
const filterOperationName = ref<string>('')
const filterIncludeInactive = ref<boolean>(false)

async function searchSlots(): Promise<void> {
  await store.loadSlots(
    {
      departmentId: filterDepartmentId.value ?? undefined,
      operationName: filterOperationName.value.trim() || undefined,
      includeInactive: filterIncludeInactive.value
    },
    { resetPage: true }
  )
}

function resetSlotFilter(): void {
  filterDepartmentId.value = undefined
  filterOperationName.value = ''
  filterIncludeInactive.value = false
  void store.loadSlots({}, { resetPage: true })
}

/* 10-12 RC22 收：人工槽 Tab2 接入分页（@see taskSplitRuleConfig.vue:162-170 范式）
 *  - 切页码 → loadSlots(undefined, { nextPageIndex })
 *  - 切每页条数 → loadSlots(undefined, { resetPage: true, nextPageSize }) 防越界
 */
async function onSlotPageChange(nextPage: number): Promise<void> {
  await store.loadSlots(undefined, { nextPageIndex: nextPage })
}
async function onSlotSizeChange(nextSize: number): Promise<void> {
  await store.loadSlots(undefined, { resetPage: true, nextPageSize: nextSize })
}

async function submitCreateSlot(draft: ManualSlotDraft): Promise<void> {
  const created = await store.createSlot({
    manualSlotId: 0,
    productionDepartmentId: draft.productionDepartmentId,
    operationName: draft.operationName,
    slotCode: draft.slotCode,
    isActive: true
  })
  if (created) {
    ElMessage.success(`人工能力槽「${created.slotCode}」已新增`)
    slotDialogVisible.value = false
  }
}

/** 查看某人工槽窗口 → 载入并跳 Tab3 */
async function viewSlotWindows(row: ManualCapacitySlotDto): Promise<void> {
  await store.loadSlotWindows(row.manualSlotId)
  activeTab.value = 'manual-calendar'
  loadedTabs.value.add('manual-calendar')
}

async function disableSlot(row: ManualCapacitySlotDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将软删人工能力槽「${row.slotCode}」（${row.operationName}）：停用后行保留、查询默认不含。\n\n` +
      '影响：该能力槽不再参与排产；已有日历窗口不会自动清除，如需清理请到「人工槽日历」页签删除。',
    '软删人工能力槽',
    { type: 'warning', confirmButtonText: '确认停用', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.disableSlot(row.manualSlotId)
  if (done) ElMessage.success(`人工能力槽 ${row.slotCode} 已停用`)
}

/** 从 Tab2 直接对该行发起铺窗（切换到 Tab3 后打开 Dialog） */
const pendingSlotBulkId = ref<number | null>(null)
async function bulkForSlot(row: ManualCapacitySlotDto): Promise<void> {
  if (!row.isActive) {
    ElMessage.warning('已停用的人工槽不可铺窗')
    return
  }
  await viewSlotWindows(row)
  pendingSlotBulkId.value = row.manualSlotId
  slotBulkDialogVisible.value = true
}

/* ===== Tab3：人工槽日历 ===== */
const slotBulkDialogVisible = ref(false)

function openSlotBulkDialog(): void {
  if (currentSlotId.value == null) return
  if (currentSlot.value && !currentSlot.value.isActive) {
    ElMessage.warning('已停用的人工槽不可铺窗')
    return
  }
  slotBulkDialogVisible.value = true
}

async function submitSlotBulk(draft: BulkWindowDraft): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  const inserted = await store.createSlotWindows({
    manualSlotId: id,
    startDate: `${draft.startDate}T00:00:00`,
    days: draft.days,
    startTime: draft.startTime,
    endTime: draft.endTime,
    availableFlag: draft.availableFlag,
    remark: draft.remark
  })
  if (inserted != null) {
    ElMessage.success(`已生成 ${inserted} 条人工槽窗口`)
    slotBulkDialogVisible.value = false
  }
}

async function removeSlotWindow(row: ManualSlotCalendarDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将物理删除人工槽窗口 #${row.id}（${fmt(row.startTime)} ~ ${fmt(row.endTime)}）。确认删除？`,
    '删除人工槽窗口',
    { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.removeSlotWindow(row.id, row.manualSlotId)
  if (done) ElMessage.success('人工槽窗口已删除')
}

const currentSlotLabel = computed<string>(() => {
  const s = currentSlot.value
  if (!s) return ''
  return `${s.slotCode}（${s.operationName}｜${s.departmentName ?? '未知部门'}）`
})

const currentSlotWindowStatus = computed(() =>
  currentSlotId.value == null ? 'UNKNOWN' : store.slotCalendarStatus(currentSlotId.value)
)

/* ===== 公共 ===== */
/** ISO 时间 → "YYYY-MM-DD HH:mm" */
function fmt(iso?: string): string {
  if (!iso) return '—'
  return iso.replace('T', ' ').slice(0, 16)
}

function windowCountLabel(rows: Array<unknown>): string {
  return `${rows.length} 条`
}

/* ===== mock 角色切换器（仅 mock 模式显示；与甘特图页同一范式） =====
 * 真实绑定已落地（3号位 脚本 APS_Auth_resource_calendar_role_binding_20260924.sql）：
 *   viewer = view；planner = view + edit（不含 delete）；admin.aps / admin.system = 全 6 码。
 * mock 下用本切换器验证按钮级门控（planner 无可删按钮 / admin 可删）。
 */
const ROLE_TAG: Record<RoleKey, 'success' | 'warning' | 'info' | 'primary' | 'danger'> = {
  'aps.viewer.management': 'info',
  'aps.planner': 'success',
  'aps.admin.aps': 'warning',
  'aps.supervisor.workshop': 'primary',
  'aps.coordinator.material': 'primary',
  'aps.service.api': 'primary',
  'aps.admin.system': 'danger'
}

function switchMockRole(role: RoleKey): void {
  apsAuth.mockSwitchRole(role)
  ElMessage.success(`已切换角色：${role}（仅演示模式）`)
}

/* ===== 10-14 RC27 收：撤版本选择器（RC19 撤回）=====
 *  - 冻结 v1.4 §「4号位：资源能力维护页面Owner」只列 3 项维护对象（设备日历 / 人工能力槽 / 人工槽日历），
 *    **未要求计划版本锚点**；上级工作日历（同为基础数据）同样无版本条，两页口径现已一致
 *  - 原 RC19 加它是为绕过"资源池取自甘特图、ACTIVE 版本无甘特数据 → 资源池空"的排查障碍；
 *    同日 RC23 已把资源池改走 GET /api/resource（与版本无关的基础主数据），**该障碍不复存在**
 *  - 且本页从不读取 gantt / summary：切版本只触发一次本页用不上的取数，纯装饰且误导
 *    （让用户误以为资源日历按计划版本分版本）
 */

onMounted(async () => {
  if (!canViewDevice.value && !canViewManual.value) {
    ElMessage.warning('当前操作者无资源日历查看权限，请联系系统管理员授予')
  }
  loadedTabs.value.add('device')
  // 10-12 RC23：资源主表走正式端点 /api/resource（基础数据，与计划版本无关）
  await store.loadResources()
})

onBeforeUnmount(() => {
  store.reset()
})
</script>

<template>
  <div class="rc-page">
    <!-- 页面说明（10-12 收：原 ElCard 仅有 header 无 body，空 body 占行高；改用 div 紧凑布局） -->
    <div class="rc-intro">
      <div class="intro-header">
        <span class="intro-title">资源日历维护</span>
        <span class="intro-sub">设备资源日历 / 人工能力槽 / 人工槽日历</span>
        <ElTooltip placement="bottom-start" effect="light" :show-after="150">
          <span class="intro-help" tabindex="0">?</span>
          <template #content>
            <div class="help-pop">
              <p class="help-pop-title">能不能排产，看厂和部门开没开</p>
              <p>排产时间从上往下继承：工厂 → 部门 → 设备 / 工位。</p>
              <p
                >本级没铺窗口，只说明<strong>这台还没单独登记</strong>，不代表一定不能排 ——
                厂里或部门开放了，它照样能排。</p
              >
              <p>只有三级都没开放，才是真的排不了；系统也不会默认按 7×24 小时当成一直可排。</p>
            </div>
          </template>
        </ElTooltip>
        <!-- mock 角色切换器（仅 mock 模式显示；生产模式自动隐藏） -->
        <ElDropdown
          v-if="apsAuth.mockAvailableRoles.length > 0"
          trigger="click"
          class="role-switcher"
          @command="switchMockRole"
        >
          <ElTag
            :type="ROLE_TAG[apsAuth.mockActiveRole]"
            effect="dark"
            class="role-badge"
            title="点击切换角色（仅演示模式可用）"
          >
            {{ apsAuth.mockActiveRole }}
          </ElTag>
          <template #dropdown>
            <ElDropdownMenu>
              <ElDropdownItem
                v-for="r in apsAuth.mockAvailableRoles"
                :key="r"
                :command="r"
                :disabled="r === apsAuth.mockActiveRole"
              >
                <ElTag :type="ROLE_TAG[r]" size="small" effect="plain">{{ r }}</ElTag>
                <span class="role-hint">
                  {{
                    r === 'aps.viewer.management'
                      ? '仅 view（可看不可改）'
                      : r === 'aps.planner'
                        ? 'view + edit（不含 delete）'
                        : r === 'aps.admin.aps' || r === 'aps.admin.system'
                          ? '全 6 码（含 delete）'
                          : ''
                  }}
                </span>
              </ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>
      </div>
    </div>

    <!-- 错误条 -->
    <ElAlert
      v-if="error"
      type="error"
      :closable="true"
      show-icon
      class="rc-alert rc-error"
      title="操作失败"
      @close="store.clearError()"
    >
      {{ error }}
    </ElAlert>

    <ElCard shadow="never">
      <ElTabs v-model="activeTab" @tab-change="(n) => onTabChange(n as TabName)">
        <!-- ============ Tab1 设备资源日历 ============ -->
        <ElTabPane label="设备资源日历" name="device" lazy>
          <ElForm inline label-position="right" label-width="80px" class="rc-toolbar">
            <ElFormItem v-if="resources.length > 0" label="资源">
              <!-- 10-12 RC23：el-select-v2 虚拟滚动（dev 7773+ records → el-select 会卡） -->
              <ElSelectV2
                :model-value="currentResourceId ?? undefined"
                :options="resourceOptions"
                :loading="resourcesLoading"
                filterable
                clearable
                placeholder="从资源主表选择（输入编码/名称/部门搜索）"
                style="width: 360px"
                @change="(v: number | undefined) => v && pickResource(v)"
              />
            </ElFormItem>
            <ElFormItem v-else label="资源">
              <span class="rc-hint">{{
                resourcesLoading ? '资源主表加载中…' : '资源主表为空'
              }}</span>
            </ElFormItem>
            <ElFormItem>
              <ElButton
                type="primary"
                :disabled="currentResourceId == null || !canEditDevice"
                @click="openDeviceDialog"
              >
                批量铺窗
              </ElButton>
            </ElFormItem>
          </ElForm>

          <div v-if="currentResourceId != null" class="rc-status-row">
            <span class="rc-status-label">当前资源：</span>
            <strong>{{ currentResourceLabel }}</strong>
            <ElTag v-if="currentResourceDept" type="info" class="rc-btn-gap" effect="plain">
              所属部门：{{ currentResourceDept }}
            </ElTag>
            <ElTag
              :type="CALENDAR_CONFIG_META[deviceCalendarStatus].tagType"
              class="rc-btn-gap"
              effect="dark"
            >
              日历 {{ CALENDAR_CONFIG_META[deviceCalendarStatus].label }}
            </ElTag>
            <span class="rc-hint">{{
              CALENDAR_CONFIG_META[deviceCalendarStatus].description
            }}</span>
            <span class="rc-hint">窗口：{{ windowCountLabel(deviceWindows) }}</span>
          </div>

          <ElEmpty
            v-if="currentResourceId == null"
            description="请先在上方资源下拉中选中目标设备"
          />
          <template v-else>
            <ElAlert
              v-if="deviceWindowsLoaded && deviceWindows.length === 0"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这台设备本级还没登记"
            >
              只说明没有单独设过可用时段，<strong>不代表它一定不能排产</strong> ——
              所属工厂或部门开放了，它照样能排。需要单独调整它的可用时段时，再点「批量铺窗」。
            </ElAlert>

            <ElTable
              v-if="deviceWindows.length > 0"
              :data="deviceWindowPageRows"
              :loading="loading"
              border
              stripe
              row-key="id"
              size="small"
              style="width: 100%"
            >
              <ElTableColumn prop="id" label="窗口编号" width="120" fixed="left" />
              <ElTableColumn prop="resourceCode" label="资源编码" width="120" />
              <ElTableColumn prop="resourceName" label="资源名称" width="150" />
              <ElTableColumn label="开始时间" min-width="170">
                <template #default="{ row }">{{ fmt(row.startTime) }}</template>
              </ElTableColumn>
              <ElTableColumn label="结束时间" min-width="170">
                <template #default="{ row }">{{ fmt(row.endTime) }}</template>
              </ElTableColumn>
              <ElTableColumn label="可用" width="90" align="center">
                <template #default="{ row }">
                  <ElTag :type="row.availableFlag ? 'success' : 'danger'" size="small">
                    {{ row.availableFlag ? '可用' : '禁用' }}
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn prop="remark" label="备注" min-width="160" show-overflow-tooltip />
              <ElTableColumn label="操作" width="110" fixed="right" v-if="canDeleteDevice">
                <template #default="{ row }">
                  <ElButton
                    type="danger"
                    size="small"
                    :loading="saving"
                    @click="removeDeviceWindow(row)"
                  >
                    删除
                  </ElButton>
                </template>
              </ElTableColumn>
            </ElTable>

            <!-- 10-14 RC26 收：设备窗口分页（过渡期前端切片；服务端分页端点待落地后切换） -->
            <ElPagination
              v-if="deviceWindowTotal > 0"
              :current-page="deviceWindowPage"
              :page-size="deviceWindowPageSize"
              :total="deviceWindowTotal"
              :page-sizes="[10, 20, 50, 100, 200]"
              layout="total, sizes, prev, pager, next, jumper"
              background
              class="rc-pagination"
              @current-change="onDeviceWindowPageChange"
              @size-change="onDeviceWindowSizeChange"
            />
          </template>
        </ElTabPane>

        <!-- ============ Tab2 人工能力槽 ============ -->
        <ElTabPane label="人工能力槽" name="manual" lazy>
          <ElForm inline label-position="right" label-width="80px" class="rc-toolbar">
            <ElFormItem label="部门">
              <ElSelect
                v-if="departmentCandidates.length > 0"
                v-model="filterDepartmentId"
                filterable
                clearable
                placeholder="全部部门"
                style="width: 220px"
              >
                <ElOption
                  v-for="d in departmentCandidates"
                  :key="d.productionDepartmentId"
                  :label="d.productionDepartmentName ?? '未知部门'"
                  :value="d.productionDepartmentId"
                />
              </ElSelect>
              <ElInputNumber
                v-else
                v-model="filterDepartmentId"
                :min="1"
                :step="1"
                style="width: 140px"
              />
            </ElFormItem>
            <ElFormItem label="小工序">
              <ElInput
                v-model="filterOperationName"
                placeholder="如 精修（自由文本）"
                clearable
                style="width: 180px"
                @keyup.enter="searchSlots"
              />
            </ElFormItem>
            <ElFormItem label="含已停用">
              <ElSwitch v-model="filterIncludeInactive" />
            </ElFormItem>
            <ElFormItem>
              <ElButton type="primary" :loading="slotsLoading" @click="searchSlots">查询</ElButton>
              <ElButton :disabled="slotsLoading" @click="resetSlotFilter">重置</ElButton>
            </ElFormItem>
            <ElFormItem>
              <ElButton type="primary" :disabled="!canEditManual" @click="slotDialogVisible = true">
                新增人工能力槽
              </ElButton>
            </ElFormItem>
          </ElForm>

          <ElEmpty
            v-if="slots.length === 0 && !slotsLoading"
            description="无人工能力槽；可点击「新增人工能力槽」创建"
          />

          <ElTable
            v-else
            :data="slots"
            :loading="slotsLoading"
            border
            stripe
            row-key="manualSlotId"
            size="small"
            :row-class-name="({ row }) => (!row.isActive ? 'rc-row-disabled' : '')"
            style="width: 100%"
          >
            <ElTableColumn prop="manualSlotId" label="人工槽编号" width="130" fixed="left" />
            <ElTableColumn prop="departmentName" label="生产部门" width="150">
              <template #default="{ row }">
                {{ row.departmentName ?? '未知部门' }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="operationName" label="小工序" width="140" />
            <ElTableColumn prop="slotCode" label="能力槽编码" width="150" />
            <ElTableColumn label="状态" width="100" align="center">
              <template #default="{ row }">
                <ElTag :type="row.isActive ? 'success' : 'info'" size="small">
                  {{ row.isActive ? '启用' : '已停用' }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="日历" width="120" align="center">
              <template #default="{ row }">
                <ElTag
                  :type="CALENDAR_CONFIG_META[store.slotCalendarStatus(row.manualSlotId)].tagType"
                  size="small"
                  effect="plain"
                >
                  {{ CALENDAR_CONFIG_META[store.slotCalendarStatus(row.manualSlotId)].label }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="操作" width="260" fixed="right">
              <template #default="{ row }">
                <ElButton size="small" @click="viewSlotWindows(row)">查看窗口</ElButton>
                <ElButton
                  size="small"
                  type="primary"
                  :disabled="!canEditManual || !row.isActive"
                  @click="bulkForSlot(row)"
                >
                  铺窗
                </ElButton>
                <ElButton
                  size="small"
                  type="danger"
                  :disabled="!canDeleteManual || !row.isActive"
                  :loading="saving"
                  @click="disableSlot(row)"
                >
                  软删
                </ElButton>
              </template>
            </ElTableColumn>
          </ElTable>

          <!-- 10-12 RC22 收：分页 ElPagination（@see TaskSplitRuleConfig.vue:842-852 范式） -->
          <ElPagination
            v-if="slotTotal > 0"
            :current-page="slotPage"
            :page-size="slotPageSize"
            :total="slotTotal"
            :page-sizes="[10, 20, 50, 100, 200]"
            layout="total, sizes, prev, pager, next, jumper"
            background
            class="rc-pagination"
            @current-change="onSlotPageChange"
            @size-change="onSlotSizeChange"
          />
        </ElTabPane>

        <!-- ============ Tab3 人工槽日历 ============ -->
        <ElTabPane label="人工槽日历" name="manual-calendar" lazy>
          <ElEmpty
            v-if="currentSlotId == null"
            description="请先在「人工能力槽」页签中点击某行的「查看窗口」或「铺窗」"
          />
          <template v-else>
            <div class="rc-status-row">
              <span class="rc-status-label">当前人工槽：</span>
              <strong>{{ currentSlotLabel }}</strong>
              <ElTag
                :type="CALENDAR_CONFIG_META[currentSlotWindowStatus].tagType"
                class="rc-btn-gap"
                effect="dark"
              >
                日历 {{ CALENDAR_CONFIG_META[currentSlotWindowStatus].label }}
              </ElTag>
              <span class="rc-hint">{{
                CALENDAR_CONFIG_META[currentSlotWindowStatus].description
              }}</span>
              <span class="rc-hint">窗口：{{ windowCountLabel(slotWindows) }}</span>
              <ElButton
                class="rc-btn-gap"
                size="small"
                :loading="loading"
                @click="store.loadSlotWindows(currentSlotId)"
              >
                刷新
              </ElButton>
              <ElButton
                type="primary"
                size="small"
                :disabled="!canEditManual || (currentSlot ? !currentSlot.isActive : true)"
                @click="openSlotBulkDialog"
              >
                批量铺窗
              </ElButton>
            </div>

            <ElAlert
              v-if="currentSlot && !currentSlot.isActive"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="该人工槽已停用"
            >
              停用后不再参与排产；窗口保留，不会被自动清理。
            </ElAlert>

            <ElAlert
              v-if="slotWindows.length === 0"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这个工位本级还没登记"
            >
              只说明没有单独设过可用时段，<strong>不代表它一定不能排产</strong> ——
              所属工厂或部门开放了，它照样能排。需要单独调整它的可用时段时，再点「批量铺窗」。
            </ElAlert>

            <ElTable
              v-if="slotWindows.length > 0"
              :data="slotWindows"
              :loading="loading"
              border
              stripe
              row-key="id"
              size="small"
              style="width: 100%"
            >
              <ElTableColumn prop="id" label="窗口编号" width="130" fixed="left" />
              <ElTableColumn prop="manualSlotId" label="人工槽编号" width="130" />
              <ElTableColumn label="开始时间" min-width="170">
                <template #default="{ row }">{{ fmt(row.startTime) }}</template>
              </ElTableColumn>
              <ElTableColumn label="结束时间" min-width="170">
                <template #default="{ row }">{{ fmt(row.endTime) }}</template>
              </ElTableColumn>
              <ElTableColumn label="可用" width="90" align="center">
                <template #default="{ row }">
                  <ElTag :type="row.availableFlag ? 'success' : 'danger'" size="small">
                    {{ row.availableFlag ? '可用' : '禁用' }}
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn prop="remark" label="备注" min-width="160" show-overflow-tooltip />
              <ElTableColumn label="操作" width="110" fixed="right" v-if="canDeleteManual">
                <template #default="{ row }">
                  <ElButton
                    type="danger"
                    size="small"
                    :loading="saving"
                    @click="removeSlotWindow(row)"
                  >
                    删除
                  </ElButton>
                </template>
              </ElTableColumn>
            </ElTable>
          </template>
        </ElTabPane>
      </ElTabs>
    </ElCard>

    <!-- ===== Dialog：设备铺窗 ===== -->
    <CalendarBulkDialog
      :visible="deviceDialogVisible"
      :submitting="saving"
      mode="DEVICE"
      :target-id="currentResourceId"
      :target-label="currentResourceLabel"
      @update:visible="(v) => (deviceDialogVisible = v)"
      @submit="submitDeviceBulk"
    />

    <!-- ===== Dialog：新增人工能力槽 ===== -->
    <ManualSlotCreateDialog
      :visible="slotDialogVisible"
      :submitting="saving"
      :department-candidates="departmentCandidates"
      :is-fallback-source="true"
      @update:visible="(v) => (slotDialogVisible = v)"
      @submit="submitCreateSlot"
    />

    <!-- ===== Dialog：人工槽铺窗 ===== -->
    <CalendarBulkDialog
      :visible="slotBulkDialogVisible"
      :submitting="saving"
      mode="MANUAL_SLOT"
      :target-id="pendingSlotBulkId ?? currentSlotId"
      :target-label="currentSlotLabel"
      @update:visible="(v) => (slotBulkDialogVisible = v)"
      @submit="submitSlotBulk"
    />
  </div>
</template>

<style scoped>
.rc-page {
  padding: 16px;
}
.rc-intro {
  margin-bottom: 12px;
}
.intro-header {
  display: flex;
  align-items: center;
  gap: 16px;
}
.intro-title {
  font-weight: 600;
  font-size: 15px;
}
.intro-sub {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.intro-help {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  border: 1px solid var(--el-border-color);
  color: var(--el-text-color-secondary);
  font-size: 11px;
  cursor: help;
  user-select: none;
}
.intro-help:hover {
  border-color: var(--el-color-primary);
  color: var(--el-color-primary);
}
.help-pop {
  max-width: 300px;
  font-size: 12px;
  line-height: 1.7;
}
.help-pop p {
  margin: 0 0 4px;
}
.help-pop-title {
  font-weight: 600;
}
.role-switcher {
  margin-left: auto;
}
.role-badge {
  cursor: pointer;
  font-size: 12px;
  letter-spacing: 0.5px;
  transition: transform 0.15s ease;
}
.role-badge:hover {
  transform: translateY(-1px);
}
.role-hint {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.rc-alert {
  margin-top: 10px;
}
.rc-error {
  margin-bottom: 12px;
}
.rc-alert :deep(code) {
  background: rgba(0, 0, 0, 0.05);
  padding: 1px 6px;
  border-radius: 3px;
  font-size: 12px;
}
.rc-toolbar {
  margin-bottom: 6px;
}
.rc-btn-gap {
  margin-left: 10px;
}
.rc-status-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin: 10px 0 12px;
  font-size: 13px;
}
.rc-status-label {
  color: var(--el-text-color-secondary);
}
.rc-hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  margin-left: 6px;
}
.rc-source-hint {
  margin-left: 0;
  margin-bottom: 8px;
}

/* 分页（10-12 RC22 收：人工槽 Tab2 接入 ElPagination） */
.rc-pagination {
  margin-top: 12px;
  justify-content: flex-end;
}

:deep(.rc-row-disabled) {
  opacity: 0.6;
}
</style>
