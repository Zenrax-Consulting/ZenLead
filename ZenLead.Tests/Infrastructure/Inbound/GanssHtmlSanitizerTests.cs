using ZenLead.Infrastructure.Inbound;

namespace ZenLead.Tests.Infrastructure.Inbound;

public class GanssHtmlSanitizerTests
{
    private readonly GanssHtmlSanitizer _sanitizer = new();

    [Theory]
    [InlineData("<p>Hi</p><script>alert(1)</script>", "alert")]
    [InlineData("<img src=x onerror=\"alert(1)\">", "onerror")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<img src=\"https://tracker.example.com/p.gif\" width=1 height=1>", "tracker")]
    [InlineData("<iframe src=\"https://evil.example.com\"></iframe>", "evil")]
    [InlineData("<style>body{background:url(https://evil.example.com)}</style><p>x</p>", "evil")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "data:")]
    [InlineData("<p style=\"color:red\" class=\"c\" id=\"i\">x</p>", "color")]
    [InlineData("<form action=\"https://evil.example.com\"><input name=p></form>", "evil")]
    [InlineData("<p>x<!-- tracking comment --></p>", "tracking")]
    [InlineData("<svg><script>alert(1)</script></svg>", "alert")]
    [InlineData("<div onclick=\"steal()\">x</div>", "steal")]
    public void Dangerous_content_is_stripped(string html, string mustNotContain)
        => Assert.DoesNotContain(mustNotContain, _sanitizer.Sanitize(html), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Plain_formatting_survives()
    {
        var output = _sanitizer.Sanitize("<p>Hello <b>Jane</b>, <em>thanks</em><br><u>u</u></p><ul><li>one</li></ul><blockquote>q</blockquote>");
        Assert.Equal("<p>Hello <b>Jane</b>, <em>thanks</em><br><u>u</u></p><ul><li>one</li></ul><blockquote>q</blockquote>", output);
    }

    [Fact]
    public void Https_links_survive_with_nofollow_noopener_and_no_target()
    {
        var output = _sanitizer.Sanitize("<a href=\"https://acme.test/deck\" target=\"_blank\" rel=\"opener\">deck</a>");
        Assert.Equal("<a href=\"https://acme.test/deck\" rel=\"nofollow noopener\">deck</a>", output);
    }

    [Fact]
    public void Mailto_links_survive()
        => Assert.Contains("href=\"mailto:jane@acme.test\"", _sanitizer.Sanitize("<a href=\"mailto:jane@acme.test\">mail</a>"));

    [Fact]
    public void Text_inside_removed_layout_tags_is_kept()
        => Assert.Equal("Sounds good", _sanitizer.Sanitize("<div><table><tr><td><span>Sounds good</span></td></tr></table></div>"));

    [Fact]
    public void Malformed_nesting_does_not_leak_markup()
    {
        var output = _sanitizer.Sanitize("<p><b>bold<i>both</p></b><script>x()</script><img src=x");
        Assert.DoesNotContain("<script", output);
        Assert.DoesNotContain("<img", output);
        Assert.Contains("both", output);
    }

    [Fact]
    public void Empty_input_is_empty()
        => Assert.Equal("", _sanitizer.Sanitize("  "));
}
