using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **2026-10-12 整改判别性单测**（答 2号位《APS_V1_20261012_技术事项汇总回执》§一 / §九#2、#3）。
///
/// 两块：
///   · **①「严格改法」** —— <see cref="StageTimingNodeBuilder.GetRoutelessStages"/> 不再把
///     `RouteCode == "DEFAULT"` 写死；改为按**需求已选中路径**（末端真实 Task 的 `RouteCode/PathId`）过滤，
///     无选中路径则退回 `DEFAULT` 口径（⇒ 归一化 ON 的今天**严格 no-op**）。
///   · **§2.3** —— <see cref="PhaseFiveCompression.ComposeErrorMessage"/> 把 Batch Policy 硬失败明细并入
///     `DomainSolveResult.ErrorMessage`，**带稳定前缀且**有界**。
///
/// 全部纯内存，**不触库**（用户红线：`LPS.APS.Tests/Integration/` 直连生产库，只跑 Unit）。
/// </summary>
public class StrictRoutelessStagesAndErrorMessageTests
{
    private const int MaterialId = 100;

    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0);

    private static DomainSolveRequest RequestWith(params RoutingOperation[] ops)
        => new() { RoutingOperations = ops };

    private static RoutingOperation Op(string routeCode, int pathId, string stageCode, int materialId = MaterialId)
        => new()
        {
            MaterialId = materialId,
            RouteCode = routeCode,
            PathId = pathId,
            StageCode = stageCode,
            OperationCode = $"{routeCode}-{stageCode}",
        };

    private static IReadOnlyDictionary<int, HashSet<string>> Effective(params string[] stages)
        => new Dictionary<int, HashSet<string>>
        {
            [MaterialId] = new HashSet<string>(stages, StringComparer.Ordinal),
        };

    // ══════════════════════════════════════════════════════════════════
    // ① 严格改法
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 判别性反证：**多路径**下必须**只认选中路径**。
    /// 旧实现（写死 `RouteCode == "DEFAULT"`）在真实 RouteCode 载荷下 ⇒ `stagesWithRouting` 恒空
    /// ⇒ 两个 Stage **都被判「无路由」**（凭空多出提前期）。整改后：选中 R1 ⇒ 只有 S2 无路由。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_MultiPath_FollowsSelectedRoute_NotHardCodedDefault()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R2", 2, "S2"));

        var effective = Effective("S1", "S2");

        var selectedR1 = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", 1);
        var selectedR2 = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R2", 2);

        Assert.Equal(new[] { "S2" }, selectedR1.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "S1" }, selectedR2.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 判别性反证：**无选中路径**（需求整条没排下 / `RouteCode` 未填）⇒ **退回 `DEFAULT` 口径**。
    /// 真实 RouteCode 载荷 + 无选中 ⇒ 两个 Stage 都算无路由 —— 与**整改前逐字一致**（旧行为）。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_NoSelectedRoute_FallsBackToLegacyDefaultSemantics()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R2", 2, "S2"));

        var effective = Effective("S1", "S2");

        var noSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, null, null);
        var blankSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "  ", null);

        Assert.Equal(new[] { "S1", "S2" }, noSelection.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "S1", "S2" }, blankSelection.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// **零回归反证**：归一化 ON（全部 `DEFAULT/1`）时，「传选中路径」与「不传」结果**逐元素相同**
    /// ⇒ 本改动对当前生产载荷是**严格 no-op**。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_NormalizedDefaultPayload_SelectedAndLegacyAreIdentical()
    {
        var request = RequestWith(
            Op("DEFAULT", 1, "S1"),
            Op("DEFAULT", 1, "S2"));

        var effective = Effective("S1", "S2", "S3");

        var withSelection = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "DEFAULT", 1);
        var legacy = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, null, null);

        Assert.Equal(new[] { "S3" }, withSelection.ToArray());
        Assert.Equal(withSelection.OrderBy(s => s, StringComparer.Ordinal), legacy.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// 边界：选中路径给定时**只按 `RouteCode` 过滤**，`PathId` 缺省（null）不参与 ⇒ 同 RouteCode 的
    /// 两条 Path 都算「有路由」（保守方向：**少**判无路由 ⇒ 不凭空多出提前期）。
    /// </summary>
    [Fact]
    public void GetRoutelessStages_SelectedRouteWithoutPathId_MatchesAllPathsOfThatRoute()
    {
        var request = RequestWith(
            Op("R1", 1, "S1"),
            Op("R1", 2, "S2"),
            Op("R2", 1, "S3"));

        var effective = Effective("S1", "S2", "S3");

        var result = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", null);

        Assert.Equal(new[] { "S3" }, result.ToArray());
    }

    /// <summary>边界：范围内无该物料 / 空集 ⇒ 返回空表（不抛）。</summary>
    [Fact]
    public void GetRoutelessStages_MaterialNotInScope_ReturnsEmpty()
    {
        var request = RequestWith(Op("R1", 1, "S1"));
        var effective = new Dictionary<int, HashSet<string>>
        {
            [999] = new HashSet<string>(new[] { "S1" }, StringComparer.Ordinal),
        };

        var result = StageTimingNodeBuilder.GetRoutelessStages(MaterialId, effective, request, "R1", 1);

        Assert.Empty(result);
    }

    /// <summary>
    /// **生产入口级判别性用例**（经 <see cref="StageTimingNodeBuilder.BuildCore"/>，**两版签名通用**
    /// ⇒ 可作整改前/后的对照）：真实 `RouteCode` 载荷 + 末端 Task 带选中路径 ⇒
    /// **不得**把选中路径上、位于末端**之后**的 Stage 当成「无路由」。
    ///
    /// 载荷：`R1` 上有 `S1`(末端) 与 `S2`；`S3` 无工序；Stage 链 `S1(1)→S2(2)→S3(3)`。
    ///   · 旧实现（写死 `op.RouteCode != "DEFAULT"` ⇒ 恒假）⇒ routeless = {S1,S2,S3}
    ///     ⇒ 末端 `S1` 之后产出 **`S2`、`S3` 两段**（凭空多出 `S2` 的 5h）。
    ///   · 整改后（按选中路径 `R1` 过滤）⇒ routeless = {S3} ⇒ **恰一段**。
    /// </summary>
    [Fact]
    public void BuildCore_RealRouteCodePayload_DoesNotTreatSelectedRouteStagesAsRouteless()
    {
        var request = new DomainSolveRequest
        {
            PlanningStart = Start,
            PlanningEnd = Start.AddYears(100),
            LogicalProductionDemands = new[]
            {
                new LogicalProductionDemand
                {
                    LogicalDemandKey = "D1",
                    MaterialId = MaterialId,
                    AllocationSequence = 1,
                },
            },
            StageLeadTimes = new[]
            {
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S2", LeadTimeHours = 5m, MatchLevel = "DEPT_EXACT" },
                new StageLeadTimeFact { MaterialId = MaterialId, StageCode = "S3", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
            },
            StageSequenceChains = new[]
            {
                new StageSequenceChain
                {
                    MaterialId = MaterialId,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "S1", StageSeq = 1 },
                        new() { StageCode = "S2", StageSeq = 2 },
                        new() { StageCode = "S3", StageSeq = 3 },
                    },
                },
            },
            RoutingOperations = new[]
            {
                Op("R1", 1, "S1"),
                Op("R1", 1, "S2"),
            },
        };

        var stages = Effective("S1", "S2", "S3");
        var terminals = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal)
        {
            ["D1"] = new FinalTaskDraft
            {
                FinalDraftId = "F1",
                SourceDraftId = "D1",
                MaterialId = MaterialId,
                StageCode = "S1",
                OperationCode = "R1-S1",
                RouteCode = "R1",
                PathId = 1,
                PlannedStartTime = Start,
                PlannedEndTime = Start.AddHours(8),
            },
        };

        var fact = Assert.Single(StageTimingNodeBuilder.BuildCore(request, stages, terminals));

        // 只有 S3 无路由 ⇒ 恰一段；S2 在选中路径 R1 上有工序 ⇒ 不得再被当无路由
        var seg = Assert.Single(fact.StageTimings);
        Assert.Equal("S3", seg.StageCode);
        Assert.Equal(Start.AddHours(8 + 7), fact.CompletionTime);
    }

    // ══════════════════════════════════════════════════════════════════
    // §2.3 ErrorMessage 组合
    // ══════════════════════════════════════════════════════════════════

    private static InitialScheduleResult ResultWith(
        IEnumerable<KeyValuePair<string, string>>? hardFailures = null,
        IEnumerable<string>? conflicts = null,
        IEnumerable<string>? missing = null,
        IEnumerable<string>? unavailable = null)
    {
        var result = new InitialScheduleResult();
        if (hardFailures is not null)
        {
            foreach (var kv in hardFailures)
            {
                result.BatchPolicyHardFailures[kv.Key] = kv.Value;
            }
        }

        if (conflicts is not null)
        {
            result.BatchPolicyConflicts.AddRange(conflicts);
        }

        if (missing is not null)
        {
            result.BatchPolicyMissingDemandKeys.AddRange(missing);
        }

        if (unavailable is not null)
        {
            result.BatchPolicyUnavailableDemandKeys.AddRange(unavailable);
        }

        return result;
    }

    /// <summary>三段皆空 ⇒ `null`（**不产出空串**，避免把「无失败」写成空 `ErrorMessage`）。</summary>
    [Fact]
    public void ComposeErrorMessage_AllSegmentsEmpty_ReturnsNull()
    {
        var result = ResultWith();

        Assert.Null(PhaseFiveCompression.ComposeErrorMessage(null, null, result));
        Assert.Null(PhaseFiveCompression.ComposeErrorMessage("  ", "  ", result));
    }

    /// <summary>前缀顺序稳定：`[Phase5] … | [Technical] … | [BatchPolicy] …`（2号位 要按前缀拆列）。</summary>
    [Fact]
    public void ComposeErrorMessage_SegmentOrderIsStableAndPrefixed()
    {
        var result = ResultWith(
            hardFailures: new Dictionary<string, string> { ["D-1"] = "BATCH_POLICY_CONFLICT" },
            conflicts: new[] { "[D-1] PI_LEGAL_QTY_CONTRACT_PENDING：缺 Stage 合法自由量上下文" });

        var text = PhaseFiveCompression.ComposeErrorMessage(
            "[Phase5] 硬约束校验失败: X", "[Technical] Y", result);

        Assert.NotNull(text);
        Assert.StartsWith("[Phase5] ", text);
        Assert.Contains(" | [Technical] ", text);
        Assert.Contains(" | [BatchPolicy] ", text);
        Assert.True(
            text!.IndexOf("[Phase5]", StringComparison.Ordinal)
                < text.IndexOf("[Technical]", StringComparison.Ordinal));
        Assert.True(
            text.IndexOf("[Technical]", StringComparison.Ordinal)
                < text.IndexOf("[BatchPolicy]", StringComparison.Ordinal));
        // 明细正文（真实原因）确实被带出来，不再只留在零读者的分列表里
        Assert.Contains("PI_LEGAL_QTY_CONTRACT_PENDING", text);
    }

    /// <summary>
    /// **有界性反证**（2号位 实测 21,772/21,787 会被拦下 ⇒ 无界拼接会写出兆字节字符串）：
    /// 2,000 条硬失败 ⇒ 输出仍**远小于**明细总量，且**示例最多 3 条**、每条截断。
    /// </summary>
    [Fact]
    public void ComposeErrorMessage_LargeBatchPolicySet_IsBounded()
    {
        const int n = 2000;
        var hardFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var key = $"D-{i:D5}";
            hardFailures[key] = "BATCH_POLICY_CONFLICT";
            conflicts.Add($"[{key}] PI_LEGAL_QTY_CONTRACT_PENDING：" + new string('X', 400));
        }

        var result = ResultWith(hardFailures: hardFailures, conflicts: conflicts);

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains($"[BatchPolicy] 硬失败需求 {n} 条", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 2000", text);
        // 有界：远小于 2000 × 400 字符的明细总量
        Assert.True(text!.Length < 2000, $"ErrorMessage 未被有界化，长度 = {text.Length}");
        // 示例恰 3 条（`" ; "` 出现 2 次分隔）
        Assert.Equal(2, text.Split(" ; ").Length - 1);
        // 单条示例被截断
        Assert.Contains("…", text);
    }

    /// <summary>分类计数逐项正确（`BATCH_POLICY_MISSING` 与 `BATCH_POLICY_CONFLICT` 分列）。</summary>
    [Fact]
    public void ComposeErrorMessage_CountsEachHardFailureClassSeparately()
    {
        var result = ResultWith(hardFailures: new Dictionary<string, string>
        {
            ["D-1"] = "BATCH_POLICY_MISSING",
            ["D-2"] = "BATCH_POLICY_MISSING",
            ["D-3"] = "BATCH_POLICY_CONFLICT",
        });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains("硬失败需求 3 条", text);
        Assert.Contains("BATCH_POLICY_MISSING 2", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 1", text);
    }

    /// <summary>
    /// **第三类单列**：`BATCH_POLICY_STATE_UNAVAILABLE`（2026-10-13 新增 —— 0号位 文件4 §3.2／§3.3
    ///   明令「状态不可获得」**不得**并入 `MISSING`）⇒ 必须与另两类**分列计数**，不得被吞进任一旧桶。
    /// </summary>
    [Fact]
    public void ComposeErrorMessage_CountsStateUnavailableSeparately()
    {
        var result = ResultWith(hardFailures: new Dictionary<string, string>
        {
            ["D-1"] = "BATCH_POLICY_MISSING",
            ["D-2"] = "BATCH_POLICY_STATE_UNAVAILABLE",
            ["D-3"] = "BATCH_POLICY_STATE_UNAVAILABLE",
            ["D-4"] = "BATCH_POLICY_CONFLICT",
        });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.Contains("硬失败需求 4 条", text);
        Assert.Contains("BATCH_POLICY_MISSING 1", text);
        Assert.Contains("BATCH_POLICY_CONFLICT 1", text);
        Assert.Contains("BATCH_POLICY_STATE_UNAVAILABLE 2", text);
    }

    /// <summary>
    /// 明细取回：命中 ⇒ 返回该条；未命中 / 分列表为空 ⇒ `null`（**不伪造**）。
    /// **三个**分列表都要查（缺策略走 `BatchPolicyMissingDemandKeys`；状态不可获得走
    /// `BatchPolicyUnavailableDemandKeys`）。
    /// </summary>
    [Fact]
    public void FindBatchPolicyDetail_ResolvesFromEitherList_OrReturnsNull()
    {
        var result = ResultWith(
            conflicts: new[] { "[D-1] CONFLICT-TEXT" },
            missing: new[] { "[D-2] MISSING-TEXT" },
            unavailable: new[] { "[D-4] UNAVAILABLE-TEXT" });

        Assert.Equal("[D-1] CONFLICT-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-1"));
        Assert.Equal("[D-2] MISSING-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-2"));
        Assert.Equal("[D-4] UNAVAILABLE-TEXT", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-4"));
        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(result, "D-3"));

        var empty = ResultWith();
        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(empty, "D-1"));
    }

    /// <summary>
    /// 前缀匹配是**严格**的（`[D-1] ` 不得命中 `[D-10] `）—— 否则会把别的需求的明细当成自己的。
    /// </summary>
    [Fact]
    public void FindBatchPolicyDetail_PrefixMatchIsExact_NotSubstring()
    {
        var result = ResultWith(conflicts: new[] { "[D-10] TEN" });

        Assert.Null(PhaseFiveCompression.FindBatchPolicyDetail(result, "D-1"));
        Assert.Equal("[D-10] TEN", PhaseFiveCompression.FindBatchPolicyDetail(result, "D-10"));
    }

    /// <summary>只有 `[BatchPolicy]` 段时，输出**不以** `|` 开头（无多余分隔符）。</summary>
    [Fact]
    public void ComposeErrorMessage_BatchPolicyOnly_HasNoLeadingSeparator()
    {
        var result = ResultWith(
            hardFailures: new Dictionary<string, string> { ["D-1"] = "BATCH_POLICY_CONFLICT" },
            conflicts: new[] { "[D-1] X" });

        var text = PhaseFiveCompression.ComposeErrorMessage(null, null, result);

        Assert.NotNull(text);
        Assert.StartsWith("[BatchPolicy] ", text);
        Assert.DoesNotContain(" | ", text);
    }
}
