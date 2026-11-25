using ZenLead.Application.Csv;

namespace ZenLead.Tests.Application.Csv;

public class CsvRowMapperTests
{
    private static readonly string[] Headers = ["Email", "First", "Last", "Full", "Title", "Company"];
    private static ColumnMapping Mapping(string? name = null, string? first = "First", string? last = "Last", string email = "Email")
        => new(name, first, last, email, "Title", "Company", null, null, null, null);

    private static Dictionary<string, string> Fields(params (string, string)[] f)
        => new(f.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FirstAndLastName_AreJoinedIntoName()
    {
        var c = CsvRowMapper.Map(Fields(("Email", "a@x.com"), ("First", " Ann "), ("Last", "Lee")), Mapping());

        Assert.Equal("Ann Lee", c.Name);
        Assert.Equal("a@x.com", c.Email);
    }

    [Fact]
    public void OnlyOneOfFirstAndLast_IsUsedWithoutStraySpaces()
    {
        Assert.Equal("Ann", CsvRowMapper.Map(Fields(("Email", "a@x.com"), ("First", "Ann"), ("Last", "")), Mapping()).Name);
        Assert.Equal("Lee", CsvRowMapper.Map(Fields(("Email", "a@x.com"), ("First", ""), ("Last", "Lee")), Mapping()).Name);
    }

    [Fact]
    public void FullNameColumn_TakesPrecedence()
    {
        var c = CsvRowMapper.Map(Fields(("Email", "a@x.com"), ("Full", "Ann Lee")), Mapping(name: "Full", first: null, last: null));

        Assert.Equal("Ann Lee", c.Name);
    }

    [Fact]
    public void BlankCells_BecomeNull()
    {
        var c = CsvRowMapper.Map(Fields(("Email", "  "), ("Title", ""), ("Company", "   ")), Mapping());

        Assert.Null(c.Email);
        Assert.Null(c.Title);
        Assert.Null(c.CompanyName);
        Assert.Equal("", c.Name);
    }

    [Fact]
    public void AMappedHeaderMissingFromTheRow_IsNull_NotAnException()
    {
        var c = CsvRowMapper.Map(Fields(("Email", "a@x.com")), Mapping());

        Assert.Null(c.Title);
    }

    [Fact]
    public void Validate_AcceptsAGoodMapping()
        => Assert.Empty(CsvRowMapper.Validate(Mapping(), Headers));

    [Fact]
    public void Validate_RequiresEmail()
    {
        var problems = CsvRowMapper.Validate(Mapping(email: ""), Headers);

        Assert.Contains(problems, p => p.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsHeadersThatAreNotInTheFile()
    {
        var problems = CsvRowMapper.Validate(Mapping(email: "Mail"), Headers);

        Assert.Contains(problems, p => p.Contains("\"Mail\""));
    }

    [Fact]
    public void Validate_MatchesHeadersCaseInsensitively()
        => Assert.Empty(CsvRowMapper.Validate(Mapping(email: "email"), Headers));

    [Fact]
    public void Validate_RejectsNameTogetherWithFirstName()
    {
        var problems = CsvRowMapper.Validate(Mapping(name: "Full"), Headers);

        Assert.Contains(problems, p => p.Contains("Name or First name"));
    }

    [Fact]
    public void Validate_RejectsOneHeaderMappedToTwoFields()
    {
        var problems = CsvRowMapper.Validate(Mapping() with { Title = "Company" }, Headers);

        Assert.Contains(problems, p => p.Contains("\"Company\"") && p.Contains("more than one"));
    }

    [Fact]
    public void Normalize_UsesTheFilesSpelling_AndTrims()
    {
        var m = CsvRowMapper.Normalize(Mapping(email: " EMAIL ", first: "first"), Headers);

        Assert.Equal("Email", m.Email);
        Assert.Equal("First", m.FirstName);
    }
}
