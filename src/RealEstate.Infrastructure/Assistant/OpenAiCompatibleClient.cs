using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RealEstate.Application.Assistant;

namespace RealEstate.Infrastructure.Assistant;

/// <summary>Settings of the assistant, section <c>Assistant</c>. The API key belongs in user-secrets or the environment (<c>Assistant__ApiKey</c>), never in a project file.</summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";
    public bool Enabled { get; set; }
    /// <summary>Base URL of an OpenAI-compatible API, including the version segment, e.g. https://api.openai.com/v1.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public bool VoiceEnabled { get; set; } = true;
    public string TranscriptionModel { get; set; } = "whisper-1";
    public string SpeechModel { get; set; } = "tts-1";
    public string Voice { get; set; } = "alloy";
    public int TimeoutSeconds { get; set; } = 60;
    public long MaxAudioBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>Chat (tool calling), speech-to-text and text-to-speech against any OpenAI-compatible endpoint.</summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpFactory, IOptions<AssistantOptions> options, ILogger<OpenAiCompatibleClient> log) : ILlmClient, ISpeechClient
{
    public const string HttpClientName = "assistant-llm";
    private AssistantOptions O => options.Value;

    public bool IsEnabled => O.Enabled && !string.IsNullOrWhiteSpace(O.ApiKey) && !string.IsNullOrWhiteSpace(O.BaseUrl);
    bool ISpeechClient.IsEnabled => IsEnabled && O.VoiceEnabled;
    public long MaxAudioBytes => O.MaxAudioBytes;

    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = O.ChatModel,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray()),
        };
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersJsonSchema) },
            }).ToArray());

        using var request = NewRequest("chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await SendAsync(request, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var message = json?["choices"]?[0]?["message"] ?? throw new AssistantUnavailableException("The AI provider returned an unexpected response.");
        var calls = (message["tool_calls"] as JsonArray ?? [])
            .Select(c => new LlmToolCall(c?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), c?["function"]?["name"]?.GetValue<string>() ?? "", c?["function"]?["arguments"]?.GetValue<string>() ?? "{}"))
            .Where(c => c.Name.Length > 0).ToList();
        return new LlmResponse(message["content"]?.GetValue<string>(), calls);
    }

    private static JsonNode ToJson(LlmMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    public async Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, string? language, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var ct2) ? ct2 : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(O.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        using var request = NewRequest("audio/transcriptions", form);
        using var response = await SendAsync(request, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>() ?? "";
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = O.SpeechModel, ["input"] = text, ["voice"] = O.Voice, ["response_format"] = "mp3" };
        using var request = NewRequest("audio/speech", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await SendAsync(request, ct);
        return new SpeechAudio(await response.Content.ReadAsByteArrayAsync(ct), "audio/mpeg");
    }

    private HttpRequestMessage NewRequest(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{O.BaseUrl.TrimEnd('/')}/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.ApiKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsEnabled) throw new AssistantUnavailableException("The AI assistant is not configured. Ask an administrator to enable it.");
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return response;
            log.LogWarning("AI provider returned {Status} for {Path}", (int)response.StatusCode, request.RequestUri?.AbsolutePath);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new AssistantUnavailableException(status is 401 or 403 ? "The AI provider rejected the configured key." : status == 429 ? "The AI provider is busy; try again shortly." : "The AI provider could not answer right now.");
        }
        catch (HttpRequestException ex) { throw new AssistantUnavailableException("The AI provider could not be reached.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new AssistantUnavailableException("The AI provider took too long to answer.", ex); }
    }
}

public static class AssistantRegistration
{
    public static IServiceCollection AddAssistant(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.AddOptions<AssistantOptions>().Bind(config.GetSection(AssistantOptions.Section));
        services.AddHttpClient(OpenAiCompatibleClient.HttpClientName, (sp, c) =>
            c.Timeout = TimeSpan.FromSeconds(Math.Clamp(sp.GetRequiredService<IOptions<AssistantOptions>>().Value.TimeoutSeconds, 5, 300)));
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<OpenAiCompatibleClient>());
        services.AddSingleton<ISpeechClient>(sp => sp.GetRequiredService<OpenAiCompatibleClient>());
        return services;
    }
}
