namespace ZenLead.Application.Abstractions;

public record EmailComposeContext(string LeadName, string LeadEmail, string? LeadTitle, string? AdditionalContext);
public record ComposedEmail(string Subject, string Body, int TokensUsed, int PromptTokens = 0, int CompletionTokens = 0);

public interface IEmailComposer
{
    Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default);
}
