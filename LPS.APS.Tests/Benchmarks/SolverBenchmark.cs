using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Benchmarks;

/// <summary>
/// P1-02 item1 性能标定工装（0号位 裁决项1：1号位 按性能测试提「默认值+允许范围」，3号位 ParameterSetVersion 治理）。
/// 基准口径（0号位 冻结）：10 万 Task / 90 天 / 夜间约 15 分钟总目标；
/// 日历口径（2号位 20260920 确认现状）：「整计划期单窗口」7×24——真实班次日历接入后需按生产日窗口重测。
///
/// 默认 Skip（不进 CI）；手动运行方式：
///   1) 注释掉 Skip 参数后 dotnet test --filter "FullyQualifiedName~SolverBenchmark"
///   2) 规模经环境变量覆盖：LPS_BENCH_RESOURCES / LPS_BENCH_TASKS_PER_RESOURCE / LPS_BENCH_RULE_DENSITY
/// 默认规模 = 50 资源 × 200 Task/资源 = 1 万 Task（先验证工装与量级外推，正式标定跑 200×500=10万）。
///
/// 输出指标：构造耗时 / SolveAsync 总耗时（Summary.ElapsedMs）/ Task 数 / ΣSetup / 延期数 / 未排数——
/// 标定 SetupSearchBudget/SetupMaxNeighborhoodTries 时对照「ΣSetup 降幅 vs 耗时」曲线取点。
/// </summary>
public class SolverBenchmark
{
    private readonly ITestOutputHelper _output;

