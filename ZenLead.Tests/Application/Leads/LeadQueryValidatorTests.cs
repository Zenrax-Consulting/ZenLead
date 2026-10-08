using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Validation.Leads;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Leads;

public class LeadQueryValidatorTests
{
    private readonly LeadQueryValidator _validator = new();

    [Fact]
    public void Defaults_AreValid() => Assert.True(_validator.Validate(new LeadQuery()).IsValid);

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-1, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void PageAndPageSize_OutOfBounds_AreInvalid(int page, int pageSize)
        => Assert.False(_validator.Validate(new LeadQuery(Page: page, PageSize: pageSize)).IsValid);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2000, 100)]
    public void PageAndPageSize_AtLimits_AreValid(int page, int pageSize)
        => Assert.True(_validator.Validate(new LeadQuery(Page: page, PageSize: pageSize)).IsValid);

    [Fact]
    public void Q_Over100Chars_IsInvalid()
        => Assert.False(_validator.Validate(new LeadQuery(Q: new string('a', 101))).IsValid);

    [Theory]
    [InlineData("name")]
    [InlineData("-name")]
    [InlineData("email")]
    [InlineData("status")]
    [InlineData("-company")]
    [InlineData("createdAt")]
    [InlineData("-createdAt")]
    public void Sort_AllowListed_IsValid(string sort)
        => Assert.True(_validator.Validate(new LeadQuery(Sort: sort)).IsValid);

    [Theory]
    [InlineData("password")]
    [InlineData("--name")]
    [InlineData("name; drop table")]
    [InlineData("")]
    public void Sort_NotAllowListed_IsInvalid(string sort)
        => Assert.False(_validator.Validate(new LeadQuery(Sort: sort)).IsValid);

    [Fact]
    public void UndefinedEnums_AreInvalid()
    {
        Assert.False(_validator.Validate(new LeadQuery(Status: (LeadStatus)99)).IsValid);
        Assert.False(_validator.Validate(new LeadQuery(Source: (LeadSource)99)).IsValid);
    }
}
