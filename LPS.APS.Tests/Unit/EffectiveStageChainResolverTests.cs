using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;
using static LPS.APS.Application.Services.EffectiveStageChainResolver;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 「每个需求唯一一条有序 Stage 链」收敛器的单测（0号位 2026-10-16 第十二轮 J12-02／2026-10-17 第十四轮 J14-01/02/03）。
///
/// 断言目的**不是**「函数返回了非 null」，而是它的**声明用途**（`self-check-must-assert-its-stated-purpose`）：
///   ① **ROOT 与 EDGE 绝不混查、绝不并集** —— 冻结 `07 v5.1.13 :416`／`:723`；
///   ② **选行前置**：需求的 `BOMNO`／`RepWorksetId`／父边选择键生效后，**别的请求的链不得被取到**（J14-02）；
///   ③ **首选 scope 无行 ⇒ 无链，绝不回退另一 scope**（J14-01）；
///   ④ **同 scope 多条不一致 ⇒ 取最长 + 报出未选用**（`:722`／`:415`），不得静默丢；
///   ⑤ **产出恒为一条真实链或零条**：不合成、不补步、不改 `StageSeq`；
///   ⑥ 结果**确定性**（同输入同输出，不依赖行序）。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class EffectiveStageChainResolverTests
{
    private static InstanceRow Row(
        string? scope, string? parent, long? worksetId, string stageCode, int stageSeq, string? bomNo = "B1")
        => new(scope, parent, worksetId, bomNo, stageSeq, stageCode);

    private static SelectionContext Sel(string? bomNo, long? rootWorksetId = null, params string[] parents)
        => new(bomNo, rootWorksetId, parents.Length == 0 ? null : parents);

    /// <summary>
    /// 本类多数用例校验的是**链的形态**（scope 不混查、取最长、不合成…），不校验请求身份，
    /// 因此统一走「无锚点兼容」放行路径（2026-10-17 J15-01 引入严格默认后，生产默认已改为「三键全空 ⇒ 无链」）。
    /// **身份三态本身**由文件末尾 `请求身份三态` 一组用例以产品默认参数（严格）单独钉死。
    /// </summary>
    private static Resolution? Resolve(
        IReadOnlyList<InstanceRow> rows, bool prefersEdgeScope,
        SelectionContext? selection = null, bool allowUnanchoredCompatibility = true)
        => EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope, selection, allowUnanchoredCompatibility);

    // ───────────────────────── ① ROOT 与 EDGE 不得混查、不得并集 ─────────────────────────

    [Fact]
    public void 子件需求偏好EDGE时_不得并入同物料的ROOT链()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PA", 100, "CN_SURF", 20),
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_SURF", 20),
            Row(ScopeRoot, null, 900, "CN_ASM",  30),   // 仅 ROOT 侧有
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.ScopeType.Should().Be(ScopeEdge);
        r.MissingPreferredScope.Should().BeFalse();
        // 并集口径会给出 3 步（含仅 ROOT 有的 CN_ASM）；隔离口径必须只给 EDGE 的 2 步
        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_SURF");
    }

    [Fact]
    public void 根产品需求偏好ROOT时_不得并入同物料的EDGE链()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_SURF", 20),
        };

        var r = Resolve(rows, prefersEdgeScope: false)!;

        r.ScopeType.Should().Be(ScopeRoot);
        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_SURF");
    }

    // ───────────────────────── ② 选行前置：请求隔离（J14-02）─────────────────────────

    [Fact]
    public void 按BOMNO选行_别的请求更长的ROOT链不得被取到()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10, "B1"),                    // 本需求（B1）：1 步
            Row(ScopeRoot, null, 901, "CN_MACH", 10, "B2"),
            Row(ScopeRoot, null, 901, "CN_SURF", 20, "B2"),
            Row(ScopeRoot, null, 901, "CN_ASM",  30, "B2"),                    // 别的请求（B2）：3 步
        };

        var r = Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH");
        r.RejectedChainKeys.Should().BeEmpty("B2 的链已被选行前置剔除，不应作为「未选用」出现");
        r.BomNoFilterApplied.Should().BeTrue();
    }

    [Fact]
    public void 按代表WorksetId收窄_同BOMNO下别的请求实例不得被取到()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10, "B1"),
            Row(ScopeRoot, null, 901, "CN_MACH", 10, "B1"),
            Row(ScopeRoot, null, 901, "CN_SURF", 20, "B1"),
        };

        var r = Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH");
        r.WorksetIdFilterApplied.Should().BeTrue();
        r.RejectedChainKeys.Should().BeEmpty();
    }

    [Fact]
    public void 当前批次WorksetId全为NULL时_退化为仅按BOMNO收窄且留痕()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, null, "CN_MACH", 10, "B1"),
            Row(ScopeRoot, null, null, "CN_SURF", 20, "B1"),
        };

        var r = Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_SURF");
        r.WorksetIdFilterApplied.Should().BeFalse("WorksetId 未回灌时不得假装按实例收窄过");
    }

    [Fact]
    public void 按父边选行_只取本父边的EDGE链()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PA", 100, "CN_SURF", 20),
            Row(ScopeEdge, "PB", 200, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 200, "CN_OUTS", 30),
        };

        var r = Resolve(rows, prefersEdgeScope: true, Sel("B1", null, "PB"))!;

        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_OUTS");
        r.RejectedChainKeys.Should().BeEmpty("另一父边的链不属于本需求，不应作为「未选用」出现");
    }

    [Fact]
    public void 不同WorksetId的ROOT链_视为不同链而非合并()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10),          // 请求 A
            Row(ScopeRoot, null, 901, "CN_MACH", 10),          // 请求 B：同为 (MACH@10)，签名相同 ⇒ 去重为 1 条
        };

        var r = Resolve(rows, prefersEdgeScope: false)!;

        r.Stages.Should().HaveCount(1);
        r.RejectedChainKeys.Should().BeEmpty("两条实例的链签名相同 ⇒ 是同一物理链，不应产生伪告警");
    }

    [Fact]
    public void 无选择键时_候选不一致_不得取最长_不出链并报出全部候选()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 901, "CN_MACH", 10),
            Row(ScopeRoot, null, 901, "CN_SURF", 20),          // 另一实例的链更长
        };

        var r = Resolve(rows, prefersEdgeScope: false)!;

        // J16-01 准入：请求实例未经证明（这里三键全空）时，候选内容不一致 ⇒ 不得取最长冒充本需求的路径。
        r.Stages.Should().BeEmpty("取最长会把另一请求的路径塞给本需求");
        r.IdentityAmbiguous.Should().BeTrue();
        r.MissingPreferredScope.Should().BeFalse("这不是「无行」，是身份未证实下的候选不等价");
        r.RejectedChainKeys.Should().BeEquivalentTo("10:CN_MACH", "10:CN_MACH|20:CN_SURF");
    }

    [Fact]
    public void 同子件不同父边在无选择键时_既不并集也不取最长_不出链()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),          // 父件 PA 的边实例 → 10:MACH|20:SURF
            Row(ScopeEdge, "PA", 100, "CN_SURF", 20),
            Row(ScopeEdge, "PB", 200, "CN_MACH", 10),          // 父件 PB 的边实例 → 10:MACH|30:OUTS
            Row(ScopeEdge, "PB", 200, "CN_OUTS", 30),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        // 合并口径会给出 3 步 {MACH, SURF, OUTS}（谁也不是真实链）；取最长会给 PA 的 2 步冒充 PB。
        // 身份未证实 ⇒ 零条，两条真实链原样作为证据报出。
        r.Stages.Should().BeEmpty("不得并集、也不得任取一条真实链冒充本请求");
        r.IdentityAmbiguous.Should().BeTrue();
        r.RejectedChainKeys.Should().BeEquivalentTo("10:CN_MACH|20:CN_SURF", "10:CN_MACH|30:CN_OUTS");
        r.RejectedChainKeys.Should().NotContain(k => k.Split(new[] { '|' }).Length == 3, "并集链 3 步绝不能出现");
    }

    [Fact]
    public void 父件为NULL与空串不得混淆()
    {
        var rows = new[]
        {
            Row(ScopeEdge, null, 100, "CN_MACH", 10),
            Row(ScopeEdge, "",   100, "CN_MACH", 10),
            Row(ScopeEdge, "",   100, "CN_SURF", 20),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        // 若 NULL 与空串被当成同一实例键，两条边实例会被合并成一条 2 步链（从而「有链」）；
        // 现在的零链 + 两条候选，正是「没被吞并」的证明。
        r.Stages.Should().BeEmpty();
        r.IdentityAmbiguous.Should().BeTrue();
        r.RejectedChainKeys.Should().BeEquivalentTo("10:CN_MACH", "10:CN_MACH|20:CN_SURF");
    }

    // ───────────────────────── ③ 取最长（仅限 Anchored） + 必须报出 ─────────────────────────

    [Fact]
    public void ROOT按WorksetId收窄后_只取本请求实例_不跨请求取最长()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_SURF", 20),
            Row(ScopeRoot, null, 901, "CN_MACH", 10),
            Row(ScopeRoot, null, 901, "CN_SURF", 20),
            Row(ScopeRoot, null, 901, "CN_ASM",  30),
        };

        var r = Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.IdentityState.Should().Be(RequestIdentityState.Anchored);
        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_SURF");
        r.RejectedChainKeys.Should().BeEmpty("另一请求实例的行已被 WorksetId 等值收窄剔掉，不应产生伪告警");
    }

    [Fact]
    public void 同为最长时_按链签名降序取定_结果确定()
    {
        var rowsA = new[]
        {
            Row(ScopeRoot, null, 900, "CN_SURF", 20),
            Row(ScopeRoot, null, 901, "CN_MACH", 10),
        };
        var rowsB = rowsA.Reverse().ToArray();   // 仅行序不同

        var a = Resolve(rowsA, prefersEdgeScope: false)!;
        var b = Resolve(rowsB, prefersEdgeScope: false)!;

        a.Stages.Should().BeEquivalentTo(b.Stages, o => o.WithStrictOrdering());
        BuildChainKey(a.Stages).Should().Be(BuildChainKey(b.Stages));
    }

    // ───────────────────────── ④ 产出恒为一条真实链 ─────────────────────────

    [Fact]
    public void 同一实例内同StageCode重复_取最小StageSeq且只出现一次()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 30),
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.Stages.Should().ContainSingle();
        r.Stages[0].StageCode.Should().Be("CN_MACH");
        r.Stages[0].StageSeq.Should().Be(10);
    }

    [Fact]
    public void 链按StageSeq升序_与行序无关()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_OUTS", 30),
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PA", 100, "CN_SURF", 20),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.Stages.Select(s => s.StageSeq).Should().Equal(10, 20, 30);
    }

    // ───────────────────────── ⑤ 不回退与边界（J14-01）─────────────────────────

    [Fact]
    public void 首选scope无行_不得回退另一scope_返回无链()
    {
        var rows = new[] { Row(ScopeRoot, null, 900, "CN_MACH", 10) };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.ScopeType.Should().Be(ScopeEdge);
        r.MissingPreferredScope.Should().BeTrue("首选 scope 无行只能得到「无链」，不得改用另一类业务路径");
        r.Stages.Should().BeEmpty();
        r.OtherScopeDistinctChainCount.Should().Be(1, "另一 scope 的存在只用于诊断计数，不参与选定");
    }

    [Fact]
    public void 只有EDGE行的根产品需求偏好ROOT_返回无链而非拿EDGE顶上()
    {
        // 对照上一条：两个方向都必须成立，否则「不回退」只修了一半（ROOT→EDGE 漏修）
        var rows = new[] { Row(ScopeEdge, "PA", 100, "CN_MACH", 10) };

        var r = Resolve(rows, prefersEdgeScope: false)!;

        r.ScopeType.Should().Be(ScopeRoot);
        r.MissingPreferredScope.Should().BeTrue("根需求只能取 ROOT 链，EDGE 行是子件供给路径");
        r.Stages.Should().BeEmpty();
        r.OtherScopeDistinctChainCount.Should().Be(1);
    }

    [Fact]
    public void 按BOMNO收窄后无行_返回无链而非改用别的请求()
    {
        var rows = new[] { Row(ScopeRoot, null, 900, "CN_MACH", 10, "B2") };

        var r = Resolve(rows, prefersEdgeScope: false, Sel("B1"))!;

        r.MissingPreferredScope.Should().BeTrue();
        r.Stages.Should().BeEmpty();
        r.BomNoFilterApplied.Should().BeTrue();
    }

    [Fact]
    public void 两scope皆无行_返回null()
    {
        Resolve(Array.Empty<InstanceRow>(), prefersEdgeScope: true).Should().BeNull();
    }

    [Fact]
    public void 未知StageScopeType不得被当作合法scope()
    {
        var rows = new[] { Row("WIP", null, 900, "CN_MACH", 10) };

        Resolve(rows, prefersEdgeScope: true)!.MissingPreferredScope.Should().BeTrue();
        Resolve(rows, prefersEdgeScope: false)!.MissingPreferredScope.Should().BeTrue();
    }

    [Theory]
    [InlineData("root")]
    [InlineData(" ROOT ")]
    [InlineData("Root")]
    public void StageScopeType大小写与空白归一(string raw)
    {
        var rows = new[] { Row(raw, null, 900, "CN_MACH", 10) };

        var r = Resolve(rows, prefersEdgeScope: false)!;

        r.ScopeType.Should().Be(ScopeRoot);
        r.MissingPreferredScope.Should().BeFalse();
    }

    [Fact]
    public void 空StageCode行被忽略()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PA", 100, null!, 20),
            Row(ScopeEdge, "PA", 100, "", 30),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.Stages.Should().ContainSingle();
        r.Stages[0].StageCode.Should().Be("CN_MACH");
    }

    [Fact]
    public void 另一scope的链条数_供分歧计数使用()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 900, "CN_SURF", 20),
        };

        var r = Resolve(rows, prefersEdgeScope: true)!;

        r.OtherScopeDistinctChainCount.Should().Be(1);
        r.OtherScopeLongestChainKey.Should().Be("10:CN_MACH|20:CN_SURF");
        r.OtherScopeLongestChainKey.Should().NotBe(BuildChainKey(r.Stages));
    }

    // ═══════════════════ ⑦ 请求身份三态（0号位 2026-10-17 第十五轮 J15-01）═══════════════════
    //
    // 声明用途：**不得把「仅按 BOMNO 回退」描述为已实现请求隔离**。
    //   状态 a：权威锚点齐全并已实际收窄 ⇒ Anchored；
    //   状态 b：有 BOMNO、缺第二层锚点 ⇒ 内容等价兼容，链可用但标 UnverifiedRequestIdentity；
    //   状态 c：三键全空 ⇒ **无链** ＋ 诊断（默认口径），Q/U 不受影响。
    // 本组一律使用**产品默认参数**（严格），不借用上面放行兼容的测试辅助。

    [Fact]
    public void 三键全空_默认口径_不出链且状态为NoAnchor()
    {
        var rows = new[] { Row(ScopeRoot, null, 900, "CN_MACH", 10) };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false)!;

        r.IdentityState.Should().Be(RequestIdentityState.NoAnchor);
        r.MissingPreferredScope.Should().BeTrue();
        r.Stages.Should().BeEmpty();
        r.BomNoFilterApplied.Should().BeFalse();
    }

    [Fact]
    public void 三键全空_系统性兼容放行_出链但不得声称已隔离()
    {
        var rows = new[] { Row(ScopeRoot, null, 900, "CN_MACH", 10) };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false,
            selection: null, allowUnanchoredCompatibility: true)!;

        r.IdentityState.Should().Be(RequestIdentityState.NoAnchor);
        r.MissingPreferredScope.Should().BeFalse();
        r.Stages.Should().ContainSingle();
    }

    [Fact]
    public void ROOT有BOMNO且按WorksetId收窄_状态为Anchored()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10),
            Row(ScopeRoot, null, 901, "CN_MACH", 10),   // 另一个请求实例
            Row(ScopeRoot, null, 901, "CN_SURF", 20),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.IdentityState.Should().Be(RequestIdentityState.Anchored);
        r.WorksetIdFilterApplied.Should().BeTrue();
        r.Stages.Should().ContainSingle().Which.StageCode.Should().Be("CN_MACH");
    }

    [Fact]
    public void ROOT有BOMNO但WorksetId全空_状态为Unverified()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, null, "CN_MACH", 10),
            Row(ScopeRoot, null, null, "CN_SURF", 20),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.IdentityState.Should().Be(RequestIdentityState.UnverifiedRequestIdentity);
        r.WorksetIdFilterApplied.Should().BeFalse();
        r.BomNoFilterApplied.Should().BeTrue();
        r.Stages.Should().HaveCount(2);
    }

    [Fact]
    public void EDGE有BOMNO且父边已证明_状态为ParentEdgeScoped而非Anchored()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 100, "CN_SURF", 20),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: true, Sel("B1", null, "PA"))!;

        // J16-01：父边只是边维度收窄，不能称「请求实例已确认」。
        r.IdentityState.Should().Be(RequestIdentityState.ParentEdgeScoped);
        r.Stages.Should().ContainSingle().Which.StageCode.Should().Be("CN_MACH");
        r.IdentityAmbiguous.Should().BeFalse("收窄后候选只有一条，等价性无需额外断言");
    }

    [Fact]
    public void EDGE有BOMNO但父边集未知_候选不一致_不出链()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 100, "CN_SURF", 20),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: true, Sel("B1"))!;

        r.IdentityState.Should().Be(RequestIdentityState.UnverifiedRequestIdentity);
        r.Stages.Should().BeEmpty("父边未知 + 候选不等价 ⇒ 不得取最长");
        r.IdentityAmbiguous.Should().BeTrue();
        r.RejectedChainKeys.Should().BeEquivalentTo("10:CN_MACH", "10:CN_MACH|20:CN_SURF");
    }

    [Fact]
    public void EDGE父边已证明且各父边候选内容完全一致_兼容放行()
    {
        var rows = new[]
        {
            Row(ScopeEdge, "PA", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PA", 100, "CN_SURF", 20),
            Row(ScopeEdge, "PB", 100, "CN_MACH", 10),
            Row(ScopeEdge, "PB", 100, "CN_SURF", 20),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: true, Sel("B1", null, "PA", "PB"))!;

        // J16-01 的**正例**：同 BOM、同角色、同父边的候选有序内容完全一致 ⇒ 历史兼容准许，但仍标非请求级。
        r.IdentityState.Should().Be(RequestIdentityState.ParentEdgeScoped);
        r.IdentityAmbiguous.Should().BeFalse();
        r.Stages.Select(s => s.StageCode).Should().Equal("CN_MACH", "CN_SURF");
    }

    [Fact]
    public void 系统性无锚点放行_候选不一致_依旧不出链()
    {
        var rows = new[]
        {
            Row(ScopeRoot, null, 900, "CN_MACH", 10, bomNo: null),
            Row(ScopeRoot, null, 901, "CN_MACH", 10, bomNo: null),
            Row(ScopeRoot, null, 901, "CN_SURF", 20, bomNo: null),
        };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false,
            selection: null, allowUnanchoredCompatibility: true)!;

        // 放行只解除「三键全空即无链」，**不解除** J16-01 的等价守卫。
        r.IdentityState.Should().Be(RequestIdentityState.NoAnchor);
        r.IdentityAmbiguous.Should().BeTrue();
        r.Stages.Should().BeEmpty("全量 Stage 路径不得冒充有效业务链");
    }

    [Fact]
    public void 有锚点但本批无该请求的行_无链且状态为Anchored()
    {
        var rows = new[] { Row(ScopeRoot, null, 900, "CN_MACH", 10, bomNo: "B2") };

        var r = EffectiveStageChainResolver.Resolve(rows, prefersEdgeScope: false, Sel("B1", 900))!;

        r.MissingPreferredScope.Should().BeTrue();
        r.Stages.Should().BeEmpty();
        r.IdentityState.Should().Be(RequestIdentityState.Anchored);
    }
}
