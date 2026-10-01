using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WbsApp.Models.ViewModels;
using WbsApp.Services;

namespace WbsApp.Controllers;

[Route("Projects")]
public sealed class ProjectsController(ProjectService service, ILogger<ProjectsController> logger) : Controller
{
    [HttpGet("Create")]
    public IActionResult Create() => Form(new ProjectForm { Status = WbsApp.Models.Enums.ProjectStatus.InProgress });

    [HttpPost("Create")]
    public async Task<IActionResult> Create(ProjectForm input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Form(input);
        try
        {
            var id = await service.CreateAsync(input, cancellationToken);
            TempData["SuccessMessage"] = "プロジェクトを登録しました。";
            return RedirectToAction(nameof(Edit), new { id });
        }
        catch (ValidationException exception) { ModelState.AddModelError("", exception.Message); }
        catch (DbUpdateException exception) { AddSaveError(exception); }
        return Form(input);
    }

    [HttpGet("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var input = await service.FindAsync(id, cancellationToken);
        return input is null ? NotFound() : Form(input, id);
    }

    [HttpPost("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, ProjectForm input, CancellationToken cancellationToken)
    {
        if (await service.FindAsync(id, cancellationToken) is null) return NotFound();
        if (!ModelState.IsValid) return Form(input, id);
        try
        {
            if (!await service.UpdateAsync(id, input, cancellationToken))
            {
                ModelState.AddModelError("", "対象が削除されています。入力内容を控えてホームへ戻ってください。");
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Form(input, id);
            }
            TempData["SuccessMessage"] = "プロジェクトを保存しました。";
            return RedirectToAction(nameof(Edit), new { id });
        }
        catch (ValidationException exception) { ModelState.AddModelError("", exception.Message); }
        catch (DbUpdateException exception) { AddSaveError(exception); }
        return Form(input, id);
    }

    private ViewResult Form(ProjectForm input, Guid? id = null)
    {
        ViewData["ProjectId"] = id;
        ViewData["Title"] = id is null ? "プロジェクト登録" : "プロジェクト編集";
        return View("Form", input);
    }

    private void AddSaveError(Exception exception)
    {
        var errorId = Guid.NewGuid().ToString("N");
        logger.LogError(exception, "プロジェクトの保存に失敗しました。エラーID: {ErrorId}", errorId);
        ModelState.AddModelError("", $"保存できませんでした。入力内容を保持しています。もう一度操作してください。エラーID：{errorId}");
        Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }
}
