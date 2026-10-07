using System.Text.Json;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

public class SubjectBodyDtoParsingTests
{
    [Fact]
    public void Deserialize_ValidJson_ProducesExpectedShape()
    {
        // camelCase, as the model actually returns it; same options as EmailComposer.ComposeAsync
        var json = """{ "subject": "Quick question", "body": "Hi Jane, ..." }""";

        var result = JsonSerializer.Deserialize<SubjectBodyDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal("Quick question", result!.Subject);
        Assert.Equal("Hi Jane, ...", result.Body);
    }

    [Fact]
    public void Deserialize_DefaultOptions_IgnoresCamelCaseFields()
    {
        // Regression guard for the Feature 6 finding: default System.Text.Json is
        // case-sensitive, so camelCase model output only parses with the Web options.
        var json = """{ "subject": "Quick question", "body": "Hi Jane, ..." }""";

        var result = JsonSerializer.Deserialize<SubjectBodyDto>(json);

        Assert.Null(result!.Subject);
    }

    [Fact]
    public void Deserialize_MissingField_LeavesItNull()
    {
        // Documents current behavior: System.Text.Json doesn't enforce non-null record
        // parameters on deserialize, so a malformed model response silently produces a
        // null Body rather than throwing. If EmailComposer.ComposeAsync should instead
        // reject this, add that validation explicitly — don't rely on the deserializer.
        var json = """{ "subject": "Quick question" }""";

        var result = JsonSerializer.Deserialize<SubjectBodyDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Null(result!.Body);
    }
}
