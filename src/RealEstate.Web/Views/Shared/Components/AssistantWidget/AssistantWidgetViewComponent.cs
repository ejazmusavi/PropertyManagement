using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Assistant;

namespace RealEstate.Web.Views.Shared.Components.AssistantWidget;

/// <summary>Floating assistant on every page. Not shown when the assistant is switched off or on the full assistant page itself.</summary>
public class AssistantWidgetViewComponent(AssistantService assistant) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (!assistant.IsEnabled || string.Equals(ViewContext.RouteData.Values["controller"]?.ToString(), "Assistant", StringComparison.OrdinalIgnoreCase))
            return Content(string.Empty);
        return View(assistant.VoiceEnabled);
    }
}
