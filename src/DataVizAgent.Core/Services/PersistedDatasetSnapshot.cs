namespace DataVizAgent.Services;

public sealed class PersistedDatasetSnapshot
{
    public string DatasetName { get; init; } = string.Empty;
    public string[] Headers { get; init; } = [];
    public List<Dictionary<string, string?>> Rows { get; init; } = [];

    /// <summary>
    /// When set, the dataset is a view over this file and is restored by re-attaching it in place
    /// (<see cref="Rows"/> stays empty). When null, the snapshot embeds the rows (legacy/materialized).
    /// </summary>
    public string? SourcePath { get; init; }
}