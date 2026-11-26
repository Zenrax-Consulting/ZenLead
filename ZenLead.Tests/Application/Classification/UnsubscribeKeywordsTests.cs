using ZenLead.Application.Classification;

namespace ZenLead.Tests.Application.Classification;

public class UnsubscribeKeywordsTests
{
    [Theory]
    [InlineData("please unsubscribe me")]
    [InlineData("Remove me from your list")]
    [InlineData("REMOVE MY address please")]
    [InlineData("Take me off this list.")]
    [InlineData("stop emailing me")]
    [InlineData("Stop   sending these")]                       // any whitespace between words
    [InlineData("Do not contact me again")]
    [InlineData("don't contact me")]
    [InlineData("don’t contact me")]                          // curly apostrophe
    [InlineData("I want to opt out")]
    [InlineData("opt-out")]
    [InlineData("No more emails, thanks")]
    [InlineData("Unsubscribe.")]
    public void Positives(string text) => Assert.True(UnsubscribeKeywords.LooksLikeUnsubscribeRequest(text));

    [Theory]
    [InlineData("not interested, thanks")]
    [InlineData("Sounds interesting - send pricing")]
    [InlineData("I'm out of office until Monday")]
    [InlineData("we removed meetings from Fridays")]          // "removed me" is not "remove me"
    [InlineData("unsubscribed")]                              // word boundary: not the same word
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Negatives(string? text) => Assert.False(UnsubscribeKeywords.LooksLikeUnsubscribeRequest(text));

    [Fact]
    public void False_positive_risk_is_why_the_model_must_also_say_unsubscribe()
    {
        // the keyword check alone would flag this — it only ever raises the confidence of an unsubscribe label the model chose
        Assert.True(UnsubscribeKeywords.LooksLikeUnsubscribeRequest("I can't unsubscribe from my gym lol, but yes let's talk"));
    }
}
