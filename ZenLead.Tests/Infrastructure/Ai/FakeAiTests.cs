using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

/// <summary>The Ai:Provider=Fake stand-ins (T00). Their usage logging goes through the normal use cases (see RecordedJobsTests).</summary>
public class FakeAiTests
{
    [Theory]
    [InlineData("Please [[unsubscribe]] me", ReplyClassification.Unsubscribe, 0.99)]
    [InlineData("I'm away [[OOO]] until Monday", ReplyClassification.OutOfOffice, 0.95)]
    [InlineData("[[not-interested]] thanks", ReplyClassification.NotInterested, 0.95)]
    [InlineData("hmm [[review]]", ReplyClassification.Unsubscribe, 0.50)]
    [InlineData("Sounds great, let's talk", ReplyClassification.Interested, 0.95)]
    [InlineData("", ReplyClassification.Interested, 0.95)]
    public async Task Classifier_MapsMarkersToLabels(string reply, ReplyClassification expected, decimal confidence)
    {
        var result = await new FakeReplyClassifier().ClassifyAsync(new ReplyClassificationInput(reply, "Subject", "Body"));
        Assert.Equal(expected, result.Classification);
        Assert.Equal(confidence, result.Confidence);
        Assert.Equal(FakeReplyClassifier.PromptTokens, result.PromptTokens);
        Assert.Equal(FakeReplyClassifier.CompletionTokens, result.CompletionTokens);
    }

    [Fact]
    public async Task Classifier_ReviewMarker_IsBelowTheAutoActionThreshold()
    {
        var result = await new FakeReplyClassifier().ClassifyAsync(new ReplyClassificationInput("[[review]]", null, null));
        Assert.True(result.Confidence < new ZenLead.Application.Classification.ClassificationOptions().AutoActionMinConfidence);
    }

    [Fact]
    public async Task Composer_SameInput_SameOutput()
    {
        var composer = new FakeEmailComposer();
        var context = new EmailComposeContext("Jane Doe", "jane@example.test", "CTO", null, CompanyName: "Acme");
        var a = await composer.ComposeAsync(context);
        var b = await composer.ComposeAsync(context);

        Assert.Equal(a, b);
        Assert.StartsWith("Hi Jane,", a.Body);
        Assert.Contains("Acme", a.Body);
        Assert.Equal(FakeEmailComposer.PromptTokens + FakeEmailComposer.CompletionTokens, a.TokensUsed);
    }

    [Fact]
    public async Task Composer_Personalisation_KeepsTheStepSubjectAndBody()
    {
        // the shape SendDueEmailsUseCase passes for an AI-personalised step
        var context = new EmailComposeContext("Ben Smith", "ben@example.test", null,
            "Personalise this email for the recipient.\n---\nSubject: Quick question for Globex\n\nWould a short call help?\n---", CompanyName: "Globex");

        var result = await new FakeEmailComposer().ComposeAsync(context);

        Assert.Equal("[AI] Quick question for Globex", result.Subject);
        Assert.Contains("Would a short call help?", result.Body);
        Assert.StartsWith("Hi Ben,", result.Body);
    }
}
