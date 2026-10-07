using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

public class EmailComposerPromptTests
{
    [Fact]
    public void BuildUserMessage_IncludesLeadFields()
    {
        var context = new EmailComposeContext("Jane Doe", "jane@acme.com", "VP Sales", "Met at conference");

        var message = EmailComposer.BuildUserMessage(context);

        Assert.Contains("Jane Doe", message);
        Assert.Contains("jane@acme.com", message);
        Assert.Contains("VP Sales", message);
        Assert.Contains("Met at conference", message);
    }
}
