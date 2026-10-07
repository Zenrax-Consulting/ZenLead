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

        var settings = new OpenAIPromptExecutionSettings { ResponseFormat = typeof(SubjectBodyDto) };
        var response = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);

        var tokensUsed = ExtractTokenUsage(response);
        logger.LogInformation("compose-email call used {TokensUsed} tokens for lead {LeadEmail}", tokensUsed, context.LeadEmail);

        var parsed = JsonSerializer.Deserialize<SubjectBodyDto>(response.Content!, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Model did not return the expected JSON shape.");

        return new ComposedEmail(parsed.Subject, parsed.Body, tokensUsed);
    }

    private static string BuildUserMessage(EmailComposeContext context) =>
        $"""
        Lead name: {context.LeadName}
        Lead email: {context.LeadEmail}
        Lead title: {context.LeadTitle ?? "unknown"}
        Additional context: {context.AdditionalContext ?? "none"}
        """;

    private static int ExtractTokenUsage(ChatMessageContent response)
    {
        if (response.Metadata is not null && response.Metadata.TryGetValue("Usage", out var usage) && usage is not null)
        {
            var property = usage.GetType().GetProperty("TotalTokens") ?? usage.GetType().GetProperty("TotalTokenCount");
            if (property?.GetValue(usage) is int total) return total;
        }
        return 0;
    }
}
