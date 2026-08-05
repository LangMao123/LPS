using LPS.APS.Scheduling.Algorithms;
using LPS.APS.Scheduling.DataStructures;
using LPS.APS.Core.Models.Scheduling;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 有限产能排程求解器
/// 【1号位核心算法引擎】阶段3：纯粹的时空时序推演（排俄罗斯方块）
/// 
/// 职责：
/// - 步骤3.1：任务优先级排序（Priority DESC）
/// - 步骤3.2：时间槽寻址（倒排 + 撞墙翻转正排）
/// - 步骤3.3：换型优化启发式（SetupAttribute分组）
/// - 虚拟库存硬约束（AvailableTime撞墙推迟）
/// 
/// 架构红线：
/// - 纯内存计算，严禁任何I/O操作
/// - 不修改ScheduleContext中的订单/库存数据，只填充Task的StartTime/EndTime
/// - 设备负荷率必须 ≤ 100%（这是算法正确性保证，不是业务校验）
/// </summary>
public class FiniteCapacitySolver
{
    private readonly TimeSlotFinder _timeSlotFinder;
    private readonly SetupOptimizer _setupOptimizer;

    public FiniteCapacitySolver()
    {
        _timeSlotFinder = new TimeSlotFinder();
        _setupOptimizer = new SetupOptimizer();
    }

    /// <summary>
    /// 执行有限产能排程
    /// </summary>
    /// <param name="context">排程沙盘上下文（由2号位在阶段1填充）</param>
    /// <param name="options">排程配置选项</param>
    /// <returns>排程结果</returns>
    public SchedulingResult Solve(SchedulingContext context, SchedulingOptions options)
    {
        // L42: 记录排程开始的UTC时间戳，用于最后统计总耗时
        var startTime = DateTime.UtcNow;

        // L43: 创建排程结果对象，后续会累计成功/失败任务数
        var result = new SchedulingResult();

        // L46: 步骤3.1 - 把context中所有Task按Priority降序装入优先级队列
        //      BuildPriorityQueue内部调用PriorityTaskQueue，值越大越优先出队
        var taskQueue = BuildPriorityQueue(context);

        // L49-70: 步骤3.2+3.3 - 主循环，逐个Task出队并寻址时间槽
        while (!taskQueue.IsEmpty)
        {
            // L51: 从队列取出当前优先级最高的Task（Priority DESC，相同优先级按入队顺序）
            var task = taskQueue.Dequeue();

            // L54: 调用TimeSlotFinder为这个Task寻找可排时间槽
            //      内部逻辑：
            //        1. 检查前驱Task完成时间 + 物料AvailableTime → earliestStart
            //        2. 根据Strategy.Mode选倒排(Backward)/正排(Forward)/混合(BackwardThenForward)
            //        3. 倒排：从CustomerDueDate往前推DurationMinutes，检查冲突
            //        4. 正排/撞墙翻转：从earliestStart往后扫设备日历空闲槽，用IntervalTree加速
            //        5. 返回TimeWindow(Start, End)或null（找不到）
            var slot = _timeSlotFinder.FindSlot(task, context, options);

            // L56: 判断是否找到可用时间槽
            if (slot.HasValue)
            {
                // L58-59: 找到了 → 回填Task的计划开始/结束时间
                //         这是1号位唯一允许写SchedulingTask的字段
                task.PlannedStartTime = slot.Value.Start;
                task.PlannedEndTime = slot.Value.End;

                // L60: 累加成功排程计数
                result.ScheduledCount++;
            }
            else
            {
                // L64-65: 找不到 → 清空时间字段（标记为未排程状态）
                task.PlannedStartTime = null;
                task.PlannedEndTime = null;

                // L66: 累加失败计数
                result.UnscheduledCount++;

                // L67-68: 记录失败原因到结果对象的列表中，供后续诊断
                result.UnscheduledReasons.Add(
                    $"Task {task.TaskId}: 无法在计划期内找到可用时间槽");
            }
        }
        // L70: while循环结束，所有Task都已处理完毕

        // L72: 计算排程总耗时（当前时间 - 开始时间）
        result.SolveDuration = DateTime.UtcNow - startTime;

        // L73: 判定排程是否全部成功（UnscheduledCount == 0 才算Success）
        result.Success = result.UnscheduledCount == 0;

        // L74: 返回排程结果对象给3号位Orchestrator，由其写回数据库
        return result;
    }

