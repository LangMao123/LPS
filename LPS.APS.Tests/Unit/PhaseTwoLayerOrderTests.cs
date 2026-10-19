using System;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using FrozenLayer = LPS.APS.Scheduling.Solvers.PhaseTwoInitialScheduler.FrozenLayer;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 冻结次级目标**层序**的纯函数级反证（0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》§二.3）。
///
/// 判词逐字：「1号位回执说"Setup不同必导致最终完成时间不同，因此不能出现完成时间相同但Setup不同"的断言
///   不属于普适数学事实…**不得因此把这种场景一概登记'业务上不可构造'**。先对排序比较函数添加
///   『完工与上层交付相同、真实Setup不同、经济偏离方向相反』的**纯函数级反证**，再尝试用真实合法日历
///   构造Solver级反例；如特定Solver架构不能形成该形态，应报告技术限制及测试覆盖范围，
///   **不能修改业务目标替代码开脱**。」
///
/// 本文件即该「纯函数级反证」：直接喂入
///   <see cref="PhaseTwoInitialScheduler.PreferMergeByFrozenLayers"/> 的**完整输入元组**，
/// 逐层验证冻结层序（① 可行 → ② 履约 → ③ 交期 → ④ Setup → ④.5 经济软偏离 → ④.6 资源效率 → ⑤ 判平取独立排程）
/// 的**先后**与**方向**。纯内存、无 Context、无日历、不触库。
///
/// ⚠ 本文件**只**主张层序；**不**主张 Solver 级能构造出同一形态（那由
///   `StageBatchMergeTests.M_14` 与既有 M-11/M-12/M-13 覆盖）。
/// </summary>
public class PhaseTwoLayerOrderTests
{
    private static readonly DateTime T = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime T1 = T.AddHours(1);

    /// <summary>默认「完全判平」输入：可行/按期/同完工/同 Setup/经济偏离 (0,0)。</summary>
    private static bool Prefer(
        bool separateFeasible = true,
        bool mergeFeasible = true,
        long delaySeparate = 0,
        long delayMerge = 0,
        string? direction = null,
        DateTime? separateEnd = null,
        DateTime? mergeEnd = null,
        decimal separateSetup = 5m,
        decimal mergeSetup = 5m,
        (decimal Range, decimal Preferred) separateEconDev = default,
        (decimal Range, decimal Preferred) mergeEconDev = default,
        decimal? separateNonSetupOccupiedMinutes = null,
        decimal? mergeNonSetupOccupiedMinutes = null)
        => PhaseTwoInitialScheduler.PreferMergeByFrozenLayers(
            separateFeasible,
            mergeFeasible,
            delaySeparate,
            delayMerge,
            direction ?? SchedulingDirectionResolver.Forward,
            separateEnd ?? T,
            mergeEnd ?? T,
            separateSetup,
            mergeSetup,
            separateEconDev,
            mergeEconDev,
            separateNonSetupOccupiedMinutes,
            mergeNonSetupOccupiedMinutes);

    /// <summary>
    /// 同 <see cref="Prefer"/>，但取**落点层身份**（R2-C：区分「到达④.6」与「④.6决定采纳」）。
    /// </summary>
    private static (FrozenLayer Layer, bool MergeWins) Layer(
        bool separateFeasible = true,
        bool mergeFeasible = true,
        long delaySeparate = 0,
        long delayMerge = 0,
        string? direction = null,
        DateTime? separateEnd = null,
        DateTime? mergeEnd = null,
        decimal separateSetup = 5m,
        decimal mergeSetup = 5m,
        (decimal Range, decimal Preferred) separateEconDev = default,
        (decimal Range, decimal Preferred) mergeEconDev = default,
        decimal? separateNonSetupOccupiedMinutes = null,
        decimal? mergeNonSetupOccupiedMinutes = null)
        => PhaseTwoInitialScheduler.ResolveStageMerge(
            separateFeasible,
            mergeFeasible,
            delaySeparate,
            delayMerge,
            direction ?? SchedulingDirectionResolver.Forward,
            separateEnd ?? T,
            mergeEnd ?? T,
            separateSetup,
            mergeSetup,
            separateEconDev,
            mergeEconDev,
            separateNonSetupOccupiedMinutes,
            mergeNonSetupOccupiedMinutes);

