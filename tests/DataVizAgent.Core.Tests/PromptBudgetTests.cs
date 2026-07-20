using DataVizAgent.Ai;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class PromptBudgetTests
{
    private static LlamaConfig Config(uint contextSize = 4096, int maxTokens = -1) =>
        new() { ContextSize = contextSize, MaxTokens = maxTokens };

    [Fact]
    public void PromptBudgetTokens_LeavesRoomForTheAnswer()
    {
        Assert.Equal(4096 - PromptBudget.MinResponseTokens, PromptBudget.PromptBudgetTokens(Config()));
    }

    [Theory]
    [InlineData(4064, true)]   // exactly ContextSize - MinRoomToAttempt (4096 - 32)
    [InlineData(4065, false)]  // one token too big
    [InlineData(1000, true)]
    public void PromptFits_TracksTheContextWindow(int promptTokens, bool expected)
    {
        Assert.Equal(expected, PromptBudget.PromptFits(Config(), promptTokens));
    }

    [Fact]
    public void MaxGeneration_Unlimited_FillsRemainingContext()
    {
        // 4096 - 1000 prompt - 16 margin
        Assert.Equal(3080, PromptBudget.MaxGeneration(Config(maxTokens: -1), promptTokens: 1000));
    }

    [Fact]
    public void MaxGeneration_RespectsConfiguredCap()
    {
        // configured 512 is below the 3080 available, so it wins
        Assert.Equal(512, PromptBudget.MaxGeneration(Config(maxTokens: 512), promptTokens: 1000));
    }

    [Fact]
    public void MaxGeneration_NearlyFullPrompt_StaysAtLeastOne()
    {
        // 4096 - 4090 - 16 would be negative; clamped to 1 so inference never gets a non-positive cap
        Assert.Equal(1, PromptBudget.MaxGeneration(Config(maxTokens: -1), promptTokens: 4090));
    }

    [Fact]
    public void ContextTooSmallMessage_NamesTheSetting()
    {
        string message = PromptBudget.ContextTooSmallMessage(Config(contextSize: 8192));
        Assert.Contains("ContextSize", message);
        Assert.Contains("8192", message);
    }
}
