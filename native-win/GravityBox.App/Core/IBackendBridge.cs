using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GravityBox.App.Core;

public sealed class BackendFailureException : Exception
{
    public bool Retryable { get; }

    public BackendFailureException(string message, bool retryable = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Retryable = retryable;
    }
}

public interface IBackendBridge : IAsyncDisposable
{
    string StateDir { get; }
    string PipeName { get; }
    bool IsOwnedProcessRunning { get; }
    int? OwnedPid { get; }
    string? PreferredLocale { get; set; }

    event Action<string>? OnExit;

    Task LaunchAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    Task<JsonObject> SendAsync(
        string command,
        JsonObject? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
