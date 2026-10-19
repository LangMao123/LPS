/**
 * APS V1 4号位 — 排程查询 API
 *
 * 接口路径与 LPS.APS.Web/Controllers/ScheduleController.cs 一致：
 *  - GET /api/schedule/versions?take=30
 *  - GET /api/schedule/gantt/{planVersionId}
 *  - GET /api/schedule/summary/{planVersionId}
 *
 * mock 切换：VITE_USE_MOCK=true 时返回 fixture，否则走真实后端
 */

import { apsHttp, APS_USE_MOCK } from './http'
import { mockGantt, mockSummary, mockVersions } from './__mocks__/fixtures'
import type { GanttDataDto, PlanVersionSummaryDto, ScheduleSummaryDto } from './types'

export const scheduleApi = {
  /** 计划版本列表（默认 30 条） */
  async getVersions(take = 30): Promise<PlanVersionSummaryDto[]> {
    if (APS_USE_MOCK) return mockVersions.slice(0, take)
    return apsHttp.get<PlanVersionSummaryDto[]>({
      url: '/api/schedule/versions',
      params: { take }
    })
  },

  /** 指定版本的甘特图数据（@owner 5号位 中转 2号位 ScheduleResult） */
  async getGantt(planVersionId: number): Promise<GanttDataDto> {
    if (APS_USE_MOCK) return mockGantt(planVersionId)
    // 10-12 RC21：30s 超时保护，dev 后端 gantt/143 第二次卡死不再拖死前端
    // - 兜底：store handleError 接 ApiError，ElMessage 提示；不静默吞
    // - 与 loadCurrent 内 Promise.allSettled 不冲突（getGantt 自身先超时抛错）
    return apsHttp.get<GanttDataDto>({
      url: `/api/schedule/gantt/${planVersionId}`,
      timeout: 30_000
    })
  },

  /** 指定版本的 KPI 概要（@owner 5号位 中转 2号位 ScheduleResult 聚合） */
  async getSummary(planVersionId: number): Promise<ScheduleSummaryDto> {
    if (APS_USE_MOCK) return mockSummary(planVersionId)
    return apsHttp.get<ScheduleSummaryDto>({
      url: `/api/schedule/summary/${planVersionId}`
    })
  }
}
