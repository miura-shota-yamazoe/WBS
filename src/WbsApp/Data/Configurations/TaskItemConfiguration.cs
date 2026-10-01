using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Data.Configurations;

public sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("tasks", table =>
        {
            table.HasCheckConstraint("ck_tasks_date_range", "end_date >= start_date");
            table.HasCheckConstraint("ck_tasks_progress", "progress BETWEEN 0 AND 100");
            table.HasCheckConstraint("ck_tasks_status", "status IN ('NOT_STARTED', 'IN_PROGRESS', 'IN_REVIEW', 'COMPLETED')");
            table.HasCheckConstraint("ck_tasks_priority", "priority IN ('HIGH', 'MEDIUM', 'LOW')");
            table.HasCheckConstraint("ck_tasks_sort_order", "sort_order >= 1");
            table.HasCheckConstraint("ck_tasks_not_self_parent", "parent_task_id IS NULL OR parent_task_id <> task_id");
            table.HasCheckConstraint("ck_tasks_completion", "(progress = 100) = (status = 'COMPLETED')");
        });
        builder.HasKey(value => value.TaskId).HasName("pk_tasks");
        builder.Property(value => value.TaskId).HasColumnName("task_id").ValueGeneratedNever();
        builder.Property(value => value.ProjectId).HasColumnName("project_id");
        builder.Property(value => value.ParentTaskId).HasColumnName("parent_task_id");
        builder.Property(value => value.SortOrder).HasColumnName("sort_order");
        builder.Property(value => value.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(value => value.AssigneeName).HasColumnName("assignee_name").HasMaxLength(100);
        builder.Property(value => value.StartDate).HasColumnName("start_date");
        builder.Property(value => value.EndDate).HasColumnName("end_date");
        builder.Property(value => value.Progress).HasColumnName("progress").HasDefaultValue(0);
        builder.Property(value => value.Status).HasColumnName("status")
            .HasConversion(DatabaseValueConverters.TaskStatusConverter).HasDefaultValue(TaskStatus.NotStarted);
        builder.Property(value => value.Priority).HasColumnName("priority")
            .HasConversion(DatabaseValueConverters.TaskPriorityConverter).HasDefaultValue(TaskPriority.Medium)
            .HasSentinel(TaskPriority.Medium);
        builder.Property(value => value.Memo).HasColumnName("memo").HasMaxLength(2000);
        builder.Property(value => value.CreatedAt).HasColumnName("created_at")
            .HasConversion(DatabaseValueConverters.UtcConverter);
        builder.Property(value => value.UpdatedAt).HasColumnName("updated_at")
            .HasConversion(DatabaseValueConverters.UtcConverter);
        builder.HasOne(value => value.Project).WithMany(value => value.Tasks)
            .HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_tasks_projects");
        builder.HasOne(value => value.ParentTask).WithMany(value => value.ChildTasks)
            .HasForeignKey(value => value.ParentTaskId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_tasks_parent");
        builder.HasIndex(value => new { value.ProjectId, value.SortOrder })
            .IsUnique().HasFilter("parent_task_id IS NULL").HasDatabaseName("uq_tasks_root_order");
        builder.HasIndex(value => new { value.ProjectId, value.ParentTaskId, value.SortOrder })
            .IsUnique().HasFilter("parent_task_id IS NOT NULL").HasDatabaseName("uq_tasks_child_order");
        builder.HasIndex(value => value.ParentTaskId).HasDatabaseName("ix_tasks_parent_task_id");
        builder.HasIndex(value => new { value.ProjectId, value.EndDate, value.Status })
            .HasDatabaseName("ix_tasks_project_end_status");
    }
}
