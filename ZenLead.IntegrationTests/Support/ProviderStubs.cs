using System.Net;
using System.Text.Json;
using WireMock.Logging;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// HTTP-level stand-ins for the three paid providers (T03), each a WireMock server on a free localhost port. The app's real clients
/// (SendGrid SDK, Semantic Kernel's OpenAI connector, <c>PdlLeadSource</c>) talk to them, so serialisation and status mapping are
/// exercised for real. Every request is recorded, so tests assert on the outbound payload. <see cref="Reset"/> restores the defaults.
/// </summary>
public sealed class ProviderStubs : IDisposable
{
    public SendGridStub SendGrid { get; } = new();
    public OpenAiStub OpenAi { get; } = new();
    public PdlStub Pdl { get; } = new();

    /// <summary>Clears recorded requests and custom mappings, then re-adds each stub's happy-path default.</summary>
    public void Reset()
    {
        SendGrid.Reset();
        OpenAi.Reset();
        Pdl.Reset();
    }

    public void Dispose()
    {
        SendGrid.Dispose();
        OpenAi.Dispose();
        Pdl.Dispose();
    }
}

/// <summary>Common plumbing: a server, its recorded requests and JSON access to their bodies.</summary>
public abstract class StubServer : IDisposable
{
    protected StubServer()
    {
        Server = WireMockServer.Start();
        // the first request to a fresh server is slow (seconds, on a cold machine); pay that here, not inside a client timeout
        using (var warmUp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            warmUp.GetAsync($"{Server.Url}/__warm-up").GetAwaiter().GetResult().Dispose();
        Reset();
    }

    public WireMockServer Server { get; }
    public string Url => Server.Url!;

    /// <summary>Requests received on <paramref name="path"/> (all paths when null), oldest first.</summary>
    public IReadOnlyList<ILogEntry> Requests(string? path = null)
        => Server.LogEntries.Where(e => path is null || (e.RequestMessage?.Path ?? "").EndsWith(path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.RequestMessage!.DateTime).ToList();

    /// <summary>
    /// Waits until at least <paramref name="count"/> requests have arrived on <paramref name="path"/>. WireMock logs a request only
    /// once its (possibly delayed) response is written, so use this after a client gave up on a slow stub.
    /// </summary>
    public async Task<IReadOnlyList<ILogEntry>> WaitForRequestsAsync(string? path, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (Requests(path).Count < count && DateTime.UtcNow < deadline) await Task.Delay(100);
        return Requests(path);
    }

    public static JsonElement BodyJson(ILogEntry entry) => JsonDocument.Parse(entry.RequestMessage?.Body ?? "{}").RootElement.Clone();

    public void Reset()
    {
        Server.Reset();
        AddDefaults();
    }

    protected abstract void AddDefaults();

    public void Dispose() => Server.Stop();
}

/// <summary>SendGrid v3 Mail Send. Default: 202 Accepted with an <c>X-Message-Id</c> header.</summary>
public sealed class SendGridStub : StubServer
{
    public const string MailSendPath = "/v3/mail/send";
    public const string MessageId = "wiremock-sg-message-id";

    /// <summary>The HttpClient timeout the integration factory gives the SendGrid client, so <see cref="RespondSlowly"/> stays fast.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(4);

    public IReadOnlyList<ILogEntry> MailSends => Requests(MailSendPath);

    protected override void AddDefaults()
        => Server.Given(Request.Create().WithPath(MailSendPath).UsingPost())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(202).WithHeader("X-Message-Id", MessageId));

    /// <summary>Every send gets <paramref name="status"/> with a SendGrid-shaped error body.</summary>
    public void RespondWith(HttpStatusCode status, string message = "stubbed failure")
        => Server.Given(Request.Create().WithPath(MailSendPath).UsingPost())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(status)
                .WithBodyAsJson(new { errors = new[] { new { message, field = (string?)null, help = (string?)null } } }));

    /// <summary>Answers after the client has given up (longer than <see cref="ClientTimeout"/>).</summary>
    public void RespondSlowly()
        => Server.Given(Request.Create().WithPath(MailSendPath).UsingPost())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(202).WithHeader("X-Message-Id", MessageId).WithDelay(ClientTimeout + TimeSpan.FromSeconds(2)));
}

/// <summary>OpenAI Chat Completions. Default: a valid compose answer (<c>{"subject","body"}</c>) with a <c>usage</c> block.</summary>
public sealed class OpenAiStub : StubServer
{
    public const string ChatPath = "/chat/completions";
    public const int DefaultPromptTokens = 120;
    public const int DefaultCompletionTokens = 45;
    public const string DefaultSubject = "Stubbed subject";
    public const string DefaultBody = "Stubbed body from WireMock.";

