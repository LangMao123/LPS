using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Application.Models;

namespace LPS.APS.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// BOM 结构展开（Explosion）纯函数层 —— 阶段1 / 阶段2 共用的第一段
//
// PM 0918-1 / 0918-2 裁决：把「BOM 结构展开（纯、可缓存）」从「供给承接（Allocation，
// 有状态、不可缓存）」里拆出来。本服务只回答「这个需求沿某条 BOM 路径需要哪些下层物料、
// 各要多少」—— 与订单优先级无关、与 Supply 状态无关、与 Allocation 顺序无关。
//
// 当前实现 = 阶段1：
//   * 产出「逐路径毛需求行」（共享子件 D 经 B/C 两路径 → 两行，各带 Path 标识完整血缘）；
//   * 不净额（netting）、不合并 —— 合并数量保留血缘是阶段2 Allocation 层的事；
//   * 环检测 = 路径级 visited（与旧 TraverseBomNode 同语义，只防真实环，不阻断共享展开）。
//
// 缓存边界（阶段2 接线时）：
//   * 可缓存单元 = GetUnitVector（每物料一层展开）；缓存键 = MaterialId + FactoryId +
//     BOMVersion + EffectiveDate（PM 0918-2 §七）。
//   * 本服务保持无状态；缓存由调用方按 BOM 版本建一次 BomStructure 复用实现。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>BOM 结构展开纯函数层。</summary>
public interface IBomExplosionService
{
    /// <summary>单位需求向量：某物料往下一层展开的 (子件, 单位配比)。可缓存的最小单元。</summary>
    IReadOnlyList<BomComponent> GetUnitVector(BomStructure structure, string materialCode);

    /// <summary>单订单毛需求爆炸：根需求 × 单位配比递推，产出逐路径需求行（不净额、不合并）。</summary>
    IReadOnlyList<BomExplosionLine> ExplodeOrder(BomStructure structure, BomOrderDemand root);

    /// <summary>
    /// 去重逐层结构（阶段2 Allocation 骨架）：共享子件合并为一行带父边列表，每物料取其最深出现层
    /// （LLC 语义，保证所有父件均已先净额），采购件即叶（对齐旧 TraverseBomNode 不下钻）。
    /// </summary>
    IReadOnlyList<BomLevelNode> ExplodeOrderStructure(BomStructure structure, BomOrderDemand root);
}

