using System.ComponentModel;
using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas.Customers;

/// <summary>
/// Shared list-state plumbing for the Customers area (UI_CONVENTIONS §States):
/// loading = IsBusy, error = HasError (+ retry), empty = ShowEmpty, content = ShowContent.
/// RunLatestAsync cancels a superseded load (search-as-you-type, date changes) so a stale result never wins.
/// </summary>
public abstract partial class CustomersAreaViewModel(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs)
    : ViewModelBase(sender, text, nav, dialogs)
{
    private CancellationTokenSource? _loadCts;
    private int _itemCount = -1;   // -1 = not loaded yet

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool ShowEmpty => !IsBusy && !HasError && _itemCount == 0;
    public bool ShowContent => _itemCount > 0;

    protected void SetItemCount(int count)
    {
        _itemCount = count;
        RaiseStates();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(ErrorMessage)) RaiseStates();
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowContent));
    }

    /// <summary>
    /// Runs a load; a newer call cancels the older one. <paramref name="work"/> must call
    /// ct.ThrowIfCancellationRequested() after its awaits and before touching properties.
    /// </summary>
    protected async Task RunLatestAsync(Func<CancellationToken, Task> work)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer load
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                ErrorMessage = Text.Error(new Error(ErrorCodes.Unexpected));
                GlobalExceptions.Report(ex, GetType().Name);
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                IsBusy = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>For actions that wait on the user (share sheet, WhatsApp) — no spinner, never throws.</summary>
    protected async Task SafeAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GlobalExceptions.Report(ex, GetType().Name);
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.Unexpected)));
        }
    }

    protected Task NavigateAsync(string route, string key, Guid id) =>
        SafeAsync(() => Nav.GoToAsync(route, new Dictionary<string, object> { [key] = id }));

    protected string TenderLabel(TenderType? tender) => tender switch
    {
        TenderType.Cash => Text["tender.cash"],
        TenderType.Upi => Text["tender.upi"],
        TenderType.Udhaar => Text["tender.udhaar"],
        _ => "",
    };
}

/// <summary>Display helpers for this area. Not user-facing words, only number/date shapes.</summary>
public static class CustomersFormat
{
    public static string DateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd-MM-yyyy  HH:mm", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DayTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd-MM  HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"919876543210" → "98•••••210" for lists (privacy on a shop counter).</summary>
    public static string? MaskPhone(string? e164)
    {
        var d = LocalDigits(e164);
        return d is null ? null : d[..2] + "•••••" + d[^3..];
    }

    /// <summary>"919876543210" → "+91 98765 43210".</summary>
    public static string? FullPhone(string? e164)
    {
        var d = LocalDigits(e164);
        return d is null ? null : "+91 " + d[..5] + " " + d[5..];
    }

    /// <summary>The 10 local digits, for editing; whatever is stored if it isn't the expected shape.</summary>
    public static string? LocalDigits(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164)) return null;
        var digits = e164.Trim();
        if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal)) return digits[2..];
        return digits.Length == 10 ? digits : null;
    }

    /// <summary>Paise → an editable rupee string: 15050 → "150.5".</summary>
    public static string RupeesInput(Paise amount) =>
        (amount.Value / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"1,250.50" / "₹ 300" → paise. False for blanks, negatives, or more than 2 decimals.</summary>
    public static bool TryParseRupees(string? input, out Paise amount)
    {
        amount = Paise.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var cleaned = input.Replace("₹", "", StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var rupees)) return false;
        var paise = rupees * 100m;
        if (paise != decimal.Truncate(paise) || paise > 100_000_000_00m) return false;   // ≤ ₹10 crore
        amount = new Paise((long)paise);
        return true;
    }

    /// <summary>Reads a Guid shell parameter passed as a Guid (dictionary) or a string (query string).</summary>
    public static Guid? ReadId(IDictionary<string, object> query, string key) =>
        query.TryGetValue(key, out var raw) ? raw switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => null,
        } : null;
}
