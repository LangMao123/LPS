using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 需求优先级执行器（2号位职责 — 消费3号位的DemandPriorityConfig）
///
/// 执行算法（PM冻结口径）：
/// 1. 按CalculationLayer分层
/// 2. 每层内按SegmentOrder升序遍历Segment
/// 3. 每个Demand从第一个Segment开始匹配
/// 4. 命中第一条后停止，不再进入其它Segment（First Match）
/// 5. 每个Segment内部按SortFields依次排序
/// 6. 最后StableTieBreak确保确定性（最终兜底：DemandKey ASC）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出FrozenStrategySnapshot.DemandPriority
/// - 2号位负责执行器实现，消费策略并生成DemandSequence
/// </summary>
public sealed class DemandPriorityExecutor : IDemandPriorityExecutor
{
    private readonly ILogger<DemandPriorityExecutor> _logger;

    public DemandPriorityExecutor(ILogger<DemandPriorityExecutor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 执行Demand排序（按3号位策略）
    ///
    /// 返回：有序的Demand列表，已赋值DemandSequence = 1, 2, 3...
    /// </summary>
    public List<UpstreamDemand> ExecutePrioritySort(
        IEnumerable<UpstreamDemand> demands,
        DemandPriorityConfig config)
    {
        var demandList = demands.ToList();
        if (demandList.Count == 0)
        {
            return demandList;
        }

        var segments = config.Segments
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.CalculationLayer)
            .ThenBy(s => s.SegmentOrder)
            .ToList();

        if (segments.Count == 0)
        {
            _logger.LogWarning("DemandPriorityConfig has no enabled segments, returning original order");
            StampDemandSequence(demandList);
            return demandList;
        }

        // 按CalculationLayer分组
        var layerGroups = segments.GroupBy(s => s.CalculationLayer).OrderBy(g => g.Key);

        var sortedDemands = new List<UpstreamDemand>();

        foreach (var layerGroup in layerGroups)
        {
            var layerSegments = layerGroup.OrderBy(s => s.SegmentOrder).ToList();
            var remainingDemands = demandList.Except(sortedDemands).ToList();

            if (remainingDemands.Count == 0)
            {
                break;
            }

            // 每个Segment收集匹配的Demand
            var segmentBuckets = new List<(PrioritySegmentConfig Segment, List<UpstreamDemand> Demands)>();

            foreach (var segment in layerSegments)
            {
                var matchedDemands = new List<UpstreamDemand>();

                foreach (var demand in remainingDemands)
                {
                    if (IsMatchSegment(demand, segment))
                    {
                        matchedDemands.Add(demand);
                    }
                }

                if (matchedDemands.Count > 0)
                {
                    segmentBuckets.Add((segment, matchedDemands));
                }
            }

            // First Match原则：每个Demand只进入第一个匹配的Segment
            var assignedDemands = new HashSet<UpstreamDemand>();

            foreach (var (segment, candidates) in segmentBuckets)
            {
                var actualMatches = candidates.Where(d => !assignedDemands.Contains(d)).ToList();

                if (actualMatches.Count > 0)
                {
                    // Segment内排序
                    var sortedInSegment = SortWithinSegment(actualMatches, segment);
                    sortedDemands.AddRange(sortedInSegment);

                    foreach (var demand in actualMatches)
                    {
                        assignedDemands.Add(demand);
                    }
                }
            }

            // 未匹配任何Segment的Demand，放到该Layer末尾（按DemandKey兜底）
            var unmatchedInLayer = remainingDemands.Except(assignedDemands).OrderBy(d => d.DemandKey).ToList();
            if (unmatchedInLayer.Count > 0)
            {
                _logger.LogWarning("Layer {Layer} has {Count} unmatched demands, appending with DemandKey sort",
                    layerGroup.Key, unmatchedInLayer.Count);
                sortedDemands.AddRange(unmatchedInLayer);
            }
        }

        StampDemandSequence(sortedDemands);
        return sortedDemands;
    }

