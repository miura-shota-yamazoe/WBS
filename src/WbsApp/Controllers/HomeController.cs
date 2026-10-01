using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics;
using WbsApp.Models.ViewModels;

namespace WbsApp.Controllers;

public sealed class HomeController(ILogger<HomeController> logger) : Controller
{
    public IActionResult Index() => View();

    [IgnoreAntiforgeryToken]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerFeature>();
        if (feature is null)
        {
            return NotFound();
        }
        var errorId = Guid.NewGuid().ToString("N");
        logger.LogError(feature.Error, "処理に失敗しました。エラーID: {ErrorId}", errorId);
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View("Error", new ErrorViewModel("処理を完了できませんでした",
            "操作をやり直してください。繰り返し発生する場合は、エラーIDを控えてログを確認してください。", errorId));
    }

    [IgnoreAntiforgeryToken]
    [Route("Home/Status/{code:int}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code is >= 400 and <= 599 ? code : 404;
        return View("Error", code switch
        {
            400 => new ErrorViewModel("送信内容を確認できませんでした", "画面を開き直してから、もう一度操作してください。"),
            404 => new ErrorViewModel("ページが見つかりません", "対象が削除されたか、URLが変更された可能性があります。"),
            _ => new ErrorViewModel("処理を完了できませんでした", "画面を開き直してから、もう一度操作してください。")
        });
    }
}
