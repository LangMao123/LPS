using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// T-04 契约① **消费链隔离 fixture**：把 2号位 投影出的 <see cref="ExecutionConstraint"/> 直接喂给
/// 1号位 <see cref="FiniteCapacitySolver"/>，验证「装载给 1号位 的数据正确」。
///
/// `PM回复1010.md` §T-04 逐字：「用**隔离 fixture 验证消费链**；**没有真实ACTIVE样本也不应阻止开发和单测**。」
/// 本组即该条：不连库、不落库、不调参，只验载荷形状与消费端契约是否对得上。
///
/// 【本组锁住的两条硬契约】
///   ① 正向：`DraftId` == 本次 Run 的 `LogicalDemandKey` ⇒ 1号位 解出真实路径身份、继承锁定
///      Resource/Start/End 产出 FinalTask，`Success=true`。
///   ② 反向：`DraftId` 挂成旧 `TaskNo`（跨版本原样搬运）⇒ 1号位 `PhaseTwoInitialScheduler` 的
///      `lockedGraph` 解不出（`demand == null` ⇒ 图解析整段跳过）⇒ `lockedNode == null`
///      ⇒ **`TechnicalFailure`**（输入完整性问题，非「排不下」）。
///      这条以前只在文档里写着，本组把它变成可执行断言 —— 也正是 <see cref="ExecutionConstraintProjector"/>
///      必须**重算** DraftId 而不能透传 <c>[Task].TaskNo</c> 的机器可验证理由。
/// </summary>
public class ExecutionConstraintLoadToPosition1Tests
{
    private static readonly DateTime Day = new(2026, 9, 1);
    private const int MaterialId = 1;
    private const int DepartmentId = 100;
    private const int ResourceId = 1;

    /// <summary>本 Run 的真实需求键（A/B 桶带 /WO: 后缀）。</summary>
    private const string DemandKey = "ORD-1001|MAT-1|STAGE1|OP10/WO:W1";

    // ─────────────────────── 正向：投影出的锚点可被 1号位 消费 ───────────────────────

    [Fact]
    public async Task 投影出的锚点_DraftId取本次需求键_被1号位正常继承()
    {
        var demands = new List<LogicalProductionDemand> { Demand(DemandKey) };
        var baseRows = new List<BasePlanAnchorRow>
        {
            AnchorRow(taskId: 11, resourceId: ResourceId,
                      start: Day.AddHours(14), end: Day.AddHours(15))
        };

        var projection = ExecutionConstraintProjector.Project(demands, baseRows);
        projection.Constraints.Should().HaveCount(1);

        var result = await new FiniteCapacitySolver().SolveAsync(Build(demands, projection.Constraints));

        result.Success.Should().BeTrue(result.ErrorMessage);

        var task = result.FinalTasks.Single(t => t.SourceDraftId == DemandKey);
        task.ResourceId.Should().Be(ResourceId);
        task.PlannedStartTime.Should().Be(Day.AddHours(14));
        task.PlannedEndTime.Should().Be(Day.AddHours(15));
    }

    // ─────────────────────── 反向：DraftId 错挂 ⇒ 1号位 TechnicalFailure ───────────────────────

    [Fact]
    public async Task 锚点DraftId挂成旧TaskNo_被1号位判TechnicalFailure()
    {
        var demands = new List<LogicalProductionDemand> { Demand(DemandKey) };

        // 模拟「把上一版 Task 原样搬运」的错误载荷：DraftId = 旧 TaskNo。
        var wrongConstraints = new List<ExecutionConstraint>
        {
            new()
            {
                DraftId        = "T-OLD-999",
                ResourceId     = ResourceId,
                LockedStart    = Day.AddHours(14),
                LockedEnd      = Day.AddHours(15),
                ConstraintType = ExecutionConstraintTypes.DispatchedImmovable,
                StageCode      = "STAGE1",
                OperationCode  = "OP10",
                LockedQuantity = 1m,
                LockedNetOutputQty = 1m,
                LockedPlannedProcessQty = 1m,
                TaskKey        = null
            }
        };

        var result = await new FiniteCapacitySolver().SolveAsync(Build(demands, wrongConstraints));

        // `DomainSolveResult` 无 `TechnicalFailure` 属性；1号位 把它折进 `Success=false` +
        // `ErrorMessage` 的 `[Technical] ` 前缀（`PhaseFiveCompression.ComposeErrorMessage`）。
        result.Success.Should().BeFalse("DraftId 挂不上本次需求 ⇒ 1号位 输入完整性失败");
        result.ErrorMessage.Should().Contain("[Technical]");

        // 投影器正是为了消灭这种载荷：它产出的 DraftId 一定等于本次 LogicalDemandKey。
        var projection = ExecutionConstraintProjector.Project(demands, new List<BasePlanAnchorRow>
        {
            AnchorRow(taskId: 11, resourceId: ResourceId,
                      start: Day.AddHours(14), end: Day.AddHours(15))
        });
        projection.Constraints.Single().DraftId.Should().Be(DemandKey);
    }

