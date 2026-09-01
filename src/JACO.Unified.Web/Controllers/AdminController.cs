using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JACO.Unified.Web.Controllers;

[Authorize(Policy = "UnifiedAdmin")]
public sealed class AdminController : Controller
{
    public IActionResult Index() => View();
}
