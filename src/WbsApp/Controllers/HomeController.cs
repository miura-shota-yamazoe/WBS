using Microsoft.AspNetCore.Mvc;

namespace WbsApp.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
