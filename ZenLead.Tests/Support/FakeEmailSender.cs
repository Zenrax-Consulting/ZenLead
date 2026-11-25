using ZenLead.Application.Abstractions;

namespace ZenLead.Tests.Support;

/// <summary>Records every message instead of sending it; <see cref="Result"/> controls what callers get back.</summary>
public class FakeEmailSender : IEmailSender
{
    public List<OutboundEmail> Sent { get; } = [];
    public SendResult Result { get; set; } = SendResult.Ok("fake-id");

    public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Sent.Add(email);
        return Task.FromResult(Result);
    }
}
