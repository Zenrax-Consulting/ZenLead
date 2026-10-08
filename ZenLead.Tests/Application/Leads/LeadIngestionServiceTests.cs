using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Leads;

public class LeadIngestionServiceTests
{
    private static readonly IngestionSource Src = new(LeadSource.Csv, null);
    private readonly FakeLeadIngestionStore _store = new();
    private readonly Guid _ws = Guid.NewGuid();

    private LeadIngestionService Sut => new(_store);

    private static CandidateLead Row(string? email, string? name = "Jane", string? company = null, string? domain = null,
        string? industry = null, string? country = null, EmailVerificationStatus v = EmailVerificationStatus.Unverified)
        => new(name, email, "CTO", company, domain, industry, country, null, v);

    private Lead Seed(string email, LeadStatus status = LeadStatus.New, Guid? workspace = null)
    {
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspace ?? _ws, Name = "x", Email = email, Status = status, CreatedAt = DateTime.UtcNow };
        _store.Leads.Add(lead);
        return lead;
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Name <a@b.com>")]
    [InlineData("a@localhost")]
    public async Task InvalidEmails_AreRejected_AndNothingIsInserted(string email)
    {
        var summary = await Sut.IngestAsync(_ws, [Row(email)], Src);

        Assert.Equal(IngestionOutcome.Invalid, summary.Rows[0].Outcome);
        Assert.NotNull(summary.Rows[0].Reason);
        Assert.Empty(_store.Leads);
    }

    [Fact]
    public async Task OverlongEmail_IsInvalid()
    {
        var summary = await Sut.IngestAsync(_ws, [Row(new string('a', 260) + "@x.com")], Src);
        Assert.Equal(IngestionOutcome.Invalid, summary.Rows[0].Outcome);
    }

    [Fact]
    public async Task SameEmailDifferentCaseInOneBatch_SecondIsDuplicate()
    {
        var summary = await Sut.IngestAsync(_ws, [Row("A@X.com"), Row("a@x.com")], Src);

        Assert.Equal(IngestionOutcome.Imported, summary.Rows[0].Outcome);
        Assert.Equal(IngestionOutcome.Duplicate, summary.Rows[1].Outcome);
        Assert.Equal("a@x.com", Assert.Single(_store.Leads).Email);
    }

    [Fact]
    public async Task ExistingEmail_IsDuplicateWithExistingId_ButOtherWorkspaceImports()
    {
        var existing = Seed("jane@acme.com");

        var same = await Sut.IngestAsync(_ws, [Row("jane@acme.com")], Src);
        var other = await Sut.IngestAsync(Guid.NewGuid(), [Row("jane@acme.com")], Src);

        Assert.Equal(IngestionOutcome.Duplicate, same.Rows[0].Outcome);
        Assert.Equal(existing.Id, same.Rows[0].LeadId);
        Assert.Equal(IngestionOutcome.Imported, other.Rows[0].Outcome);
    }

    [Theory]
    [InlineData(LeadStatus.Unsubscribed)]
    [InlineData(LeadStatus.Bounced)]
    public async Task SuppressedExistingLead_IsReportedSuppressed(LeadStatus status)
    {
        Seed("jane@acme.com", status);
        var summary = await Sut.IngestAsync(_ws, [Row("jane@acme.com")], Src);
        Assert.Equal(IngestionOutcome.Suppressed, summary.Rows[0].Outcome);
        Assert.Equal(1, summary.Suppressed);
    }

    [Fact]
    public async Task SoftDeletedExistingLead_IsDuplicate()
    {
        var lead = Seed("jane@acme.com", LeadStatus.Contacted);
        _store.SoftDeleted.Add(lead.Id);

        var summary = await Sut.IngestAsync(_ws, [Row("jane@acme.com")], Src);

        Assert.Equal(IngestionOutcome.Duplicate, summary.Rows[0].Outcome);
        Assert.Equal("Previously deleted lead", summary.Rows[0].Reason);
    }

    [Fact]
    public async Task ProviderInvalidVerification_IsInvalid()
    {
        var summary = await Sut.IngestAsync(_ws, [Row("jane@acme.com", v: EmailVerificationStatus.Invalid)], Src);
        Assert.Equal(IngestionOutcome.Invalid, summary.Rows[0].Outcome);
    }

    [Fact]
    public async Task Companies_AreDedupedByDomain_ThenByNameAndFillOnlyBlanks()
    {
        await Sut.IngestAsync(_ws, [
            Row("a@acme.com", company: "Acme", domain: "https://www.Acme.com/"),
            Row("b@acme.com", company: "ACME Inc", domain: "acme.com", industry: "SaaS"),
            Row("c@nodomain.io", company: "NoDomain"),
            Row("d@nodomain.io", company: "nodomain", country: "DE")], Src);

        Assert.Equal(2, _store.Companies.Count);
        var acme = _store.Companies.Values.Single(c => c.Domain == "acme.com");
        Assert.Equal("SaaS", acme.Industry);
        Assert.Equal("Acme", acme.Name);
        Assert.Equal("DE", _store.Companies.Values.Single(c => c.Domain is null).Country);
        Assert.Equal(_store.Leads[0].CompanyId, _store.Leads[1].CompanyId);
        Assert.Equal(_store.Leads[2].CompanyId, _store.Leads[3].CompanyId);
    }

    [Fact]
    public async Task BlankName_FallsBackToEmailLocalPart_AndLongValuesAreTruncated()
    {
        await Sut.IngestAsync(_ws, [
            Row("jane.doe@acme.com", name: " "),
            new CandidateLead(new string('n', 300), "long@acme.com", new string('t', 300), null, null, null, null)], Src);

        Assert.Equal("jane.doe", _store.Leads[0].Name);
        Assert.Equal(200, _store.Leads[1].Name.Length);
        Assert.Equal(200, _store.Leads[1].Title!.Length);
    }

    [Fact]
    public async Task RaceLoser_IsReportedDuplicate()
    {
        _store.RaceLosers.Add("jane@acme.com");
        var summary = await Sut.IngestAsync(_ws, [Row("jane@acme.com")], Src);

        Assert.Equal(IngestionOutcome.Duplicate, summary.Rows[0].Outcome);
        Assert.StartsWith("Created concurrently", summary.Rows[0].Reason);
    }

    [Fact]
    public async Task MixedBatch_ResultsMapBackToInputIndex()
    {
        Seed("dup@acme.com");
        Seed("gone@acme.com", LeadStatus.Unsubscribed);

        var summary = await Sut.IngestAsync(_ws, [
            Row("bad"), Row("dup@acme.com"), Row("new@acme.com"), Row("gone@acme.com")], Src);

        Assert.Equal([IngestionOutcome.Invalid, IngestionOutcome.Duplicate, IngestionOutcome.Imported, IngestionOutcome.Suppressed],
            summary.Rows.Select(r => r.Outcome));
        Assert.Equal([0, 1, 2, 3], summary.Rows.Select(r => r.Index));
        Assert.Equal((1, 1, 1, 1), (summary.Imported, summary.Duplicates, summary.Suppressed, summary.Invalid));
    }

    [Fact]
    public async Task ImportedLead_CarriesSourceProvenance()
    {
        var run = Guid.NewGuid();
        await Sut.IngestAsync(_ws, [Row("jane@acme.com")], new IngestionSource(LeadSource.Discovery, run));

        var lead = Assert.Single(_store.Leads);
        Assert.Equal(LeadSource.Discovery, lead.Source);
        Assert.Equal(run, lead.SourceRunId);
        Assert.Equal(LeadStatus.New, lead.Status);
    }
}
