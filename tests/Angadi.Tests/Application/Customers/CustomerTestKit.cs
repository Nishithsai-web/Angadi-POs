using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Shop;
using Microsoft.Extensions.Logging;

namespace Angadi.Tests.Application.Customers;

public enum ShareBehaviour { Works, Fails, Throws }

/// <summary>A share sheet / WhatsApp / SMS edge that can be told to fail or throw, and records what it was asked.</summary>
public sealed class ScriptedShare : IReceiptShare
{
    public ShareBehaviour Behaviour { get; set; } = ShareBehaviour.Works;
    public List<(string Phone, string Message)> WhatsApp { get; } = [];
    public List<(string Phone, string Message)> Sms { get; } = [];
    public List<(string File, string Title)> Files { get; } = [];

    private Task<Result> Outcome() => Behaviour switch
    {
        ShareBehaviour.Fails => Task.FromResult(Result.Failure(ErrorCodes.ShareFailed, "no handler")),
        ShareBehaviour.Throws => throw new InvalidOperationException("platform share crashed"),
        _ => Task.FromResult(Result.Success()),
    };

    public Task<Result> ShareFileAsync(string filePath, string title, CancellationToken ct = default)
    {
        Files.Add((filePath, title));
        return Outcome();
    }

    public Task<Result> OpenWhatsAppAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        WhatsApp.Add((phoneE164, message));
        return Outcome();
    }

    public Task<Result> OpenSmsAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        Sms.Add((phoneE164, message));
        return Outcome();
    }

    public Task<Result> ShareTextAsync(string text, string title, CancellationToken ct = default) => Outcome();
}

/// <summary>A receipt renderer that fails (disk full, font missing) so the failure path can be checked.</summary>
public sealed class FailingRenderer : IReceiptRenderer
{
    public Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(ErrorCodes.ReceiptRenderFailed, "test"));
}

/// <summary>Captures every log line with its arguments, scopes and exception text.</summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class Capture(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var sb = new System.Text.StringBuilder(category).Append(' ').Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var p in pairs) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value);
            owner._scopes.ForEachScope((s, b) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object>> kv)
                    foreach (var p in kv) b.Append(' ').Append(p.Key).Append('=').Append(p.Value);
                else b.Append(' ').Append(s);
            }, sb);
            if (exception is not null) sb.Append(' ').Append(exception);
            lock (owner._lines) owner._lines.Add(sb.ToString());
        }
    }
}