    /// <summary>
    /// 执行局部重排（场景6步骤6.3）
    /// 锁定的Task作为时间锚点不移动，只对未锁定Task重新寻址
    /// 典型场景：插单/急单到达，需要重排未开工的Task，但已在制Task不能动
    /// </summary>
    /// <param name="context">排程沙盘上下文</param>
    /// <param name="options">排程配置选项</param>
    /// <param name="lockedTaskIds">锁定Task的ID列表（通常是IsLocked=true或已开工的Task）</param>
    /// <returns>重排结果</returns>
    public SchedulingResult Reschedule(SchedulingContext context, SchedulingOptions options, IReadOnlyList<string> lockedTaskIds)
    {
        // L83: 记录重排开始时间戳
        var startTime = DateTime.UtcNow;

        // L84: 创建结果对象
        var result = new SchedulingResult();

        // L86: 将锁定ID列表转为HashSet，加速后续Contains查询（O(1)复杂度）
        var lockedSet = new HashSet<string>(lockedTaskIds);

        // L89-99: 遍历所有Task，清空未锁定Task的时间（锁定Task保持原时间不动）
        foreach (var task in context.Tasks)
        {
            // L91: 判断当前Task是否在锁定列表中
            if (lockedSet.Contains(task.TaskId))
            {
                // L93: 锁定Task作为时间锚点，PlannedStartTime/EndTime保持不变
                //      这些Task被视为"已占用时间槽"，后续寻址时会避开它们
                continue;
            }

            // L97-98: 非锁定Task清空时间字段，标记为"待重排"状态
            //         后续会重新为它们寻址
            task.PlannedStartTime = null;
            task.PlannedEndTime   = null;
        }

        // L102-106: 只对未锁定Task构建优先级队列
        //           锁定Task不参与排队（它们的时间已经固定）
        var queue = new PriorityTaskQueue<SchedulingTask>();
        queue.EnqueueRange(
            context.Tasks
                .Where(t => !lockedSet.Contains(t.TaskId))  // 过滤掉锁定Task
                .Select(t => (t, (double)t.Priority)));

        // L108-127: 主循环，与Solve逻辑相同，逐个未锁定Task出队并寻址
        while (!queue.IsEmpty)
        {
            // L110: 取出当前优先级最高的未锁定Task
            var task = queue.Dequeue();

            // L112: 调用FindSlot寻址
            //       此时锁定Task的时间槽已经在context.Tasks中，会被FindSlot识别为"已占用"
            //       算法会自动避开这些锁定Task，在空闲时间槽中寻址
            var slot = _timeSlotFinder.FindSlot(task, context, options);

            // L113-126: 根据寻址结果回填时间或记录失败（与Solve逻辑一致）
            if (slot.HasValue)
            {
                // L115-116: 找到槽 → 回填时间
                task.PlannedStartTime = slot.Value.Start;
                task.PlannedEndTime   = slot.Value.End;

                // L117: 累加成功计数
                result.ScheduledCount++;
            }
            else
            {
                // L121-122: 找不到槽 → 清空时间（保持未排程状态）
                task.PlannedStartTime = null;
                task.PlannedEndTime   = null;

                // L123: 累加失败计数
                result.UnscheduledCount++;

                // L124-125: 记录失败原因（标注为"局部重排"失败）
                result.UnscheduledReasons.Add(
                    $"Task {task.TaskId}: 局部重排无法在计划期内找到可用时间槽");
            }
        }

        // L129: 计算重排总耗时
        result.SolveDuration = DateTime.UtcNow - startTime;

        // L130: 判定是否全部未锁定Task都重排成功
        result.Success = result.UnscheduledCount == 0;

        // L131: 返回结果
        return result;
    }

    /// <summary>
    /// 构建优先级队列（Priority降序）
    /// </summary>
    /// <param name="context">排程沙盘上下文</param>
    /// <returns>按Priority DESC排序的任务队列</returns>
    private PriorityTaskQueue<SchedulingTask> BuildPriorityQueue(SchedulingContext context)
    {
        // L136: 创建优先级队列实例（内部是懒排序，Dequeue时才真正排序）
        var queue = new PriorityTaskQueue<SchedulingTask>();

        // L137-138: 把context中所有Task批量入队
        //           LINQ投影：每个Task → (Task对象, Priority值转double)
        //           Priority值越大，出队优先级越高（降序）
        //           相同Priority时按入队顺序FIFO
        queue.EnqueueRange(
            context.Tasks.Select(t => (t, (double)t.Priority)));

        // L139: 返回构建好的队列，供Solve主循环消费
        return queue;
    }
}
