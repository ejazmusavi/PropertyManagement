using FluentValidation;

namespace RealEstate.Application.Assistant;

/// <summary>One chat turn kept by the browser. Only user and assistant text is accepted back from the client; tool calls and results never round-trip through it.</summary>
public sealed record AssistantMessage(string Role, string Content);

public sealed record AssistantRequest(IReadOnlyList<AssistantMessage> Messages, string? CurrentPath);

/// <summary>A navigation the browser should perform. The URL always comes from the server-side page catalogue, never from the model's free text.</summary>
public sealed record AssistantNavigation(string Url, string Title);

public sealed record AssistantReply(string Text, AssistantNavigation? Navigate, IReadOnlyList<string> ToolsUsed);

public sealed class AssistantRequestValidator : AbstractValidator<AssistantRequest>
{
    public const int MaxMessages = 20;
    public const int MaxMessageLength = 2000;

    public AssistantRequestValidator()
    {
        RuleFor(x => x.Messages).NotNull().Must(m => m is { Count: > 0 and <= MaxMessages }).WithMessage($"Send between 1 and {MaxMessages} messages.");
        RuleFor(x => x.Messages).Must(m => m is null || (m.Count > 0 && m[^1].Role == AssistantRoles.User)).WithMessage("The last message must be from the user.");
        RuleForEach(x => x.Messages).ChildRules(m =>
        {
            m.RuleFor(x => x.Role).Must(r => r is AssistantRoles.User or AssistantRoles.Assistant).WithMessage("Role must be 'user' or 'assistant'.");
            m.RuleFor(x => x.Content).NotEmpty().MaximumLength(MaxMessageLength);
        });
        RuleFor(x => x.CurrentPath).MaximumLength(300);
    }
}

public static class AssistantRoles
{
    public const string User = "user", Assistant = "assistant", System = "system", Tool = "tool";
}

// ---- LLM abstraction (provider-neutral; the OpenAI-compatible adapter lives in Infrastructure) ----

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed record LlmMessage(string Role, string? Content, IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null);

/// <summary>A callable tool; <see cref="ParametersJsonSchema"/> is a JSON-Schema object.</summary>
public sealed record LlmToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls);

public interface ILlmClient
{
    bool IsEnabled { get; }
    Task<LlmResponse> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct = default);
}

public sealed record SpeechAudio(byte[] Bytes, string ContentType);

public interface ISpeechClient
{
    bool IsEnabled { get; }
    long MaxAudioBytes { get; }
    Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, string? language, CancellationToken ct = default);
    Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct = default);
}

/// <summary>Raised when the assistant is switched off or the provider fails; the message is safe to show to the user.</summary>
public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