    /// <summary>The HttpClient timeout the integration factory gives the OpenAI client, so the timeout tests stay fast.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Base URL for Semantic Kernel's custom-endpoint registration (it appends <c>/chat/completions</c>).</summary>
    public Uri Endpoint => new($"{Url}/v1");

    public IReadOnlyList<ILogEntry> ChatCalls => Requests(ChatPath);

    protected override void AddDefaults() => ReplyWith(ComposeJson(DefaultSubject, DefaultBody), priority: 100);

    public static string ComposeJson(string subject, string body) => JsonSerializer.Serialize(new { subject, body });
    public static string ClassifyJson(string label, decimal confidence) => JsonSerializer.Serialize(new { classification = label, confidence });

    /// <summary>Every chat call returns <paramref name="content"/> as the assistant message, with the given token usage.</summary>
    public void ReplyWith(string content, int promptTokens = DefaultPromptTokens, int completionTokens = DefaultCompletionTokens, int priority = 1)
        => Server.Given(ChatRequest()).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(Completion(content, promptTokens, completionTokens)));

    public void RespondWith(HttpStatusCode status)
        => Server.Given(ChatRequest()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"stubbed failure","type":"server_error"}}"""));

    /// <summary>The first call hangs past the client timeout; later calls answer with <paramref name="content"/>.</summary>
    public void TimeOutOnceThenReplyWith(string content)
    {
        const string scenario = "timeout-once";
        Server.Given(ChatRequest()).AtPriority(1).InScenario(scenario).WillSetStateTo("answered")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(Completion(content, DefaultPromptTokens, DefaultCompletionTokens)).WithDelay(ClientTimeout + TimeSpan.FromSeconds(2)));
        Server.Given(ChatRequest()).AtPriority(1).InScenario(scenario).WhenStateIs("answered")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(Completion(content, DefaultPromptTokens, DefaultCompletionTokens)));
    }

    private static IRequestBuilder ChatRequest() => Request.Create().WithPath(new WildcardMatcher("*" + ChatPath)).UsingPost();

    private static string Completion(string content, int promptTokens, int completionTokens) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-wiremock",
        @object = "chat.completion",
        created = 1_767_225_600,
        model = "gpt-4o-2024-08-06",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens }
    });
}

/// <summary>
/// People Data Labs Person Search. Default: two pages joined by a scroll token. Page 1 has three people, one without a work email;
/// page 2 has two people and no further token.
/// </summary>
public sealed class PdlStub : StubServer
{
    public const string SearchPath = "/v5/person/search";
    public const string SecondPageToken = "scroll-page-2";

    public IReadOnlyList<ILogEntry> Searches => Requests(SearchPath);

    protected override void AddDefaults()
    {
        Server.Given(Request.Create().WithPath(SearchPath).UsingPost().WithBody(new WildcardMatcher($"*\"scroll_token\":\"{SecondPageToken}\"*")))
            .AtPriority(50)
            .RespondWith(Page([Person("pdl-4", "Dana", "Four", "dana.four@pdl-co.example.test"), Person("pdl-5", "Eli", "Five", "eli.five@pdl-co.example.test")], null));
        Server.Given(Request.Create().WithPath(SearchPath).UsingPost())
            .AtPriority(100)
            .RespondWith(Page([Person("pdl-1", "Ada", "One", "ada.one@pdl-co.example.test"), Person("pdl-2", "Ben", "Two", null),
                Person("pdl-3", "Cy", "Three", "cy.three@pdl-co.example.test")], SecondPageToken));
    }

    /// <summary>Every search gets <paramref name="status"/> (402 out of credits, 429 rate limit, …).</summary>
    public void RespondWith(HttpStatusCode status)
        => Server.Given(Request.Create().WithPath(SearchPath).UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(new { status = (int)status, error = new { type = "stubbed", message = "stubbed failure" } }));

    private static IResponseBuilder Page(object[] people, string? scrollToken) => Response.Create().WithStatusCode(200)
        .WithHeader("X-RateLimit-Remaining", "99")
        .WithHeader("X-TotalLimit-Remaining", "1000")
        .WithBodyAsJson(new { status = 200, data = people, scroll_token = scrollToken, total = 5 });

    private static object Person(string id, string first, string last, string? workEmail) => new Dictionary<string, object?>
    {
        ["id"] = id, ["first_name"] = first, ["last_name"] = last, ["work_email"] = workEmail, ["job_title"] = "Head of Operations",
        ["job_company_name"] = "PDL Co", ["job_company_website"] = "pdl-co.example.test", ["job_company_industry"] = "logistics",
        ["job_company_size"] = "51-200", ["location_country"] = "united kingdom"
    };
}
