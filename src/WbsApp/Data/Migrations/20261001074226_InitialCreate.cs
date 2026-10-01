using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WbsApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    end_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "IN_PROGRESS"),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.project_id);
                    table.CheckConstraint("ck_projects_date_range", "end_date >= start_date");
                    table.CheckConstraint("ck_projects_status", "status IN ('IN_PROGRESS', 'COMPLETED')");
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_task_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    assignee_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    start_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    end_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    progress = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "NOT_STARTED"),
                    priority = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "MEDIUM"),
                    memo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks", x => x.task_id);
                    table.CheckConstraint("ck_tasks_completion", "(progress = 100) = (status = 'COMPLETED')");
                    table.CheckConstraint("ck_tasks_date_range", "end_date >= start_date");
                    table.CheckConstraint("ck_tasks_not_self_parent", "parent_task_id IS NULL OR parent_task_id <> task_id");
                    table.CheckConstraint("ck_tasks_priority", "priority IN ('HIGH', 'MEDIUM', 'LOW')");
                    table.CheckConstraint("ck_tasks_progress", "progress BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_tasks_sort_order", "sort_order >= 1");
                    table.CheckConstraint("ck_tasks_status", "status IN ('NOT_STARTED', 'IN_PROGRESS', 'IN_REVIEW', 'COMPLETED')");
                    table.ForeignKey(
                        name: "fk_tasks_parent",
                        column: x => x.parent_task_id,
                        principalTable: "tasks",
                        principalColumn: "task_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tasks_projects",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_projects_status",
                table: "projects",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_projects_updated_at",
                table: "projects",
                columns: new[] { "updated_at", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_parent_task_id",
                table: "tasks",
                column: "parent_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_project_end_status",
                table: "tasks",
                columns: new[] { "project_id", "end_date", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_tasks_child_order",
                table: "tasks",
                columns: new[] { "project_id", "parent_task_id", "sort_order" },
                unique: true,
                filter: "parent_task_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_tasks_root_order",
                table: "tasks",
                columns: new[] { "project_id", "sort_order" },
                unique: true,
                filter: "parent_task_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
