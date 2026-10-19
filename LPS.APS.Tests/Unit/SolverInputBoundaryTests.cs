using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **消费侧「0 命中」反证**（1号位 2026-10-16 交付承诺 §四 第 **4／5** 项）：
///
///   · **#4 资源合法性消费侧反证** —— `ResourceType` 在 `LPS.APS.Scheduling/**` **0 命中**
///     ⇒ 我方**分不出**人工槽／设备 ⇒ 只能给「**缺字段时的 Fail Closed 边界**」
///     （= 无类别字段 ⇒ **不拒绝、一律按 `ResourceId` 排**；要不要过滤归**装载层**）。
///   · **#5 采购占位区分反证** —— `PLANNING_PURCHASE_PLACEHOLDER` 在 `LPS.APS.Scheduling/**` **0 命中**
///     ⇒ 我方**拿不到**该类型 ⇒ 天然区分，**不会**拿它当正式可用时间。
///
/// ⚠ 本类**只做结构断言**（反射 ＋ 源码扫描）：**不起 Solver、不读日历、不碰数据库**。
/// ⚠ 断言的**鉴别性**：任何人往 `LPS.APS.Scheduling/**` 加一个 `ResourceType` 成员、或加一处
///   `PLANNING_PURCHASE_PLACEHOLDER`／`SupplySourceType` 引用 ⇒ 本类**必红**
///   —— 这就是「0 命中」的**可执行形式**（不是「结果一致性」护栏）。
/// </summary>
public class SolverInputBoundaryTests
{
    // ════════════════════════════════════════════════════════════════════
    //  #4 资源合法性消费侧：ResourceDefinition 无类别字段
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **锁定字段集**：`ResourceDefinition` **恰有 4 个**属性 ——
    ///   `{ ResourceId, ResourceCode, FactoryCode, Capacity }`。
    /// 冻结出处 = 我方 2026-10-16 交付承诺 §二（`DomainSolveRequest.cs:409-415`）。
    /// ⇒ 一旦有人加第 5 个字段（尤其 `ResourceType`）**本用例红**，逼一次显式决策。
    /// </summary>
    [Fact]
    public void 资源定义恰四个字段_无类别字段_分不出人工槽与设备()
    {
        var names = typeof(ResourceDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "Capacity", "FactoryCode", "ResourceCode", "ResourceId" },
            names);
    }

    /// <summary>
    /// **Fail Closed 边界的可证形式**：`ResourceDefinition` **没有**任何以「类型／类别／种类」命名的成员
    ///   ⇒ Solver **无依据**按资源类别拒绝 ⇒ 只能「一律按 `ResourceId` 排」。
    /// ⚠ 这条**不是**「Solver 会 fail closed 拒绝」；恰恰相反：**缺字段 ⇒ 不拒绝**。
    ///   真正的合法性过滤**必须在装载层做完**（我方已提请 2号位，见交付承诺 Q5）。
    /// </summary>
    [Fact]
    public void 资源定义无以类型类别命名的成员_故不按类别拒绝()
    {
        var suspicious = typeof(ResourceDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Category", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Kind", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Class", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(suspicious);
    }

    /// <summary>
    /// **源码级 0 命中**：`LPS.APS.Scheduling/**` 全部 `.cs`（跳过 `bin`/`obj`）**零**出现 `ResourceType`。
    /// </summary>
    [Fact]
    public void Scheduling源码零命中ResourceType()
    {
        Assert.Empty(ScanSchedulingSources("ResourceType"));
    }

    // ════════════════════════════════════════════════════════════════════
    //  #5 采购占位区分：PLANNING_PURCHASE_PLACEHOLDER 进不了 Solver 输入
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **先钉事实，再钉边界**：
    ///   (a) 枚举值 `PLANNING_PURCHASE_PLACEHOLDER = 8` 存在（冻结出处 `SupplySourceType.cs:54-59`，
    ///       逐字「**仅内存，不生成采购单/Task**」「标记 ESTIMATED + NOT_COMMITTED，**不可作为 CTP 承诺**」）；
    ///   (b) Solver 的**输入契约** `DomainSolveRequest` **没有任何** `SupplySourceType` 成员
    ///       （含其集合／可空包装）⇒ 该占位**根本进不来**。
    /// </summary>
    [Fact]
    public void 采购占位枚举值存在_但其进不了Solver输入契约()
    {
        // (a) 钉事实
        Assert.Equal(8, (int)SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER);

        // (b) 钉边界：DomainSolveRequest 无任何 SupplySourceType 成员
        var offending = typeof(DomainSolveRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => MentionsSupplySourceType(p.PropertyType))
            .Select(p => $"{p.Name}: {p.PropertyType.Name}")
            .ToArray();

        Assert.Empty(offending);
    }

    /// <summary>
    /// **源码级 0 命中**：`LPS.APS.Scheduling/**` 零出现 `PLANNING_PURCHASE_PLACEHOLDER` **或** `SupplySourceType`
    ///   ⇒ 我方侧**连这个枚举都不引用** ⇒ 天然区分。
    /// </summary>
    [Fact]
    public void Scheduling源码零命中采购占位与供给来源枚举()
    {
        Assert.Empty(ScanSchedulingSources("PLANNING_PURCHASE_PLACEHOLDER"));
        Assert.Empty(ScanSchedulingSources("SupplySourceType"));
    }

    /// <summary>
    /// **类型级 0 命中**：`LPS.APS.Scheduling` 程序集内**没有**任何以 `SupplySourceType` /
    ///   `PlanningPurchasePlaceholder` 命名的类型（补源码扫描的盲区：动态生成／部分类）。
    /// </summary>
    [Fact]
    public void Scheduling程序集内无供给来源相关类型()
    {
        var assembly = typeof(PhaseFiveCompression).Assembly;

        var offending = assembly
            .GetTypes()
            .Where(t => t.Name.Contains("SupplySourceType", StringComparison.Ordinal)
                     || t.Name.Contains("PlanningPurchasePlaceholder", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.Empty(offending);
    }

    // ════════════════════════════════════════════════════════════════════
    //  工装
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// `SupplySourceType` 本身、其可空、其数组、其泛型集合（含嵌套）—— 任一即算「提到」。
    /// </summary>
    private static bool MentionsSupplySourceType(Type type)
    {
        if (type == typeof(SupplySourceType))
        {
            return true;
        }

        if (type.IsArray)
        {
            return MentionsSupplySourceType(type.GetElementType()!);
        }

        return type.IsGenericType
            && type.GetGenericArguments().Any(MentionsSupplySourceType);
    }

    /// <summary>
    /// 在 `LPS.APS.Scheduling/**` 全部 `.cs` 里找**纯子串**；返回命中的**文件名**。
    /// ⚠ 找不到源码目录 ⇒ **直接抛**（**不是**静默跳过）—— 「扫不到」等于**没证明**「0 命中」。
    /// </summary>
    private static string[] ScanSchedulingSources(string identifier)
    {
        var root = FindSchedulingSourceRoot();

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderBuildOutput(f))
            .Where(f => File.ReadAllText(f).Contains(identifier, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray()!;
    }

    private static bool IsUnderBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>从测试程序集目录向上找含 `LPS.APS.Scheduling` 的目录（= 仓库工作副本根）。</summary>
    private static string FindSchedulingSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "LPS.APS.Scheduling");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"找不到 LPS.APS.Scheduling 源码目录（从 {AppContext.BaseDirectory} 向上找）。"
            + "本用例依赖源码树在场；扫不到就等于没证明「0 命中」，故直接失败。");
    }
}
