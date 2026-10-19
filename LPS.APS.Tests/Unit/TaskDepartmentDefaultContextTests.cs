using System.Collections.Generic;
using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A1：`Task.ProductionDepartmentId` **默认上下文**取值契约单测（v1.1 §六.1.3 对 0号位 的承诺）。
///
/// 断言目的是**承诺本身**，不是「函数返回了某个值」：
///   ① **双轨**：命中 ⇒ 落真值；未命中 ⇒ 返回 `null`（留 NULL，由调用方记 Issue，**不得静默**）；
///   ② **键必须是 `(MaterialId, StageCode)` 双键** —— 退化成 `MaterialId` 单键会**静默**把某个 Stage
///      的部门套到同物料的其它 Stage 上（这是最危险的错法，故单列一例钉死）；
///   ③ `StageCode` 为 `null` / 空 / 纯空白 ⇒ **必然未命中**（MSC 实测 `StageCode` 零空），
///      不得拿空键去撞字典而「恰好」撞到某行。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class TaskDepartmentDefaultContextTests
{
    private const int MatA = 1001;
    private const int MatB = 1002;

    private static Dictionary<(int MaterialId, string StageCode), int> Ctx()
        => new()
        {
            [(MatA, "S10")] = 71,
            [(MatA, "S20")] = 72,   // 同物料、不同 Stage ⇒ 不同部门（双键的必要性）
            [(MatB, "S10")] = 73,
        };

    [Fact]
    public void 命中_落默认上下文部门()
    {
        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, "S10")
            .Should().Be(71);
    }

    [Fact]
    public void 同物料不同Stage_必须各取其部门()
    {
        // 若实现退化成 MaterialId 单键，本例会返回同一个值 ⇒ 静默套用错误部门。
        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, "S20")
            .Should().Be(72);

        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, "S20")
            .Should().NotBe(PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, "S10"));
    }

    [Fact]
    public void 物料命中但Stage不存在_未命中返回Null()
    {
        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, "S99")
            .Should().BeNull("同物料其它 Stage 的部门不得被套用");
    }

    [Fact]
    public void 物料不在上下文内_未命中返回Null()
    {
        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), 9_999, "S10")
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StageCode为空或空白_必然未命中(string? stageCode)
    {
        PeggingOrchestrator.ResolvePersistedDepartmentId(Ctx(), MatA, stageCode)
            .Should().BeNull("空 StageCode 不得拿空键去撞字典");
    }

    [Fact]
    public void 上下文为空_一律未命中返回Null()
    {
        var empty = new Dictionary<(int MaterialId, string StageCode), int>();

        PeggingOrchestrator.ResolvePersistedDepartmentId(empty, MatA, "S10")
            .Should().BeNull();
    }
}
