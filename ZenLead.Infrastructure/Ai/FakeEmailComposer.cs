using System.Text.RegularExpressions;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

/// <summary>
/// Deterministic stand-in for the OpenAI composer, selected by <c>Ai:Provider=Fake</c> (Development and E2E only). The same input
/// always gives the same output, and token counts are fixed, so usage and budget screens still show something. Callers log usage
/// and check the budget exactly as they do for the real composer.
/// </summary>
public partial class FakeEmailComposer : IEmailComposer
{
    public const int PromptTokens = 120;
    public const int CompletionTokens = 60;

    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        var firstName = context.LeadName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";
        var company = string.IsNullOrWhiteSpace(context.CompanyName) ? "your team" : context.CompanyName;

        // campaign personalisation passes the step as "Subject: …\n\n<body>" between --- lines; keep its intent
        var match = context.AdditionalContext is null ? null : StepPattern().Match(context.AdditionalContext);
        var subject = match is { Success: true } ? match.Groups["subject"].Value.Trim() : $"A quick idea for {company}";
        var body = match is { Success: true }
            ? match.Groups["body"].Value.Trim()
            : $"I noticed what {company} is working on and thought a short conversation could help.";

        var text = $"Hi {firstName},\n\n{body}\n\n(fake AI personalisation for {company})";
        return Task.FromResult(new ComposedEmail($"[AI] {subject}", text, PromptTokens + CompletionTokens, PromptTokens, CompletionTokens));
    }

    [GeneratedRegex(@"Subject:\s*(?<subject>[^\r\n]*)\r?\n\r?\n(?<body>.*?)(\r?\n---|$)", RegexOptions.Singleline)]
    private static partial Regex StepPattern();
}
