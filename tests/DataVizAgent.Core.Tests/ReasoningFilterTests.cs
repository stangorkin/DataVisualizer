using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ReasoningFilterTests
{
    [Theory]
    // Qwen / DeepSeek style
    [InlineData("<think>\nlots of reasoning\n</think>\n\nYes, I can see the data.", "Yes, I can see the data.")]
    // gpt-oss harmony channel
    [InlineData("<|channel|>analysis<|message|>reasoning<|channel|>final<|message|>The answer.", "The answer.")]
    // No reasoning at all — passed through untouched
    [InlineData("Just a plain answer.", "Just a plain answer.")]
    // think block embedded with no explicit answer marker after a stray prefix
    [InlineData("<think>hidden</think>Visible.", "Visible.")]
    public void StripForFinal_RemovesReasoning(string input, string expected)
    {
        Assert.Equal(expected, ReasoningFilter.StripForFinal(input));
    }

    [Fact]
    public void StripForStreaming_HidesOutputWhileThinkBlockIsOpen()
    {
        Assert.Equal(string.Empty, ReasoningFilter.StripForStreaming("<think>still reasoning, no answer yet"));
    }

    [Fact]
    public void StripForStreaming_HidesOutputWhileGptOssAnalysisChannelIsOpen()
    {
        Assert.Equal(string.Empty, ReasoningFilter.StripForStreaming("<|channel|>analysis<|message|>thinking..."));
    }

    [Fact]
    public void StripForStreaming_ShowsAnswerAfterThinkCloses()
    {
        Assert.Equal("Partial ans", ReasoningFilter.StripForStreaming("<think>r</think>Partial ans"));
    }

    [Fact]
    public void StripForStreaming_PassesPlainTextThrough()
    {
        Assert.Equal("Hello the", ReasoningFilter.StripForStreaming("Hello the"));
    }
}
