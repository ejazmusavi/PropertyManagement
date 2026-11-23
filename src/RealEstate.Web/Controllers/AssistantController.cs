using Microsoft.AspNetCore.Mvc;
using RealEstate.Application.Assistant;
using RealEstate.Application.Common;

namespace RealEstate.Web.Controllers;

/// <summary>The AI assistant: full page plus the JSON endpoints used by the floating widget and the page (ask, speech-to-text, text-to-speech). Any signed-in staff user may use it; every data tool enforces the user's own permissions.</summary>
public class AssistantController(AssistantService assistant) : Controller
{
    public sealed record AskBody(List<AssistantMessage>? Messages, string? CurrentPath);
    public sealed record SpeakBody(string? Text);

    [HttpGet]
    public IActionResult Index() => View(new AssistantPageViewModel(assistant.IsEnabled, assistant.VoiceEnabled));

    [HttpPost]
    public async Task<IActionResult> Ask([FromBody] AskBody body, CancellationToken ct)
    {
        try
        {
            var reply = await assistant.AskAsync(new AssistantRequest(body.Messages ?? [], body.CurrentPath), ct);
            return Json(new { text = reply.Text, navigate = reply.Navigate is null ? null : new { url = reply.Navigate.Url, title = reply.Navigate.Title }, tools = reply.ToolsUsed });
        }
        catch (RequestValidationException ex) { return Problem(StatusCodes.Status400BadRequest, string.Join(" ", ex.Errors.SelectMany(e => e.Value))); }
        catch (AssistantUnavailableException ex) { return Problem(StatusCodes.Status503ServiceUnavailable, ex.Message); }
    }

    [HttpPost, RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Transcribe(IFormFile? audio, [FromForm] string? language, CancellationToken ct)
    {
        if (audio is null || audio.Length == 0) return Problem(StatusCodes.Status400BadRequest, "No audio was received.");
        try
        {
            await using var stream = audio.OpenReadStream();
            var text = await assistant.TranscribeAsync(stream, Path.GetFileName(audio.FileName), audio.ContentType, language, ct);
            return Json(new { text });
        }
        catch (AssistantUnavailableException ex) { return Problem(StatusCodes.Status503ServiceUnavailable, ex.Message); }
    }

    [HttpPost]
    public async Task<IActionResult> Speak([FromBody] SpeakBody body, CancellationToken ct)
    {
        try
        {
            var audio = await assistant.SpeakAsync(body.Text ?? "", ct);
            return File(audio.Bytes, audio.ContentType);
        }
        catch (RequestValidationException ex) { return Problem(StatusCodes.Status400BadRequest, string.Join(" ", ex.Errors.SelectMany(e => e.Value))); }
        catch (AssistantUnavailableException ex) { return Problem(StatusCodes.Status503ServiceUnavailable, ex.Message); }
    }

    private JsonResult Problem(int status, string message) { Response.StatusCode = status; return Json(new { error = message }); }
}

public sealed record AssistantPageViewModel(bool Enabled, bool VoiceEnabled);
