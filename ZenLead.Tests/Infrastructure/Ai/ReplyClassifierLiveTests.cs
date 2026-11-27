using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Xunit.Abstractions;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Classification;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Ai;

/// <summary>Runs only with <c>RUN_LIVE_AI=1</c> and an OpenAI key (env <c>OpenAI__ApiKey</c> or the API project's user secrets).</summary>
public sealed class LiveAiFactAttribute : FactAttribute
{
    public LiveAiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_AI") != "1") Skip = "Live AI check: set RUN_LIVE_AI=1 (and OpenAI:ApiKey) to run.";
        else if (string.IsNullOrWhiteSpace(LiveAiConfig.ApiKey)) Skip = "Live AI check: OpenAI:ApiKey is not configured.";
    }
}

internal static class LiveAiConfig
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddUserSecrets(typeof(Program).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static string? ApiKey => Config["OpenAI:ApiKey"];
    public static string Model => Config["OpenAI:ClassifierModel"] ?? "gpt-4o-mini";
}

/// <summary>
/// Not in CI. Run manually before Gate 2 and whenever the prompt or model changes; record the result in docs/.
/// <c>$env:RUN_LIVE_AI="1"; dotnet test ZenLead.Tests --filter Category=Live</c>
/// </summary>
[Trait("Category", TestCategories.Live)]
public class ReplyClassifierLiveTests(ITestOutputHelper output)
{
    private static ReplyClassifier CreateClassifier()
    {
        var services = new ServiceCollection();
        services.AddKernel().AddOpenAIChatCompletion(modelId: LiveAiConfig.Model, apiKey: LiveAiConfig.ApiKey!, serviceId: ClassificationModelOptions.DefaultServiceId);
        var kernel = services.BuildServiceProvider().GetRequiredService<Kernel>();
        return new ReplyClassifier(kernel, Options.Create(new ClassificationModelOptions()), NullLogger<ReplyClassifier>.Instance);
    }

    private static readonly ReplyClassificationInput Template = new("", "Quick question about your outbound pipeline",
        "Hi Jane, I noticed Acme is hiring SDRs. We help teams like yours book more meetings with personalised outreach. Worth a 15-minute call next week?");

    [LiveAiFact]
    public async Task Labelled_set_is_at_least_90_percent_accurate()
    {
        var sut = CreateClassifier();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Classification", "labelled.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        int total = 0, correct = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var reply = e.GetProperty("reply").GetString()!;
            var expected = ReplyClassifier.Labels[e.GetProperty("expected").GetString()!];
            var result = await sut.ClassifyAsync(Template with { ReplyText = reply });
            total++;
            if (result.Classification == expected) correct++;
            output.WriteLine($"{(result.Classification == expected ? "ok  " : "MISS")} {result.Classification,-14} {result.Confidence:0.00}  expected {expected,-14} | {reply}");
        }
        output.WriteLine($"Accuracy: {correct}/{total} with {LiveAiConfig.Model}");
        Assert.True(correct >= total * 0.9, $"Accuracy {correct}/{total} is below 90%.");
    }

    [LiveAiFact]
    public async Task No_adversarial_reply_gets_an_actionable_label_at_high_confidence()
    {
        var sut = CreateClassifier();
        var failures = new List<string>();
        foreach (var a in ReplyClassifierPromptTests.LoadAdversarial())
        {
            var reply = ClassifyReplyUseCase.Truncate(a.Reply, 2000)!;
            try
            {
                var r = await sut.ClassifyAsync(Template with { ReplyText = reply });
                output.WriteLine($"{a.Name,-20} {r.Classification,-14} {r.Confidence:0.00}");
                if (r.Classification is ReplyClassification.Unsubscribe or ReplyClassification.OutOfOffice && r.Confidence >= 0.8m)
                    failures.Add($"{a.Name}: {r.Classification} @ {r.Confidence}");
            }
            catch (AiProviderException ex) when (ex.Kind == AiProviderFailureKind.InvalidResponse)
            {
                output.WriteLine($"{a.Name,-20} invalid output (left Unclassified — safe)");
            }
        }
        Assert.Empty(failures);
    }
}