    // ════════════════════════════════════════════════════════════════════
    // PL_01（0号位 §二.3 逐字要求的四元组）：**完工与上层交付相同、真实 Setup 不同、经济偏离方向相反**
    //   ⇒ 第 ④ 层（Setup）必须**压过**第 ④.5 层（经济软偏离），**两个方向都测**。
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void PL_01a_完工同交付同_Setup更小但经济偏离更差_须由第4层Setup胜出()
    {
        var mergeWins = Prefer(
            separateEnd: T, mergeEnd: T,                 // ③ 判平：完工相同、均按期
            separateSetup: 10m, mergeSetup: 5m,          // ④：合批更好
            separateEconDev: (0m, 0m),
            mergeEconDev: (9m, 9m));                     // ④.5：合批更差（方向相反）

        Assert.True(mergeWins);                          // ④ 在先 ⇒ 合批胜出
    }

    [Fact]
    public void PL_01b_完工同交付同_Setup更大但经济偏离更好_须由第4层Setup否决合批()
    {
        var mergeWins = Prefer(
            separateEnd: T, mergeEnd: T,                 // ③ 判平
            separateSetup: 5m, mergeSetup: 10m,          // ④：合批更差
            separateEconDev: (9m, 9m),
            mergeEconDev: (0m, 0m));                     // ④.5：合批更好（方向相反）

        Assert.False(mergeWins);                         // ④ 在先 ⇒ 独立排程胜出
    }

    [Fact]
    public void PL_01c_同夹具下抹掉Setup差异_第45层须真的生效_否则PL_01是空测()
    {
        // 反证 PL_01a/PL_01b 的**鉴别力**：若 ④.5 层恒平（不生效），上面两条会因为「无差异」而平凡通过。
        //   本用例去掉 Setup 差异（两侧同为 5）⇒ 择优**唯一**由 ④.5 决定 ⇒ 必须**反向**。
        Assert.False(Prefer(
            separateEnd: T, mergeEnd: T,
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (0m, 0m),
            mergeEconDev: (9m, 9m)));

        Assert.True(Prefer(
            separateEnd: T, mergeEnd: T,
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (9m, 9m),
            mergeEconDev: (0m, 0m)));
    }

    // ════════════════════════════════════════════════════════════════════
    // PL_02：第 ④.5 层**内部**口径（`Range` 字典序先于 `Preferred`）+ 第 ⑤ 层判平取独立排程
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void PL_02a_Range优先于Preferred_字典序()
    {
        // 合批 Preferred 更好（0 < 5），但 Range 更差（1 > 0）⇒ 按字典序必须**否决**合批。
        Assert.False(Prefer(
            separateEconDev: (0m, 5m),
            mergeEconDev: (1m, 0m)));

        // Range 判平后，Preferred 才生效。
        Assert.True(Prefer(
            separateEconDev: (0m, 5m),
            mergeEconDev: (0m, 2m)));
    }

    [Fact]
    public void PL_02b_六层全平_取独立排程()
    {
        Assert.False(Prefer());
    }

