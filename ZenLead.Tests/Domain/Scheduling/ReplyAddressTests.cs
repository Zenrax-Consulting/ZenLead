using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Domain.Scheduling;

public class ReplyAddressTests
{
    private const string Domain = "reply.leads.zenraxconsulting.com";

    [Fact]
    public void Round_trips_an_enrollment_id()
    {
        var id = Guid.NewGuid();
        var address = ReplyAddress.Encode(id, Domain);

        Assert.Equal($"r-{id:N}@{Domain}", address);
        Assert.True(ReplyAddress.TryDecode(address, Domain, out var decoded));
        Assert.Equal(id, decoded);
        Assert.True(ReplyAddress.TryDecode(address.ToUpperInvariant(), Domain, out var upper));   // mail systems may change case
        Assert.Equal(id, upper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("r-0123@reply.leads.zenraxconsulting.com")]
    [InlineData("x-0123456789abcdef0123456789abcdef@reply.leads.zenraxconsulting.com")]
    [InlineData("r-0123456789abcdef0123456789abcdef@evil.test")]
    [InlineData("r-0123456789abcdef0123456789abcdef@sub.reply.leads.zenraxconsulting.com")]
    [InlineData("@reply.leads.zenraxconsulting.com")]
    [InlineData("r-0123456789abcdef0123456789abcdef")]
    public void Rejects_anything_else(string? address)
    {
        Assert.False(ReplyAddress.TryDecode(address, Domain, out var id));
        Assert.Equal(Guid.Empty, id);
    }
}
