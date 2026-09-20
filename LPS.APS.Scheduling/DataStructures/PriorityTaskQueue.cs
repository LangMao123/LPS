namespace LPS.APS.Scheduling.DataStructures;

/// <summary>
/// 优先级任务队列
/// 按优先级降序排列，支持换型优化时的局部微调
///
/// 【遗留死代码】本类仅被 FiniteCapacitySolver 的死代码方法 Solve()（BuildPriorityQueue）和
/// Reschedule() 使用（见 FiniteCapacitySolver.cs 头注释）。
/// 生产五阶段流程（SolveAsync → Phase1-5）不经过本类：Phase2 主循环按 CrossMaterialOrder 分层遍历，
/// 不再使用基于 Priority 的优先级队列。
/// 保留本类仅为历史参考，请勿据此判断生产排程顺序。
/// </summary>
/// <typeparam name="T">任务类型</typeparam>
public class PriorityTaskQueue<T>
{
    private readonly List<(T Item, double Priority)> _items = new();
    private bool _sorted;

    /// <summary>
    /// 队列中的任务数量
    /// </summary>
    public int Count => _items.Count;

    /// <summary>
    /// 队列是否为空
    /// </summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>
    /// 入队（优先级值越大，优先级越高）
    /// </summary>
    public void Enqueue(T item, double priority)
    {
        _items.Add((item, priority));
        _sorted = false;
    }

    /// <summary>
    /// 批量入队
    /// </summary>
    public void EnqueueRange(IEnumerable<(T Item, double Priority)> items)
    {
        _items.AddRange(items);
        _sorted = false;
    }

    /// <summary>
    /// 出队（返回优先级最高的任务）
    /// </summary>
    public T Dequeue()
    {
        EnsureSorted();
        if (_items.Count == 0)
            throw new InvalidOperationException("队列为空");

        var item = _items[0].Item;
        _items.RemoveAt(0);
        return item;
    }

    /// <summary>
    /// 查看队首元素（不移除）
    /// </summary>
    public T Peek()
    {
        EnsureSorted();
        if (_items.Count == 0)
            throw new InvalidOperationException("队列为空");

        return _items[0].Item;
    }

    /// <summary>
    /// 获取排序后的所有任务（只读）
    /// </summary>
    public IReadOnlyList<T> GetOrderedItems()
    {
        EnsureSorted();
        return _items.Select(x => x.Item).ToList().AsReadOnly();
    }

    /// <summary>
    /// 清空队列
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        _sorted = true;
    }

    private void EnsureSorted()
    {
        if (!_sorted)
        {
            _items.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            _sorted = true;
        }
    }
}
