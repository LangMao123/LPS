using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R-2 设备口径留痕增强**（1号位 2026-10-16 回执 §四；载体 = `DomainSolveRequest.DeviceResourceIds`，
///   2号位 已于 r13723 落码，见 `LPS.APS.Core/Dto/DomainSolveRequest.cs:154`）。
///
/// **契约三态**（0号位 2026-10-16《第十三轮跨号位联合技术契约 v1.2 与 E1/E2/E3 正式复核意见 v1.0》
///   §5.1 ／处置矩阵 **J13-05** 之后）：
///   · `null` ⇒ **未投影** ⇒ **不产**留痕、④.6 排序键**维持含人工槽**（兼容边界，与改造前**逐字一致**）；
///   · 非 `null` ⇒ 只列**纯设备** `ResourceId` ⇒ ④.6 排序键**改取纯设备键**（J13-05 逐字：
///     「由 1号位 在**评分分支**使用该子集而非全量 `AffectedResources`」）；
///     留痕**并列**写出「纯设备」与「含人工槽」两个**绝对**键作对照；
///   · **空集 ≠ `null`** ⇒ 已投影、但本 Run 无任何纯设备 ⇒ 纯设备键恒 `0` ⇒ ④.6 判平（走 ⑤ Tie-breaker），
///     **不得**回落成 `null` 语义。
///
/// ⚠ 本类只测 `ResourceEfficiencyEvaluator` 的**四个纯函数**（`EvaluateDeviceScope` / `DescribeDeviceScope` /
///   `BuildDeviceScopeSet` / `DeviceScopeNonSetupOccupiedMinutes`）与「`Evaluate` 的默认键不受白名单影响」
///   这条**零回归**证明。**不起 Solver、不读日历、不碰数据库**。
/// ⚠ 白名单**外**的 `ResourceId` 在本文件用**任意不透明 int** 表示 —— 真实形态是人工槽合成键
///   （`ManualSlotResourceOffset + ManualSlotId`），但该**合成键规则属 2号位**（`SolverResourceProjection`）；
///   1号位 侧**不做资源身份判别、不硬编码阈值** ⇒ 本处**不抄**该常量，只把越界身份当**不透明 int**。
/// </summary>
public class DeviceResourceScopeTests
{
    /// <summary>白名单**外**的资源身份（不透明 int；真实形态 = 人工槽合成键，由 2号位 装载侧注入）。</summary>
    private const int OutOfScopeResourceId = 987654321;

    /// <summary>白名单内的两个纯设备身份。</summary>
    private const int DeviceA = 11;
    private const int DeviceB = 22;

    // ════════════════════════════════════════════════════════════════════
    //  态一：`null` ⇒ 未投影 ⇒ 不留痕（零回归）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **`null` = 未投影** ⇒ `EvaluateDeviceScope` 返回 `null`，`DescribeDeviceScope` 返回**空串**
    ///   ⇒ ④.6 留痕文本**逐字不变**（这就是「默认行为不变」的可证形式）。
    /// </summary>
    [Fact]
    public void 未投影_返回null且留痕为空串_零回归()
    {
        var tasks = SampleTasks();

        Assert.Null(ResourceEfficiencyEvaluator.EvaluateDeviceScope(tasks, deviceResourceIds: null));

        Assert.Equal(
            string.Empty,
            ResourceEfficiencyEvaluator.DescribeDeviceScope(
                incumbentDevice: null, incumbentAllNonSetup: 120m, incumbentLabel: "统一基线（未合批）",
                challengerDevice: null, challengerAllNonSetup: 80m, challengerLabel: "合批到锚#1[BATCH-7]"));
    }

    // ════════════════════════════════════════════════════════════════════
    //  态二：非 null ⇒ ④.6 排序键改取**纯设备键**（J13-05）；`Evaluate` 的默认键不受影响
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 白名单 `{DeviceA, DeviceB}`（**不含**越界身份、**不含** `null` 身份）⇒ 实测：
    ///   · **纯设备键** = `(60−10) + (30−0) = 80` 分钟；`InScope=2`、`OutOfScope=1`（越界身份一个）；
    ///   · **默认（含人工槽）键** = `(60+30+45) − (10+0+5) = 120` 分钟；
    ///   · 两者**不等** ⇒ 本用例有**鉴别性**（若白名单被忽略，纯设备键会等于 120 ⇒ 红）。
    /// ⚠ `ResourceId == null` 的非资源工序在**两个口径都**不计（`NonResourceTaskCount=1` 佐证）。
    /// </summary>
    [Fact]
    public void 已投影_纯设备键与含人工槽键并列_且Evaluate默认键不变()
    {
        var tasks = SampleTasks();

        var all = ResourceEfficiencyEvaluator.Evaluate(tasks);
        var device = ResourceEfficiencyEvaluator.EvaluateDeviceScope(
            tasks, new List<int> { DeviceA, DeviceB });

        // 默认键（含人工槽）：120 分钟；非资源 Task 1 个。
        Assert.Equal(120m, all.NonSetupOccupiedMinutes);
        Assert.Equal(1, all.NonResourceTaskCount);

        // 纯设备键：80 分钟；1 个越界身份（人工槽形态）被排除。
        Assert.NotNull(device);
        Assert.Equal(80m, device!.NonSetupOccupiedMinutes);
        Assert.Equal(2, device.InScopeTaskCount);
        Assert.Equal(1, device.OutOfScopeTaskCount);

        // ★ 鉴别性：两个键**确实不同** ⇒ 「默认仍取含人工槽那个」不是空话。
        Assert.NotEqual(all.NonSetupOccupiedMinutes, device.NonSetupOccupiedMinutes);
    }

