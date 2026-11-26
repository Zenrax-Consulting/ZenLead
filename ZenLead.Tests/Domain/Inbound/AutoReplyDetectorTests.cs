using ZenLead.Domain.Inbound;

namespace ZenLead.Tests.Domain.Inbound;

public class AutoReplyDetectorTests
{
    private static AutoReplyKind Detect(string subject = "Re: Quick question", string from = "jane@acme.test", params (string Key, string Value)[] headers)
        => AutoReplyDetector.Detect(headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase), from, subject);

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied")]
    [InlineData("Auto-Submitted", "auto-generated")]
    [InlineData("X-Autoreply", "yes")]
    [InlineData("X-Autorespond", "1")]
    [InlineData("X-Auto-Response-Suppress", "All")]
    [InlineData("Precedence", "bulk")]
    [InlineData("Precedence", "auto_reply")]
    [InlineData("Precedence", "Junk")]
    public void Auto_reply_headers_mean_out_of_office(string header, string value)
        => Assert.Equal(AutoReplyKind.OutOfOffice, Detect(headers: (header, value)));

    [Fact]
    public void Auto_submitted_no_is_a_human_reply()
        => Assert.Equal(AutoReplyKind.None, Detect(headers: ("Auto-Submitted", "no")));

    [Fact]
    public void Precedence_list_is_not_auto()
        => Assert.Equal(AutoReplyKind.None, Detect(headers: ("Precedence", "list")));

    [Fact]
    public void Delivery_status_report_is_a_bounce()
        => Assert.Equal(AutoReplyKind.Bounce, Detect(headers: ("Content-Type", "multipart/report; report-type=delivery-status; boundary=\"x\"")));

    [Fact]
    public void Multipart_report_that_is_not_delivery_status_is_not_a_bounce()
        => Assert.Equal(AutoReplyKind.None, Detect(headers: ("Content-Type", "multipart/report; report-type=disposition-notification")));

    [Fact]
    public void Null_return_path_is_a_bounce()
        => Assert.Equal(AutoReplyKind.Bounce, Detect(headers: ("Return-Path", "<>")));

    [Theory]
    [InlineData("MAILER-DAEMON@mx.example.com")]
    [InlineData("postmaster@example.com")]
    public void Mailer_daemon_senders_are_bounces(string from)
        => Assert.Equal(AutoReplyKind.Bounce, Detect(from: from));

    [Fact]
    public void Bounce_wins_over_auto_submitted()
        => Assert.Equal(AutoReplyKind.Bounce, Detect(from: "mailer-daemon@googlemail.com", headers: ("Auto-Submitted", "auto-replied")));

    [Theory]
    [InlineData("Automatic reply: Quick question")]
    [InlineData("Out of Office: Quick question")]
    [InlineData("AUTOREPLY: Quick question")]
    [InlineData("Auto: Quick question")]
    public void Subject_only_out_of_office_is_detected(string subject)
        => Assert.Equal(AutoReplyKind.OutOfOffice, Detect(subject));

    [Fact]
    public void A_normal_reply_mentioning_out_of_office_in_the_body_is_not_auto()
        => Assert.Equal(AutoReplyKind.None, Detect("Re: Quick question", "jane@acme.test", ("Content-Type", "text/plain")));

    [Fact]
    public void Header_names_are_matched_case_insensitively_even_in_a_case_sensitive_dictionary()
        => Assert.Equal(AutoReplyKind.OutOfOffice,
            AutoReplyDetector.Detect(new Dictionary<string, string> { ["auto-submitted"] = "auto-replied" }, "a@b.c", "Hi"));
}
