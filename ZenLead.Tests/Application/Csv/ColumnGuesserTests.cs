using ZenLead.Application.Csv;

namespace ZenLead.Tests.Application.Csv;

public class ColumnGuesserTests
{
    [Theory]
    [InlineData("email")] [InlineData("E-mail")] [InlineData("Email Address")] [InlineData("WORK EMAIL")]
    public void Email_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).Email);

    [Theory]
    [InlineData("first name")] [InlineData("FirstName")] [InlineData("Given Name")]
    public void FirstName_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).FirstName);

    [Theory]
    [InlineData("last name")] [InlineData("LastName")] [InlineData("Surname")] [InlineData("Family Name")]
    public void LastName_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).LastName);

    [Theory]
    [InlineData("name")] [InlineData("Full Name")]
    public void Name_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).Name);

    [Theory]
    [InlineData("title")] [InlineData("Job Title")] [InlineData("Position")]
    public void Title_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).Title);

    [Theory]
    [InlineData("company")] [InlineData("Company Name")] [InlineData("Organization")] [InlineData("Organisation")]
    public void Company_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).CompanyName);

    [Theory]
    [InlineData("domain")] [InlineData("Website")] [InlineData("Company Domain")] [InlineData("URL")]
    public void Domain_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).CompanyDomain);

    [Fact]
    public void Industry_AndCountry_AreGuessed()
    {
        var g = ColumnGuesser.Guess(["Industry", "Country"]);

        Assert.Equal("Industry", g.Industry);
        Assert.Equal("Country", g.Country);
    }

    [Theory]
    [InlineData("size")] [InlineData("Employees")] [InlineData("Company Size")]
    public void Size_Synonyms(string header) => Assert.Equal(header, ColumnGuesser.Guess([header]).CompanySize);

    [Fact]
    public void FullExport_IsGuessedColumnByColumn()
    {
        var g = ColumnGuesser.Guess(["First Name", "Last Name", "E-mail", "Job Title", "Company", "Website", "Industry", "Country", "Employees", "Notes"]);

        Assert.Equal("E-mail", g.Email);
        Assert.Equal("First Name", g.FirstName);
        Assert.Equal("Last Name", g.LastName);
        Assert.Null(g.Name);
        Assert.Equal("Job Title", g.Title);
        Assert.Equal("Company", g.CompanyName);
        Assert.Equal("Website", g.CompanyDomain);
        Assert.Equal("Employees", g.CompanySize);
    }

    [Fact]
    public void CompanyName_IsNotMistakenForAPersonsName()
        => Assert.Null(ColumnGuesser.Guess(["Company Name", "Email"]).Name);

    [Fact]
    public void WhenFirstAndLastExist_NameIsLeftUnmapped_SoTheMappingValidates()
    {
        var g = ColumnGuesser.Guess(["Name", "First Name", "Last Name", "Email"]);

        Assert.Null(g.Name);
        Assert.Equal("First Name", g.FirstName);
    }

    [Fact]
    public void ExactMatchBeatsLaterSynonym_AndAHeaderIsNeverUsedTwice()
    {
        var g = ColumnGuesser.Guess(["Website", "Domain", "Company Domain"]);

        Assert.Equal("Company Domain", g.CompanyDomain);
    }

    [Fact]
    public void NoEmailHeader_LeavesEmailNull()
        => Assert.Null(ColumnGuesser.Guess(["Name", "Phone"]).Email);
}
