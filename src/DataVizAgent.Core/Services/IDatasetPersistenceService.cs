namespace DataVizAgent.Services;

public interface IDatasetPersistenceService
{
    PersistedDatasetSnapshot? TryLoadDataset();
    void SaveDataset(PersistedDatasetSnapshot snapshot);
    void DeleteSavedDataset();
}