    public SolverBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "性能标定工装：手动启用（见类注释）。默认不进 CI，避免拖慢全量测试。")]
    public async Task 夜间FULL基准_FORWARD_单窗口日历()
    {
        await RunBenchmark("FORWARD");
    }

    [Fact(Skip = "性能标定工装：手动启用（见类注释）。BACKWARD 对照基线（Phase5 压实/序列优化门控跳过）。")]
    public async Task 夜间FULL基准_BACKWARD_对照()
    {
        await RunBenchmark("BACKWARD");
    }

    /// <summary>工装自检（常开，微型规模 &lt;1s）：场景构造器 + 求解链路可跑通、数量闭合。</summary>
    [Fact]
    public async Task 工装自检_微型场景可跑通()
    {
        var request = BuildScenario(resources: 2, tasksPerResource: 5, ruleDensity: 0.1, "FORWARD", seed: 7);
        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(10, result.FinalTasks.Count);
        Assert.Empty(result.UnscheduledTasks);
    }

    /// <summary>
    /// 正式标定矩阵（0号位 裁决项1：1号位 按性能测试提「默认值+允许范围」→ 3号位 ParameterSetVersion）：
    /// 10 万 Task（200 资源 × 500）FORWARD 下扫 SetupSearchBudget / SetupMaxNeighborhoodTries，
    /// 输出「ΣSetup 降幅 vs 耗时」表——修订值的实证依据。基线点 budget=1（≈关闭序列优化）。
    /// 手动启用：注释 Skip 后 dotnet test --filter "FullyQualifiedName~标定矩阵"（预计 7 点 × 单点耗时）。
    /// </summary>
    [Fact(Skip = "正式标定矩阵：手动启用（10万 Task × 7 点，预计 5~20 分钟，不进 CI）。")]
    public async Task 标定矩阵_预算敏感性_10万Task()
    {
        int resources = EnvInt("LPS_BENCH_RESOURCES", 200);
        int tasksPerResource = EnvInt("LPS_BENCH_TASKS_PER_RESOURCE", 500);
        double ruleDensity = EnvDouble("LPS_BENCH_RULE_DENSITY", 0.05);

        var points = new (int Budget, int Tries)[]
        {
            (1, 50),                                          // 基线 ≈ 无序列优化
            (100, 50), (500, 50), (2000, 50), (5000, 50),     // budget 扫描
            (500, 20), (500, 150)                             // 停滞上限扫描
        };

        double baselineSetup = -1;
        foreach (var (budget, tries) in points)
        {
            var request = BuildScenario(resources, tasksPerResource, ruleDensity, "FORWARD",
                seed: 20260920, searchBudget: budget, maxStaleTries: tries);

            var sw = Stopwatch.StartNew();
            var result = await new FiniteCapacitySolver().SolveAsync(request);
            sw.Stop();
            Assert.True(result.Success, result.ErrorMessage);

            double sumSetup = result.FinalTasks.Sum(t => (double)t.SetupTime);
            if (baselineSetup < 0) baselineSetup = sumSetup;
            double dropPercent = baselineSetup > 0 ? (baselineSetup - sumSetup) / baselineSetup * 100.0 : 0;

            _output.WriteLine(
                $"budget={budget,5} tries={tries,4} | solve={sw.ElapsedMilliseconds,9:N0}ms | " +
                $"ΣSetup={sumSetup,12:N0}min | 降幅={dropPercent,5:F2}% | " +
                $"Tasks={result.FinalTasks.Count:N0} Unscheduled={result.UnscheduledTasks.Count:N0}");
        }
    }

    private async Task RunBenchmark(string direction)
    {
        int resources = EnvInt("LPS_BENCH_RESOURCES", 50);
        int tasksPerResource = EnvInt("LPS_BENCH_TASKS_PER_RESOURCE", 200);
        double ruleDensity = EnvDouble("LPS_BENCH_RULE_DENSITY", 0.05);
        int budget = EnvInt("LPS_BENCH_BUDGET", 500);          // 标定自变量：SetupSearchBudget
        int tries = EnvInt("LPS_BENCH_TRIES", 50);             // 标定自变量：SetupMaxNeighborhoodTries
        int materialPool = EnvInt("LPS_BENCH_MATERIAL_POOL", 500);  // 物料池（同产品重复度 = 序列优化增益来源）

        var sw = Stopwatch.StartNew();
        var request = BuildScenario(resources, tasksPerResource, ruleDensity, direction,
            seed: 20260920, searchBudget: budget, maxStaleTries: tries, materialPool: materialPool);
        var buildMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var solver = new FiniteCapacitySolver();
        var result = await solver.SolveAsync(request);
        var solveMs = sw.ElapsedMilliseconds;

        var tasks = result.FinalTasks;
        double sumSetup = tasks.Sum(t => (double)t.SetupTime);
        var demandByKey = request.LogicalProductionDemands.ToDictionary(d => d.LogicalDemandKey);
        int delayed = tasks
            .GroupBy(t => t.SourceDraftId)
            .Count(g => demandByKey.TryGetValue(g.Key, out var d) &&
                        g.Max(t => t.PlannedEndTime) > d.RequiredAvailableTime);

        _output.WriteLine($"[Benchmark {direction}] 规模: {resources} 资源 × {tasksPerResource} Task/资源, 规则密度 {ruleDensity:P0}, budget={budget}, tries={tries}");
        _output.WriteLine($"  构造耗时:   {buildMs,10:N0} ms");
        _output.WriteLine($"  SolveAsync: {solveMs,10:N0} ms  (Summary.ElapsedMs={result.Summary.ElapsedMs:N0})");
        _output.WriteLine($"  Success={result.Success}  FinalTasks={tasks.Count:N0}  Unscheduled={result.UnscheduledTasks.Count:N0}");
        _output.WriteLine($"  ΣSetup={sumSetup:N0} min  延期需求数={delayed:N0}  ExplanationFacts={result.ExplanationFacts.Count:N0}");
        _output.WriteLine($"  夜间15分钟红线: {(solveMs <= 900_000 ? "✅ 达标" : "❌ 超时")} (含全部五阶段; 标定口径见类注释)");

        Assert.True(result.Success, result.ErrorMessage);
    }

    /// <summary>
    /// 确定性场景构造（seeded LCG，同参同景可重放）：
    /// - 物料池 materialPool（默认 500）：需求循环取物料 → 同产品重复生产（真实工厂形态，
    ///   序列优化的「同产品相邻」增益来源；池=需求数时 DEFAULT 主导 → ΣSetup 与顺序无关 → 优化零空间，
    ///   首轮 10 万标定已实证该退化形态）；
    /// - 每个物料一道 OP10（30~60min，qty=1），主资源 + 1 备份资源（资格两选）；
    /// - 需求按轮转分配到资源 → 每资源约 tasksPerResource 个 Task（可移动段原料）；
    /// - 交期 = PlanningStart + 5~85 天（部分紧张 → 激活延期保护路径）；
    /// - Setup 规则：EXACT 物料对按 ruleDensity 生成（10~60min，覆盖物料池），每资源 DEFAULT=30；
    /// - 日历：整计划期单窗口（生产现状口径）；
    /// - searchBudget/maxStaleTries：写入 SolverStrategy.Setup（标定「ΣSetup 降幅 vs 耗时」曲线的自变量）。
    /// </summary>
    public static DomainSolveRequest BuildScenario(
        int resources, int tasksPerResource, double ruleDensity, string direction, long seed,
        int searchBudget = 500, int maxStaleTries = 50, int materialPool = 500)
    {
        var rng = new DeterministicRandom(seed);
        var planningStart = new DateTime(2026, 10, 1);
        var planningEnd = planningStart.AddDays(90);

        int demandCount = resources * tasksPerResource;
        int poolSize = Math.Min(materialPool, demandCount);

        var demands = new List<LogicalProductionDemand>(demandCount);
        var ops = new List<RoutingOperation>(poolSize);
        var els = new List<OperationResourceEligibility>(demandCount * 2);
        var deptContexts = new List<MaterialStageDepartmentContextDto>(poolSize);

        // 物料级结构（Routing/资格/部门上下文按物料生成一次——资格是物料级主数据，同物料多需求共享；
        // 重复键会让 Phase1 资格字典构建崩溃）
        var durationByMaterial = new Dictionary<int, int>();
        for (int m = 1; m <= poolSize; m++)
        {
            int duration = 30 + rng.Next(31);   // 30~60 min
            durationByMaterial[m] = duration;
            ops.Add(new RoutingOperation
            {
                MaterialId = m,
                ProductionDepartmentId = 100,
                RouteCode = "DEFAULT",
                OperationCode = "OP10",
                StageCode = "STAGE1",
                StandardDuration = duration,
                SetupTime = 0m   // 已废止字段，置 0（规则查找是唯一 Setup 来源）
            });
            int primaryRes = (m % resources) + 1;
            int backupRes = ((m + 1) % resources) + 1;
            els.Add(MakeEligibility(m, primaryRes, 1));
            if (backupRes != primaryRes)
                els.Add(MakeEligibility(m, backupRes, 2));
            deptContexts.Add(new MaterialStageDepartmentContextDto
            {
                MaterialId = m,
                StageCode = "STAGE1",
                ProductionDepartmentId = 100
            });
        }

        for (int i = 0; i < demandCount; i++)
        {
            int materialId = (i % poolSize) + 1;          // 物料池循环 → 同产品重复生产
            int dueDay = 5 + rng.Next(81);                // 交期 5~85 天（部分紧张）

            demands.Add(new LogicalProductionDemand
            {
                LogicalDemandKey = $"D{i + 1}",
                PlanVersionId = 1L,
                DomainKey = "BENCH",
                AllocationSequence = i + 1,
                DemandKey = $"D{i + 1}",
                MaterialId = materialId,
                FactoryId = 1,
                NetOutputQty = 1m,
                PlannedProcessQty = 1m,
                RequiredAvailableTime = planningStart.AddDays(dueDay),
                DemandSequence = i + 1
            });
        }

        var resourceDefs = new List<ResourceDefinition>(resources);
        var calendarSlots = new List<ResourceCalendarSlot>(resources);
        for (int r = 1; r <= resources; r++)
        {
            resourceDefs.Add(new ResourceDefinition
            {
                ResourceId = r,
                ResourceCode = $"R{r}",
                FactoryCode = "F1",
                Capacity = 1m
            });
            // 生产现状口径：整计划期单窗口 7×24（真实班次日历接入后按生产日窗口重测）
            calendarSlots.Add(new ResourceCalendarSlot
            {
                ResourceId = r,
                Start = planningStart,
                End = planningEnd,
                IsAvailable = true
            });
        }

        // Setup 规则：EXACT 物料对按密度随机 + 每资源 DEFAULT=30
        var rules = new List<SetupTransitionRuleSnapshot>();
        int ruleMaterialCap = poolSize;   // 规则面 = 物料池（同产品重复生产形态下全部有效）
        for (int a = 1; a <= ruleMaterialCap; a++)
        {
            for (int b = 1; b <= ruleMaterialCap; b++)
            {
                if (a == b) continue;
                if (rng.Next(10000) >= ruleDensity * 10000) continue;
                rules.Add(new SetupTransitionRuleSnapshot
                {
                    ProductionDepartmentId = 100,
                    StageCode = "STAGE1",
                    OperationCode = "OP10",
                    ResourceId = (a % resources) + 1,
                    FromMaterialId = a,
                    ToMaterialId = b,
                    RuleType = "EXACT",
                    SetupMinutes = 10 + rng.Next(51)   // 10~60 min
                });
            }
        }
        for (int r = 1; r <= resources; r++)
        {
            rules.Add(new SetupTransitionRuleSnapshot
            {
                ProductionDepartmentId = 100,
                StageCode = "STAGE1",
                OperationCode = "OP10",
                ResourceId = r,
                RuleType = "DEFAULT",
                SetupMinutes = 30m
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = seed,
            DomainKey = "BENCH",
            PlanningStart = planningStart,
            PlanningEnd = planningEnd,
            LogicalProductionDemands = demands,
            RoutingOperations = ops,
            OperationResourceEligibility = els,
            MaterialStageDepartmentContexts = deptContexts,
            Resources = resourceDefs,
            CalendarSlots = calendarSlots,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = direction,
                    AllowMerge = false,
                    AllowSplit = false
                },
                SolverStrategy = new SolverStrategyBlock
                {
                    Setup = new SetupParams
                    {
                        SetupSearchBudget = searchBudget,
                        SetupMaxNeighborhoodTries = maxStaleTries
                    }
                },
                SetupTransitionRules = rules
            }
        };
    }

    private static OperationResourceEligibility MakeEligibility(int materialId, int resourceId, int priority)
        => new OperationResourceEligibility
        {
            MaterialId = materialId,
            ProductionDepartmentId = 100,
            RouteCode = "DEFAULT",
            OperationCode = "OP10",
            ResourceId = resourceId,
            Priority = priority,
            CapacityFactor = 1m
        };

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static double EnvDouble(string name, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
