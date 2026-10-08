using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.LeadSources;

namespace ZenLead.Tests.Infrastructure.LeadSources;

public class LeadSourceTests
{
    private static LeadSearchCriteria Countries(params string[] c) => new([], [], c, null, null, []);

    [Fact]
    public async Task Fake_FiltersByCountry_AndPagesWithCursor()
    {
        var source = new FakeLeadSource();

        var first = await source.SearchAsync(Countries("US"), null, 10, default);
        var second = await source.SearchAsync(Countries("US"), first.NextCursor, 10, default);

        Assert.Equal(10, first.Leads.Count);
        Assert.All(first.Leads.Concat(second.Leads), l => Assert.Equal("US", l.Country));
        Assert.Empty(first.Leads.Select(l => l.ProviderId).Intersect(second.Leads.Select(l => l.ProviderId)));
        Assert.Equal(10, first.CreditsUsed);
    }

    [Fact]
    public async Task Fake_DatasetCoversEdgeCases()
    {
        var all = new List<DiscoveredLead>();
        string? cursor = null;
        do
        {
            var page = await new FakeLeadSource().SearchAsync(LeadSearchCriteria.Empty, cursor, 100, default);
            all.AddRange(page.Leads);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(200, all.Count);
        Assert.Contains(all, l => l.Email is null);
        Assert.All(new[] { EmailVerificationStatus.Verified, EmailVerificationStatus.Risky, EmailVerificationStatus.Invalid, EmailVerificationStatus.Unverified },
            v => Assert.Contains(all, l => l.Verification == v));
        Assert.Contains(all, l => l.Email is not null && l.Email != l.Email.ToLowerInvariant());
    }

    [Fact]
    public async Task Fake_QueuedFailure_IsThrownOnNextCall()
    {
        var source = new FakeLeadSource();
        source.FailuresToThrow.Enqueue(new LeadSourceException(LeadSourceFailureKind.RateLimited, "x"));

        await Assert.ThrowsAsync<LeadSourceException>(() => source.SearchAsync(LeadSearchCriteria.Empty, null, 5, default));
        Assert.NotEmpty((await source.SearchAsync(LeadSearchCriteria.Empty, null, 5, default)).Leads);
    }

    private class StubSource(string name) : ILeadSource
    {
        public string Name => name;
        public Task<LeadSearchPage> SearchAsync(LeadSearchCriteria c, string? cursor, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> GetRemainingCreditsAsync(CancellationToken ct) => Task.FromResult<int?>(null);
    }

    [Fact]
    public void Registry_SwapsVendorsByNameAlone()
    {
        var registry = new LeadSourceRegistry()
            .Register("VendorA", _ => new StubSource("A"))
            .Register("VendorB", _ => new StubSource("B"));
        var sp = new ServiceCollection().BuildServiceProvider();

        Assert.Equal("A", registry.Resolve("VendorA", sp).Name);
        Assert.Equal("B", registry.Resolve("vendorb", sp).Name);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("Nope", sp));
        Assert.True(LeadSourceRegistry.Default().IsKnown("Pdl"));
    }

    private class StubHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static PdlLeadSource Pdl(StubHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.peopledatalabs.com/") });

    [Fact]
    public async Task Pdl_MapsResults_AndChargesPerRecord()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"status":200,"total":2,"scroll_token":"tok2","data":[
              {"id":"1","first_name":"Ann","last_name":"Lee","work_email":"ann@acme.com","email_status":"verified","job_title":"CTO",
               "job_company_name":"Acme","job_company_website":"acme.com","job_company_industry":"software","job_company_size":"51-200","location_country":"united states"},
              {"id":"2","first_name":"Bob","last_name":null,"work_email":null,"job_title":null}]}
            """);

        var page = await Pdl(handler).SearchAsync(Countries("US"), "tok1", 25, default);

        Assert.Equal(2, page.CreditsUsed);
        Assert.Equal("tok2", page.NextCursor);
        Assert.Equal(EmailVerificationStatus.Verified, page.Leads[0].Verification);
        Assert.Equal("ann@acme.com", page.Leads[0].Email);
        Assert.Equal(EmailVerificationStatus.Unverified, page.Leads[1].Verification);
        Assert.Contains("\"scroll_token\":\"tok1\"", handler.RequestBody);
        Assert.Contains("\"location_country\"", handler.RequestBody);
        Assert.Equal("/v5/person/search", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LeadSourceFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, LeadSourceFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired, LeadSourceFailureKind.OutOfCredits)]
    [InlineData(HttpStatusCode.TooManyRequests, LeadSourceFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, LeadSourceFailureKind.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, LeadSourceFailureKind.Unavailable)]
    public async Task Pdl_MapsHttpFailures(HttpStatusCode status, LeadSourceFailureKind expected)
    {
        var ex = await Assert.ThrowsAsync<LeadSourceException>(() => Pdl(new StubHandler(status)).SearchAsync(Countries("US"), null, 5, default));
        Assert.Equal(expected, ex.Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task Pdl_UnparseableOrMissingData_IsInvalidResponse(string body)
    {
        var ex = await Assert.ThrowsAsync<LeadSourceException>(() => Pdl(new StubHandler(HttpStatusCode.OK, body)).SearchAsync(Countries("US"), null, 5, default));
        Assert.Equal(LeadSourceFailureKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Pdl_NoMatches404_IsAnEmptyPage()
    {
        var page = await Pdl(new StubHandler(HttpStatusCode.NotFound)).SearchAsync(Countries("US"), null, 5, default);

        Assert.Empty(page.Leads);
        Assert.Null(page.NextCursor);
        Assert.Equal(0, page.CreditsUsed);
    }

    [Theory]
    [InlineData("verified", EmailVerificationStatus.Verified)]
    [InlineData("guessed", EmailVerificationStatus.Risky)]
    [InlineData("likely", EmailVerificationStatus.Risky)]
    [InlineData("invalid", EmailVerificationStatus.Invalid)]
    [InlineData(null, EmailVerificationStatus.Unverified)]
    [InlineData("whatever", EmailVerificationStatus.Unverified)]
    public void Pdl_VerificationMapping(string? flag, EmailVerificationStatus expected)
        => Assert.Equal(expected, PdlLeadSource.MapVerification(new PdlLeadSource.PdlPerson { EmailStatus = flag }));
}
