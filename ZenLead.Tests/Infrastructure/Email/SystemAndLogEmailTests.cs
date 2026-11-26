using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Infrastructure.Email;

namespace ZenLead.Tests.Infrastructure.Email;

public class SystemEmailTests
{
    [Fact]
    public void Html_twin_encodes_display_text_and_url()
    {
        var email = SystemEmail.Create("a@b.test", "Ann", "Subj", "Hi <script>alert(\"x\")</script>",
            "https://app.test/r?a=1&b=\"2\"", "Approve <now>");

        Assert.DoesNotContain("<script>", email.HtmlBody);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.Contains("&quot;", email.HtmlBody);
        Assert.Contains("href=\"https://app.test/r?a=1&amp;b=&quot;2&quot;\"", email.HtmlBody);
        Assert.Contains("Approve &lt;now&gt;", email.HtmlBody);
    }

    [Fact]
    public void Plain_text_always_contains_the_link()
    {
        var email = SystemEmail.Create("a@b.test", null, "Subj", "Line one\nLine two", "https://app.test/reset?t=abc");

        Assert.Contains("https://app.test/reset?t=abc", email.TextBody);
        Assert.StartsWith("Line one\nLine two", email.TextBody);
        Assert.Contains("<br>", email.HtmlBody);
    }

    [Fact]
    public void Is_categorised_as_system_mail_with_no_tracking()
    {
        var email = SystemEmail.Create("a@b.test", "Ann", "Subj", "Body");

        Assert.Equal(new EmailAddress("a@b.test", "Ann"), email.To);
        Assert.Equal(["system"], email.Categories!);
        Assert.False(email.TrackOpens);
        Assert.Equal("Body", email.TextBody);
    }
}

public class LogEmailSenderTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Returns_success_and_logs_recipient_subject_and_body()
    {
        var logger = new CapturingLogger<LogEmailSender>();

        var result = await new LogEmailSender(logger).SendAsync(
            SystemEmail.Create("ann@acme.test", null, "Reset your password", "Click below", "https://app.test/reset?t=xyz"));

        Assert.True(result.Success);
        Assert.StartsWith("log-", result.ProviderMessageId);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("ann@acme.test", message);
        Assert.Contains("Reset your password", message);
        Assert.Contains("https://app.test/reset?t=xyz", message);
    }
}
