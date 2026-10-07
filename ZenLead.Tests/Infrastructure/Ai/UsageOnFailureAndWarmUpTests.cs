using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;
using ZenLead.Tests.Application.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

public class UsageOnFailureAndWarmUpTests
{
    // mimics the shape of the OpenAI connector's usage object (read by reflection in EmailComposer)
    private record FakeUsage(int InputTokenCount, int OutputTokenCount, int TotalTokenCount);

    private static ChatMessageContent ReplyWithUsage(string? content, int input, int output)
        => new(AuthorRole.Assistant, content)
        {
            Metadata = new Dictionary<string, object?> { ["Usage"] = new FakeUsage(input, output, input + output) }
        };

    private static EmailComposer BuildComposer(Func<ChatMessageContent> respond)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new ScriptedChatService(respond));
        return new EmailComposer(builder.Build(), NullLogger<EmailComposer>.Instance);
    }

    private static readonly EmailComposeContext Context = new("Jane", "jane@acme.com", "CTO", null);

    [Fact]
    public async Task ComposeAsync_UnparseableReply_CarriesTheTokensTheProviderBilled()
    {
        var sut = BuildComposer(() => ReplyWithUsage("not json", input: 120, output: 30));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => sut.ComposeAsync(Context));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, ex.Kind);
        Assert.Equal(120, ex.PromptTokens);
        Assert.Equal(30, ex.CompletionTokens);
    }

    private class FailingComposer(AiProviderException exception) : IEmailComposer
    {
        public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default) => throw exception;
    }

    private static (ComposeEmailUseCase UseCase, FakeTokenUsageTracker Usage, Guid WorkspaceId, Guid LeadId) BuildUseCase(IEmailComposer composer)
    {
        var workspaceId = Guid.NewGuid();
        var leads = new FakeLeadRepositoryForAi();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        leads.Seed(lead);
        var usage = new FakeTokenUsageTracker();
        return (new ComposeEmailUseCase(leads, composer, usage, new AiPricing()), usage, workspaceId, lead.Id);
    }

    [Fact]
    public async Task ExecuteAsync_BilledButUnusableReply_RecordsUsageAndStillThrows()
    {
        var failure = new AiProviderException(AiProviderFailureKind.InvalidResponse, "bad json", null, promptTokens: 100, completionTokens: 40);
        var (useCase, usage, workspaceId, leadId) = BuildUseCase(new FailingComposer(failure));

        await Assert.ThrowsAsync<AiProviderException>(() => useCase.ExecuteAsync(new ComposeEmailRequest(leadId, null), workspaceId));

        var entry = Assert.Single(usage.Entries);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(100, entry.PromptTokens);
        Assert.Equal(40, entry.CompletionTokens);
        Assert.True(entry.EstimatedCostUsd > 0);
    }

    [Fact]
    public async Task ExecuteAsync_FailureThatConsumedNoTokens_RecordsNothing()
    {
        var failure = new AiProviderException(AiProviderFailureKind.RateLimited, "429");
        var (useCase, usage, workspaceId, leadId) = BuildUseCase(new FailingComposer(failure));

        await Assert.ThrowsAsync<AiProviderException>(() => useCase.ExecuteAsync(new ComposeEmailRequest(leadId, null), workspaceId));

        Assert.Empty(usage.Entries);
    }

    private class CountingChatService(Exception? toThrow = null) : IChatCompletionService
    {
        public int Calls { get; private set; }
        public int? MaxTokens { get; private set; }
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            MaxTokens = (executionSettings as Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings)?.MaxTokens;
            if (toThrow is not null) throw toThrow;
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>([new ChatMessageContent(AuthorRole.Assistant, "pong")]);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task WarmUp_MakesOneMinimalCallOnStartup()
    {
        var chat = new CountingChatService();
        var sut = new OpenAiWarmUpService(chat, NullLogger<OpenAiWarmUpService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!;

        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, chat.MaxTokens); // keeps the warm-up to a fraction of a cent
    }

    [Fact]
    public async Task WarmUp_ProviderFailure_IsSwallowedAndDoesNotCrashStartup()
    {
        var chat = new CountingChatService(new HttpRequestException("network down"));
        var sut = new OpenAiWarmUpService(chat, NullLogger<OpenAiWarmUpService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!; // would throw if the exception escaped

        Assert.Equal(1, chat.Calls);
    }
}
