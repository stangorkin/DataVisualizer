using System;
using System.Threading.Tasks;
using System.Windows;
using DataVizAgent.Services;
using Microsoft.Win32;

namespace DataVizAgent.Desktop.Services;

internal sealed class DesktopDataFileDialogService : IDataFileDialogService
{
    public bool UsesNativeDialogs => true;

    public async Task<string?> PickDataFileAsync()
    {
        return await InvokeOnUiThreadAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open data file",
                Filter =
                    "Data files (*.csv;*.tsv;*.txt;*.parquet;*.xlsx;*.json;*.ndjson)|*.csv;*.tsv;*.txt;*.parquet;*.xlsx;*.json;*.ndjson|" +
                    "All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
    }

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return await dispatcher.InvokeAsync(action);
    }
}