    // ─────────────────────── 负向：约束键撞车 ⇒ 1号位 输入契约冲突（Fail Closed）───────────────────────

    [Fact]
    public async Task 同DraftId加StageOp重复_被1号位判输入契约冲突()
    {
        var demands = new List<LogicalProductionDemand> { Demand(DemandKey) };

        ExecutionConstraint MakeConstraint() => new()
        {
            DraftId        = DemandKey,
            ResourceId     = ResourceId,
            LockedStart    = Day.AddHours(14),
            LockedEnd      = Day.AddHours(15),
            ConstraintType = ExecutionConstraintTypes.Frozen,
            StageCode      = "STAGE1",
            OperationCode  = "OP10",
            LockedQuantity = 1m,
            LockedNetOutputQty = 1m,
            LockedPlannedProcessQty = 1m
        };

        // 重复键在 1号位 PhaseOneConstraintBuilder.BuildLockedTasks 处 **受控 Fail Closed**：
        // 抛出的 `SolverInputContractException`（`LPS.APS.Scheduling` 内 `internal`）被
        // `FiniteCapacitySolver.SolveAsync` 捕获 ⇒ 转成 `Success=false` + 「输入契约不满足（Fail Closed）」，
        // **不向调用方抛异常**。故 2号位 投影器必须自己先去重 ——
        // 见 ExecutionConstraintProjectorTests.同DraftId加StageOp撞键_保留首条并记DUPLICATE_KEY。
        var duplicated = new List<ExecutionConstraint> { MakeConstraint(), MakeConstraint() };

        var result = await new FiniteCapacitySolver().SolveAsync(Build(demands, duplicated));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("输入契约不满足");
    }

    // ─────────────────────── fixtures ───────────────────────

    private static LogicalProductionDemand Demand(string key)
        => new()
        {
            LogicalDemandKey   = key,
            PlanVersionId      = 1L,
            DomainKey          = "DOMAIN",
            AllocationSequence = 1,
            DemandKey          = key,
            OrderId            = 1001,
            MaterialId         = MaterialId,
            FactoryId          = 1,
            StartStageCode     = "STAGE1",
            StartOperationCode = "OP10",
            NetOutputQty       = 1m,
            PlannedProcessQty  = 1m,
            RequiredAvailableTime = Day.AddDays(20),
            DemandSequence     = 1
        };

    private static BasePlanAnchorRow AnchorRow(long taskId, int? resourceId, DateTime start, DateTime end)
        => new()
        {
            TaskId            = taskId,
            OrderId           = 1001,
            MaterialId        = MaterialId,
            StageCode         = "STAGE1",
            OperationCode     = "OP10",
            ResourceId        = resourceId,
            PlannedStartTime  = start,
            PlannedEndTime    = end,
            Quantity          = 1m,
            PlannedProcessQty = 1m,
            TaskNo            = "T-OLD-999",
            // 执行事实载体：已下发（该 Task 挂着 MES 侧已存在的工单）。
            MESWorkOrderNo    = "WO-MES-9988",
            ConstraintType    = ExecutionConstraintTypes.DispatchedImmovable
        };

    private static DomainSolveRequest Build(
        List<LogicalProductionDemand> demands,
        IReadOnlyList<ExecutionConstraint> constraints)
        => new()
        {
            PlanVersionId  = 1,
            ScheduleRunId  = 42,
            DomainKey      = "DOMAIN",
            PlanningStart  = Day,
            PlanningEnd    = Day.AddDays(30),
            LogicalProductionDemands = demands,
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DepartmentId,
                    RouteCode = "DEFAULT", OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 60m, SetupTime = 0m
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DepartmentId,
                    RouteCode = "DEFAULT", OperationCode = "OP10",
                    ResourceId = ResourceId, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = "STAGE1", ProductionDepartmentId = DepartmentId }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = ResourceId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResourceId, Start = Day.AddHours(8), End = Day.AddHours(18), IsAvailable = true }
            },
            ExecutionConstraints = constraints.ToList(),
            MaterialConstraints  = new List<MaterialAvailabilitySlice>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(new[] { MaterialId }, DepartmentId),
                SetupTransitionRules = new List<SetupTransitionRuleSnapshot>()
            }
        };
}
