/**
 * APS V1 4号位 — Candidate 列表 / 对比 / 确认 / 激活 API
 *
 * 全部接后端**已部署**端点，无 mock 分支（VITE_USE_MOCK 在 .env.base/.env.dev/.env.pro/.env.test
 * 均为 false，且项目规矩是页面不造假数据，故 2026-10-16 把 4 份手写 Candidate 桩整体删除）。
 *
 * @owner 列表 5号位；对比/确认/激活 3号位
 * @see lps/LPS.APS.Core/Dto/OverviewDtos.cs（CandidateBriefDto / OverviewCandidateSummaryDto）
 * @see lps/LPS.APS.Application/Services/Query/Dto/CandidateComparisonDto.cs
 * @see lps/LPS.APS.Web/Controllers/GovernanceController.cs:544-613
 */

import { apsAxiosRaw, apsHttp, ApiError } from './http'
import type { ApiResponse } from './types'
import { isApsSuccessCode } from './types'
import type {
  CandidateActionAck,
  CandidateActivateInput,
  CandidateComparisonDto,
  CandidateConfirmInput,
  OverviewCandidateSummaryDto
} from './types'

/**
 * 取写端点的 message 回执。
 * 后端两个写端点返回 `ApiResponse.Ok(提示语)`（data=null），常规 post() 的 unwrap 会把
 * message 丢掉只剩 null —— 这里直连实例拿完整信封，并把非成功码抛成 ApiError。
 */
async function postForAck(url: string, data: unknown): Promise<CandidateActionAck> {
  const res = await apsAxiosRaw.post<ApiResponse<null>>(url, data)
  const payload = res.data
  if (!isApsSuccessCode(payload.code)) {
    throw new ApiError(
      payload.message || '请求失败',
      payload.code,
      payload.traceId,
      payload.timestamp
    )
  }
  return { message: payload.message || '操作成功', serverTime: payload.timestamp }
}

export const candidateApi = {
  /**
   * Candidate 列表（5号位）
   *  - GET /api/overview/candidate-summary → { pendingCount, candidates: CandidateBriefDto[] }
   *  - 2026-10-16 换端点：此前绕道调 `/api/governance/runs?status=CANDIDATE`（返回 ScheduleRun，
   *    与页面所需字段无一相同），导致列表恒空、且拿不到 compare-with 需要的 basePlanVersionId。
   *    正式端点的 CandidateBriefDto 两个字段都有，绕道不再必要。
   *  - 列表**不含** diff 统计（DTO 注释明示），影响订单/新增延期只在对比详情里展示。
   */
  async list(): Promise<OverviewCandidateSummaryDto> {
    return apsHttp.get<OverviewCandidateSummaryDto>({ url: '/api/overview/candidate-summary' })
  },

  /**
   * 候选 vs 基准 对比（3号位）
   *  - GET /api/governance/plan-version/{candidatePlanVersionId}/compare-with/{basePlanVersionId}
   *  - 双路径参数；basePlanVersionId 由列表项 CandidateBriefDto.basePlanVersionId 提供
   *  - 返回仅含任务变化汇总 / 影响汇总 / 双方版本概要，无订单级明细、无根因列表
   */
  async getComparison(
    candidatePlanVersionId: number,
    basePlanVersionId: number
  ): Promise<CandidateComparisonDto> {
    return apsHttp.get<CandidateComparisonDto>({
      url: `/api/governance/plan-version/${candidatePlanVersionId}/compare-with/${basePlanVersionId}`
    })
  },

  /**
   * 人工确认（3号位）
   *  - POST /api/governance/plan-version/{id}/confirm-candidate，body 仅 remark
   *  - CTP / INSERT_IMPACT_ANALYSIS 产生的 WHATIF 候选由 canActivate=false 挡住，页面不给按钮
   */
  async confirm(input: CandidateConfirmInput): Promise<CandidateActionAck> {
    return postForAck(
      `/api/governance/plan-version/${input.candidatePlanVersionId}/confirm-candidate`,
      { remark: input.remark }
    )
  },

  /**
   * 激活（CANDIDATE → ACTIVE，每排程域单一正式采用版本，3号位）
   *  - POST /api/governance/plan-version/{id}/activate-candidate，ActivateCandidateRequest 为空 body
   *  - 权限码 aps.candidate.activate
   *  - 错误：400（非 CANDIDATE）/ 403（无权限或越权）/ 409（该域已存在其它 ACTIVE）
   */
  async activate(input: CandidateActivateInput): Promise<CandidateActionAck> {
    return postForAck(
      `/api/governance/plan-version/${input.candidatePlanVersionId}/activate-candidate`,
      {}
    )
  }
}
