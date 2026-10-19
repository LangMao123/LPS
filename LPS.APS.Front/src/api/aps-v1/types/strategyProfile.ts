/**
 * APS V1 4号位 — 策略配置 DTO
 *
 * @see lps/LPS.APS.Core/Entities/Aps/StrategyProfile.cs（主表）
 * @see lps/LPS.APS.Core/Entities/Aps/StrategyProfileVersion.cs（版本表）
 * @see lps/LPS.APS.Core/Enum/GovernanceVersionStatus.cs（六态字典 UPPER_CASE）
 * @see lps/LPS.APS.Web/Controllers/GovernanceController.cs（12 端点）
 *
 * 设计要点：
 *  - 状态用大写字符串字面量严格对齐后端 DDL CK 校验（NOT PascalCase）
 *  - 主表 StrategyProfile + 版本表 StrategyProfileVersion 分离
 *  - 发布请求体只含 changeReason（IsDefault 通过 PUT /version/{id} 提前设）
 *  - 红线：UQ_StrategyProfileVersion_DefaultPublished（每 Profile 同一时刻仅 1 个 IsDefault=1 的 PUBLISHED）
 */

import type { IsoDateTime } from './common'

/* ==================== 六态状态机（对齐 DDL CK 字面量） ==================== */

/** 策略版本六态（与 GovernanceVersionStatus.cs 严格一致） */
export const STRATEGY_VERSION_STATUSES = [
  'DRAFT',
  'SUBMITTED',
  'APPROVED',
  'PUBLISHED',
  'DISABLED',
  'ARCHIVED'
] as const

export type StrategyVersionStatus = (typeof STRATEGY_VERSION_STATUSES)[number]

/** UI 中文标签 + Tag 颜色映射 */
export interface StrategyStatusMeta {
  label: string
  tag: 'info' | 'primary' | 'success' | 'warning' | 'danger'
  effect?: 'light' | 'dark' | 'plain'
}

export const STRATEGY_STATUS_META: Record<StrategyVersionStatus, StrategyStatusMeta> = {
  DRAFT: { label: '草稿', tag: 'warning' },
  SUBMITTED: { label: '已提交', tag: 'primary' },
  APPROVED: { label: '已批准', tag: 'success' },
  PUBLISHED: { label: '已发布', tag: 'success', effect: 'dark' },
  DISABLED: { label: '已退役', tag: 'info' },
  ARCHIVED: { label: '已归档', tag: 'info', effect: 'plain' }
}

/** 哪些状态允许编辑 */
export const STRATEGY_EDITABLE_STATUSES: StrategyVersionStatus[] = ['DRAFT']

/** 哪些状态允许发布 */
export const STRATEGY_PUBLISHABLE_STATUSES: StrategyVersionStatus[] = [
  'DRAFT',
  'SUBMITTED',
  'APPROVED'
]

/** 哪些状态允许 Disable（仅 PUBLISHED 可 Disable；DRAFT 拒绝；DISABLED/ARCHIVED 幂等保护拒绝） */
export const STRATEGY_DISABLEABLE_STATUSES: StrategyVersionStatus[] = ['PUBLISHED']

/**
 * V1 实际可产生的状态（DDL 六态里只有这三个有写入路径）。
 *
 * SUBMITTED / APPROVED / ARCHIVED 是 `07 v5.1.10:3774`「V1 不做完整审批流闭环
 * （仅保留审批/发布字段为后续预留）」为后续预留的，后端无任何赋值点：
 * GovernanceVersionService 只写 Draft(:1670) / Published(:84,:131) / Disabled(:206,:237,:277)，
 * 另外三个仅出现在读取侧白名单 PublishableStatuses(:63) 与拒绝提示(:193) 中。
 *
 * KPI 不要展示这三个 —— 恒为 0 会让用户误读成「还没提交过」，而 V1 根本提交不了。
 */
export const STRATEGY_V1_REACHABLE_STATUSES: StrategyVersionStatus[] = [
  'DRAFT',
  'PUBLISHED',
  'DISABLED'
]

