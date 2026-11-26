using System.Diagnostics;
using ZenLead.Domain.Inbound;

namespace ZenLead.Tests.Domain.Inbound;

public class ReplyTextCleanerTests
{
    [Fact]
    public void Gmail_on_wrote_block_is_cut()
        => Assert.Equal("Sounds good, Tuesday works.",
            ReplyTextCleaner.StripQuoted("Sounds good, Tuesday works.\n\nOn Tue, 5 Oct 2026 at 10:15, Zenrax <business@zenrax.test> wrote:\n> Hello Jane\n> Are you free?"));

    [Fact]
    public void Gmail_block_with_crlf_line_endings_is_cut()
        => Assert.Equal("Yes.", ReplyTextCleaner.StripQuoted("Yes.\r\n\r\nOn Tue, 5 Oct 2026 at 10:15, X <x@y.z> wrote:\r\n> quoted\r\n"));

    [Fact]
    public void Outlook_original_message_separator_is_cut()
        => Assert.Equal("Not now, thanks.",
            ReplyTextCleaner.StripQuoted("Not now, thanks.\n\n-----Original Message-----\nFrom: Zenrax\nSent: Monday\nHello"));

    [Fact]
    public void Outlook_from_sent_header_block_is_cut()
        => Assert.Equal("Call me Thursday.\n\nJane Doe\nHead of Ops",
            ReplyTextCleaner.StripQuoted("Call me Thursday.\n\nJane Doe\nHead of Ops\n\nFrom: Zenrax <business@zenrax.test>\nSent: 11 March 2026 06:00\nTo: Jane\nSubject: Hi\n\nHello Jane"));

    [Fact]
    public void Underscore_separator_is_cut()
        => Assert.Equal("Thanks!", ReplyTextCleaner.StripQuoted("Thanks!\n\n________________________________\nFrom: Zenrax\nHello"));

    [Fact]
    public void French_a_ecrit_is_cut()
        => Assert.Equal("Merci, mardi me va.", ReplyTextCleaner.StripQuoted("Merci, mardi me va.\n\nLe mar. 10 mars 2026 à 10:00, Zenrax <b@z.test> a écrit :\n> Bonjour"));

    [Fact]
    public void Inline_quoted_lines_are_dropped_and_the_earliest_cutoff_wins()
        => Assert.Equal("Answer one.\nAnswer two.",
            ReplyTextCleaner.StripQuoted("> Question one?\nAnswer one.\n> Question two?\nAnswer two.\n\n-----Original Message-----\nFrom: x\n\nOn Mon, 1 Jan 2026, X wrote:\nold"));

    [Fact]
    public void Top_posting_with_a_signature_keeps_the_signature()
        => Assert.Equal("Interested - send details.\n\n--\nJane Doe | Acme\n+44 20 0000 0000",
            ReplyTextCleaner.StripQuoted("Interested - send details.\n\n--\nJane Doe | Acme\n+44 20 0000 0000\n\nOn Wed, 11 Mar 2026 at 10:00, Zenrax <b@z.test> wrote:\n> Hello"));

    [Fact]
    public void A_reply_that_is_only_a_quote_returns_the_original_never_empty()
    {
        const string onlyQuote = "On Tue, 5 Oct 2026 at 10:15, Zenrax <b@z.test> wrote:\n> Hello Jane";
        Assert.Equal(onlyQuote, ReplyTextCleaner.StripQuoted(onlyQuote));
        Assert.Equal("> just quoted", ReplyTextCleaner.StripQuoted("> just quoted\n"));
    }

    [Fact]
    public void Text_without_quotes_is_returned_trimmed()
        => Assert.Equal("Hi there", ReplyTextCleaner.StripQuoted("  Hi there \n"));

    [Fact]
    public void Empty_input_is_empty()
        => Assert.Equal("", ReplyTextCleaner.StripQuoted(""));

    [Fact]
    public void Very_long_adversarial_input_stays_fast()
    {
        var inputs = new[]
        {
            "On " + new string('a', 200_000),                                            // never reaches "wrote:"
            string.Concat(Enumerable.Repeat("On aaaaaaaaaa wrote ", 20_000)),
            string.Concat(Enumerable.Repeat("From: x\n", 20_000)),
            new string('-', 200_000) + "Original",
            string.Concat(Enumerable.Repeat("> quoted line\n", 20_000)) + "real"
        };
        var sw = Stopwatch.StartNew();
        foreach (var input in inputs) ReplyTextCleaner.StripQuoted(input);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }
}
