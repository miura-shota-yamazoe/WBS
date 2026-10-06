using System.ComponentModel.DataAnnotations;
using WbsApp.Models.ViewModels;

namespace WbsApp.Services;

public static class TaskWbsSearch
{
    public static (IReadOnlyList<TaskListRow> Rows, int MatchCount) Filter(IReadOnlyList<TaskListRow> rows, TaskSearch search)
    {
        Validator.ValidateObject(search, new ValidationContext(search), true);
        search.Name = search.Name?.Trim();
        if (!search.IsFiltered) return (rows, rows.Count);
        var matches = rows.Where(row =>
            (string.IsNullOrEmpty(search.Name) || row.Name.Contains(search.Name, StringComparison.Ordinal)) &&
            (!search.Status.HasValue || row.Status == search.Status.Value) &&
            (!search.DelayedOnly || row.IsDelayed)).Select(row => row.TaskId).ToHashSet();
        var byId = rows.ToDictionary(row => row.TaskId);
        var included = new HashSet<Guid>();
        foreach (var match in matches)
        {
            Guid? current = match;
            while (current.HasValue && included.Add(current.Value))
                current = byId[current.Value].ParentTaskId;
        }
        return (rows.Where(row => included.Contains(row.TaskId))
            .Select(row => row with { IsMatch = matches.Contains(row.TaskId) }).ToArray(), matches.Count);
    }
}
