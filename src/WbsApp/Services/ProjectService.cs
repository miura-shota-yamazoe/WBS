using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Services;

public sealed class ProjectService(AppDbContext db, TimeProvider clock)
{
    public async Task<ProjectList> SearchAsync(ProjectSearch search, CancellationToken cancellationToken = default)
    {
        if (search.Status.HasValue && !Enum.IsDefined(search.Status.Value))
            throw new ValidationException("状態はすべて・進行中・完了から選択してください。");
        search.Name = search.Name?.Trim();
        var query = db.Projects.AsNoTracking();
        if (!string.IsNullOrEmpty(search.Name)) query = query.Where(value => value.Name.Contains(search.Name));
        if (search.Status.HasValue) query = query.Where(value => value.Status == search.Status.Value);
        var count = await query.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (count - 1) / 100 + 1);
        search.Page = Math.Clamp(search.Page, 1, pageCount);
        var items = await query.OrderByDescending(value => value.UpdatedAt).ThenBy(value => value.ProjectId)
            .Skip((search.Page - 1) * 100).Take(100)
            .Select(value => new ProjectListItem(value.ProjectId, value.Name, value.StartDate, value.EndDate, value.Status, value.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new ProjectList { Search = search, Items = items, TotalCount = count };
    }

    public async Task<ProjectForm?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(value => value.ProjectId == id, cancellationToken);
        return project is null ? null : new ProjectForm
        {
            Name = project.Name, Description = project.Description, StartDate = project.StartDate,
            EndDate = project.EndDate, Status = project.Status
        };
    }

    public async Task<Guid> CreateAsync(ProjectForm input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        var now = clock.GetUtcNow().UtcDateTime;
        var project = new Project { Name = input.Name!, CreatedAt = now, UpdatedAt = now };
        Apply(project, input);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project.ProjectId;
    }

    public async Task<bool> UpdateAsync(Guid id, ProjectForm input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        var project = await db.Projects.SingleOrDefaultAsync(value => value.ProjectId == id, cancellationToken);
        if (project is null) return false;
        // Until T14 supplies warning confirmation, do not silently save changes requiring it.
        if (input.Status == ProjectStatus.Completed && project.Status != ProjectStatus.Completed && await db.Tasks.AnyAsync(value =>
            value.ProjectId == id && value.Status != TaskStatus.Completed, cancellationToken))
            throw new ValidationException("未完了のタスクがあります。完了への変更には確認が必要です。");
        if ((input.StartDate != project.StartDate || input.EndDate != project.EndDate) &&
            await db.Tasks.AnyAsync(value => value.ProjectId == id &&
            (value.StartDate < input.StartDate!.Value || value.EndDate > input.EndDate!.Value), cancellationToken))
            throw new ValidationException("期間外になるタスクがあります。期間の変更には確認が必要です。");
        Apply(project, input);
        var now = clock.GetUtcNow().UtcDateTime;
        project.UpdatedAt = now > project.UpdatedAt ? now : project.UpdatedAt.AddTicks(1);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return false; }
        return true;
    }

    private static void Validate(ProjectForm input) => Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
    private static void Apply(Project project, ProjectForm input)
    {
        project.Name = input.Name!;
        project.Description = string.IsNullOrEmpty(input.Description) ? null : input.Description;
        project.StartDate = input.StartDate!.Value;
        project.EndDate = input.EndDate!.Value;
        project.Status = input.Status!.Value;
    }
}
