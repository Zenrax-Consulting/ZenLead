using System.Net;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

/// <summary>Shared by <see cref="EmailComposer"/> and <see cref="ReplyClassifier"/>: one place that maps Semantic Kernel failures and reads token usage.</summary>
internal static class ChatCompletionHelper
{
    /// <summary>One call; provider HTTP failures become <see cref="AiProviderException"/> so SK/OpenAI types stay inside Infrastructure.</summary>
    public static async Task<ChatMessageContent> CallAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings, CancellationToken ct)
    {
        try
        {
            return await chat.GetChatMessageContentAsync(history, settings, kernel: null, ct);
        }
        catch (HttpOperationException ex)
        {
            var kind = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? AiProviderFailureKind.RateLimited
                : AiProviderFailureKind.Unavailable;
            throw new AiProviderException(kind, $"AI provider returned {(int?)ex.StatusCode}.", ex);
        }
    }

    /// <summary><see cref="CallAsync"/> with one immediate retry when the HTTP client timed out (not when the caller cancelled).</summary>
    public static async Task<ChatMessageContent> CallWithTimeoutRetryAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings, Action onRetry, CancellationToken ct)
    {
        try
        {
            return await CallAsync(chat, history, settings, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            onRetry();
            return await CallAsync(chat, history, settings, ct);
        }
    }

    public static (int Prompt, int Completion, int Total) ExtractTokenUsage(ChatMessageContent response)
    {
        if (response.Metadata is null || !response.Metadata.TryGetValue("Usage", out var usage) || usage is null)
            return (0, 0, 0);

        int Read(params string[] names)
        {
            foreach (var name in names)
                if (usage.GetType().GetProperty(name)?.GetValue(usage) is int value) return value;
            return 0;
        }

        var prompt = Read("InputTokenCount", "PromptTokens");
        var completion = Read("OutputTokenCount", "CompletionTokens");
        var total = Read("TotalTokenCount", "TotalTokens");
        return (prompt, completion, total != 0 ? total : prompt + completion);
    }
}