/* ==================== 主表 StrategyProfile ==================== */

/** 策略包主表（不存规则/参数内容，只存 Profile 元信息） */
export interface StrategyProfileDto {
  id: number
  strategyProfileCode: string
  strategyProfileName: string
  description?: string | null
  runType?: string | null
  isActive: boolean
  createdAt: IsoDateTime
  createdBy?: string | null
  updatedAt?: IsoDateTime
  updatedBy?: string | null
}

/* ==================== 版本表 StrategyProfileVersion ==================== */

/** 策略版本（绑定规则集版本 + 参数集版本，可发布可追溯）
 *
 * 10-14 起 GET /strategy-profile/{id}/versions 返回列表 DTO（3号位 A组 A4/A5），
 * 比实体多 4 列（LEFT JOIN 带出），仅列表端点有；单条端点（published-version / default）仍是实体。
 *   ruleSetVersionCode / parameterSetVersionCode —— 展示用编码（A4）
 *   ruleSetId / parameterSetId —— 父主表 Id，供编辑态一级下拉直接定位（A5，替代串行反查）
 */
export interface StrategyProfileVersionDto {
  id: number
  strategyProfileId: number
  versionCode: string
  ruleSetVersionId: number
  parameterSetVersionId: number
  status: StrategyVersionStatus
  effectiveFrom?: IsoDateTime | null
  effectiveTo?: IsoDateTime | null
  isDefault: boolean
  publishedAt?: IsoDateTime | null
  publishedBy?: string | null
  approvedAt?: IsoDateTime | null
  approvedBy?: string | null
  createdAt: IsoDateTime
  createdBy?: string | null
  /* ===== 以下 4 项仅列表端点返回 ===== */
  ruleSetVersionCode?: string | null
  parameterSetVersionCode?: string | null
  ruleSetId?: number | null
  parameterSetId?: number | null
}

/** 创建/更新 DRAFT 输入（对齐后端 StrategyProfileVersion 实体） */
export interface StrategyVersionDraftInput {
  strategyProfileId: number
  versionCode: string
  ruleSetVersionId: number
  parameterSetVersionId: number
  effectiveFrom?: IsoDateTime | null
  effectiveTo?: IsoDateTime | null
  isDefault: boolean
}

/* ==================== 发布/退役 请求体 ==================== */

/** POST /publish 请求体（3号位 DTO 只含 ChangeReason；IsDefault 由 PUT /version/{id} 提前置） */
export interface PublishStrategyVersionRequest {
  changeReason: string
}

/** POST /disable 请求体 */
export interface DisableStrategyVersionRequest {
  reason?: string | null
}

/* ==================== 校验结果 / Diff ==================== */

/** 发布前校验错误（与 PublishValidationResult.ValidationError 对齐） */
export interface ValidationErrorDto {
  code: string
  message: string
  fieldName?: string | null
  details?: string | null
}

/** 发布前校验警告 */
export interface ValidationWarningDto {
  code: string
  message: string
  fieldName?: string | null
}

/** 发布前校验结果（GET /validate 响应） */
export interface PublishValidationResultDto {
  isValid: boolean
  errors: ValidationErrorDto[]
  warnings: ValidationWarningDto[]
  validatedAt: IsoDateTime
}

/** 字段级 Diff 一行 */
export interface FieldDiffDto {
  fieldName: string
  fieldDisplayName: string
  sourceValue?: string | null
  targetValue?: string | null
  isChanged: boolean
}

/** 版本 Diff 结果（GET /diff 响应） */
export interface StrategyVersionDiffDto {
  sourceVersionId: number
  targetVersionId: number
  sourceVersionCode: string
  targetVersionCode: string
  entityType: string
  fieldDiffs: FieldDiffDto[]
  comparedAt: IsoDateTime
}

/* ==================== Run 引用追溯 ==================== */

