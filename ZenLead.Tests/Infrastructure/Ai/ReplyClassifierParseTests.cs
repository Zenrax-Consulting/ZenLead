using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

/// <summary>No network: <see cref="ReplyClassifier.Parse"/> against camelCase fixtures, as the model actually returns them.</summary>
public class ReplyClassifierParseTests
{
    [Fact]
    public void Valid_camelCase_json_parses()
    {
        var r = ReplyClassifier.Parse("""{"classification":"interested","confidence":0.93}""", 100, 10);

        Assert.Equal(ReplyClassification.Interested, r.Classification);
        Assert.Equal(0.93m, r.Confidence);
        Assert.Equal(100, r.PromptTokens);
        Assert.Equal(10, r.CompletionTokens);
    }

    [Theory]
    [InlineData("interested", ReplyClassification.Interested)]
    [InlineData("not_interested", ReplyClassification.NotInterested)]
    [InlineData("unsubscribe", ReplyClassification.Unsubscribe)]
    [InlineData("out_of_office", ReplyClassification.OutOfOffice)]
    [InlineData("Unsubscribe", ReplyClassification.Unsubscribe)]          // label case-insensitive
    [InlineData(" OUT_OF_OFFICE ", ReplyClassification.OutOfOffice)]      // trimmed
    public void Each_label_maps(string label, ReplyClassification expected)
        => Assert.Equal(expected, ReplyClassifier.Parse($$"""{"classification":"{{label}}","confidence":0.5}""", 1, 1).Classification);

    [Theory]
    [InlineData("""{"classification":"maybe","confidence":0.9}""")]
    [InlineData("""{"classification":"interested; drop","confidence":0.9}""")]
    [InlineData("""{"classification":"","confidence":0.9}""")]
    [InlineData("""{"classification":null,"confidence":0.9}""")]
    [InlineData("""{"classification":"unclassified","confidence":0.9}""")]   // our state, never a model label
    [InlineData("""{"classification":"bounce","confidence":0.9}""")]
    [InlineData("""{"confidence":0.9}""")]
    public void Unknown_label_is_InvalidResponse_carrying_the_billed_tokens(string json)
    {
        var ex = Assert.Throws<AiProviderException>(() => ReplyClassifier.Parse(json, 321, 7));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, ex.Kind);
        Assert.Equal(321, ex.PromptTokens);
        Assert.Equal(7, ex.CompletionTokens);
    }

    [Theory]
    [InlineData("""{"classification":"interested"}""")]
    [InlineData("""{"classification":"interested","confidence":null}""")]
    [InlineData("""{"classification":"interested","confidence":-0.1}""")]
    [InlineData("""{"classification":"interested","confidence":1.5}""")]
    [InlineData("""{"classification":"interested","confidence":"high"}""")]
    [InlineData("""{"classification":"interested","confidence":true}""")]
    public void Missing_or_out_of_range_confidence_is_InvalidResponse(string json)
    {
        var ex = Assert.Throws<AiProviderException>(() => ReplyClassifier.Parse(json, 5, 6));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, ex.Kind);
        Assert.Equal(5, ex.PromptTokens);
        Assert.Equal(6, ex.CompletionTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"classification":"interested","confid""")]          // truncated at MaxTokens
    [InlineData("interested")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Empty_truncated_or_non_object_content_is_InvalidResponse(string? json)
    {
        var ex = Assert.Throws<AiProviderException>(() => ReplyClassifier.Parse(json, 9, 9));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, ex.Kind);
        Assert.Equal(9, ex.PromptTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Confidence_bounds_are_inclusive(int confidence)
        => Assert.Equal(confidence, ReplyClassifier.Parse($$"""{"classification":"interested","confidence":{{confidence}}}""", 0, 0).Confidence);

    [Fact]
    public void Extra_properties_are_ignored()
        => Assert.Equal(ReplyClassification.NotInterested,
            ReplyClassifier.Parse("""{"classification":"not_interested","confidence":0.7,"reason":"declined","tools":[]}""", 0, 0).Classification);

    [Fact]
    public void PascalCase_json_still_parses_because_Web_defaults_are_case_insensitive()
        => Assert.Equal(ReplyClassification.Unsubscribe, ReplyClassifier.Parse("""{"Classification":"unsubscribe","Confidence":0.8}""", 0, 0).Classification);

    private record PascalOnlyDto(string? Classification, decimal? Confidence);

    [Fact]
    public void Regression_guard_a_PascalCase_DTO_without_Web_defaults_misses_camelCase_output()
    {
        // the Phase 1 (Feature 6) lesson: default System.Text.Json is case-sensitive, so camelCase model output silently binds nothing
        var dto = JsonSerializer.Deserialize<PascalOnlyDto>("""{"classification":"interested","confidence":0.9}""");

        Assert.Null(dto!.Classification);
        Assert.Null(dto.Confidence);
    }
}