    private static void StampDemandSequence(List<UpstreamDemand> sorted)
    {
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DemandSequence = i + 1;
        }
    }

    private bool IsMatchSegment(UpstreamDemand demand, PrioritySegmentConfig segment)
    {
        if (segment.MatchConditions.Count == 0)
        {
            return true;
        }

        foreach (var condition in segment.MatchConditions)
        {
            if (!EvaluateCondition(demand, condition))
            {
                return false;
            }
        }

        return true;
    }

    private bool EvaluateCondition(UpstreamDemand demand, MatchCondition condition)
    {
        var fieldValue = GetFieldValue(demand, condition.FieldName);

        return condition.Operator.ToUpperInvariant() switch
        {
            "EQ" => string.Equals(fieldValue, condition.Value, StringComparison.OrdinalIgnoreCase),
            "IN" => condition.Value.Split(',').Any(v => string.Equals(v.Trim(), fieldValue, StringComparison.OrdinalIgnoreCase)),
            "LT" => CompareNumericOrDate(fieldValue, condition.Value) < 0,
            "LTE" => CompareNumericOrDate(fieldValue, condition.Value) <= 0,
            "GT" => CompareNumericOrDate(fieldValue, condition.Value) > 0,
            "GTE" => CompareNumericOrDate(fieldValue, condition.Value) >= 0,
            _ => throw new NotSupportedException($"Unsupported operator: {condition.Operator}")
        };
    }

    private List<UpstreamDemand> SortWithinSegment(
        List<UpstreamDemand> demands,
        PrioritySegmentConfig segment)
    {
        var query = demands.AsEnumerable();

        // 依次按SortFields排序
        if (segment.SortFields.Count > 0)
        {
            IOrderedEnumerable<UpstreamDemand>? orderedQuery = null;

            foreach (var sortField in segment.SortFields)
            {
                var isAscending = string.Equals(sortField.Direction, "ASC", StringComparison.OrdinalIgnoreCase);

                if (orderedQuery == null)
                {
                    orderedQuery = isAscending
                        ? query.OrderBy(d => GetComparableValue(d, sortField.FieldName))
                        : query.OrderByDescending(d => GetComparableValue(d, sortField.FieldName));
                }
                else
                {
                    orderedQuery = isAscending
                        ? orderedQuery.ThenBy(d => GetComparableValue(d, sortField.FieldName))
                        : orderedQuery.ThenByDescending(d => GetComparableValue(d, sortField.FieldName));
                }
            }

            query = orderedQuery ?? query;
        }

        // StableTieBreak
        if (segment.StableTieBreakFields.Count > 0)
        {
            var orderedQuery = query as IOrderedEnumerable<UpstreamDemand>;

            foreach (var tieBreakField in segment.StableTieBreakFields)
            {
                orderedQuery = orderedQuery == null
                    ? query.OrderBy(d => GetComparableValue(d, tieBreakField))
                    : orderedQuery.ThenBy(d => GetComparableValue(d, tieBreakField));
            }

            query = orderedQuery ?? query;
        }

        // 最终兜底：DemandKey ASC
        var finalOrdered = (query as IOrderedEnumerable<UpstreamDemand>)?.ThenBy(d => d.DemandKey)
                           ?? query.OrderBy(d => d.DemandKey);

        return finalOrdered.ToList();
    }

    private string GetFieldValue(UpstreamDemand demand, string fieldName)
    {
        return fieldName.ToUpperInvariant() switch
        {
            "ORDERTYPE" => demand.OrderType ?? string.Empty,
            "DELAYSTATUS" => demand.DelayStatus ?? string.Empty,
            "CUSTOMERTIER" => demand.CustomerTier ?? string.Empty,
            "DUEDATE" => demand.DueDate?.ToString("O") ?? string.Empty,
            "ISSUEDATE" => demand.IssueDate?.ToString("O") ?? string.Empty,
            "PROTECTIONSTATUS" => demand.ProtectionStatus ?? string.Empty,
            "DEMANDKEY" => demand.DemandKey,
            _ => string.Empty
        };
    }

    private IComparable GetComparableValue(UpstreamDemand demand, string fieldName)
    {
        return fieldName.ToUpperInvariant() switch
        {
            "DUEDATE" => demand.DueDate ?? DateTime.MaxValue,
            "ISSUEDATE" => demand.IssueDate ?? DateTime.MaxValue,
            "DEMANDKEY" => demand.DemandKey,
            "ORDERTYPE" => demand.OrderType ?? string.Empty,
            "DELAYSTATUS" => demand.DelayStatus ?? string.Empty,
            "CUSTOMERTIER" => demand.CustomerTier ?? string.Empty,
            "PROTECTIONSTATUS" => demand.ProtectionStatus ?? string.Empty,
            _ => string.Empty
        };
    }

    private int CompareNumericOrDate(string value1, string value2)
    {
        if (DateTime.TryParse(value1, out var date1) && DateTime.TryParse(value2, out var date2))
        {
            return date1.CompareTo(date2);
        }

        if (decimal.TryParse(value1, out var num1) && decimal.TryParse(value2, out var num2))
        {
            return num1.CompareTo(num2);
        }

        return string.Compare(value1, value2, StringComparison.OrdinalIgnoreCase);
    }
}
