using ZenLead.Domain.Entities;
using ZenLead.Domain.Templates;

namespace ZenLead.Tests.Domain.Templates;

public class TemplateRendererTests
{
    private static Lead Lead(string name, string? company = "Acme", string? title = "CTO") => new()
    {
        Name = name, Email = "x@y.com", Title = title, Company = company is null ? null : new Company { Name = company }
    };

    private static string Render(string template, Lead lead) => TemplateRenderer.Render(template, TemplateRenderer.ValuesFor(lead)).Text;

    [Fact]
    public void Renders_All_Tokens()
        => Assert.Equal("Jane|Doe|Jane Doe|Acme|CTO",
            Render("{{firstName}}|{{lastName}}|{{name}}|{{company}}|{{title}}", Lead("Jane Doe")));

    [Fact]
    public void Token_Names_Are_Case_Insensitive_And_Ignore_Inner_Spaces()
        => Assert.Equal("Hi Jane at Acme", Render("Hi {{ FIRSTNAME }} at {{Company }}", Lead("Jane Doe")));

    [Fact]
    public void Fallback_Is_Used_When_The_Value_Is_Blank()
    {
        var result = TemplateRenderer.Render("Hi {{company|your team}}", TemplateRenderer.ValuesFor(Lead("Jane", company: "  ")));
        Assert.Equal("Hi your team", result.Text);
        Assert.Empty(result.MissingTokens);
    }

    [Fact]
    public void Missing_Without_Fallback_Is_Reported_And_Replaced_By_Empty()
    {
        var result = TemplateRenderer.Render("Hi {{firstName}} from {{company}}, {{company}}", TemplateRenderer.ValuesFor(Lead("Jane", company: null)));
        Assert.Equal("Hi Jane from , ", result.Text);
        Assert.Equal(["company"], result.MissingTokens);
    }

    [Fact]
    public void UnknownTokens_Lists_Each_Unknown_Name_Once()
        => Assert.Equal(["favouriteColour", "city"],
            TemplateRenderer.UnknownTokens("{{favouriteColour}} {{firstName}} {{city|London}} {{FavouriteColour}}"));

    [Fact]
    public void RequiredTokens_Excludes_Fallbacked_Ones()
        => Assert.Equal(["company"], TemplateRenderer.RequiredTokens("{{firstName|there}} {{company}} {{Company}}"));

    [Fact]
    public void Lead_Data_Is_Sanitised_To_A_Single_Line()
    {
        var rendered = Render("Hello {{name}}", Lead("Bob\r\nBcc: x@y.com"));
        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.Equal("Hello Bob Bcc: x@y.com", rendered);
    }

    [Theory]
    [InlineData("Cher", "Cher", null)]
    [InlineData("Jane Doe", "Jane", "Doe")]
    [InlineData("  Mary  Ann Smith ", "Mary", "Ann Smith")]
    public void First_And_Last_Name_Split_On_The_First_Space(string name, string first, string? last)
    {
        var values = TemplateRenderer.ValuesFor(Lead(name));
        Assert.Equal(first, values["firstName"]);
        Assert.Equal(last, values["lastName"]);
    }

    [Theory]
    [InlineData("Price: {100}")]
    [InlineData("{{ unclosed")]
    [InlineData("}} {{")]
    [InlineData("{{first name}}")]
    public void Text_That_Is_Not_A_Token_Is_Left_Alone(string template)
        => Assert.Equal(template, Render(template, Lead("Jane")));

    [Fact]
    public void Text_Footer_Is_Appended_Exactly_Once()
    {
        var once = UnsubscribeFooter.Append("Body", "https://app.test/u/abc");
        var twice = UnsubscribeFooter.Append(once, "https://app.test/u/abc");

        Assert.Equal(once, twice);
        Assert.StartsWith("Body\n\n—\n", once);
        Assert.EndsWith("Unsubscribe: https://app.test/u/abc", once);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "Unsubscribe:"));
    }

    [Fact]
    public void Html_Footer_Is_Appended_Exactly_Once_With_The_Url_Encoded()
    {
        var once = UnsubscribeFooter.AppendHtml("<p>Body</p>", "https://app.test/u/a?b=1&c=\"2\"");
        var twice = UnsubscribeFooter.AppendHtml(once, "https://app.test/u/a?b=1&c=\"2\"");

        Assert.Equal(once, twice);
        Assert.Contains("href=\"https://app.test/u/a?b=1&amp;c=&quot;2&quot;\"", once);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "Unsubscribe</a>"));
    }
}