    // ════════════════════════════════════════════════════════════════════
    // PL_03：①②③ 必须**先于** ④/④.5 —— 每一条都让 ④ 与 ④.5 **同时偏向合批**，
    //        以证明胜出/否决确实来自更前的层，而不是 ④/④.5。
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void PL_03a_可行性优先于一切()
    {
        // 合批在 ②③④④.5 全部更差，但只要独立排程**不可行**，合批仍必须胜出。
        Assert.True(Prefer(
            separateFeasible: false, mergeFeasible: true,
            delaySeparate: 100, delayMerge: 0,
            separateEnd: T, mergeEnd: T1,
            separateSetup: 1m, mergeSetup: 99m,
            separateEconDev: (0m, 0m), mergeEconDev: (99m, 99m)));

        // 反向：合批不可行 ⇒ 一律否决。
        Assert.False(Prefer(
            separateFeasible: true, mergeFeasible: false,
            separateEnd: T1, mergeEnd: T,
            separateSetup: 99m, mergeSetup: 1m,
            separateEconDev: (99m, 99m), mergeEconDev: (0m, 0m)));
    }

    [Fact]
    public void PL_03b_履约优先于Setup与经济偏离()
    {
        // 合批延期更长 ⇒ 即使 Setup 与经济偏离都更好，也必须否决。
        Assert.False(Prefer(
            delaySeparate: 0, delayMerge: 10,
            separateSetup: 99m, mergeSetup: 1m,
            separateEconDev: (99m, 99m), mergeEconDev: (0m, 0m)));
    }

    [Fact]
    public void PL_03c_交期正排优先于Setup与经济偏离()
    {
        // 均按期（delay 0）但合批完工更晚 ⇒ 正排下否决（即便 Setup/经济都更好）。
        Assert.False(Prefer(
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: T, mergeEnd: T1,
            separateSetup: 99m, mergeSetup: 1m,
            separateEconDev: (99m, 99m), mergeEconDev: (0m, 0m)));
    }

    [Fact]
    public void PL_03d_交期倒排方向相反_仍优先于Setup与经济偏离()
    {
        // 倒排：不延期前提下**更靠近 Due**（更晚）优先 ⇒ 同一输入下结论翻转，
        //   证明 ③ 由 `direction` 控制，且仍在 ④/④.5 之前。
        Assert.True(Prefer(
            direction: SchedulingDirectionResolver.Backward,
            separateEnd: T, mergeEnd: T1,
            separateSetup: 99m, mergeSetup: 1m,
            separateEconDev: (99m, 99m), mergeEconDev: (0m, 0m)));
    }

    [Fact]
    public void PL_03e_双方同等延期时_交期层不参与_由Setup决定()
    {
        // 记录**现行**语义：③ 的触发条件是「均按期」（`delaySeparate == 0`）。
        //   两侧同等延期 ⇒ ② 判平、③ **不触发** ⇒ 落到 ④。本用例把这个边界钉住，防止将来被误改。
        Assert.True(Prefer(
            delaySeparate: 5, delayMerge: 5,
            separateEnd: T, mergeEnd: T1,          // 完工不同，但双方都已延期 ⇒ ③ 不适用
            separateSetup: 5m, mergeSetup: 1m));
    }

    // ════════════════════════════════════════════════════════════════════
    // PL_04：第 ④.6 层（**资源效率**，R-2，2026-10-14 新增）的层序与零回归
    //   ⚠ 排序键 = 「除 Setup 外占用分钟」（**越小越好**；**已扣 Setup** ⇒ 不与 ④ 重复计分，§一.2.5）。
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void PL_04a_前五层全平_由第46层资源效率定胜负()
    {
        // ①②③④④.5 全平 ⇒ 胜负**只能**由 ④.6 决定。
        Assert.True(Prefer(
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 50m));      // 合批占用更少 ⇒ 胜出

        Assert.False(Prefer(
            separateNonSetupOccupiedMinutes: 50m,
            mergeNonSetupOccupiedMinutes: 100m));     // 反向
    }

    [Fact]
    public void PL_04b_第46层不得压过第4层Setup()
    {
        // Setup 不同（④ 可判）⇒ 即便 ④.6 **反向**，也必须由 ④ 决定。
        Assert.False(Prefer(
            separateSetup: 5m, mergeSetup: 10m,       // ④：合批更差
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m));       // ④.6：合批更好 ⇒ 必须被 ④ 压住

        Assert.True(Prefer(
            separateSetup: 10m, mergeSetup: 5m,
            separateNonSetupOccupiedMinutes: 1m,
            mergeNonSetupOccupiedMinutes: 100m));
    }

