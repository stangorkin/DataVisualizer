namespace DataVizAgent.Models;

/// <summary>Whether the agent wants to add a new chart or modify the chart the user has selected.</summary>
public enum ChartAction { Create, Update }

/// <summary>A computed chart plus the action the agent requested for it.</summary>
public sealed record ChartSpecResult(ChartSpec Chart, ChartAction Action, string? TargetPage = null);
