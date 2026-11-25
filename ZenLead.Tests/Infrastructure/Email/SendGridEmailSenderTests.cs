using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using ZenLead.Application.Email;
using ZenLead.Infrastructure.Email;
using EmailAddress = ZenLead.Application.Abstractions.EmailAddress;
using OutboundEmail = ZenLead.Application.Abstractions.OutboundEmail;

namespace ZenLead.Tests.Infrastructure.Email;

/// <summary>Hand-rolled ISendGridClient: either returns a canned response or throws.</summary>
internal class FakeSendGridClient(Func<CancellationToken, Response> respond) : ISendGridClient
{
    public List<SendGridMessage> Sent { get; } = [];

    public Task<Response> SendEmailAsync(SendGridMessage msg, CancellationToken cancellationToken = default)
    {
        Sent.Add(msg);
        return Task.FromResult(respond(cancellationToken));
    }

    public static Response Reply(HttpStatusCode status, string body = "", string? messageId = null)
    {
        var headers = new HttpResponseMessage().Headers;
        if (messageId is not null) headers.Add("X-Message-Id", messageId);
        return new Response(status, new StringContent(body), headers);
    }

    public string UrlPath { get; set; } = "";
    public string Version { get; set; } = "v3";
    public string MediaType { get; set; } = "application/json";
    public AuthenticationHeaderValue AddAuthorization(KeyValuePair<string, string> header) => throw new NotSupportedException();
    public Task<Response> MakeRequest(HttpRequestMessage request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Response> RequestAsync(BaseClient.Method method, string? requestBody = null, string? queryParams = null, string? urlPath = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

public class SendGridEmailSenderTests
{
    private static readonly OutboundEmail Email = new(new EmailAddress("ann@acme.test"), "Hello", "Hi");

    private static SendGridEmailSender Sender(FakeSendGridClient client)
        => new(client, Options.Create(new EmailOptions()), NullLogger<SendGridEmailSender>.Instance);

    [Fact]
    public async Task Accepted_returns_ok_with_the_X_Message_Id()
    {
        var client = new FakeSendGridClient(_ => FakeSendGridClient.Reply(HttpStatusCode.Accepted, messageId: "sg-123"));

        var result = await Sender(client).SendAsync(Email);

        Assert.True(result.Success);
        Assert.Equal("sg-123", result.ProviderMessageId);
        Assert.Single(client.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Client_errors_fail_without_retry(HttpStatusCode status)
    {
        var result = await Sender(new FakeSendGridClient(_ => FakeSendGridClient.Reply(status, "{\"errors\":[]}"))).SendAsync(Email);

        Assert.False(result.Success);
        Assert.False(result.Retryable);
        Assert.Contains(((int)status).ToString(), result.Error);
    }

    [Theory]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Throttling_and_server_errors_are_retryable(HttpStatusCode status)
    {
        var result = await Sender(new FakeSendGridClient(_ => FakeSendGridClient.Reply(status))).SendAsync(Email);

        Assert.False(result.Success);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Network_failure_is_retryable()
    {
        var result = await Sender(new FakeSendGridClient(_ => throw new HttpRequestException("boom"))).SendAsync(Email);

        Assert.False(result.Success);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Timeout_without_caller_cancellation_is_retryable()
    {
        var result = await Sender(new FakeSendGridClient(_ => throw new TaskCanceledException())).SendAsync(Email);

        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeSendGridClient(ct => throw new TaskCanceledException(null, null, ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(client).SendAsync(Email, cts.Token));
    }

    [Fact]
    public async Task Error_body_is_truncated()
    {
        var body = new string('e', 2000);

        var result = await Sender(new FakeSendGridClient(_ => FakeSendGridClient.Reply(HttpStatusCode.BadRequest, body))).SendAsync(Email);

        Assert.True(result.Error!.Length < SendGridEmailSender.MaxErrorBodyLength + 50);
        Assert.StartsWith("SendGrid 400: eee", result.Error);
    }
}
