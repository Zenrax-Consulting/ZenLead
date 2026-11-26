using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Inbound;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Inbound;

public class SendGridFormParserTests
{
    private static readonly Guid EnrollmentId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Token = $"r-{EnrollmentId:N}@{InboundFixtures.InboundDomain}";
    private static readonly DateTime Now = new(2026, 3, 11, 6, 15, 0, DateTimeKind.Utc);

    private readonly CapturingLoggerProvider _logs = new();

    private SendGridFormParser Parser() => new(LoggerFactory.Create(b => b.AddProvider(_logs)).CreateLogger<SendGridFormParser>());

    private InboundEmail Parse(string fixture) => Parser().Parse(InboundFixtures.Load(fixture, EnrollmentId, "jane@example.com"), Now);

    public static TheoryData<string> Fixtures => new(InboundFixtures.FieldModeFixtures);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_fixture_parses_with_a_sender_the_token_and_a_message_id(string fixture)
    {
        var mail = Parse(fixture);

        Assert.False(string.IsNullOrWhiteSpace(mail.FromAddress));
        Assert.Contains(Token, mail.ToAddresses);
        Assert.False(string.IsNullOrWhiteSpace(mail.MessageId));
        Assert.DoesNotContain('<', mail.MessageId);
        Assert.Equal(Now, mail.ReceivedAt);
        Assert.NotEmpty(mail.Headers);
    }

    [Fact]
    public void Gmail_reply_maps_every_field()
    {
        var mail = Parse("gmail-reply");

        Assert.Equal("jane@example.com", mail.FromAddress);
        Assert.Equal("Jane Doe", mail.FromName);
        Assert.Equal("Re: Quick question about Acme", mail.Subject);
        Assert.StartsWith("Hi,", mail.Text);
        Assert.Contains("<div", mail.Html);
        Assert.Equal("CAF=gmail-reply-0001@mail.gmail.com", mail.MessageId);
        Assert.Equal("14c5d75ce93.dfd.64b469.filter0001.16648.5515E0B88.0@ismtpd0001p1lon1.sendgrid.net", mail.InReplyTo);
        Assert.Equal("{@example.com : pass}", mail.Dkim);
        Assert.Equal("pass", mail.Spf);
    }

    [Fact]
    public void Folded_headers_are_unfolded_and_references_split()
    {
        var mail = Parse("gmail-reply");

        Assert.Equal(["14c5d75ce93.dfd.64b469.filter0001.16648.5515E0B88.0@ismtpd0001p1lon1.sendgrid.net", "older-thread-id@mail.example.com"], mail.References);
        Assert.Equal("v=1; a=rsa-sha256; d=example.com; s=20230601; bh=abc=; b=def=", mail.Headers["DKIM-Signature"]);
        Assert.True(mail.Headers.ContainsKey("message-id"));                                 // case-insensitive
    }

    [Fact]
    public void Message_id_on_a_continuation_line_is_found_without_brackets()
        => Assert.Equal("AM0PR01MB0001.outlook-reply-0001@AM0PR01MB0001.eurprd01.prod.outlook.com", Parse("outlook-reply").MessageId);

    [Fact]
    public void Quoted_display_name_with_a_comma_is_parsed()
    {
        var mail = Parse("outlook-reply");
        Assert.Equal("jane@example.com", mail.FromAddress);
        Assert.Equal("Doe, Jane", mail.FromName);
        Assert.Equal([Token], mail.ToAddresses);                                             // envelope and To deduped
    }

    [Fact]
    public void Bare_address_has_no_name_and_envelope_recipients_are_merged_with_to()
    {
        var mail = Parse("apple-mail-reply");
        Assert.Equal("jane@example.com", mail.FromAddress);
        Assert.Null(mail.FromName);
        Assert.Equal([Token, "business@zenraxconsulting.test"], mail.ToAddresses);          // envelope first: it carries the token
        Assert.Equal("APPLE-0001@example.com", mail.MessageId);                            // "Message-Id" spelling
    }

    [Theory]
    [InlineData("Jane Doe <jane@acme.test>", "jane@acme.test", "Jane Doe")]
    [InlineData("\"Doe, Jane\" <jane@acme.test>", "jane@acme.test", "Doe, Jane")]
    [InlineData("jane@acme.test", "jane@acme.test", null)]
    [InlineData("Doe, Jane (Acme) <jane@acme.test>", "jane@acme.test", "Doe, Jane (Acme)")]
    [InlineData("\"Jane \\\"JD\\\" Doe\" <jane@acme.test>", "jane@acme.test", null)]
    [InlineData("<jane@acme.test>", "jane@acme.test", null)]
    public void From_parsing_is_tolerant_of_odd_display_names(string from, string address, string? name)
    {
        var fields = new Dictionary<string, string> { ["from"] = from, ["to"] = Token };
        var mail = Parser().Parse(fields, Now);
        Assert.Equal(address, mail.FromAddress);
        if (name is not null) Assert.Equal(name, mail.FromName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    public void Missing_or_unusable_from_throws_a_parse_exception(string? from)
    {
        var fields = new Dictionary<string, string> { ["to"] = Token };
        if (from is not null) fields["from"] = from;
        Assert.Throws<InboundParseException>(() => Parser().Parse(fields, Now));
    }

    [Fact]
    public void No_recipients_throws_a_parse_exception()
        => Assert.Throws<InboundParseException>(() => Parser().Parse(new Dictionary<string, string> { ["from"] = "a@b.test", ["envelope"] = "{\"to\":[]}" }, Now));

    [Fact]
    public void Malformed_envelope_json_is_ignored_not_fatal()
    {
        var mail = Parser().Parse(new Dictionary<string, string> { ["from"] = "a@b.test", ["to"] = Token, ["envelope"] = "{not json" }, Now);
        Assert.Equal([Token], mail.ToAddresses);
    }

    [Fact]
    public void Non_utf8_charset_logs_one_warning()
    {
        Parse("non-utf8");
        var warnings = _logs.Messages.Where(m => m.StartsWith("Warning")).ToList();
        Assert.Single(warnings);
        Assert.Contains("iso-8859-1", warnings[0]);
        Assert.Contains("RawMime", warnings[0]);
    }

    [Fact]
    public void Utf8_and_ascii_charsets_log_nothing()
    {
        Parse("gmail-reply");
        Parse("apple-mail-reply");
        Assert.DoesNotContain(_logs.Messages, m => m.StartsWith("Warning"));
    }

    [Fact]
    public void Missing_headers_field_still_parses()
    {
        var mail = Parser().Parse(new Dictionary<string, string> { ["from"] = "a@b.test", ["to"] = Token, ["subject"] = "Hi", ["text"] = "Body" }, Now);
        Assert.Null(mail.MessageId);
        Assert.Empty(mail.References);
        Assert.Equal("Body", mail.Text);
    }
}
