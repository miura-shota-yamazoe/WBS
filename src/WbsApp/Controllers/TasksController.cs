using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WbsApp.Models.ViewModels;
using WbsApp.Services;

namespace WbsApp.Controllers;

[Route("Projects/{projectId:guid}/Tasks")]
public sealed class TasksController(TaskService service, ILogger<TasksController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] Guid projectId, [FromQuery] TaskSearch search, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var unfiltered = await service.ListAsync(projectId, cancellationToken);
            if (unfiltered is null) return NotFound();
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(unfiltered with { Search = search, Tasks = [] });
        }
        var list = await service.SearchAsync(projectId, search, cancellationToken);
        ModelState.Clear();
        return list is null ? NotFound() : View(list);
    }
    [HttpGet("Create")]
    public async Task<IActionResult> Create([FromRoute] Guid projectId, Guid? parentId, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest();
        var form = await service.FormAsync(projectId, parentId: parentId, cancellationToken: cancellationToken);
        return form is null ? NotFound() : View("Form", form);
    }
    [HttpPost("Create")]
    public Task<IActionResult> Create([FromRoute] Guid projectId, TaskForm input, CancellationToken cancellationToken) => SaveAsync(projectId, null, input, cancellationToken);
    [HttpGet("{taskId:guid}/Edit")]
    public async Task<IActionResult> Edit([FromRoute] Guid projectId, [FromRoute] Guid taskId, CancellationToken cancellationToken)
    {
        var form = await service.FormAsync(projectId, taskId, cancellationToken: cancellationToken);
        return form is null ? NotFound() : View("Form", form);
    }
    [HttpPost("{taskId:guid}/Edit")]
    public Task<IActionResult> Edit([FromRoute] Guid projectId, [FromRoute] Guid taskId, TaskForm input, CancellationToken cancellationToken) => SaveAsync(projectId, taskId, input, cancellationToken);

    private async Task<IActionResult> SaveAsync(Guid projectId, Guid? taskId, TaskForm input, CancellationToken cancellationToken)
    {
        var form = await service.FormAsync(projectId, taskId, input, cancellationToken: cancellationToken);
        if (form is null)
        {
            // Keep attempted values even if the target disappeared between opening and saving.
            input.ProjectId = projectId; input.TaskId = taskId;
            Response.StatusCode = StatusCodes.Status404NotFound;
            ModelState.AddModelError("", "対象が削除されたか存在しません。入力内容を控えてプロジェクト一覧へ戻ってください。");
            return View("Form", input);
        }
        if (ModelState.IsValid)
        {
            try
            {
                var saved = taskId.HasValue ? await service.UpdateAsync(projectId, taskId.Value, input, cancellationToken)
                    : (await service.CreateAsync(projectId, input, cancellationToken)).HasValue;
                if (saved)
                {
                    TempData["SuccessMessage"] = taskId.HasValue ? "タスクを保存しました。" : "タスクを登録しました。";
                    return RedirectToAction(nameof(Index), new { projectId });
                }
                Response.StatusCode = StatusCodes.Status409Conflict;
                ModelState.AddModelError("", "対象が削除されています。入力内容を控えてプロジェクト一覧へ戻ってください。");
            }
            catch (ValidationException exception)
            {
                foreach (var field in exception.ValidationResult.MemberNames.DefaultIfEmpty(""))
                    ModelState.AddModelError(field, exception.Message);
            }
            catch (DbUpdateException exception)
            {
                var errorId = Guid.NewGuid().ToString("N");
                logger.LogError(exception, "タスク保存に失敗しました。エラーID: {ErrorId}", errorId);
                ModelState.AddModelError("", $"保存できませんでした。入力内容を保持しています。もう一度操作してください。エラーID：{errorId}");
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
        return View("Form", form);
    }
}
