using ZenLead.Domain.Discovery;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Domain;

public class TargetProfileSuggesterTests
{
    private static LeadProfileInput L(string? title, string? industry, string? country, string? size, LeadStatus status = LeadStatus.New)
        => new(title, industry, country, size, status);

    [Fact]
    public void SingleLead_CopiesItsFields()
    {
        var s = TargetProfileSuggester.Suggest([L("CTO", "Software", "US", "51-200")], "Jane Doe");

        Assert.Equal("Similar to Jane Doe", s.Name);
        Assert.Equal(["CTO"], s.JobTitles);
        Assert.Equal(["Software"], s.Industries);
        Assert.Equal(["US"], s.Countries);
        Assert.Equal(51, s.SizeMin);
        Assert.Equal(200, s.SizeMax);
    }

    [Fact]
    public void ManyLeads_TakeTop3ByFrequency_CaseInsensitive_TiesByFirstAppearance()
    {
        var leads = new[]
        {
            L("CTO", null, "US", null), L("cto", null, "US", null), L("CEO", null, "DE", null), L("CEO", null, "DE", null),
            L("COO", null, "FR", null), L("CFO", null, "IN", null), L("CIO", null, "UK", null)
        };

        var s = TargetProfileSuggester.Suggest(leads);

        Assert.Equal("Profile from 7 leads", s.Name);
        Assert.Equal(["CTO", "CEO", "COO"], s.JobTitles);       // CTO/CEO have 2 each (CTO first); COO is first of the 1-count ties
        Assert.Equal(["US", "DE", "FR"], s.Countries);
    }

    [Fact]
    public void Sizes_SpanMinToMax_AndUnparseableAreIgnored()
    {
        var s = TargetProfileSuggester.Suggest([L("A", null, null, "11-50"), L("B", null, null, "500"), L("C", null, null, "lots")]);

        Assert.Equal(11, s.SizeMin);
        Assert.Equal(500, s.SizeMax);
    }

    [Fact]
    public void OpenEndedSize_LeavesMaxOpen()
    {
        var s = TargetProfileSuggester.Suggest([L("A", null, null, "11-50"), L("B", null, null, "1000+")]);

        Assert.Equal(11, s.SizeMin);
        Assert.Null(s.SizeMax);
    }

    [Fact]
    public void OnlyReplied_FiltersLeads_AndThrowsWhenNoneReplied()
    {
        var leads = new[] { L("CTO", null, null, null, LeadStatus.Replied), L("CEO", null, null, null) };

        var s = TargetProfileSuggester.Suggest(leads, onlyReplied: true);

        Assert.Equal(["CTO"], s.JobTitles);
        Assert.Throws<InvalidOperationException>(() => TargetProfileSuggester.Suggest([L("CEO", null, null, null)], onlyReplied: true));
    }
}

public class CompanySizeParserTests
{
    [Theory]
    [InlineData("51-200", 51, 200)]
    [InlineData("1000+", 1000, null)]
    [InlineData("500", 500, 500)]
    [InlineData(" 1,000-5,000 ", 1000, 5000)]
    public void Parses(string raw, int min, int? max)
        => Assert.Equal(new CompanySizeRange(min, max), CompanySizeParser.TryParse(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("junk")]
    [InlineData("200-51")]
    [InlineData("-5")]
    public void Junk_ReturnsNull(string? raw) => Assert.Null(CompanySizeParser.TryParse(raw));
}
