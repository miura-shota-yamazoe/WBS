using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Services;

public sealed record TaskTreeSource(Guid ProjectId, Guid TaskId, Guid? ParentTaskId, int SortOrder,
    string Name, string? AssigneeName, DateOnly StartDate, DateOnly EndDate,
    int Progress, TaskStatus Status, TaskPriority Priority);

public static class TaskTreeBuilder
{
    public static IReadOnlyList<TaskListRow> Build(IReadOnlyList<TaskTreeSource> tasks, DateOnly today)
    {
        var byId = new Dictionary<Guid, TaskTreeSource>(tasks.Count);
        var roots = new List<TaskTreeSource>();
        var children = new Dictionary<Guid, List<TaskTreeSource>>();
        foreach (var task in tasks)
            if (!byId.TryAdd(task.TaskId, task)) throw new InvalidDataException("タスクIDが重複しています。");

        foreach (var task in tasks)
        {
            if (task.ParentTaskId is not Guid parentId) { roots.Add(task); continue; }
            if (!byId.ContainsKey(parentId) || parentId == task.TaskId)
                throw new InvalidDataException("タスクの親参照が不正です。");
            if (!children.TryGetValue(parentId, out var siblings)) children[parentId] = siblings = [];
            siblings.Add(task);
        }

        static void OrderAndCheck(List<TaskTreeSource> siblings)
        {
            siblings.Sort((left, right) => left.SortOrder.CompareTo(right.SortOrder));
            for (var index = 0; index < siblings.Count; index++)
                if (siblings[index].SortOrder != index + 1)
                    throw new InvalidDataException("同じ親のタスク表示順が連続していません。");
        }
        OrderAndCheck(roots);
        foreach (var siblings in children.Values) OrderAndCheck(siblings);

        var result = new List<TaskListRow>(tasks.Count);
        var stack = new Stack<(TaskTreeSource Task, string Number, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--)
            stack.Push((roots[index], (index + 1).ToString(), 0));

        while (stack.Count > 0)
        {
            var (task, number, depth) = stack.Pop();
            if (result.Count >= tasks.Count) throw new InvalidDataException("タスクの階層が循環しています。");
            var hasChildren = children.TryGetValue(task.TaskId, out var siblings) && siblings.Count > 0;
            result.Add(new TaskListRow(task.TaskId, task.Name, task.AssigneeName, task.StartDate, task.EndDate,
                task.Progress, task.Status, task.Priority, number, depth, !hasChildren,
                TaskProgressRules.IsDelayed(task.EndDate, task.Status, today)));
            if (!hasChildren) continue;
            for (var index = siblings!.Count - 1; index >= 0; index--)
                stack.Push((siblings[index], number + "." + (index + 1), depth + 1));
        }
        if (result.Count != tasks.Count) throw new InvalidDataException("到達できないタスクがあります。階層を確認してください。");
        return result;
    }
}
