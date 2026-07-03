using DataVizAgent.Models;

namespace DataVizAgent.Services;

public interface IReportPersistenceService
{
    ReportDocument? TryLoadReport();
    void SaveReport(ReportDocument report);
    void DeleteSavedReport();
}