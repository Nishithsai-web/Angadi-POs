namespace Angadi.Application.Common;

/// <summary>
/// An expected failure. <see cref="Code"/> is a key into Resources/errors.json, which holds the
/// Telugu and English message shown to the shopkeeper. <see cref="Detail"/> is for logs only.
/// </summary>
public sealed record Error(string Code, string? Detail = null, object[]? Args = null)
{
    public static readonly Error None = new("");

    /// <summary>M11: an error whose message template takes values ("{0} short", "dues are only {0}").</summary>
    public static Error With(string code, params object[] args) => new(code, null, args);
}

/// <summary>Service-boundary result. Exceptions are for programmer errors only.</summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None) throw new ArgumentException("Success cannot carry an error.", nameof(error));
        if (!isSuccess && error == Error.None) throw new ArgumentException("Failure needs an error.", nameof(error));
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result Failure(string code, string? detail = null) => new(false, new Error(code, detail));
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);
    public static Result<T> Failure<T>(string code, string? detail = null) => Result<T>.Failure(new Error(code, detail));
    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, bool isSuccess, Error error) : base(isSuccess, error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"No value on a failed result ({Error.Code}).");

    public static Result<T> Success(T value) => new(value, true, Error.None);
    public static new Result<T> Failure(Error error) => new(default, false, error);

    public static implicit operator Result<T>(T value) => Success(value);
}

/// <summary>
/// Every code here must exist in Resources/errors.json (enforced by a test).
/// Add new codes here first, then to errors.json in both languages.
/// </summary>
public static class ErrorCodes
{
    public const string VoiceNoInternet = "VOICE_NO_INTERNET";
    public const string VoicePermissionDenied = "VOICE_PERMISSION_DENIED";
    public const string VoiceNothingHeard = "VOICE_NOTHING_HEARD";
    public const string VoiceNoMatch = "VOICE_NO_MATCH";
    public const string CameraPermissionDenied = "CAMERA_PERMISSION_DENIED";
    public const string BarcodeUnknown = "BARCODE_UNKNOWN";
    public const string BarcodeDuplicate = "BARCODE_DUPLICATE";
    public const string BillEmpty = "BILL_EMPTY";
    public const string BillQtyInvalid = "BILL_QTY_INVALID";
    public const string BillTenderMissing = "BILL_TENDER_MISSING";
    public const string BillCashShort = "BILL_CASH_SHORT";
    public const string BillUdhaarNeedsCustomer = "BILL_UDHAAR_NEEDS_CUSTOMER";
    public const string BillAlreadyVoid = "BILL_ALREADY_VOID";
    public const string BillNotFound = "BILL_NOT_FOUND";
    public const string BillVoidBlockedByPayments = "BILL_VOID_BLOCKED_BY_PAYMENTS";
    public const string BillLegacySlab = "BILL_LEGACY_SLAB";
    public const string BillDiscountTooLarge = "BILL_DISCOUNT_TOO_LARGE";
    public const string FreeLimitReached = "FREE_LIMIT_REACHED";
    public const string PurchaseFailed = "PURCHASE_FAILED";
    public const string ShareFailed = "SHARE_FAILED";
    public const string ReceiptRenderFailed = "RECEIPT_RENDER_FAILED";
    public const string BackupFailed = "BACKUP_FAILED";
    public const string RestoreInvalidFile = "RESTORE_INVALID_FILE";
    public const string LogExportFailed = "LOG_EXPORT_FAILED";
    public const string ItemNotFound = "ITEM_NOT_FOUND";
    public const string CustomerNotFound = "CUSTOMER_NOT_FOUND";
    public const string DuesExceedsBalance = "DUES_EXCEEDS_BALANCE";
    public const string AmountInvalid = "AMOUNT_INVALID";
    public const string CustomerNoPhone = "CUSTOMER_NO_PHONE";
    public const string CustomerNoDues = "CUSTOMER_NO_DUES";
    public const string VoidReasonRequired = "VOID_REASON_REQUIRED";
    public const string ItemNameRequired = "ITEM_NAME_REQUIRED";
    public const string ItemPriceInvalid = "ITEM_PRICE_INVALID";
    public const string UpiIdInvalid = "UPI_ID_INVALID";
    public const string GstinInvalid = "GSTIN_INVALID";
    public const string PhoneInvalid = "PHONE_INVALID";
    public const string ShopNameRequired = "SHOP_NAME_REQUIRED";
    public const string BillAmountTooLarge = "BILL_AMOUNT_TOO_LARGE";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unexpected = "UNEXPECTED";
}
