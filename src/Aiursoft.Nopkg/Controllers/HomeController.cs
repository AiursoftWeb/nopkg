using Aiursoft.Nopkg.Models.HomeViewModels;
using Aiursoft.Nopkg.Services;
using Aiursoft.UiStack.Layout;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.Nopkg.Controllers;

[LimitPerMin]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        var model = new IndexViewModel();
        var view = this.SimpleView(model);
        model.Theme = Request.Cookies[ThemeController.ThemeCookieKey] == bool.TrueString ? UiTheme.Dark : UiTheme.Light;
        return view;
    }
}
