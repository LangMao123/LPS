using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Setup 换型规则投影/裁剪纯函数测试（v1.2 §19.3 ①/② + §22 S01~S06 中 2号位 可覆盖的纯逻辑）。
/// 覆盖：实体→快照字段映射（去审计字段）、IsActive 过滤、EXACT/DEFAULT 分流（From/To null 语义）、
/// 按 Domain（ProductionDepartmentId + StageCode）裁剪。命中/兜底（EXACT 优先、无规则 0 分钟、
/// 禁止 SetupTime fallback）属 1号位 SetupOptimizer.ResolveSetup，不在此层。
/// </summary>
public class SetupTransitionRuleProjectionTests
{
    private static SetupTransitionRule Rule(
        int dept = 1, string stage = "S1", string op = "OP20", int resource = 10,
        int? from = null, int? to = null, string ruleType = SetupTransitionRuleType.Default,
        decimal minutes = 30m, bool isActive = true)
        => new SetupTransitionRule
        {
            Id = 0,
            RuleSetVersionId = 100,
            ProductionDepartmentId = dept,
            StageCode = stage,
            OperationCode = op,
            ResourceId = resource,
            FromMaterialId = from,
            ToMaterialId = to,
            RuleType = ruleType,
            SetupMinutes = minutes,
            IsActive = isActive
        };

    private static MaterialStageDepartmentContextDto Context(int dept, string stage)
        => new MaterialStageDepartmentContextDto { ProductionDepartmentId = dept, StageCode = stage };

    [Fact]
    public void Project_映射业务键_去审计字段_保留RuleType与分钟()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 45m)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().ContainSingle();
        var s = snapshots[0];
        s.ProductionDepartmentId.Should().Be(1);
        s.StageCode.Should().Be("S1");
        s.OperationCode.Should().Be("OP20");
        s.ResourceId.Should().Be(10);
        s.FromMaterialId.Should().Be(101);
        s.ToMaterialId.Should().Be(102);
        s.RuleType.Should().Be(SetupTransitionRuleType.Exact);
        s.SetupMinutes.Should().Be(45m);
    }

    [Fact]
    public void Project_筛选IsActive_排除无效规则()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(ruleType: SetupTransitionRuleType.Exact, minutes: 10m, isActive: true),
            Rule(ruleType: SetupTransitionRuleType.Exact, minutes: 20m, isActive: false)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().ContainSingle();
        snapshots[0].SetupMinutes.Should().Be(10m);
    }

    [Fact]
    public void Project_DEFAULT规则FromTo为null_EXACT有值()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(ruleType: SetupTransitionRuleType.Default, from: null, to: null),
            Rule(ruleType: SetupTransitionRuleType.Exact, from: 1, to: 2)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().HaveCount(2);
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Default).FromMaterialId.Should().BeNull();
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Default).ToMaterialId.Should().BeNull();
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Exact).FromMaterialId.Should().Be(1);
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Exact).ToMaterialId.Should().Be(2);
    }

    [Fact]
    public void ProjectFromSnapshot_子块JSON_映射快照_筛IsActive()
    {
        // S-9：子块承载 → 消费快照整链（S-8 便捷入口），IsActive=false 被过滤
        var rules = new List<SetupTransitionRule>
        {
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 45m),
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 20m, isActive: false)
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [SetupTransitionRuleProjector.SetupTransitionRulesBlockName] = rules
        });

        var snapshots = SetupTransitionRuleProjector.ProjectFromSnapshot(json);

        snapshots.Should().ContainSingle();
        snapshots[0].RuleType.Should().Be(SetupTransitionRuleType.Exact);
        snapshots[0].SetupMinutes.Should().Be(45m);
        snapshots[0].FromMaterialId.Should().Be(101);
    }

    [Fact]
    public void ExtractRules_三态_缺失与空配置返回空列表_损坏抛红线异常()
    {
        // 2026-10-14 与 ⑧ 块同轨（1号位 2026-10-13《BatchPolicy三态载体择定…回执》§五「应同轨」，
        // 边界照该件 §5.3「仅 JSON 损坏抛异常；缺子块／null／显式 [] 仍空列表」）：
        // ① 缺子块 / null / 空串 → 空列表（该版本未配置换型规则，合法未配置，非装载失败）；
        // ② 显式空配置 "SetupTransitionRules":[] → 空列表（明确空配置，合法）；
        // ③ JSON 结构损坏 → 抛 SetupRuleDataRedLineException（显式失败，不再静默空列表）。
        SetupTransitionRuleProjector.ExtractRules(null).Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules(string.Empty).Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"DemandPriority\":{}}").Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\":null}").Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\":[]}").Should().BeEmpty();

        Action outer = () => SetupTransitionRuleProjector.ExtractRules("{ not-valid-json");
        outer.Should().Throw<SetupRuleDataRedLineException>();

        Action inner = () => SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\": [broken}");
        inner.Should().Throw<SetupRuleDataRedLineException>();
    }

    [Fact]
    public void ProjectFromSnapshot_子块缺失或为空_返回空列表()
    {
        // 合法缺配——缺失子块 / null 子块 / 空字符串 → 空列表（无规则 DEFAULT 兜底，0 分钟口径不变）
        var noBlock = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["DemandPriority"] = new { }
        });

        SetupTransitionRuleProjector.ProjectFromSnapshot(noBlock).Should().BeEmpty();
        SetupTransitionRuleProjector.ProjectFromSnapshot(null).Should().BeEmpty();
        SetupTransitionRuleProjector.ProjectFromSnapshot(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ProjectFromSnapshot_损坏JSON_抛红线异常_failFast()
    {
        // 装载链入口（FrozenStrategySnapshotProvider.DeserializeSetupTransitionRules → 第⑦块载荷）
        // 2026-10-14 由 fail-open 改 fail-fast：损坏版本**不得**再静默降级成「无规则」，
        // 否则 1号位 消费侧按 0 分钟排出看似成立、实则丢失换型时间的计划且全链无告警位。
        Action outer = () => SetupTransitionRuleProjector.ProjectFromSnapshot("{ not-valid-json");
        outer.Should().Throw<SetupRuleDataRedLineException>();

        Action inner = () => SetupTransitionRuleProjector.ProjectFromSnapshot("{\"SetupTransitionRules\": [broken}");
        inner.Should().Throw<SetupRuleDataRedLineException>();
    }

    [Fact]
    public void CropToDomain_仅保留本Domain部门阶段_跨域规则被裁掉()
    {
        var rules = new List<SetupTransitionRuleSnapshot>
        {
            new() { ProductionDepartmentId = 1, StageCode = "S1", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 30m },
            new() { ProductionDepartmentId = 1, StageCode = "S2", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 40m },
            new() { ProductionDepartmentId = 2, StageCode = "S1", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 50m }
        };
        var contexts = new List<MaterialStageDepartmentContextDto>
        {
            Context(1, "S1"),
            Context(1, "S2")
        };

        var cropped = SetupTransitionRuleProjector.CropToDomain(rules, contexts);

        cropped.Should().HaveCount(2);
        cropped.Should().OnlyContain(r => r.ProductionDepartmentId == 1); // 部门 2 规则被裁掉
    }

    [Fact]
    public void CropToDomain_上下文为空_返回空()
    {
        var rules = new List<SetupTransitionRuleSnapshot>
        {
            new() { ProductionDepartmentId = 1, StageCode = "S1", RuleType = SetupTransitionRuleType.Default }
        };

        SetupTransitionRuleProjector.CropToDomain(rules, new List<MaterialStageDepartmentContextDto>())
            .Should().BeEmpty();
    }
}
