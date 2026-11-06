using System.Globalization;
using System.Text;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Common;

namespace Angadi.Application.Reports;

public enum CsvKind { Sales = 0, Dues = 1, Stock = 2 }

/// <summary>Writes a spreadsheet file (opens in Excel / Google Sheets) for the accountant or a backup of the numbers. Sales covers From..To (at most a year).</summary>
public sealed record ExportCsvCommand(CsvKind Kind, DateOnly From, DateOnly To) : ICommand<Result<string>>, INoTransaction;

/// <summary>Where export files are written (the shareable cache folder; Infrastructure sets it).</summary>
public sealed record ExportFolder(string Path);

public static class Csv
{
    public const int MaxDays = 366;

    /// <summary>
    /// One field, RFC 4180. A text starting with = + - @ (or a tab/CR) is prefixed with an apostrophe so a customer or item
    /// name can never run as a spreadsheet formula when the file is opened.
    /// </summary>
    public static string Field(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    public static string Row(params string?[] fields) => string.Join(",", fields.Select(Field));

    /// <summary>Rupees with two decimals from whole paise, no floating point.</summary>
    public static string Money(Paise p)
    {
        var v = p.Value;
        var sign = v < 0 ? "-" : "";
        var abs = Math.Abs(v);
        return sign + (abs / 100).ToString(CultureInfo.InvariantCulture) + "." + (abs % 100).ToString("00", CultureInfo.InvariantCulture);
    }
}

internal sealed class ExportCsvHandler(ISender sender, IClock clock, ExportFolder folder) : IRequestHandler<ExportCsvCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ExportCsvCommand command, CancellationToken ct)
    {
        if (command.To < command.From || command.To.DayNumber - command.From.DayNumber >= Csv.MaxDays)
            return Result.Failure<string>(ErrorCodes.ExportRangeInvalid);

        var lines = new List<string>();
        switch (command.Kind)
        {
            case CsvKind.Sales:
                lines.Add(Csv.Row("Bill", "Date", "Total", "Payment", "Status"));
                foreach (var b in await sender.Send(new ListBillsQuery(command.From, command.To), ct))
                    lines.Add(string.Join(",", Csv.Field(b.Number), Csv.Field(b.FinalizedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                        Csv.Money(b.GrandTotal), Csv.Field(b.Tender.ToString()), Csv.Field(b.Status.ToString())));
                break;
            case CsvKind.Dues:
                lines.Add(Csv.Row("Customer", "Phone", "Balance"));
                foreach (var d in (await sender.Send(new GetDuesOutstandingQuery(), ct)).Customers)
                    lines.Add(string.Join(",", Csv.Field(d.Name), Csv.Field(d.Phone), Csv.Money(d.Balance)));
                break;
            default:
                lines.Add(Csv.Row("Item", "Unit", "Stock", "Value", "Status"));
                foreach (var l in (await sender.Send(new GetInventoryReportQuery(), ct)).Lines)
                    lines.Add(string.Join(",", Csv.Field(l.Name), Csv.Field(l.Unit.ToString()), (l.Stock.Milli / 1000m).ToString("0.###", CultureInfo.InvariantCulture),
                        Csv.Money(l.Value), l.IsOut ? "OUT" : l.IsLow ? "LOW" : "OK"));
                break;
        }

        try
        {
            Directory.CreateDirectory(folder.Path);
            var name = "angadi-" + command.Kind.ToString().ToLowerInvariant() + "-" + clock.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
            var path = Path.Combine(folder.Path, name);
            // UTF-8 with a byte-order mark: Excel then shows Telugu names correctly.
            await File.WriteAllTextAsync(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true), ct);
            return Result<string>.Success(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new Error(StorageFailures.Classify(ex) ?? ErrorCodes.ShareFailed, ex.GetType().Name));
        }
    }
}
