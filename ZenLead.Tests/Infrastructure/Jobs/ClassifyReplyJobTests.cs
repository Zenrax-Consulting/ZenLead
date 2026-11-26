using System.Reflection;
using Hangfire;
using Hangfire.Common;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Jobs;
using ZenLead.Tests.Application.Classification;

namespace ZenLead.Tests.Infrastructure.Jobs;

/// <summary>Wiring only: the use case is covered by <see cref="ClassifyReplyUseCaseTests"/>.</summary>
public class ClassifyReplyJobTests : IDisposable
{
    private readonly ClassificationHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Forwards_workspace_and_message_ids_to_the_use_case()
    {
        var s = await _h.SeedAsync(replyText: "Tell me more");
        var (useCase, _, ctx) = _h.Build();
        await using (ctx) await new ClassifyReplyJob(useCase).ExecuteAsync(s.WorkspaceId, s.MessageId, CancellationToken.None);

        Assert.Equal("Tell me more", Assert.Single(_h.Classifier.Inputs).ReplyText);
        Assert.Equal(ClassificationSource.Ai, (await _h.Message(s.MessageId)).ClassifiedBy);
    }

    [Fact]
    public void Never_runs_concurrently_for_the_same_message_but_does_for_different_ones()
    {
        var method = typeof(ClassifyReplyJob).GetMethod(nameof(ClassifyReplyJob.ExecuteAsync))!;
        var attr = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(120, attr.TimeoutSec);
        Assert.Equal(ClassifyReplyJob.LockResource, attr.Resource);

        // the lock is keyed by the job arguments, so classification of different replies is not serialised
        var getResource = typeof(DisableConcurrentExecutionAttribute).GetMethod("GetResource", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        string ResourceFor(Guid ws, Guid msg) => (string)getResource.Invoke(attr, [Job.FromExpression<ClassifyReplyJob>(j => j.ExecuteAsync(ws, msg, CancellationToken.None))])!;
        var w = Guid.NewGuid();
        var m1 = Guid.NewGuid();
        Assert.Equal(ResourceFor(w, m1), ResourceFor(w, m1));
        Assert.NotEqual(ResourceFor(w, m1), ResourceFor(w, Guid.NewGuid()));
        Assert.Contains(m1.ToString(), ResourceFor(w, m1));
    }
}
