<!--
  APS V1 4号位 — 批量铺窗 Dialog（设备资源日历 / 人工槽日历 共用）

  依据：
    - 冻结文档 v1.4《Resource Calendar 资源日历能力补充冻结方案 v1.4》§三/§五/§九
      + §21 合并原则「相同级别重叠矛盾拒绝发布」（10-14 收：提交前拦截本级重叠）
    - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》§二.1 / §二.5

  契约（两模块同构，仅键不同）：
    - 设备：POST /api/resource-calendar/slots          body { resourceId, ...BulkWindowDraft }
    - 人工：POST /api/manual-capacity/calendar         body { manualSlotId, ...BulkWindowDraft }
    - days 1..370（后端 400 校验）；days=1 即"逐条加一天"
    - endTime 须 > startTime（后端 400 校验）

  2026-10-14 精简（用户反馈弹窗啰嗦）：
    - 撤「键 = xxx」内部Id外露、撤「本次将生成」重复摘要、撤「追加语义（不覆盖）」长提示、
      撤「现有窗口」表格（365 行与弹窗背后的页面表完全重复，还挤占视野）
    - 保留：必填项 + 时段预设 + 一条「覆盖不是追加」的说明（这是会删数据的语义，必须让用户看见）

  2026-10-14 撤除本级重叠拦截（自纠）：
    - 原拦截建立在「后端只追加不覆盖」的旧认知上，与实际相反 —— ResourceCalendarService.cs:51-59
      / ManualCapacityService.cs:154 起手即 DELETE 该区间旧窗再逐天 INSERT，注释明写
      「覆盖式铺窗…保证无重叠且重复铺窗幂等（D2 回执口径）」
    - 既然后端已保证结果无重叠且可幂等重铺，前端再拦 = 凭空阻止合法的「改时段重铺」，属功能倒退
    - 冻结 v1.4 §21「相同级别重叠矛盾拒绝发布」由后端删除语义天然满足
-->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  ElAlert,
  ElButton,
  ElDatePicker,
  ElDialog,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElSwitch,
  ElTag
} from 'element-plus'
import { BULK_DAYS_MAX, BULK_DAYS_MIN } from '@/api/aps-v1'
import type { BulkWindowDraft } from '@/api/aps-v1'
import type { FormRules } from 'element-plus'

const props = defineProps<{
  visible: boolean
  submitting?: boolean
  /** DEVICE=设备资源日历 / MANUAL_SLOT=人工能力槽日历 */
  mode: 'DEVICE' | 'MANUAL_SLOT'
  /** 铺窗对象键（resourceId / manualSlotId） */
  targetId: number | null
  /** 对象展示名（如 "MC01 注塑机-01" / "精修01（精修）"） */
  targetLabel?: string
}>()

const emit = defineEmits<{
  'update:visible': [v: boolean]
  submit: [payload: BulkWindowDraft]
}>()

/** 时段常用预设（避免手输错格式；夜班跨天会被后端拒，故不列） */
const TIME_PRESETS = [
  { label: '白班 08:00-17:00', start: '08:00:00', end: '17:00:00' },
  { label: '上午 08:00-12:00', start: '08:00:00', end: '12:00:00' },
  { label: '下午 13:00-17:00', start: '13:00:00', end: '17:00:00' }
]

const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d:[0-5]\d$/

const formRef = ref<InstanceType<typeof ElForm> | null>(null)

const form = ref<{
  startDate: string
  days: number
  startTime: string
  endTime: string
  availableFlag: boolean
  remark: string
}>({
  startDate: '',
  days: 1,
  startTime: '08:00:00',
  endTime: '17:00:00',
  availableFlag: true,
  remark: ''
})

const rules = computed<FormRules>(() => ({
  startDate: [{ required: true, message: '请选择起始日期', trigger: 'change' }],
  days: [
    {
      required: true,
      validator: (_r, v, cb) => {
        const n = Number(v)
        if (!Number.isFinite(n) || n < BULK_DAYS_MIN || n > BULK_DAYS_MAX) {
          cb(new Error(`天数须为 ${BULK_DAYS_MIN}~${BULK_DAYS_MAX} 的整数`))
          return
        }
        cb()
      },
      trigger: 'change'
    }
  ],
  startTime: [
    {
      required: true,
      validator: (_r, v, cb) => {
        if (!TIME_RE.test(String(v))) {
          cb(new Error('时间格式须为 HH:mm:ss（如 08:00:00）'))
          return
        }
        cb()
      },
      trigger: 'blur'
    }
  ],
  endTime: [
    {
      required: true,
      validator: (_r, v, cb) => {
        const end = String(v)
        if (!TIME_RE.test(end)) {
          cb(new Error('时间格式须为 HH:mm:ss（如 17:00:00）'))
          return
        }
        // 与后端一致：不允许跨天（EndTime <= StartTime → 400）
        if (end <= form.value.startTime) {
          cb(
            new Error(
              `结束时间须晚于开始时间（${form.value.startTime}）；V1 不支持跨天窗口，跨天请拆两条`
            )
          )
          return
        }
        cb()
      },
      trigger: 'blur'
    }
  ]
}))

const modeLabel = computed(() => (props.mode === 'DEVICE' ? '设备资源日历' : '人工能力槽日历'))

