using Aiursoft.Nopkg.Models.HomeViewModels;
using Aiursoft.Nopkg.Services;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.Nopkg.Controllers;

[LimitPerMin]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        return this.SimpleView(new IndexViewModel());
    }
}