    /// <summary>
    /// **零回归证明**：白名单**只**影响 `EvaluateDeviceScope`，**不改变** `Evaluate` 的默认键
    ///   —— 同一批任务、带/不带白名单，`Evaluate` 结果**逐字相同**。
    /// </summary>
    [Fact]
    public void 白名单不改变默认排序键_零回归()
    {
        var tasks = SampleTasks();

        var withoutWhitelist = ResourceEfficiencyEvaluator.Evaluate(tasks);
        var withWhitelistProbe = ResourceEfficiencyEvaluator.EvaluateDeviceScope(
            tasks, new List<int> { DeviceA, DeviceB });
        var afterWhitelist = ResourceEfficiencyEvaluator.Evaluate(tasks);

        Assert.NotNull(withWhitelistProbe);
        Assert.Equal(withoutWhitelist.NonSetupOccupiedMinutes, afterWhitelist.NonSetupOccupiedMinutes);
        Assert.Equal(withoutWhitelist.OccupiedMinutes, afterWhitelist.OccupiedMinutes);
        Assert.Equal(withoutWhitelist.SetupMinutes, afterWhitelist.SetupMinutes);
        Assert.Equal(withoutWhitelist.AffectedResources, afterWhitelist.AffectedResources);
    }

    /// <summary>
    /// 留痕文本必须**并列**写出两个**绝对**键，并**显式声明 ④.6 择优取纯设备键**（J13-05）——
    ///   否则读者会把「含人工槽」那个绝对值误当成决定依据（决定依据是**增量**，不是这里的绝对值）。
    /// </summary>
    [Fact]
    public void 留痕文本_并列两键并声明择优取纯设备键()
    {
        var tasks = SampleTasks();
        var device = ResourceEfficiencyEvaluator.EvaluateDeviceScope(
            tasks, new List<int> { DeviceA, DeviceB });

        var text = ResourceEfficiencyEvaluator.DescribeDeviceScope(
            device, incumbentAllNonSetup: 120m, incumbentLabel: "统一基线（未合批）",
            challengerDevice: device, challengerAllNonSetup: 120m, challengerLabel: "合批到锚#1[BATCH-7]");

        Assert.Contains("纯设备键 80", text);
        Assert.Contains("含人工槽 120", text);
        Assert.Contains("④.6 择优取纯设备键", text);

        // ★ 判别器：旧口径声明**必须消失** —— 否则新旧两句话互相打脸（「不改择优」与「择优取纯设备键」不能并存）。
        Assert.DoesNotContain("默认仍取含人工槽那个", text);
    }

    // ════════════════════════════════════════════════════════════════════
    //  态二·**热路径**：J13-05 落码点（`BuildDeviceScopeSet` ＋ `DeviceScopeNonSetupOccupiedMinutes`）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **J13-05 的落码点**：择优热路径用的纯设备键必须与留痕路径的 `EvaluateDeviceScope`
    ///   **逐字同值**（同一夹具、同一白名单 ⇒ 80 分钟；越界身份与 `ResourceId == null` 都不计）。
    ///   ⚠ 若两者口径漂移（例如热路径漏掉越界过滤），本用例红。
    /// </summary>
    [Fact]
    public void 热路径纯设备键_与留痕路径同口径()
    {
        var tasks = SampleTasks();
        var scope = ResourceEfficiencyEvaluator.BuildDeviceScopeSet(new List<int> { DeviceA, DeviceB });

        var hot = ResourceEfficiencyEvaluator.DeviceScopeNonSetupOccupiedMinutes(tasks, scope);
        var trace = ResourceEfficiencyEvaluator.EvaluateDeviceScope(tasks, new List<int> { DeviceA, DeviceB });

        Assert.NotNull(trace);
        Assert.Equal(trace!.NonSetupOccupiedMinutes, hot);
        Assert.Equal(80m, hot);

        // ★ 判别性：热路径**不得**退化成含人工槽键（120）—— 那正是 J13-05 要根除的行为。
        Assert.NotEqual(ResourceEfficiencyEvaluator.Evaluate(tasks).NonSetupOccupiedMinutes, hot);
    }

