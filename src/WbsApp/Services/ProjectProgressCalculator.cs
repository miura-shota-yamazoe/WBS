namespace WbsApp.Services;

public sealed record TaskProgressSource(Guid ProjectId, Guid TaskId, Guid? ParentTaskId, int Progress);

public static class ProjectProgressCalculator
{
    public static decimal? Calculate(IEnumerable<TaskProgressSource> tasks)
    {
        var snapshot = tasks.ToArray();
        var parents = snapshot.Where(task => task.ParentTaskId.HasValue)
            .Select(task => task.ParentTaskId!.Value).ToHashSet();
        long sum = 0;
        var count = 0;
        var allCompleted = true;
        foreach (var task in snapshot)
        {
            if (parents.Contains(task.TaskId)) continue;
            sum += task.Progress;
            count++;
            if (task.Progress != 100) allCompleted = false;
        }
        if (count == 0) return null;
        if (allCompleted) return 100.0m;
        return Math.Min(Math.Round((decimal)sum / count, 1, MidpointRounding.AwayFromZero), 99.9m);
    }
}
