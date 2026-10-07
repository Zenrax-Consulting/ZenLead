using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

internal record SubjectBodyDto(string Subject, string Body);

public class EmailComposer(Kernel kernel, ILogger<EmailComposer> logger) : IEmailComposer
{
    private const int MaxCompletionTokens = 400; // upper bound on cost per call; the prompt asks for < 120 words

    private const string SystemPrompt = """
        You write short, personalised cold-outreach emails on behalf of a sender reaching out to a sales lead.
        Rules:
        - Keep it under 120 words.
        - Reference the lead's name and title naturally; do not fabricate facts about the lead's company.
        - Never invent claims about the sender's own company — use only what's given in the context, or stay generic.
        - Tone: professional, warm, not salesy.
        Respond with JSON matching: { "subject": string, "body": string }.
        """;

    public async Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(context));

        var settings = new OpenAIPromptExecutionSettings { ResponseFormat = typeof(SubjectBodyDto), MaxTokens = MaxCompletionTokens };
        ChatMessageContent response;
        try
        {
            response = await CallAsync(chat, history, settings, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("compose-email call timed out, retrying once for lead {LeadEmail}", context.LeadEmail);
            response = await CallAsync(chat, history, settings, ct); // one retry, no backoff — Gate 1 scope only
        }

        var (prompt, completion, total) = ExtractTokenUsage(response);
        logger.LogInformation("compose-email call used {TokensUsed} tokens for lead {LeadEmail}", total, context.LeadEmail);

        if (string.IsNullOrWhiteSpace(response.Content))
            throw new AiProviderException(AiProviderFailureKind.InvalidResponse, "The model returned an empty response.",
                promptTokens: prompt, completionTokens: completion);

        SubjectBodyDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SubjectBodyDto>(response.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new AiProviderException(AiProviderFailureKind.InvalidResponse, "The model did not return valid JSON.", ex, prompt, completion);
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Subject) || string.IsNullOrWhiteSpace(parsed.Body))
            throw new AiProviderException(AiProviderFailureKind.InvalidResponse, "Model did not return the expected JSON shape.",
                promptTokens: prompt, completionTokens: completion);

        return new ComposedEmail(parsed.Subject, parsed.Body, total, prompt, completion);
    }

    internal static string BuildUserMessage(EmailComposeContext context) =>
        $"""
        Lead name: {context.LeadName}
        Lead email: {context.LeadEmail}
        Lead title: {context.LeadTitle ?? "unknown"}
        Additional context: {context.AdditionalContext ?? "none"}
        """;

    private static async Task<ChatMessageContent> CallAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings, CancellationToken ct)
    {
        try
        {
            return await chat.GetChatMessageContentAsync(history, settings, kernel: null, ct);
        }
        catch (HttpOperationException ex)
        {
            // keep Semantic Kernel / OpenAI exception types inside Infrastructure
            var kind = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? AiProviderFailureKind.RateLimited
                : AiProviderFailureKind.Unavailable;
            throw new AiProviderException(kind, $"AI provider returned {(int?)ex.StatusCode}.", ex);
        }
    }

    private static (int Prompt, int Completion, int Total) ExtractTokenUsage(ChatMessageContent response)
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
