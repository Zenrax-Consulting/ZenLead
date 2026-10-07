using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

internal class FakeChatCompletionService : IChatCompletionService
{
    public int CallCount { get; private set; }
    public int ThrowForFirstNCalls { get; set; }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (CallCount <= ThrowForFirstNCalls)
            throw new OperationCanceledException("Simulated timeout");

        var content = new ChatMessageContent(AuthorRole.Assistant, """{ "subject": "Hi", "body": "Body text" }""");
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([content]);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Not used by EmailComposer.");
}

public class EmailComposerRetryTests
{
    private static Kernel BuildKernel(FakeChatCompletionService fakeChat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(fakeChat);
        return builder.Build();
    }

    [Fact]
    public async Task ComposeAsync_FirstCallTimesOut_RetriesOnceAndSucceeds()
    {
        var fakeChat = new FakeChatCompletionService { ThrowForFirstNCalls = 1 };
        var sut = new EmailComposer(BuildKernel(fakeChat), NullLogger<EmailComposer>.Instance);

        var result = await sut.ComposeAsync(new EmailComposeContext("Jane", "jane@acme.com", null, null));

        Assert.Equal(2, fakeChat.CallCount);
        Assert.Equal("Hi", result.Subject);
    }

    [Fact]
    public async Task ComposeAsync_BothCallsTimeOut_PropagatesException()
    {
        var fakeChat = new FakeChatCompletionService { ThrowForFirstNCalls = 2 };
        var sut = new EmailComposer(BuildKernel(fakeChat), NullLogger<EmailComposer>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ComposeAsync(new EmailComposeContext("Jane", "jane@acme.com", null, null)));

        Assert.Equal(2, fakeChat.CallCount); // exactly one retry, not an infinite loop
    }
}
