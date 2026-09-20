using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// P1-05：Phase5 Gap Compaction（保序前向压实）专项回归（纯内存，直接调 FiniteCapacitySolver）。
/// 实施包 §六 Phase 5 最小次级优化（Level 3，不破坏 Level 0-2）：
///   ① FORWARD：Candidate 传播扰动后，最终计划必须压实到最早可行位置（减少不必要等待/WIP）；
///   ② BACKWARD：JIT 倒排锚点不得被前拉（避免过早生产——倒排语义本身）；
///   ③ 物料 floor：Task 不得早于 Material AvailableTime（避免过早生产的物料侧约束）。
/// 保序/不可移动/依赖 floor 等其余红线由全量既有回归（ContinuityShareTests C01-C06、
/// CrossMaterialTimingConstraintTests、P0RegressionTests 等 270+ 测试）联合守护。
/// </summary>
public class Phase5CompactionTests
{
    private static readonly DateTime Day = new DateTime(2026, 9, 1);
    private static readonly DateTime PlanningStart = Day;
    private static readonly DateTime PlanningEnd = Day.AddDays(30);

    private readonly FiniteCapacitySolver _solver = new();

    // ════════════════════════════════════════════════════════════
    // ① FORWARD：扰动后压实到最早可行位置（确定性终态断言）
    // 场景：D1 双工序同资源（Setup30/加工60），日历 [8-12]+[13-17]（午休断档）；
    // 5 个填充 Task 压住 30% 警戒线 → seed 触发传播直接移动（可能把 Task 推后）；
    // Phase5 压实后终态必须是紧凑早位：OP10 加工 [8:30-9:30]、OP20 加工 [10:00-11:00]。
    // （传播 HashSet 顺序不定：一支无扰动、一支推后——压实保证两支收敛到同一紧凑终态。）
    // ════════════════════════════════════════════════════════════
    [Fact]
    public async Task FORWARD_Candidate扰动后_压实到最早可行位置()
    {
        var demands = new List<LogicalProductionDemand> { D1() };
        var ops = new List<RoutingOperation>
        {
            Op(1, "OP10", 60m, 30m), Op(1, "OP20", 60m, 30m)
        };
        for (int i = 2; i <= 6; i++)
        {
            demands.Add(Filler(i));
            ops.Add(Op(i, "FOP", 60m, 0m));
        }

        var request = Build(demands, ops,
            deps: new[] { Dep(1, "OP10", "OP20") },
            els: Concat(El(1, "OP10", 1), El(1, "OP20", 1),
                Enumerable.Range(2, 5).Select(i => El(i, "FOP", i))),
            resources: Concat(Res(1, "R1", (Day.AddHours(8), Day.AddHours(12)), (Day.AddHours(13), Day.AddHours(17))),
                Enumerable.Range(2, 5).Select(i => Res(i, $"R{i}", (Day.AddHours(8), Day.AddHours(17))))),
            calendarOverrides: new Dictionary<int, (DateTime, DateTime)[]>
            {
                [1] = new[] { (Day.AddHours(8), Day.AddHours(12)), (Day.AddHours(13), Day.AddHours(17)) },
            },
            direction: "FORWARD",
            candidate: new CandidateContext { BasePlanVersionId = 1, ChangeSeedKeys = new[] { "D1" } });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var op10 = result.FinalTasks.Single(t => t.SourceDraftId == "D1" && t.OperationCode == "OP10");
        var op20 = result.FinalTasks.Single(t => t.SourceDraftId == "D1" && t.OperationCode == "OP20");

        // 压实终态：占用贴日历起点 8:00（Setup 8:00-8:30 + 加工 8:30-9:30），OP20 紧随（9:30-10:00 Setup + 10:00-11:00 加工）
        Assert.Equal(Day.AddHours(8.5), op10.PlannedStartTime);
        Assert.Equal(Day.AddHours(9.5), op10.PlannedEndTime);
        Assert.Equal(Day.AddHours(10), op20.PlannedStartTime);
        Assert.Equal(Day.AddHours(11), op20.PlannedEndTime);
    }

    // ════════════════════════════════════════════════════════════
    // ② BACKWARD：JIT 锚点不前拉（避免过早生产）
    // 场景：倒排，due=Day+5；Task 应结束于 due（JIT），压实不得把它拉到日历起点。
    // ════════════════════════════════════════════════════════════
    [Fact]
    public async Task BACKWARD_JIT锚点_不被压实前拉()
    {
        var due = Day.AddDays(5);
        var request = Build(
            demands: new[] { Simple("D1", 1, 1, 1m, due) },
            ops: new[] { Op(1, "OP10", 60m, 0m) },
            deps: Array.Empty<RoutingDependency>(),
            els: new[] { El(1, "OP10", 1) },
            resources: new[] { Res(1, "R1", (Day, Day.AddDays(29))) },
            calendarOverrides: new Dictionary<int, (DateTime, DateTime)[]>
            {
                [1] = new[] { (Day, Day.AddDays(29)) },
            },
            direction: "BACKWARD");

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        // JIT：结束锚在 due，未被前拉到日历起点（前拉 = 过早生产 = 违反 BACKWARD 语义）
        Assert.Equal(due, task.PlannedEndTime);
        Assert.Equal(due.AddMinutes(-60), task.PlannedStartTime);
    }

