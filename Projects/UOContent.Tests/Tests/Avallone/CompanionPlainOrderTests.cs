using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Plain orders a companion carries out without the model (the owner, chat, 2026-10-17). The first two lines are the
// ones Elspeth ignored that day.
public class CompanionPlainOrderTests
{
    [Theory]
    [InlineData("Elspeth folow me", "follow")]
    [InlineData("Okay Elspeth, let's head out", "follow")]
    [InlineData("Elspeth, follow me.", "follow")]
    [InlineData("Elspeth, come with me", "follow")]
    [InlineData("Elspeth, stay close", "follow")]
    [InlineData("Elspeth, go home", "go_home")]
    [InlineData("Elspeth go home", "go_home")]
    [InlineData("Elspeth, head home now", "go_home")]
    [InlineData("Elspeth, you can go home now", "go_home")]
    [InlineData("Go home, Elspeth", "go_home")]
    [InlineData("Elspeth, Please wait here for me.  I need to go check something", "wait")]
    [InlineData("Elspeth, stay", "wait")]
    [InlineData("Elspeth, wait!", "wait")]
    [InlineData("Elspeth, stay put", "wait")]
    [InlineData("Elspeth, don't follow me", null)]
    [InlineData("Elspeth, will you follow me?", null)]
    [InlineData("Elspeth, wait, what did you say", null)]
    [InlineData("Elspeth, how was your day", null)]
    [InlineData("Elspeth, wait here then follow me later", null)]
    [InlineData("Elspeth, I want to go home someday", null)]
    public void PlainOrder_ReadsOnlyClearOrders(string text, string expected) =>
        Assert.Equal(expected, BaseCompanion.PlainOrder(text, "Elspeth"));
}
