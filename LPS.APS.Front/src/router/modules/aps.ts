/**
 * APS V1 4号位 — 路由模块
 *
 * 11 类页面（依据 4号位文档第二十一节 + v1.2 Domain专项扩展）：
 *  阶段 A（只读结果页）  Phase A：overview / order / gantt / explanation / run
 *  阶段 B（CTP/Candidate）Phase B：ctp / candidate
 *  阶段 C（规则参数）    Phase C：rules / domain
 *  阶段 D（异常/PI/MES）Phase D：pi / manual-eta / demand-protection
 *
 * 菜单生成：通过 router meta 自动构建（项目原有 generateRoutes 机制）
 *
 * RBAC（v1.2 §23.1 重新启用）：
 *  - 路由级守卫函数已就绪（src/router/guard.ts setupApsRouteGuard）
 *  - 按三波挂 meta：第一波（admin/draft 风险低）→ 第二波（PMC 写操作）→ 第三波（只读页）
 *  - 第一波当前已挂：domain（仅 SYSTEM_ADMIN）+ rules（持 rule 权限码）
 *  - 第二/三波待办：ctp/candidate/manual-eta/demand-protection（第二波）+ overview/order/gantt/explanation/run/pi（第三波）
 *
 * 菜单分组（10-16 4号位）：本文件全部是 **6 个顶级节点**，侧边栏渲染为 6 个一级菜单：
 *   ① /aps             APS 排产 —— 只留 4 个直达页：排产总览 / 甘特图/资源计划 / 异常与原因解释 / 审计日志
 *   ② /aps-demand      需求与供给     订单/需求计划 · 人工到货时间 · 供给来源追溯 · 需求保护释放
 *   ③ /aps-execution   排产执行       插单评估 · 候选版本确认 · 运行与下发
 *   ④ /aps-master-data 基础数据与日历  排程域定义维护 · 工艺规划模式 · 资源日历维护 · 上级工作日历
 *   ⑤ /strategy-config 策略配置 —— 汇进同一份 FrozenStrategySnapshot 的 7 页，
 *                       组内顺序 = 实际操作顺序（规则与参数维护 → 批策略 → 换型两页 → 诊断 → 发布规则集 → 策略包版本）
 *   ⑥ /rbac            角色与权限 —— 用户 / 角色 / 权限码 / 业务范围维护 4 页
 *   ②~⑥ 全是**菜单分组壳**，壳本身不是页面（Layout 包裹 + redirect 到组内首项）：
 *     - 壳**故意不挂** apsRequiredPermissions（guard.ts:72 判无权限码即放行），与现状一致
 *       （本项目菜单本就不按权限过滤，越权在点进子页时由子路由守卫拦 /403）；
 *     - 壳下叶子页一律写**绝对路径**（/aps/xxx）：Vue Router 对以 / 开头的子路径不拼父路径，
 *       页面 URL 与分组前完全一致 ⇒ 老书签、跨页 router.push、verify-rbac.mjs 的 path 断言全部零改动。
 *       面包屑保留分组名（matched = [壳节点, 该页]）：全树只有两层，不触发
 *       routerHelper.ts 的 flatMultiLevelRoutes 摊平（那会让面包屑少一层）；
 *     - 前提：routerHelper.ts 的 pathResolve 对绝对子路径原样返回，否则菜单会跳到不存在的地址。
 *   框架自带的演示版「权限管理」（router/index.ts 的 /authorization，走 mock 未接后端）已 hidden。
 */

const Layout = () => import('@/layout/Layout.vue')

/**
 * 阶段标识：影响菜单"实现状态"角标
 *  - 'A' 已实现
 *  - B 阶段 B 待开发
 *  - C 阶段 C 待开发
 *  - D 阶段 D 待开发
 */
