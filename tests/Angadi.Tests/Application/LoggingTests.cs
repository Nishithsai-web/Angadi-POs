using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Voice;
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

    [Fact]
    public async Task NFR08_LearnedAliasText_NeverReachesTheLogs()
    {
        // PRIVACY-1 regression: AliasLearner used to write the learned alias text itself to the
        // debug log. Learned words are the shopkeeper's own speech — NFR-08 covers them too.
        // (The old NFR-08 test never called LearnAsync, so it could not catch this.)
        const string Heard = "రహస్య పదం";
        var sink = new CaptureProvider();
        using var h = new AppHost(configure: s => s.AddLogging(b => b.AddProvider(sink).SetMinimumLevel(LogLevel.Trace)));
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var learner = h.Services.GetRequiredService<IAliasLearner>();
        await learner.LearnAsync(rice.Id, Heard);

        Assert.NotEmpty(sink.Entries);
        Assert.DoesNotContain(sink.Entries, e => (e.Message + e.Scopes).Contains(Heard, StringComparison.Ordinal));
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
