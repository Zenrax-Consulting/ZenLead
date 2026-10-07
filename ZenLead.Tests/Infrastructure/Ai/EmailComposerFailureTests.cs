using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

internal class ScriptedChatService(Func<ChatMessageContent> respond) : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChatMessageContent>>([respond()]);

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

public class EmailComposerFailureTests
{
    private static EmailComposer Build(Func<ChatMessageContent> respond)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new ScriptedChatService(respond));
        return new EmailComposer(builder.Build(), NullLogger<EmailComposer>.Instance);
    }

    private static readonly EmailComposeContext Context = new("Jane", "jane@acme.com", "CTO", null);

    private static ChatMessageContent Reply(string? content) => new(AuthorRole.Assistant, content);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AiProviderFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AiProviderFailureKind.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, AiProviderFailureKind.Unavailable)]
    public async Task ComposeAsync_ProviderHttpError_MapsToAiProviderException(HttpStatusCode status, AiProviderFailureKind expected)
    {
        var sut = Build(() => throw new HttpOperationException(status, null, "provider error", null));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => sut.ComposeAsync(Context));

        Assert.Equal(expected, ex.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{ "subject": "", "body": "" }""")]
    [InlineData("null")]
    public async Task ComposeAsync_UnusableModelOutput_ThrowsInvalidResponse(string? content)
    {
        var sut = Build(() => Reply(content));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => sut.ComposeAsync(Context));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, ex.Kind);
    }
}