    /// <summary>
    /// **空集 ≠ `null`**（热路径侧）：空白名单 ⇒ 恒 `0`（本 Run 无纯设备 ⇒ ④.6 判平，走 ⑤ Tie-breaker），
    ///   **不得**退化成「含人工槽键」—— 那等于把人工槽又当设备打分（J13-05 逐字禁止）。
    /// </summary>
    [Fact]
    public void 热路径纯设备键_空集恒为零_不得退化成含人工槽()
    {
        var tasks = SampleTasks();

        Assert.Equal(
            0m,
            ResourceEfficiencyEvaluator.DeviceScopeNonSetupOccupiedMinutes(
                tasks, ResourceEfficiencyEvaluator.BuildDeviceScopeSet(new List<int>())));
    }

    /// <summary>
    /// `BuildDeviceScopeSet` **只做集合化、不做身份判别**：`List` / `HashSet` 两种入参结果一致。
    ///   ⚠ 这是「**同一份集合** ⇒ 留痕所见 = 评分所用」的前提（调用点复用同一个 `deviceScope`）。
    /// </summary>
    [Fact]
    public void 预建白名单_List与HashSet等价()
    {
        var tasks = SampleTasks();
        var fromList = ResourceEfficiencyEvaluator.BuildDeviceScopeSet(new List<int> { DeviceA, DeviceB });
        var fromSet = ResourceEfficiencyEvaluator.BuildDeviceScopeSet(new HashSet<int> { DeviceA, DeviceB });

        Assert.Equal(2, fromList.Count);
        Assert.Equal(2, fromSet.Count);
        Assert.Equal(
            ResourceEfficiencyEvaluator.DeviceScopeNonSetupOccupiedMinutes(tasks, fromList),
            ResourceEfficiencyEvaluator.DeviceScopeNonSetupOccupiedMinutes(tasks, fromSet));
    }

    // ════════════════════════════════════════════════════════════════════
    //  态三：空集 ≠ null（本轮最容易写错的一格）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **空集 ≠ `null`**：白名单为**空集**时 —— 已投影、但本 Run 无任何纯设备 ⇒
    ///   · 返回值**非 `null`**（**不得**回落成「未投影」语义）；
    ///   · 纯设备键 = `0`、`InScope=0`、**全部**有身份的 Task 计越界（`OutOfScope=3`）；
    ///   · 留痕文本**显式写明「无纯设备」**，避免被读成「键为 0 = 没占设备」。
    /// 本用例是「三态」中唯一**不可由前两态推出**的一格。
    /// </summary>
    [Fact]
    public void 空集已投影但无纯设备_不得回落成未投影语义()
    {
        var tasks = SampleTasks();

        var device = ResourceEfficiencyEvaluator.EvaluateDeviceScope(tasks, new List<int>());

        Assert.NotNull(device);
        Assert.Equal(0m, device!.NonSetupOccupiedMinutes);
        Assert.Equal(0, device.InScopeTaskCount);
        Assert.Equal(3, device.OutOfScopeTaskCount);   // DeviceA / DeviceB / 越界身份 三个「有身份」Task

        var text = ResourceEfficiencyEvaluator.DescribeDeviceScope(
            device, incumbentAllNonSetup: 120m, incumbentLabel: "统一基线（未合批）",
            challengerDevice: null, challengerAllNonSetup: 80m, challengerLabel: "合批到锚#1[BATCH-7]");

        Assert.Contains("无纯设备", text);
        Assert.Contains("未投影", text);   // 挑战者侧未投影 ⇒ 该侧显式写「未投影」
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 四类 Task（覆盖两个口径的**全部**分支）：
    ///   ① `DeviceA`：60 分钟、Setup 10 ⇒ 除 Setup 外 50；
    ///   ② `DeviceB`：30 分钟、Setup 0  ⇒ 除 Setup 外 30；
    ///   ③ 越界身份：45 分钟、Setup 5  ⇒ 除 Setup 外 40（**只在**「含人工槽」口径里）；
    ///   ④ `ResourceId == null`（非资源工序）：30 分钟、Setup 0 ⇒ **两个口径都不计**。
    /// ⇒ 含人工槽键 = `(60+30+45) − (10+0+5) = 120`；纯设备键（`{DeviceA, DeviceB}`）= `(60−10)+(30−0) = 80`。
    /// </summary>
    private static List<FinalTaskDraft> SampleTasks() => new()
    {
        Task(DeviceA, startHour: 8, minutes: 60, setup: 10m),
        Task(DeviceB, startHour: 9, minutes: 30, setup: 0m),
        Task(OutOfScopeResourceId, startHour: 10, minutes: 45, setup: 5m),
        Task(null, startHour: 11, minutes: 30, setup: 0m)
    };

    private static FinalTaskDraft Task(int? resourceId, int startHour, int minutes, decimal setup)
    {
        var start = new DateTime(2026, 10, 16, startHour, 0, 0);
        return new FinalTaskDraft
        {
            ResourceId = resourceId,
            PlannedStartTime = start,
            PlannedEndTime = start.AddMinutes(minutes),
            SetupTime = setup
        };
    }
}
