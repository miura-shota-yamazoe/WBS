using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;

namespace WbsApp.Data.Configurations;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects", table =>
        {
            table.HasCheckConstraint("ck_projects_date_range", "end_date >= start_date");
            table.HasCheckConstraint("ck_projects_status", "status IN ('IN_PROGRESS', 'COMPLETED')");
        });
        builder.HasKey(value => value.ProjectId).HasName("pk_projects");
        builder.Property(value => value.ProjectId).HasColumnName("project_id").ValueGeneratedNever();
        builder.Property(value => value.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(value => value.Description).HasColumnName("description").HasMaxLength(1000);
        builder.Property(value => value.StartDate).HasColumnName("start_date");
        builder.Property(value => value.EndDate).HasColumnName("end_date");
        builder.Property(value => value.Status).HasColumnName("status")
            .HasConversion(DatabaseValueConverters.ProjectStatusConverter)
            .HasDefaultValue(ProjectStatus.InProgress);
        builder.Property(value => value.CreatedAt).HasColumnName("created_at")
            .HasConversion(DatabaseValueConverters.UtcConverter);
        builder.Property(value => value.UpdatedAt).HasColumnName("updated_at")
            .HasConversion(DatabaseValueConverters.UtcConverter);
        builder.HasIndex(value => value.Status).HasDatabaseName("ix_projects_status");
        builder.HasIndex(value => new { value.UpdatedAt, value.ProjectId })
            .HasDatabaseName("ix_projects_updated_at");
    }
}
