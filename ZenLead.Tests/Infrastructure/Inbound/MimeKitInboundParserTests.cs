using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Inbound;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Inbound;

/// <summary>The raw-MIME switch (Inbound:ParseMode=RawMime): built and tested, not enabled by default.</summary>
public class MimeKitInboundParserTests
{
    private static readonly Guid EnrollmentId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly string Token = $"r-{EnrollmentId:N}@{InboundFixtures.InboundDomain}";
    private static readonly DateTime Now = new(2026, 3, 11, 6, 15, 0, DateTimeKind.Utc);

    private static InboundEmail Parse() => new MimeKitInboundParser().Parse(InboundFixtures.Load("raw-mime", EnrollmentId, "jane@example.com"), Now);

    [Fact]
    public void Raw_message_maps_to_the_same_shape_as_parsed_fields()
    {
        var mail = Parse();

        Assert.Equal("jane@example.com", mail.FromAddress);
        Assert.Equal("Doe, Jane", mail.FromName);
        Assert.Equal([Token], mail.ToAddresses);
        Assert.Equal("RAW-0001@mail.example.com", mail.MessageId);
        Assert.Equal("14c5d75ce93.dfd.64b469.filter0001@ismtpd0001p1lon1.sendgrid.net", mail.InReplyTo);
        Assert.Equal(["a1@example.com", "14c5d75ce93.dfd.64b469.filter0001@ismtpd0001p1lon1.sendgrid.net"], mail.References);
        Assert.Equal("{@example.com : pass}", mail.Dkim);
        Assert.Equal("pass", mail.Spf);
        Assert.Equal(Now, mail.ReceivedAt);
    }

    [Fact]
    public void Encoded_subject_and_non_utf8_body_are_decoded_correctly()
    {
        var mail = Parse();

        Assert.Equal("Re: Café next week", mail.Subject);
        Assert.Equal("Oui, un café mardi.", mail.Text!.Trim());                             // iso-8859-1 quoted-printable
        Assert.Contains("café", mail.Html);
    }

    [Fact]
    public void Missing_email_field_throws_a_parse_exception()
        => Assert.Throws<InboundParseException>(() => new MimeKitInboundParser().Parse(new Dictionary<string, string> { ["to"] = Token }, Now));

    [Fact]
    public void Message_without_from_throws_a_parse_exception()
        => Assert.Throws<InboundParseException>(() => new MimeKitInboundParser().Parse(
            new Dictionary<string, string> { ["email"] = $"To: {Token}\r\nSubject: x\r\n\r\nbody" }, Now));
}