/* 10-14 撤除本级重叠拦截 —— 原实现基于「后端只追加不覆盖」的旧认知，与实际相反：
 *   ResourceCalendarService.cs:51-59 / ManualCapacityService.cs:154 起手就是
 *   DELETE ... WHERE ResourceId=@Id AND StartTime >= @StartDate AND StartTime < @StartDate+Days
 *   再逐天 INSERT，注释明写「覆盖式铺窗…保证无重叠且重复铺窗幂等（D2 回执口径）」。
 *   既然后端已保证结果无重叠且可幂等重铺，前端再拦就是凭空阻止合法的「改时段重铺」，属功能倒退。
 *   冻结 v1.4 §21「相同级别重叠矛盾拒绝发布」由后端删除语义天然满足。 */

function applyPreset(preset: (typeof TIME_PRESETS)[number]): void {
  form.value.startTime = preset.start
  form.value.endTime = preset.end
}

watch(
  () => props.visible,
  (vis) => {
    if (!vis) return
    form.value = {
      startDate: '',
      days: 1,
      startTime: '08:00:00',
      endTime: '17:00:00',
      availableFlag: true,
      remark: ''
    }
    formRef.value?.clearValidate()
  },
  { immediate: true }
)

function close(): void {
  emit('update:visible', false)
}

async function onSubmit(): Promise<void> {
  if (props.targetId == null || props.targetId <= 0) {
    ElMessage.error('铺窗对象无效（键缺失）')
    return
  }
  if (!formRef.value) return
  const valid = await formRef.value.validate().catch(() => false)
  if (!valid) {
    ElMessage.warning('表单未通过校验，请检查红色提示项')
    return
  }
  // 覆盖语义由后端保证（先删该区间旧窗再逐天 INSERT），前端不再做重叠拦截
  emit('submit', {
    startDate: form.value.startDate,
    days: Number(form.value.days),
    startTime: form.value.startTime,
    endTime: form.value.endTime,
    availableFlag: form.value.availableFlag,
    remark: form.value.remark?.trim() ? form.value.remark.trim() : undefined
  })
}
</script>

<template>
  <ElDialog
    :model-value="visible"
    :title="`批量铺窗 —— ${modeLabel}`"
    width="820px"
    :close-on-click-modal="false"
    :close-on-press-escape="!submitting"
    @update:model-value="(v) => emit('update:visible', v)"
  >
    <ElForm ref="formRef" :model="form" :rules="rules" label-width="130px" label-position="right">
      <ElFormItem label="铺窗对象">
        <ElTag type="primary" effect="plain">
          {{ targetLabel || '（未选择）' }}
        </ElTag>
      </ElFormItem>

      <ElFormItem label="起始日期" prop="startDate" required>
        <ElDatePicker
          v-model="form.startDate"
          type="date"
          value-format="YYYY-MM-DD"
          placeholder="选择起始日期（含当日）"
          style="width: 220px"
        />
      </ElFormItem>

      <ElFormItem label="连续天数" prop="days" required>
        <ElInputNumber v-model="form.days" :min="BULK_DAYS_MIN" :max="BULK_DAYS_MAX" :step="1" />
        <span class="field-hint">{{ BULK_DAYS_MIN }}~{{ BULK_DAYS_MAX }}；填 1 即"逐条加一天"</span>
      </ElFormItem>

      <ElFormItem label="每日时段" required>
        <div class="preset-row">
          <ElButton
            v-for="p in TIME_PRESETS"
            :key="p.label"
            size="small"
            plain
            @click="applyPreset(p)"
          >
            {{ p.label }}
          </ElButton>
        </div>
        <span class="field-hint">一键填入下方时间（不改变天数）</span>
      </ElFormItem>

      <ElFormItem label="开始时间" prop="startTime" required>
        <ElInput v-model="form.startTime" placeholder="08:00:00" style="width: 160px" />
      </ElFormItem>

      <ElFormItem label="结束时间" prop="endTime" required>
        <ElInput v-model="form.endTime" placeholder="17:00:00" style="width: 160px" />
        <span class="field-hint">须晚于开始时间；V1 一个窗口不得跨天</span>
      </ElFormItem>

      <ElFormItem label="可用标记">
        <ElSwitch v-model="form.availableFlag" active-text="可用" inactive-text="禁用" />
      </ElFormItem>

      <ElFormItem label="备注">
        <ElInput
          v-model="form.remark"
          placeholder="如：白班 / 加班 / 临时维护"
          maxlength="200"
          show-word-limit
          style="width: 420px"
        />
      </ElFormItem>

      <ElAlert type="warning" :closable="false" show-icon class="d-alert">
        <template #title>这段日期是「覆盖」，不是「追加」</template>
        提交后，该对象在
        <strong>{{ form.startDate || '起始日期' }} 起 {{ form.days }} 天</strong>
        内原有的窗口会<strong>全部删掉</strong>，再按你填的时间重新生成。
      </ElAlert>
    </ElForm>

    <template #footer>
      <ElButton :disabled="submitting" @click="close">取消</ElButton>
      <ElButton type="primary" :loading="submitting" @click="onSubmit">
        确认铺窗（{{ form.days }} 条）
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.field-hint {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.d-alert {
  margin-top: 12px;
}
</style>
