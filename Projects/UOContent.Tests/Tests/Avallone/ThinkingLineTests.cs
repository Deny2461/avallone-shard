using System;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// the owner, 2026-10-17: stalling lines need variety and emotes, and must not react to whether the player asked or told.
public class ThinkingLineTests
{
    [Theory]
    [InlineData("*pauses*", true)]
    [InlineData("*tilts her head*", true)]
    [InlineData("Well...", false)]
    [InlineData("**", false)]
    [InlineData("*", false)]
    [InlineData("Hm. *sigh*", false)]
    public void EmoteLinesAreStarred(string line, bool expected) => Assert.Equal(expected, BaseCompanion.IsEmoteLine(line));

    [Fact]
    public void CommonLinesFitAnyone()
    {
        var lines = BaseCompanion.CommonThinkingLines;
        Assert.True(lines.Length >= 30);
        Assert.Equal(lines.Length, new System.Collections.Generic.HashSet<string>(lines).Count);
        foreach (var l in lines)
        {
            Assert.DoesNotContain("?", l);
            foreach (var w in System.Text.RegularExpressions.Regex.Matches(l.ToLowerInvariant(), "[a-z']+"))
            {
                Assert.DoesNotContain(w.ToString(), new[] { "he", "she", "her", "his", "him", "hers", "himself", "herself",
                    "question", "ask", "asked", "statement", "said", "say" });
            }
        }
    }

    [Fact]
    public void ElspethsLinesAreVariedAndNeutral()
    {
        // ThinkingLines is an instance property; read it without placing her in the world.
        var lines = (string[])typeof(ElspethVale).GetProperty("ThinkingLines")!
            .GetGetMethod()!.Invoke(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ElspethVale)), null)!;

        Assert.True(lines.Length >= 50);
        Assert.Contains(lines, BaseCompanion.IsEmoteLine);
        Assert.Contains(lines, l => !BaseCompanion.IsEmoteLine(l));
        Assert.Equal(lines.Length, new System.Collections.Generic.HashSet<string>(lines).Count);
        foreach (var l in lines)
        {
            Assert.DoesNotContain("?", l);
            foreach (var w in new[] { "question", "ask", "statement", "said", "say" })
            {
                Assert.DoesNotContain(w, l, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
