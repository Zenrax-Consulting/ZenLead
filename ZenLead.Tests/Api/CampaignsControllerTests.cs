using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

public class CampaignsControllerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/campaigns";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CampaignRequest NewCampaign(string? timeZone = null, string start = "09:00", string end = "17:00")
        => new("Spring outreach", null, 30, start, end, SendDays.Weekdays, timeZone);

    private static StepRequest NewStep(string body = "Hi {{firstName|there}}, about {{company}}") => new(0, "Hello {{firstName|there}}", body, false);

    private async Task<(HttpClient Client, Guid WorkspaceId)> SignInAsync()
    {
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync($"Ws {Guid.NewGuid():N}", $"{Guid.NewGuid():N}@campaigns.test");
        return (factory.ClientFor(token), workspaceId);
    }

    private async Task<Lead> AddLeadAsync(Guid workspaceId, string name, string? company)
        => await factory.WithDbAsync(async db =>
        {
            Company? co = company is null ? null : new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = company, CreatedAt = DateTime.UtcNow };
            if (co is not null) db.Companies.Add(co);
            var lead = new Lead
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name, Email = $"{Guid.NewGuid():N}@lead.test",
                Status = LeadStatus.New, EmailVerificationStatus = EmailVerificationStatus.Verified, CompanyId = co?.Id, CreatedAt = DateTime.UtcNow
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            return lead;
        });

    private static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<CampaignResponse> CreateCampaignAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Url, NewCampaign(), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<CampaignResponse>(response);
    }

    [Fact]
    public async Task Builder_Flow_Create_AddSteps_Preview_Enroll_List()
    {
        var (client, ws) = await SignInAsync();
        var created = await CreateCampaignAsync(client);
        Assert.Equal(CampaignStatus.Draft, created.Status);
        Assert.Equal("UTC", created.EffectiveTimeZone);
        Assert.Equal("09:00", created.WindowStart);

        var step = await Read<StepResponse>(await client.PostAsJsonAsync($"{Url}/{created.Id}/steps", NewStep(), Json));
        Assert.Equal(1, step.Order);
        var withCompany = await AddLeadAsync(ws, "Jane Doe", "Acme");
        var noCompany = await AddLeadAsync(ws, "John Roe", null);

        var preview = await Read<PreviewResponse>(await client.PostAsJsonAsync($"{Url}/{created.Id}/preview", new PreviewRequest(noCompany.Id, step.Id), Json));
        Assert.Equal("Hello John", preview.Subject);
        Assert.Equal(["company"], preview.MissingTokens);
        Assert.Contains("Unsubscribe:", preview.Body);

        var enroll = await client.PostAsJsonAsync($"{Url}/{created.Id}/enroll", new EnrollRequest([withCompany.Id, noCompany.Id], null), Json);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var result = await Read<EnrollResult>(enroll);
        Assert.Equal(1, result.Enrolled);
        Assert.Equal(1, result.SkippedMissingTokens);

        var rows = await client.GetFromJsonAsync<PagedResult<EnrollmentRow>>($"{Url}/{created.Id}/enrollments", Json);
        var row = Assert.Single(rows!.Items);
        Assert.Equal("Jane Doe", row.LeadName);
        Assert.Null(row.NextSendAt);

        var list = await client.GetFromJsonAsync<List<CampaignListItem>>(Url, Json);
        var item = Assert.Single(list!);
        Assert.Equal(1, item.StepCount);
        Assert.Equal(1, item.Counts.Total);
    }

    [Fact]
    public async Task Activation_Problems_Come_Back_As_409_With_The_List()
    {
        var (client, _) = await SignInAsync();
        var created = await CreateCampaignAsync(client);           // no steps

        var response = await client.PostAsync($"{Url}/{created.Id}/activate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = doc.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Equal(["no_steps"], codes);
        Assert.Equal("activation_blocked", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Another_Workspace_Gets_404_On_Every_Verb()
    {
        var (owner, ws) = await SignInAsync();
        var campaign = await CreateCampaignAsync(owner);
        var step = await Read<StepResponse>(await owner.PostAsJsonAsync($"{Url}/{campaign.Id}/steps", NewStep(), Json));
        var lead = await AddLeadAsync(ws, "Jane Doe", "Acme");
        var (intruder, _) = await SignInAsync();
        var id = campaign.Id;

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["GET"] = await intruder.GetAsync($"{Url}/{id}"),
            ["PUT"] = await intruder.PutAsJsonAsync($"{Url}/{id}", NewCampaign(), Json),
            ["POST steps"] = await intruder.PostAsJsonAsync($"{Url}/{id}/steps", NewStep(), Json),
            ["PUT step"] = await intruder.PutAsJsonAsync($"{Url}/{id}/steps/{step.Id}", NewStep(), Json),
            ["DELETE step"] = await intruder.DeleteAsync($"{Url}/{id}/steps/{step.Id}"),
            ["reorder"] = await intruder.PostAsJsonAsync($"{Url}/{id}/steps/reorder", new ReorderStepsRequest([step.Id]), Json),
            ["enroll"] = await intruder.PostAsJsonAsync($"{Url}/{id}/enroll", new EnrollRequest([lead.Id], null), Json),
            ["enrollments"] = await intruder.GetAsync($"{Url}/{id}/enrollments"),
            ["activate"] = await intruder.PostAsync($"{Url}/{id}/activate", null),
            ["activation-preview"] = await intruder.GetAsync($"{Url}/{id}/activation-preview"),
            ["pause"] = await intruder.PostAsync($"{Url}/{id}/pause", null),
            ["preview"] = await intruder.PostAsJsonAsync($"{Url}/{id}/preview", new PreviewRequest(lead.Id, step.Id), Json)
        };

        Assert.All(responses, r => Assert.True(r.Value.StatusCode == HttpStatusCode.NotFound, $"{r.Key} returned {(int)r.Value.StatusCode}"));
        Assert.Empty((await intruder.GetFromJsonAsync<List<CampaignListItem>>(Url, Json))!);
        Assert.Single((await owner.GetFromJsonAsync<CampaignResponse>($"{Url}/{id}", Json))!.Steps);
    }

    [Fact]
    public async Task Unknown_Tokens_Are_A_Validation_Error()
    {
        var (client, _) = await SignInAsync();
        var campaign = await CreateCampaignAsync(client);

        var response = await client.PostAsJsonAsync($"{Url}/{campaign.Id}/steps", NewStep("Hi {{favouriteColour}}"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("favouriteColour", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("17:00", "09:00", null)]
    [InlineData("9am", "17:00", null)]
    [InlineData("09:00", "17:00", "Mars/Olympus_Mons")]
    public async Task Bad_Window_Or_Zone_Is_A_Validation_Error(string start, string end, string? zone)
    {
        var (client, _) = await SignInAsync();
        var response = await client.PostAsJsonAsync(Url, NewCampaign(zone, start, end), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Campaign_Zone_Override_Is_Reported()
    {
        var (client, _) = await SignInAsync();
        var created = await Read<CampaignResponse>(await client.PostAsJsonAsync(Url, NewCampaign("Asia/Dubai"), Json));
        Assert.Equal("Asia/Dubai", created.EffectiveTimeZone);
        Assert.Equal("Asia/Dubai", created.TimeZoneId);
    }

    [Fact]
    public async Task Enroll_Needs_Exactly_One_Of_LeadIds_Or_Filter()
    {
        var (client, _) = await SignInAsync();
        var campaign = await CreateCampaignAsync(client);

        var both = await client.PostAsJsonAsync($"{Url}/{campaign.Id}/enroll", new EnrollRequest([Guid.NewGuid()], new LeadQuery()), Json);
        var neither = await client.PostAsJsonAsync($"{Url}/{campaign.Id}/enroll", new EnrollRequest(null, null), Json);

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
    }

    [Fact]
    public async Task Sender_Reports_The_Configured_Identity_Without_Secrets()
    {
        var (client, _) = await SignInAsync();

        var response = await client.GetAsync($"{Url}/sender");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var sender = JsonSerializer.Deserialize<SenderStatus>(body, Json)!;
        Assert.False(string.IsNullOrEmpty(sender.FromAddress));
        Assert.False(string.IsNullOrEmpty(sender.InboundDomain));
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(null).GetAsync($"{Url}/sender")).StatusCode);
    }

    [Fact]
    public async Task Activation_Preview_And_New_Response_Fields_Round_Trip()
    {
        var (client, _) = await SignInAsync();
        var created = await CreateCampaignAsync(client);
        Assert.Equal(0, created.StepsStarted);
        Assert.Equal("UTC", created.WorkspaceTimeZone);

        var preview = await client.GetFromJsonAsync<ActivationPreview>($"{Url}/{created.Id}/activation-preview", Json);
        Assert.Equal(new ActivationPreview(0, 0, 0, 0m, preview!.AiBudgetRemainingUsd), preview);

        var item = Assert.Single((await client.GetFromJsonAsync<List<CampaignListItem>>(Url, Json))!);
        Assert.Equal(0, item.Sent);

        var activate = await client.PostAsJsonAsync($"{Url}/{created.Id}/steps", new StepRequest(2, "Hi", "Body", false), Json);
        Assert.Equal(HttpStatusCode.Created, activate.StatusCode);
        using var doc = JsonDocument.Parse(await (await client.PostAsync($"{Url}/{created.Id}/activate", null)).Content.ReadAsStringAsync());
        var firstDelay = doc.RootElement.GetProperty("problems").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "first_delay");
        Assert.Equal(1, firstDelay.GetProperty("stepOrder").GetInt32());
    }

    [Fact]
    public async Task Without_A_Token_Is_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(null).GetAsync(Url)).StatusCode);
}
