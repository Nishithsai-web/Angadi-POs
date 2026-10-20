using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Tests.Application;

/// <summary>NFR-07 (every request log line carries a correlation id) and NFR-08 (no phone numbers in logs).</summary>
public class LoggingTests
{
    private const string Phone = "9876543210";

    [Fact]
    public async Task NFR07_EveryRequestLine_HasACorrelationId_EvenWhenRejected()
    {
        var sink = new CaptureProvider();
        using var h = new AppHost(configure: s => s.AddLogging(b => b.AddProvider(sink)));
        await h.SeedShopAsync();
        await h.Sender.Send(new VoidBillCommand(Guid.NewGuid(), " "));   // rejected by validation

        var requestLines = sink.Entries.Where(e => e.Category == "Angadi.Requests").ToList();
        Assert.NotEmpty(requestLines);
        Assert.All(requestLines, e => Assert.Contains("CorrelationId", e.Scopes, StringComparison.Ordinal));
        Assert.Contains(requestLines, e => e.Message.Contains("rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NFR08_CustomerPhone_NeverReachesTheLogs()
    {
        var sink = new CaptureProvider();
        using var h = new AppHost(configure: s => s.AddLogging(b => b.AddProvider(sink).SetMinimumLevel(LogLevel.Trace)));
        await h.SeedShopAsync();
        var customer = new Customer { Name = "రవి", Phone = Phone };
        await h.Sender.Send(new SaveCustomerCommand(customer));
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = customer.Id;
        await h.Sender.Send(new FinalizeBillCommand(draft));
        await h.Sender.Send(new SendDuesReminderCommand(customer.Id));
        await h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "x", Phone = "12345" }));   // rejected

        Assert.NotEmpty(sink.Entries);
        Assert.DoesNotContain(sink.Entries, e => (e.Message + e.Scopes).Contains(Phone, StringComparison.Ordinal));
    }

    /// <summary>
    /// LOG-1: PosViewModel's finalize watchdog (WaitAsync, 45 s in the app; a short stand-in here) fires on a
    /// stalled finalize. The design claim that makes the watchdog safe: the send keeps running in the
    /// background and still completes EXACTLY once — the shopkeeper can be told "still saving" and a retry
    /// can never double-bill. This exercises that pattern against the real pipeline with a hanging sender.
    /// </summary>
    [Fact]
    public async Task LOG1_WatchdogTimeout_TheSaveStillCompletesExactlyOnce()
    {
        using var h = new AppHost();
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        var slow = new SlowSender(h.Sender, TimeSpan.FromSeconds(30));

        var send = slow.Send(new FinalizeBillCommand(draft));
        await Assert.ThrowsAsync<TimeoutException>(async () => await send.WaitAsync(TimeSpan.FromMilliseconds(300)));

        var done = await send;   // late completion lands, once
        Assert.True(done.IsSuccess, done.Error.Code);
        var quota = await h.Sender.Send(new GetBillQuotaQuery());
        Assert.Equal(1, quota.UsedThisMonth);
    }

    /// <summary>Delays one request type, simulating a finalize that hangs past the watchdog.</summary>
    private sealed class SlowSender(ISender inner, TimeSpan delay) : ISender
    {
        public async Task<TResult> Send<TResult>(IRequest<TResult> request, CancellationToken ct = default)
        {
            if (request is FinalizeBillCommand) await Task.Delay(delay, ct);
            return await inner.Send(request, ct);
        }
    }

    private sealed record Entry(string Category, string Message, string Scopes);

    private sealed class CaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Capture(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new System.Text.StringBuilder();
                owner._scopes.ForEachScope((s, sb) =>
                {
                    if (s is IEnumerable<KeyValuePair<string, object>> kv)
                        foreach (var p in kv) sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
                    else sb.Append(s).Append(';');
                }, scopes);
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(";", pairs.Select(p => p.Value?.ToString()))
                    : "";
                lock (owner.Entries)
                    owner.Entries.Add(new Entry(category, formatter(state, exception) + " " + values + " " + exception, scopes.ToString()));
            }
        }
    }
}
