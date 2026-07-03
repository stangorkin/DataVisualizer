using System.Windows;
using System.Windows.Input;
using DataVizAgent.Services;

namespace DataVizAgent.Desktop;

public partial class MainWindow : Window
{
    private readonly ISessionCommandBus _commandBus;

    public MainWindow(ISessionCommandBus commandBus)
    {
        _commandBus = commandBus;
        InitializeComponent();
    }

    private async void OnNewSession(object sender, ExecutedRoutedEventArgs e)
    {
        await _commandBus.RequestAsync(SessionCommand.New);
    }

    private async void OnOpenSession(object sender, ExecutedRoutedEventArgs e)
    {
        await _commandBus.RequestAsync(SessionCommand.Open);
    }

    private async void OnSaveSession(object sender, ExecutedRoutedEventArgs e)
    {
        await _commandBus.RequestAsync(SessionCommand.Save);
    }

    private async void OnPrint(object sender, ExecutedRoutedEventArgs e)
    {
        await _commandBus.RequestAsync(SessionCommand.Print);
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Close();
    }
}