    // ════════════════════════════════════════════════════════════
    // ③ 物料 floor：FORWARD 下 Task 不得早于 Material AvailableTime（避免过早生产）
    // 场景：物料 Day+2 才可用；压实 floor 含物料时间，不得拉到日历起点 Day。
    // ════════════════════════════════════════════════════════════
    [Fact]
    public async Task FORWARD_物料floor_不得早于可用时间()
    {
        var request = Build(
            demands: new[] { Simple("D1", 1, 1, 10m, PlanningStart.AddDays(20)) },
            ops: new[] { Op(1, "OP10", 6m, 0m) },   // 6min/件 × 10 件 = 60min
            deps: Array.Empty<RoutingDependency>(),
            els: new[] { El(1, "OP10", 1) },
            resources: new[] { Res(1, "R1", (Day, Day.AddDays(29))) },
            calendarOverrides: new Dictionary<int, (DateTime, DateTime)[]>
            {
                [1] = new[] { (Day, Day.AddDays(29)) },
            },
            direction: "FORWARD",
            materialSlices: new[]
            {
                new MaterialAvailabilitySlice
                {
                    AllocationSequence = 1, MaterialId = 1, FactoryId = 1,
                    Quantity = 10m, AvailableTime = Day.AddDays(2)
                }
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        // 物料 Day+2 可用 → 加工不得早于 Day+2（压实不得越物料 floor 前拉）
        Assert.Equal(Day.AddDays(2), task.PlannedStartTime);
        Assert.Equal(Day.AddDays(2).AddMinutes(60), task.PlannedEndTime);
    }

    // ─────────────────────────── 构造辅助 ───────────────────────────

    private static LogicalProductionDemand D1() => new LogicalProductionDemand
    {
        LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
        AllocationSequence = 1, DemandKey = "D1", MaterialId = 1, FactoryId = 1,
        NetOutputQty = 1m, PlannedProcessQty = 1m,
        RequiredAvailableTime = PlanningStart.AddDays(20), DemandSequence = 1
    };

    private static LogicalProductionDemand Filler(int i) => new LogicalProductionDemand
    {
        LogicalDemandKey = $"D{i}", PlanVersionId = 1L, DomainKey = "DOMAIN",
        AllocationSequence = i, DemandKey = $"D{i}", MaterialId = i, FactoryId = 1,
        NetOutputQty = 1m, PlannedProcessQty = 1m,
        RequiredAvailableTime = PlanningStart.AddDays(20), DemandSequence = i
    };

    private static LogicalProductionDemand Simple(string key, long alloc, int materialId, decimal qty, DateTime due)
        => new LogicalProductionDemand
        {
            LogicalDemandKey = key, PlanVersionId = 1L, DomainKey = "DOMAIN",
            AllocationSequence = alloc, DemandKey = key, MaterialId = materialId, FactoryId = 1,
            NetOutputQty = qty, PlannedProcessQty = qty,
            RequiredAvailableTime = due, DemandSequence = 1
        };

    private static RoutingOperation Op(int materialId, string opCode, decimal standardDuration, decimal setup)
        => new RoutingOperation
        {
            MaterialId = materialId, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
            OperationCode = opCode, StageCode = "STAGE1",
            StandardDuration = standardDuration, SetupTime = setup
        };

    private static RoutingDependency Dep(int materialId, string from, string to)
        => new RoutingDependency
        {
            MaterialId = materialId, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
            FromOperationCode = from, ToOperationCode = to, DependencyType = "ES", LagTime = 0m
        };

    private static OperationResourceEligibility El(int materialId, string opCode, int resourceId)
        => new OperationResourceEligibility
        {
            MaterialId = materialId, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
            OperationCode = opCode, ResourceId = resourceId, Priority = 1, CapacityFactor = 1m
        };

    private static ResourceDefinition Res(int id, string code, params (DateTime Start, DateTime End)[] slots)
        => new ResourceDefinition { ResourceId = id, ResourceCode = code, FactoryCode = "F1", Capacity = 1m };

    private static List<T> Concat<T>(T first, IEnumerable<T> rest)
        => new List<T>(new[] { first }.Concat(rest));

    private static List<T> Concat<T>(T a, T b, IEnumerable<T> rest)
        => new List<T>(new[] { a, b }.Concat(rest));

    private static DomainSolveRequest Build(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<RoutingOperation> ops,
        IReadOnlyList<RoutingDependency> deps,
        IReadOnlyList<OperationResourceEligibility> els,
        IReadOnlyList<ResourceDefinition> resources,
        Dictionary<int, (DateTime Start, DateTime End)[]> calendarOverrides,
        string direction,
        CandidateContext? candidate = null,
        IReadOnlyList<MaterialAvailabilitySlice>? materialSlices = null)
    {
        var calendarSlots = new List<ResourceCalendarSlot>();
        foreach (var kvp in calendarOverrides)
        {
            foreach (var (start, end) in kvp.Value)
            {
                calendarSlots.Add(new ResourceCalendarSlot
                {
                    ResourceId = kvp.Key, Start = start, End = end, IsAvailable = true
                });
            }
        }

        var deptContexts = demands.Select(d => d.MaterialId).Distinct()
            .Select(mid => new MaterialStageDepartmentContextDto
            {
                MaterialId = mid, StageCode = "STAGE1", ProductionDepartmentId = 100
            }).ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands,
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = els,
            MaterialStageDepartmentContexts = deptContexts,
            Resources = resources,
            CalendarSlots = calendarSlots,
            MaterialConstraints = materialSlices ?? Array.Empty<MaterialAvailabilitySlice>(),
            CandidateContext = candidate,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = direction,
                    AllowMerge = false,
                    AllowSplit = false
                }
            }
        };
    }
}
