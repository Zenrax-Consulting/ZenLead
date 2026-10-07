namespace ZenLead.Application.Abstractions;

public record EmailComposeContext(string LeadName, string LeadEmail, string? LeadTitle, string? AdditionalContext);
public record ComposedEmail(string Subject, string Body, int TokensUsed);

public interface IEmailComposer
{
    Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default);
}
