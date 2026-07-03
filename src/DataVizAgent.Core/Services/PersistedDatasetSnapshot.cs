namespace DataVizAgent.Services;

public sealed class PersistedDatasetSnapshot
{
    public string DatasetName { get; init; } = string.Empty;
    public string[] Headers { get; init; } = [];
    public List<Dictionary<string, string?>> Rows { get; init; } = [];
}