/** Run → StrategyProfile 引用追溯（GET /trace 响应） */
export interface StrategyRunTraceDto {
  strategyProfileVersionId: number
  versionCode: string
  status: StrategyVersionStatus
  ruleSetVersionId: number
  parameterSetVersionId: number
  /** 展示用编码（10-14 起 trace 响应已带，替代裸 Id 展示） */
  ruleSetVersionCode?: string | null
  parameterSetVersionCode?: string | null
  /** 引用本版本的所有 ScheduleRun（仅展示用） */
  referencingRuns: Array<{
    runId: number
    runCode: string
    runType: string
    status: string
    createdAt: IsoDateTime
  }>
}

/* ==================== RuleSet / ParameterSet（策略 DRAFT 表单下拉源）====================
 * 策略 DRAFT 必须引用合法的 RuleSetVersion + ParameterSetVersion（后端 FK 强约束
 * FK_StrategyProfileVersion_RuleSetVersion / FK_StrategyProfileVersion_ParameterSetVersion，
 * 无则 SQL Server 抛 SqlException → 500）。前端用双级 ElSelect：先选主表 → 再选版本。
 *
 * 端点契约（已对齐 lps/LPS.APS.Web/Controllers/GovernanceController.cs）：
 *  - GET /api/governance/rule-sets                      → RuleSet[] 列表
 *  - GET /api/governance/rule-set/{ruleSetId}/versions  → RuleSetVersion[] 列表
 *  - GET /api/governance/parameter-sets                 → ParameterSet[] 列表
 *  - GET /api/governance/parameter-set/{id}/versions    → ParameterSetVersion[] 列表
 */

/** RuleSet 主表（GET /rule-sets） */
export interface RuleSetDto {
  id: number
  ruleSetCode: string
  ruleSetName: string
  description?: string | null
  isActive: boolean
  createdAt: IsoDateTime
  createdBy?: string | null
  /* ===== 以下 4 项仅「该规则集已有版本行」时才有；无版本则整个 key 缺失（WhenWritingNull）=====
     判定「是否已配版本」的唯一可靠依据就是 status 在不在——与 rule.ts 的 hasVersion 派生同源。 */
  status?: string | null
  currentVersionCode?: string | null
  draftVersionCode?: string | null
  lastChangeReason?: string | null
}

/** RuleSetVersion（GET /rule-set/{id}/versions） */
export interface RuleSetVersionDto {
  id: number
  ruleSetId: number
  versionCode: string
  status: StrategyVersionStatus
  effectiveFrom?: IsoDateTime | null
  effectiveTo?: IsoDateTime | null
  publishedAt?: IsoDateTime | null
  publishedBy?: string | null
  createdAt: IsoDateTime
}

/** ParameterSet 主表（GET /parameter-sets）
 *  2026-10-15：后端已从裸实体换型为 ParameterSetListItemDto（与 RuleSetListItemDto 同构，
 *  ParameterSetRepository.cs:81-103 OUTER APPLY 最新版本摘要），dev 实测 722 条里 3 条带版本摘要字段。
 *  未声明 updatedAt/updatedBy：SQL 虽 SELECT 了这两列，但 dev 全为 NULL，序列化时整键被省略。 */
export interface ParameterSetDto {
  id: number
  parameterSetCode: string
  parameterSetName: string
  description?: string | null
  isActive: boolean
  createdAt: IsoDateTime
  createdBy?: string | null
  /* ===== 以下 5 项仅「该参数集已有版本行」时才有；无版本则整个 key 缺失（WhenWritingNull）=====
     判定「是否已配版本」的唯一可靠依据就是 status 在不在——与 RuleSetDto / rule.ts 的 hasVersion 同源。 */
  status?: string | null
  currentVersionCode?: string | null
  draftVersionCode?: string | null
  lastChangeReason?: string | null
  publishedAt?: IsoDateTime | null
}

/** ParameterSetVersion（GET /parameter-set/{id}/versions） */
export interface ParameterSetVersionDto {
  id: number
  parameterSetId: number
  versionCode: string
  status: StrategyVersionStatus
  effectiveFrom?: IsoDateTime | null
  effectiveTo?: IsoDateTime | null
  publishedAt?: IsoDateTime | null
  publishedBy?: string | null
  createdAt: IsoDateTime
}
