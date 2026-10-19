using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Angadi.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Application.Common;

// In-house request dispatcher (ADR-2, revised Day 4). One record per use case; ViewModels only call ISender.
// Pipeline, outermost first: Validation → Logging → Transaction (commands only) → handler.

public interface IRequest<TResult> { }
/// <summary>Changes data. Runs inside one transaction that commits only on success.</summary>
public interface ICommand<TResult> : IRequest<TResult> { }
/// <summary>Reads data. No transaction.</summary>
public interface IQuery<TResult> : IRequest<TResult> { }
/// <summary>A command that waits on the user (share sheet, WhatsApp): must not hold the DB gate.</summary>
public interface INoTransaction { }

public interface IRequestHandler<in TRequest, TResult> where TRequest : IRequest<TResult>
{
    Task<TResult> Handle(TRequest request, CancellationToken ct);
}

/// <summary>Returns null when valid, or the first problem as an Error (code from ErrorCodes).</summary>
public interface IValidator<in TRequest>
{
    Error? Validate(TRequest request);
}

public interface ISender
{
    Task<TResult> Send<TResult>(IRequest<TResult> request, CancellationToken ct = default);
}

internal sealed class Sender(IServiceProvider services) : ISender
{
    private static readonly ConcurrentDictionary<(Type, Type), object> Wrappers = new();

    public Task<TResult> Send<TResult>(IRequest<TResult> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (Wrapper<TResult>)Wrappers.GetOrAdd((request.GetType(), typeof(TResult)),
            key => Activator.CreateInstance(typeof(Wrapper<,>).MakeGenericType(key.Item1, key.Item2))!);
        return wrapper.Handle(request, services, ct);
    }

    private abstract class Wrapper<TResult>
    {
        public abstract Task<TResult> Handle(IRequest<TResult> request, IServiceProvider sp, CancellationToken ct);
    }

    private sealed class Wrapper<TRequest, TResult> : Wrapper<TResult> where TRequest : IRequest<TResult>
    {
        public override async Task<TResult> Handle(IRequest<TResult> request, IServiceProvider sp, CancellationToken ct)
        {
            var typed = (TRequest)request;
            var name = typeof(TRequest).Name;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Angadi.Requests");

            // Validation
            foreach (var validator in sp.GetServices<IValidator<TRequest>>())
            {
                if (validator.Validate(typed) is { } error)
                {
                    log.LogWarning("{Request} rejected: {Code}", name, error.Code);
                    return ResultFactory.Failure<TResult>(error);
                }
            }

            // Logging (with a correlation id that shows up in every log line inside this request)
            var correlationId = Guid.NewGuid().ToString("N")[..8];
            using var scope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
            var sw = Stopwatch.StartNew();
            try
            {
                var handler = sp.GetRequiredService<IRequestHandler<TRequest, TResult>>();
                TResult result;

                // Transaction
                if (typed is ICommand<TResult> && typed is not INoTransaction)
                {
                    var uow = sp.GetRequiredService<IUnitOfWork>();
                    result = await uow.ExecuteAsync(() => handler.Handle(typed, ct), r => r is not Result res || res.IsSuccess, ct);
                }
                else
                {
                    result = await handler.Handle(typed, ct);
                }

                if (result is Result { IsFailure: true } failed)
                    log.LogWarning("{Request} failed {Code} in {Ms} ms", name, failed.Error.Code, sw.ElapsedMilliseconds);
                else
                    log.LogInformation("{Request} ok in {Ms} ms", name, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "{Request} threw after {Ms} ms", name, sw.ElapsedMilliseconds);
                if (ResultFactory.IsResultType<TResult>()) return ResultFactory.Failure<TResult>(new Error(ErrorCodes.Unexpected, ex.GetType().Name));
                throw;
            }
        }
    }
}

/// <summary>Builds a failed Result or Result&lt;T&gt; when only the generic TResult is known.</summary>
internal static class ResultFactory
{
    private static readonly ConcurrentDictionary<Type, Func<Error, object>> Cache = new();

    public static bool IsResultType<TResult>() => typeof(Result).IsAssignableFrom(typeof(TResult));

    public static TResult Failure<TResult>(Error error)
    {
        var type = typeof(TResult);
        if (type == typeof(Result)) return (TResult)(object)Result.Failure(error);
        if (!IsResultType<TResult>())
            throw new InvalidOperationException($"{type.Name} is not a Result; requests that can fail validation must return Result.");

        var factory = Cache.GetOrAdd(type, t =>
        {
            var method = t.GetMethod(nameof(Result<int>.Failure), BindingFlags.Public | BindingFlags.Static, [typeof(Error)])!;
            return e => method.Invoke(null, [e])!;
        });
        return (TResult)factory(error);
    }
}
