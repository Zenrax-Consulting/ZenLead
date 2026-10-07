using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

public class EmailComposer(Kernel kernel) : IEmailComposer
{
    public async Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a cold-outreach email assistant. Write a short, personalised email.");
        history.AddUserMessage($"Lead: {context.LeadName} ({context.LeadEmail}), title: {context.LeadTitle ?? "unknown"}.");

        var response = await chat.GetChatMessageContentAsync(history, kernel: kernel, cancellationToken: ct);

        return new ComposedEmail("Quick question", response.Content ?? string.Empty, 0);
    }
}