    [Fact]
    public void PL_04c_第46层不得压过第45层经济偏离()
    {
        // Setup 判平 ⇒ 落到 ④.5；④.5 可判 ⇒ 即便 ④.6 **反向**，也必须由 ④.5 决定。
        Assert.False(Prefer(
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (0m, 0m),
            mergeEconDev: (9m, 9m),                   // ④.5：合批更差
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m));       // ④.6：合批更好 ⇒ 必须被 ④.5 压住
    }

    [Fact]
    public void PL_04d_第46层不得压过履约与交期()
    {
        Assert.False(Prefer(
            delaySeparate: 0, delayMerge: 10,         // ②：合批延期
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m));

        Assert.False(Prefer(
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: T, mergeEnd: T1,             // ③：合批更晚
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m));
    }

    [Fact]
    public void PL_04e_两侧均缺该事实_第46层判平_零回归()
    {
        // `null` = 该事实不可得 ⇒ 本层判平 ⇒ 回落 ⑤ 取独立排程（= 加本层之前的既有行为）。
        Assert.False(Prefer());
        // 只给一侧也**不得**参与（避免用半个事实定胜负）。
        Assert.False(Prefer(separateNonSetupOccupiedMinutes: 100m));
        Assert.False(Prefer(mergeNonSetupOccupiedMinutes: 1m));
    }

    [Fact]
    public void PL_04f_第46层落点身份_到达与决定须可分辨()
    {
        // ── 0号位 2026-10-14《致1号位第五轮代码审核正式复核回复 v1.0》§三 **R2-C** 逐字：──
        //   「区分`该指标存在差异`、`比较器到达④.6层`和`④.6实际决定采纳方案`三个事实；
        //     只有最后一种才能使用"increment/因效率择优"含义」。
        //   ⇒ 落点层身份必须**如实**报出，不得由「差额非零」冒充「本层决定了胜负」。

        // ④.6 **到达且决定**（合批侧占用更少）⇒ 层身份 = ④.6、胜者 = 合批。
        Assert.Equal(FrozenLayer.ResourceEfficiency, Layer(
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 50m).Layer);
        Assert.True(Layer(
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 50m).MergeWins);

        // ④.6 **到达且决定**（独立侧占用更少）⇒ 层身份**仍是** ④.6（**不得**退化成 ⑤/`None`）。
        Assert.Equal(FrozenLayer.ResourceEfficiency, Layer(
            separateNonSetupOccupiedMinutes: 50m,
            mergeNonSetupOccupiedMinutes: 100m).Layer);
        Assert.False(Layer(
            separateNonSetupOccupiedMinutes: 50m,
            mergeNonSetupOccupiedMinutes: 100m).MergeWins);

        // ④.6 **到达且判平** ⇒ 层身份 = ④.6（「到达」事实保留），胜者 = 独立（⑤ 保守）。
        Assert.Equal(FrozenLayer.ResourceEfficiency, Layer(
            separateNonSetupOccupiedMinutes: 300m,
            mergeNonSetupOccupiedMinutes: 300m).Layer);
        Assert.False(Layer(
            separateNonSetupOccupiedMinutes: 300m,
            mergeNonSetupOccupiedMinutes: 300m).MergeWins);

        // ④ 先决出 ⇒ 层身份 = ④（**即便效率确有差异**，④.6 **未被到达** ⇒ 不得记 ④.6 的功）。
        Assert.Equal(FrozenLayer.Setup, Layer(
            separateSetup: 5m, mergeSetup: 10m,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m).Layer);

        // ③ 先决出 ⇒ 层身份 = ③（同上；这是 `M_18` 的纯函数级同型反证）。
        Assert.Equal(FrozenLayer.DueDate, Layer(
            direction: SchedulingDirectionResolver.Forward,
            separateEnd: T, mergeEnd: T1,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m).Layer);

        // ② 先决出 ⇒ 层身份 = ②。
        Assert.Equal(FrozenLayer.Fulfillment, Layer(
            delaySeparate: 0, delayMerge: 10,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m).Layer);

        // ① 先决出 ⇒ 层身份 = ①（不可行侧直接出局）。
        Assert.Equal(FrozenLayer.Feasibility, Layer(
            mergeFeasible: false,
            separateNonSetupOccupiedMinutes: 100m,
            mergeNonSetupOccupiedMinutes: 1m).Layer);
    }