/// <summary>BOM 结构展开纯函数层实现（无状态、无副作用）。</summary>
public sealed class BomExplosionService : IBomExplosionService
{
    public IReadOnlyList<BomComponent> GetUnitVector(BomStructure structure, string materialCode)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(materialCode);
        return structure.ByParent.TryGetValue(materialCode, out var children)
            ? children
            : Array.Empty<BomComponent>();
    }

    public IReadOnlyList<BomExplosionLine> ExplodeOrder(BomStructure structure, BomOrderDemand root)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(root);

        var lines = new List<BomExplosionLine>();
        // 路径级环检测：与旧 TraverseBomNode 同语义——只防 A→B→A 真实环；共享子件 D 经 B/C
        // 两路径时（D 随各自路径的 visited 进入/退出），仍各产一行，血缘不丢。
        var visited = new HashSet<string>(StringComparer.Ordinal);

        Walk(root.RootMaterialId, root.RootMaterialCode, root.RootQty, level: 0, path: root.RootMaterialCode);
        return lines;

        void Walk(int materialId, string materialCode, decimal grossQty, int level, string path)
        {
            if (!visited.Add(materialCode)) return; // 真实环，截断，不产出

            try
            {
                var isPurchased = structure.IsPurchasedByMaterial.TryGetValue(materialCode, out var purchased) && purchased;
                var children = GetUnitVector(structure, materialCode);

                lines.Add(new BomExplosionLine(
                    RootOrderId: root.RootOrderId,
                    MaterialId: materialId,
                    MaterialCode: materialCode,
                    FactoryId: root.FactoryId,
                    GrossQty: grossQty,
                    Level: level,
                    Path: path,
                    IsPurchased: isPurchased,
                    IsLeaf: children.Count == 0));

                foreach (var child in children)
                {
                    Walk(
                        child.ChildMaterialId,
                        child.ChildCode,
                        grossQty * child.QtyPerUnit,
                        level + 1,
                        path + "/" + child.ChildCode);
                }
            }
            finally
            {
                visited.Remove(materialCode);
            }
        }
    }

    public IReadOnlyList<BomLevelNode> ExplodeOrderStructure(BomStructure structure, BomOrderDemand root)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(root);

        var codeById = new Dictionary<int, string>();
        var levelById = new Dictionary<int, int>();
        var isPurchasedById = new Dictionary<int, bool>();
        var parentEdgesById = new Dictionary<int, Dictionary<int, BomParentEdge>>();

        // 路径级环检测（与 ExplodeOrder 同语义）：A→B→A 时，B 内回到 A 被截断，A 不记环边、不抬 level（仍是 root level 0）。
        var visited = new HashSet<string>(StringComparer.Ordinal);

        Walk(root.RootMaterialId, root.RootMaterialCode, level: 0, parentMaterialId: 0, parentMaterialCode: "", qtyPerUnit: 0m);

        return levelById
            .Select(kv => new BomLevelNode(
                MaterialId: kv.Key,
                MaterialCode: codeById[kv.Key],
                FactoryId: root.FactoryId,
                Level: kv.Value,
                IsPurchased: isPurchasedById[kv.Key],
                ParentEdges: parentEdgesById.TryGetValue(kv.Key, out var edgeMap)
                    ? (IReadOnlyList<BomParentEdge>)edgeMap.Values.OrderBy(e => e.ParentMaterialId).ToList()
                    : Array.Empty<BomParentEdge>()))
            .OrderBy(n => n.Level)
            .ThenBy(n => n.MaterialCode, StringComparer.Ordinal)
            .ToList();

        void Register(int materialId, string materialCode, int level, int parentMaterialId, string parentMaterialCode, decimal qtyPerUnit)
        {
            codeById[materialId] = materialCode;
            isPurchasedById[materialId] =
                structure.IsPurchasedByMaterial.TryGetValue(materialCode, out var purchased) && purchased;

            // 最深出现层（LLC 语义）：取 max，保证该物料的所有父件均已在其更浅层先被处理。
            if (!levelById.TryGetValue(materialId, out var prev) || level > prev)
                levelById[materialId] = level;

            // 父边去重（同一父件对同一子件只一条 BOM 边）；root（parent=0）不记父边。
            if (parentMaterialId != 0)
            {
                if (!parentEdgesById.TryGetValue(materialId, out var edgeMap))
                {
                    edgeMap = new Dictionary<int, BomParentEdge>();
                    parentEdgesById[materialId] = edgeMap;
                }
                if (!edgeMap.ContainsKey(parentMaterialId))
                    edgeMap[parentMaterialId] = new BomParentEdge(parentMaterialId, parentMaterialCode, qtyPerUnit);
            }
        }

        void Walk(int materialId, string materialCode, int level, int parentMaterialId, string parentMaterialCode, decimal qtyPerUnit)
        {
            if (!visited.Add(materialCode)) return; // 真实环二次进入：不登记、不抬 level

            try
            {
                Register(materialId, materialCode, level, parentMaterialId, parentMaterialCode, qtyPerUnit);

                if (isPurchasedById[materialId]) return; // 采购件即叶，不下钻（对齐旧 TraverseBomNode）

                foreach (var child in GetUnitVector(structure, materialCode))
                    Walk(child.ChildMaterialId, child.ChildCode, level + 1, materialId, materialCode, child.QtyPerUnit);
            }
            finally
            {
                visited.Remove(materialCode);
            }
        }
    }
}