export const apsV1Routes: AppRouteRecordRaw[] = [
  {
    path: '/aps',
    component: Layout,
    redirect: '/aps/overview',
    name: 'ApsV1',
    meta: {
      title: 'APS 排产',
      icon: 'vi-mdi:chart-gantt',
      alwaysShow: true
    },
    children: [
      /* ===== 模块入口与结果页：不埋组，直接挂在「APS 排产」下 ===== */
      {
        path: 'overview',
        component: () => import('@/views/Aps/Overview.vue'),
        name: 'ApsOverview',
        meta: {
          title: '排产总览',
          icon: 'vi-mdi:view-dashboard-outline',
          noCache: true,
          stage: 'A',
          acceptance: 'U01 / U02 / U07',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view 权限码（5 角色均持有，全员放行）
          apsRequiredPermissions: ['aps.plan.view']
        }
      },

      /* 甘特图/资源计划 —— 原属已撤销的「结果查看」组（10-16 用户裁决：不要这层壳） */
      {
        path: 'gantt',
        component: () => import('@/views/Aps/Gantt.vue'),
        name: 'ApsGantt',
        meta: {
          title: '甘特图/资源计划',
          icon: 'vi-mdi:chart-timeline',
          noTagsView: true,
          stage: 'A',
          acceptance: 'U01 / U06 / U14',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view
          apsRequiredPermissions: ['aps.plan.view']
        }
      },

      /* 异常与原因解释 —— 同上 */
      {
        path: 'explanation',
        component: () => import('@/views/Aps/Explanation.vue'),
        name: 'ApsExplanation',
        meta: {
          title: '异常与原因解释',
          icon: 'vi-mdi:alert-circle-outline',
          stage: 'A',
          acceptance: 'U09 / U20',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view
          apsRequiredPermissions: ['aps.plan.view']
        }
      },

      /* ===== 审计日志页（页面 12 §22.6 Audit / §二十四 / U42）=====
       *  - 门控用专属码 aps.audit.view（rbac.md §1.2：4 角色均持，与其余 rbac 页的
       *    aps.auth.user.edit 仅 admin.system 不同——审计是监督能力，管理层可查看）
       *  - 后端 GET /api/rbac/audit-logs（Policy=AuditView）2026-09-20 curl 实测已就绪
       *  - 10-16 用户裁决：**留在 APS 排产顶层，不进任何分组**（也不并入顶级「角色与权限」
       *    分组——那一组是「配权限」，审计是「查已发生的操作」）
       */
      {
        path: 'audit',
        component: () => import('@/views/Aps/Audit.vue'),
        name: 'ApsAudit',
        meta: {
          title: '审计日志',
          icon: 'vi-ant-design:audit-outlined',
          noCache: true,
          stage: 'E',
          acceptance: 'U42 / §22.6 Audit / §二十四',
          apsRequiredPermissions: ['aps.audit.view']
        }
      }
    ]
  },

  /* ===== 「需求与供给」分组（10-16 4号位第二轮：由 APS 排产内提到**与其平级**）=====
   *  - 本组 4 页 URL 仍是 /aps/xxx（绝对路径 ⇒ 老书签 / router.push / verify-rbac 断言零改动），
   *    壳 path='/aps-demand' 只是菜单分组，不是页面
   *  - 组内顺序 = 使用顺序：订单/需求计划（输入单据）→ 人工到货时间（供给 ETA 修正）
   *    → 供给来源追溯（在途/在库）→ 需求保护释放（冻结需求的释放通道）
   * ⚠️ 父分组**故意不挂** apsRequiredPermissions：guard.ts:72 判无权限码即放行，
   *    菜单本就不按权限过滤，越权在点进子页时由子路由守卫拦 /403。
   */
  {
    path: '/aps-demand',
    component: Layout,
    redirect: '/aps/order',
    name: 'ApsGroupDemand',
    meta: {
      title: '需求与供给',
      icon: 'vi-mdi:truck-delivery-outline',
      alwaysShow: true
    },
    children: [
      {
        path: '/aps/order',
        component: () => import('@/views/Aps/Order.vue'),
        name: 'ApsOrder',
        meta: {
          title: '订单/需求计划',
          icon: 'vi-mdi:clipboard-list-outline',
          stage: 'A',
          acceptance: 'U03 / U04 / U05',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view
          apsRequiredPermissions: ['aps.plan.view']
        }
      },
      {
        path: '/aps/manual-eta',
        component: () => import('@/views/Aps/ManualEta.vue'),
        name: 'ApsManualEta',
        meta: {
          title: '人工到货时间',
          icon: 'vi-mdi:truck-delivery-outline',
          stage: 'D',
          acceptance: 'P1-7 / 4号位文档第 11 节',
          // 第二波 RBAC：PMC 专属（人工到货 ETA 是计划员的领域操作）
          //  - 与 ManualEta.vue 内部权限码门控（has('aps.manual_eta.edit')）一致；路由级做粗粒度
          //  - v1.2 DDL：用后端系统角色码 aps.planner + aps.admin.system
          apsRequiredRoles: ['aps.planner', 'aps.admin.system']
        }
      },
      {
        path: '/aps/pi',
        component: () => import('@/views/Aps/Pi.vue'),
        name: 'ApsPi',
        meta: {
          title: '供给来源追溯',
          icon: 'vi-mdi:package-variant-closed',
          stage: 'D',
          acceptance: 'U16 / U17 / U18',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view
          apsRequiredPermissions: ['aps.plan.view']
        }
      },
      {
        path: '/aps/demand-protection',
        component: () => import('@/views/Aps/DemandProtection.vue'),
        name: 'ApsDemandProtection',
        meta: {
          title: '需求保护释放',
          icon: 'vi-mdi:shield-lock-outline',
          stage: 'D',
          acceptance: 'P1-11 / 4号位文档第 13 节',
          // 第二波 RBAC：PMC 专属（需求保护释放是计划员的领域操作）
          //  - 与 DemandProtection.vue 内部权限码门控（has('aps.demand_protection.release')）一致；
          //    路由级做粗粒度（dev seed 下 aps.planner 不持有 release 权限码，仅 aps.admin.system 持有）
          //  - v1.2 DDL：用后端系统角色码 aps.planner + aps.admin.system
          apsRequiredRoles: ['aps.planner', 'aps.admin.system']
        }
      }
    ]
  },

  /* ===== 「排产执行」分组（与「APS 排产」平级）=====
   *  - 插单评估（CTP 试算）→ 候选版本确认（真正激活一个排程结果）→ 运行与下发（落库 + 推 MES）
   */
  {
    path: '/aps-execution',
    component: Layout,
    redirect: '/aps/ctp',
    name: 'ApsGroupExecution',
    meta: {
      title: '排产执行',
      icon: 'vi-mdi:target',
      alwaysShow: true
    },
    children: [
      {
        path: '/aps/ctp',
        component: () => import('@/views/Aps/Ctp.vue'),
        name: 'ApsCtp',
        meta: {
          title: '插单评估',
          icon: 'vi-mdi:target',
          stage: 'B',
          acceptance: 'U08 / U09 / U11 / U15',
          // 第二波 RBAC：需持有 aps.ctp.evaluate 权限码（PMC / SYSTEM_ADMIN 持有）
          //  - CTP 试算是写操作；VIEWER 只持 aps.ctp.view 不持 evaluate → 被守卫拒绝
          apsRequiredPermissions: ['aps.ctp.evaluate']
        }
      },
      {
        path: '/aps/candidate',
        component: () => import('@/views/Aps/Candidate.vue'),
        name: 'ApsCandidate',
        meta: {
          title: '候选版本确认',
          icon: 'vi-mdi:compare-horizontal',
          stage: 'B',
          acceptance: 'U10 / U11 / U12 / U13 / U14',
          // 第二波 RBAC：需持有 aps.candidate.confirm 权限码（PMC / SYSTEM_ADMIN 持有）
          //  - Candidate 激活是写操作；U11/U12 WHATIF 场景已由 canActivate 字段控制按钮，路由级仅做粗粒度门控
          apsRequiredPermissions: ['aps.candidate.confirm']
        }
      },
      {
        path: '/aps/run',
        component: () => import('@/views/Aps/Run.vue'),
        name: 'ApsRun',
        meta: {
          title: '运行与下发',
          icon: 'vi-mdi:server-network',
          noCache: true,
          stage: 'A',
          acceptance: 'U01 / U02 / U19 / U16-U18',
          // 第三波 RBAC：只读页 - 需持有 aps.plan.view
          apsRequiredPermissions: ['aps.plan.view']
        }
      }
    ]
  },

  /* ===== 「基础数据与日历」分组（与「APS 排产」平级）=====
   *  - 排程域定义维护 = 排在哪（工厂/部门范围）
   *  - 工艺规划模式 = 工序走哪条工艺路线
   *  - 资源日历维护 / 上级工作日历 = 资源什么时候能排（v1.4 §三/§九 三级继承）
   *  - 与顶级「策略配置」分组的分工：本组管**资源的静态前提**，策略配置组管**排产规则与参数**；
   *    两者都是排产输入，但分别喂给不同引擎环节
   */
  {
    path: '/aps-master-data',
    component: Layout,
    redirect: '/aps/domain',
    name: 'ApsGroupMasterData',
    meta: {
      title: '基础数据与日历',
      icon: 'vi-mdi:sitemap-outline',
      alwaysShow: true
    },
    children: [
      /* 排程域定义维护（v1.2 Domain专项 Pkg-3） */
      {
        path: '/aps/domain',
        component: () => import('@/views/Aps/Domain.vue'),
        name: 'ApsDomain',
        meta: {
          title: '排程域定义维护',
          icon: 'vi-mdi:sitemap-outline',
          stage: 'C',
          acceptance: 'Domain专项 §四 / Pkg-3',
          // 第一波 RBAC：仅系统管理员可进（与 Domain.vue 按钮级 canWrite 一致）
          //  v1.2 DDL：用后端系统角色码 aps.admin.system（不再用 SYSTEM_ADMIN 别名）
          apsRequiredRoles: ['aps.admin.system']
        }
      },

      /* ===== 工艺规划属性治理（3 号位 2026-09-23 T1 交付件）=====
       *  - 后端 2 端点：GET /api/governance/routing-operations?materialId=
       *                 PUT /api/governance/routing-operations/{id}/planning-mode
       *  - 权限：view=aps.rule.view / edit=aps.rule.edit（**复用 Rule 码**，零权限种子变更）
       *  - 路由级 OR 语义：持任一权限码即可访问（VIEWER 持 view 可进）
       *  - 红线（0号位 2026-09-23 Q1 裁决）：
       *    #5 接口即契约：本路由不嵌入 Rules.vue，独立维护页（避免混入规则参数主表）
       *    #6 仅 UPDATE 值（DML），无 DDL；sp_SyncRoutingData 不 MERGE OPM
       *  - 依据：frontNew/docs/APS_V1_OPM治理API_S2S3_3号位致4号位_v1.0_20260923.md
       */
      {
        path: '/aps/operation-planning-mode',
        component: () => import('@/views/Aps/Opm.vue'),
        name: 'ApsOperationPlanningMode',
        meta: {
          title: '工艺规划模式',
          icon: 'vi-mdi:cog-transfer-outline',
          noCache: true,
          stage: 'C',
          acceptance: '3号位 T1 OPM 治理 / 字段三来源原则',
          // 路由守卫 OR 语义：持任一权限码即可访问
          //  - aps.rule.view（VIEWER / RULE_ADMIN / RULE_PUBLISHER / SYSTEM_ADMIN 全持）
          //  - aps.rule.edit（仅 RULE_ADMIN / RULE_PUBLISHER / SYSTEM_ADMIN 持）
          apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit']
        }
      },

      /* ===== 设备资源日历 / 人工能力槽维护（v1.4 三级继承第 3 级 + 5号位 2026-09-24 对接函）=====
       *  - 后端：/api/resource-calendar/*（设备 Resource + ResourceCalendarSlot）
       *          /api/manual-capacity/*（人工能力槽 ManualCapacitySlot + ManualCapacitySlotCalendar）
       *  - 单页 3 Tab：设备资源日历 / 人工能力槽 / 人工槽日历
       *  - 层级定位：工厂 → 部门 → 设备/人工槽 的**第 3 级**；第 1/2 级在「上级工作日历」页
       *  - 权限码：设备 aps.resource_calendar.{view,edit,delete}
       *            人工 aps.manual_capacity.{view,edit,delete}
       *            路由守卫 OR 语义：持 view 任一即可进（按钮级按 edit/delete 细分）
       *  - 红线（v1.4 §23 + 废止清单 O-024，已覆盖 v1.3 §九）：**「本级无窗口 = 不可排」不再是判据**；
       *    本级 MISSING 允许继承上级已认证窗口，仅三级计算后仍无权威合法窗口才判不可排；
       *    「禁止按 7×24 缺省兜底」红线未变，只是适用层级后移到"三级皆无"
       */
      {
        path: '/aps/resource-calendar',
        component: () => import('@/views/Aps/ResourceCalendar.vue'),
        name: 'ApsResourceCalendar',
        meta: {
          title: '资源日历维护',
          icon: 'vi-mdi:calendar-clock-outline',
          noCache: true,
          stage: 'C',
          acceptance: 'v1.3 §三/§五 / 5号位 2026-09-24 对接函',
          apsRequiredPermissions: ['aps.resource_calendar.view', 'aps.manual_capacity.view']
        }
      },

      /* ===== 工厂/部门上级工作日历（v1.4 §三 / §九 + 5号位 2026-10-13 对接函）=====
       *  - 后端：/api/production-work-calendar/*（6 端点：list/upsertWeekly/createException/closeOpen/closeRow/deleteRow）
       *  - 三态语义：WORK 显式可用 / OFF 显式停机（绝不被上级 WORK 复活）/ MISSING = 无行（继承上级）
       *  - 三级继承：FACTORY → DEPARTMENT → 资源（设备 / 人工槽），StageCode 不是固定第 4 层
       *  - 权限码：暂复用 aps.resource_calendar.{view,edit,delete}（5号位 函 §〇.2：G4 前不新增码）
       *            路由守卫 OR 语义：持 view 可进；按钮级按 edit/delete 细分
       *  - ⚠️ 状态：v1.4 = G1 候选，G4 公告前本路由供联调/备料；上线后按 G4 终版微调
       */
      {
        path: '/aps/production-work-calendar',
        component: () => import('@/views/Aps/ProductionWorkCalendar.vue'),
        name: 'ApsProductionWorkCalendar',
        meta: {
          title: '上级工作日历',
          icon: 'vi-mdi:calendar-week-outline',
          noCache: true,
          stage: 'C',
          acceptance: 'v1.4 §三/§九 / 5号位 2026-10-13 PWC 函',
          apsRequiredPermissions: ['aps.resource_calendar.view']
        }
      }
    ]
  },

  /* ===== 策略配置分组（10-16 4号位：把「排产前要先配什么」的 7 个页面归堆）=====
   * 这 7 页配的东西**全部汇进同一份 FrozenStrategySnapshot**，是排产的直接输入：
   *   规则集版本 → ① 需求优先级 + ⑦ 换型规则（明确 / 默认两页写的就是 ⑦）
   *   参数集版本 → ②~⑥ 五主题 + ⑧ 批策略（⑧ 由「批策略」单独一页产出，Rules.vue 的主题页不含它）
   * 排产时引擎只认「策略包版本」这一个入口（FrozenStrategySnapshotProvider 以
   * strategyProfileVersionId 为键，反查 StrategyProfileVersion.RuleSetVersionId / ParameterSetVersionId），
   * 两者都必须 PUBLISHED ⇒ **发布是两级**，组内顺序即实际配置顺序。
   *
   * 10-16 用户要求移出 APS 排产、与其平级 ⇒ 本条是 apsV1Routes 的**顶级兄弟节点**，
   *   用 Layout 包裹（与 /aps 同级，菜单渲染为可展开的一级菜单）。
   * ⚠️ 子路由一律用**绝对路径**（/aps/xxx）而非相对路径：Vue Router 对以 / 开头的子路径不再拼接父路径，
   *    父节点 path='/strategy-config' 只是菜单分组壳，7 页 URL 仍是 /aps/xxx ⇒ 老书签、
   *    SetupUncovered.vue 的 router.push、Rules.vue:764 的跳转、verify-rbac.mjs 的 path 断言全部零改动。
   *    vue-router 4.5 实测：父路径非前缀时 /aps/rules 的 matched 仍是 [本节点, 该页]，
   *    Layout 会正常渲染（4.0 及以前不成立，若将来升级 vue-router 需重测）。
   * ⚠️ 菜单 index 依赖 routerHelper.ts 的 pathResolve 拼父路径 —— 已同步改为「绝对子路径原样返回」，
   *    否则菜单会跳到 /strategy-config/aps/rules。改动前全仓无绝对子路由，故既有菜单零影响。
   * ⚠️ 父路由**故意不挂** apsRequiredPermissions：guard.ts:72 判无权限码即放行，
   *    与现状一致（本项目菜单本就不按权限过滤，越权在点进去时由子路由守卫拦）。
   */
  {
    path: '/strategy-config',
    component: Layout,
    name: 'ApsStrategyConfig',
    redirect: '/aps/strategy-profile',
    meta: {
      title: '策略配置',
      icon: 'vi-mdi:tune-variant',
      alwaysShow: true
    },
    children: [
      {
        path: '/aps/rules',
        component: () => import('@/views/Aps/Rules.vue'),
        name: 'ApsRules',
        meta: {
          title: '规则与参数维护',
          icon: 'vi-mdi:cog-outline',
          stage: 'C',
          acceptance: 'U21 / U22',
          // 第一波 RBAC：需持有 rule.edit / rule.publish 权限码任一（OR 语义）
          //  - RULE_ADMIN（持 aps.rule.edit / publish）/ RULE_PUBLISHER（同）/ SYSTEM_ADMIN（持全部）均可访问
          //  - VIEWER / PMC 无 rule.* 权限码 → 被守卫拒绝，跳 /403
          apsRequiredPermissions: ['aps.rule.edit', 'aps.rule.publish']
        }
      },

      /* --- ② 配 ⑧ 批策略（Material + 部门维度的可拆批 / 可合批）--- */
      {
        path: '/aps/task-split-rule-config',
        component: () => import('@/views/Aps/TaskSplitRuleConfig.vue'),
        name: 'ApsTaskSplitRuleConfig',
        meta: {
          title: '批策略',
          icon: 'vi-mdi:stack-exchange',
          noCache: true,
          stage: 'C',
          acceptance: '3号位 v1.0 契约 / Batch Policy 治理 / DB-003',
          // 路由守卫 OR 语义：持任一权限码即可访问（与 OPM 一致，复用 Rule 码）
          apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit']
        }
      },

      /* ===== ③ 换型规则两页（Setup 专项指导 v1.2 最终收口版 §四 / §五 / §19.2）=====
       * 后端 13 端点全在 /api/governance/setup-rules/* + /api/governance/operation-resource-eligibility
       * 权限码：aps.setup.{view, edit, publish}（不要用 aps.rule.* 错位）
       *   - VIEWER 仅持 view，可读
       *   - aps.admin.aps 持 view + edit + publish，可维护 DRAFT + 发布
       *   - aps.admin.system 持全部
       * 业务键（§五）：部门 / 大工艺 / 当前工序 / 设备 / 前产品 / 后产品
       *   - 命中优先级：明确产品转换规则 > 当前工序+当前设备默认规则 > 0 分钟兜底
       *   - 同产品连续生产 = 0 分钟（除非 A→A 的 EXACT 显式配置）
       *   - 换型分钟 > 0
       *   - 仅 DRAFT 状态的规则集版本可编辑
       */
      {
        path: '/aps/setup/exact',
        component: () => import('@/views/Aps/SetupExact.vue'),
        name: 'ApsSetupExact',
        meta: {
          title: '明确产品转换',
          icon: 'vi-mdi:swap-horizontal-bold',
          noCache: true,
          stage: 'C',
          acceptance: 'Setup v1.2 §五 / §19.2',
          apsRequiredPermissions: ['aps.setup.view', 'aps.setup.edit', 'aps.setup.publish']
        }
      },
      {
        path: '/aps/setup/default',
        component: () => import('@/views/Aps/SetupDefault.vue'),
        name: 'ApsSetupDefault',
        meta: {
          title: '默认换型规则',
          icon: 'vi-mdi:cog-outline',
          noCache: true,
          stage: 'C',
          acceptance: 'Setup v1.2 §四 / §19.2',
          apsRequiredPermissions: ['aps.setup.view', 'aps.setup.edit', 'aps.setup.publish']
        }
      },

      /* --- ④ 只读诊断：哪对产品转换没规则（0 分钟兜底），从这里回填上面两页 --- */
      {
        path: '/aps/setup/uncovered',
        component: () => import('@/views/Aps/SetupUncovered.vue'),
        name: 'ApsSetupUncovered',
        meta: {
          title: '缺失规则查询',
          icon: 'vi-mdi:help-circle-outline',
          noCache: true,
          stage: 'C',
          acceptance: 'Setup v1.2 §19.2',
          apsRequiredPermissions: ['aps.setup.view']
        }
      },

      /* --- ⑤ 对比两版差异 + **第一级发布**：发布规则集版本 --- */
      {
        path: '/aps/setup/diff',
        component: () => import('@/views/Aps/SetupDiff.vue'),
        name: 'ApsSetupDiff',
        meta: {
          title: '规则版本对比',
          icon: 'vi-mdi:file-compare-outline',
          noCache: true,
          stage: 'C',
          acceptance: 'Setup v1.2 §19.2',
          apsRequiredPermissions: ['aps.setup.view', 'aps.setup.publish']
        }
      },

      /* ===== ⑥ **第二级发布**：策略包版本（总开关）=====
       * @see frontNew/docs/project_strategy_profile_audit.md（10-10 审查报告 + 7 项问题）
       * 后端 12 端点全在 /api/governance/strategy-profile/* + /api/governance/strategy-profiles
       * 6 态状态机 + IsDefault 红线（UQ_StrategyProfileVersion_DefaultPublished）
       * 权限码：aps.strategy.{view, edit, publish}（DDL 真值见 PermissionCodes.cs:54-58）
       *   - VIEWER 仅持 view，可读
       *   - aps.admin.aps 持 view + edit，可维护 DRAFT
       *   - aps.admin.system 持全部，可发布
       * 菜单名取冻结文档 07 v5.1.10 §5.6「StrategyProfileVersion（策略包版本表）」的原词，
       * 与「规则集版本」「参数集版本」对齐；分组名保留「策略配置」。
       */
      {
        path: '/aps/strategy-profile',
        component: () => import('@/views/Aps/StrategyProfiles.vue'),
        name: 'ApsStrategyProfiles',
        meta: {
          title: '策略包版本',
          icon: 'vi-mdi:tune-variant',
          noCache: true,
          stage: 'C',
          acceptance: 'v1.6 4号位 补登',
          // 路由守卫 OR 语义：持任一权限码即可访问（VIEWER 持 view 可进）
          apsRequiredPermissions: ['aps.strategy.view', 'aps.strategy.edit', 'aps.strategy.publish']
        }
      }
    ]
  },

  /* ===== 「角色与权限」分组（10-16 4号位：4 个 RBAC 维护页归堆，与「APS 排产」「策略配置」平级）=====
   *  - 用户管理 / 角色管理（第一版）/ 权限码只读 / 业务范围维护（第二版）
   *  - 后端类级 [Authorize(Policy = PermissionCodes.AuthManage = "aps.auth.user.edit")]，
   *    前端 meta 必挂同一码；用 'aps.auth.role.edit' 会前端放行 / 后端 403 错位
   *  - 组内顺序 = 配置顺序：先有用户 → 再给用户配角色 → 看角色能拿哪些权限码 → 配角色的数据可见范围
   *
   * ⚠️ 同「策略配置」分组：这是 apsV1Routes 的**顶级兄弟节点**（Layout 包裹），
   *    4 个子路由写**绝对路径**（/aps/rbac-*），父路径 /rbac 只是菜单分组壳 ⇒ URL 与搬动前完全一致，
   *    verify-rbac.mjs 按 path 断言的 12 条零改动。
   * ⚠️ 框架自带的演示版「权限管理」（router/index.ts 的 /authorization，走 mock 未接后端）已 hidden，
   *    见该文件同处注释。真正的角色权限维护只有本组这 4 页。
   * ⚠️ 父路由**故意不挂** apsRequiredPermissions：与另两个分组同理，菜单不按权限过滤，
   *    越权在点进去时由子路由守卫拦（guard.ts:72 判父节点无权限码即放行）。
   */
  {
    path: '/rbac',
    component: Layout,
    name: 'ApsRbac',
    redirect: '/aps/rbac-users',
    meta: {
      title: '角色与权限',
      icon: 'vi-mdi:shield-lock-outline',
      alwaysShow: true
    },
    children: [
      {
        path: '/aps/rbac-users',
        component: () => import('@/views/Aps/RbacUsers.vue'),
        name: 'ApsRbacUsers',
        meta: {
          title: '用户管理',
          icon: 'vi-mdi:account-cog-outline',
          noCache: true,
          stage: 'E',
          acceptance: 'F-G5 / RBAC 管理 UI',
          apsRequiredPermissions: ['aps.auth.user.edit']
        }
      },
      {
        path: '/aps/rbac-roles',
        component: () => import('@/views/Aps/RbacRoles.vue'),
        name: 'ApsRbacRoles',
        meta: {
          title: '角色管理',
          icon: 'vi-mdi:shield-account-outline',
          noCache: true,
          stage: 'E',
          acceptance: 'F-G5 / RBAC 管理 UI',
          apsRequiredPermissions: ['aps.auth.user.edit']
        }
      },
      {
        path: '/aps/rbac-permissions',
        component: () => import('@/views/Aps/RbacPermissions.vue'),
        name: 'ApsRbacPermissions',
        meta: {
          title: '权限码',
          icon: 'vi-mdi:key-outline',
          noCache: true,
          stage: 'E',
          acceptance: 'F-G5 / RBAC 管理 UI',
          apsRequiredPermissions: ['aps.auth.user.edit']
        }
      },
      {
        path: '/aps/rbac-scopes',
        component: () => import('@/views/Aps/RbacScopes.vue'),
        name: 'ApsRbacScopes',
        meta: {
          title: '业务范围维护',
          icon: 'vi-mdi:map-marker-radius-outline',
          noCache: true,
          stage: 'E',
          acceptance: 'F-G5 / RBAC 管理 UI',
          apsRequiredPermissions: ['aps.auth.user.edit']
        }
      }
    ]
  }
]

export default apsV1Routes
