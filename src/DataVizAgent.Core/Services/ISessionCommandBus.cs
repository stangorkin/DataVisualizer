using System;
using System.Threading.Tasks;

namespace DataVizAgent.Services;

public enum SessionCommand
{
    New,
    Open,
    Save,
    Print
}

public interface ISessionCommandBus
{
    event Func<SessionCommand, Task>? CommandRequested;

    Task RequestAsync(SessionCommand command);
}

internal sealed class SessionCommandBus : ISessionCommandBus
{
    public event Func<SessionCommand, Task>? CommandRequested;

    public Task RequestAsync(SessionCommand command)
    {
        Func<SessionCommand, Task>? handler = CommandRequested;
        return handler is null ? Task.CompletedTask : handler(command);
    }
}