    // ════════════════════════════════════════════════════════════════════
    // PL_05（0号位 2026-10-14《致1号位第四轮整改回执正式复核意见 v1.0》§3.5 逐字要求）：
    //   **④.5 与 ④.6 真实相冲**的形态 —— 上层（①②③）同等合法、Setup 与交期均相同，
    //   **经济软偏离偏 A（独立排程）但真实释放设备产能偏 B（合批）**。
    //   ⇒ 现行实现由 **④.5 胜出**：为换取更小的经济偏离，**牺牲 60min 可释放设备产能**。
    //   ⚠ 本组用例**只登记现行实现的实际取舍**（`PreferMergeByFrozenLayers` 逐层 `return`），
    //     **不**主张「④.5 先于 ④.6」是已由 0号位 裁定的新增 V1 业务优先级。
    //     该取舍已按 0号位 §3.5 逐字要求**列明业务后果并交审核位汇总**（见 1号位回执）。
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public void PL_05a_经济偏离偏独立但释放产能偏合批_现行实现由45否决合批()
    {
        var mergeWins = Prefer(
            separateEnd: T, mergeEnd: T,                 // ③ 判平
            separateSetup: 5m, mergeSetup: 5m,           // ④ 判平
            separateEconDev: (0m, 0m),                   // ④.5：**独立排程**经济偏离更小 ⇒ 偏 A
            mergeEconDev: (9m, 9m),
            separateNonSetupOccupiedMinutes: 360m,       // ④.6：**合批**占用更少 ⇒ 偏 B
            mergeNonSetupOccupiedMinutes: 300m);

        Assert.False(mergeWins);                         // ④.5 在先 ⇒ 独立排程胜出（放弃 60min 可释放产能）
    }

    [Fact]
    public void PL_05b_方向完全对调_经济偏离偏合批但释放产能偏独立_由45判合批胜出()
    {
        var mergeWins = Prefer(
            separateEnd: T, mergeEnd: T,
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (9m, 9m),                   // ④.5：合批更好
            mergeEconDev: (0m, 0m),
            separateNonSetupOccupiedMinutes: 300m,       // ④.6：独立排程更好（**反向**）
            mergeNonSetupOccupiedMinutes: 360m);

        Assert.True(mergeWins);                          // ④.5 在先 ⇒ 合批胜出（放弃 60min 可释放产能）
    }

    [Fact]
    public void PL_05c_同夹具抹掉经济偏离_第46层须真的接管_否则PL_05是空测()
    {
        // 反证 PL_05a/PL_05b 的**鉴别力**：把 ④.5 抹平 ⇒ 胜负**唯一**由 ④.6 决定 ⇒ 结论必须**反向**。
        Assert.True(Prefer(
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (0m, 0m), mergeEconDev: (0m, 0m),
            separateNonSetupOccupiedMinutes: 360m,
            mergeNonSetupOccupiedMinutes: 300m));        // ④.6 ⇒ 合批胜出

        Assert.False(Prefer(
            separateSetup: 5m, mergeSetup: 5m,
            separateEconDev: (0m, 0m), mergeEconDev: (0m, 0m),
            separateNonSetupOccupiedMinutes: 300m,
            mergeNonSetupOccupiedMinutes: 360m));
    }
}
