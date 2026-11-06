using Angadi.Domain.Common;

namespace Angadi.Domain.Payroll;

/// <summary>How a worker is paid. Monthly: a fixed salary for the calendar month. Daily: a wage per day worked.</summary>
public enum PayType { Monthly = 0, Daily = 1 }

public enum AttendanceMark { Absent = 0, Present = 1, HalfDay = 2 }

/// <summary>Money moving between the shop and a worker. Advance: given early. Recovery: an advance taken back from salary. Salary: paid out.</summary>
public enum PayEntryKind { Advance = 0, Recovery = 1, Salary = 2 }

/// <summary>Someone the shop pays. Not necessarily a person who signs in to the app (those are Staff).</summary>
public sealed class Worker : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public PayType PayType { get; set; }

    /// <summary>Monthly salary, or the wage for one full day, in paise.</summary>
    public Paise Rate { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed record AttendanceEntry(Guid WorkerId, DateOnly Day, AttendanceMark Mark);

public sealed class PayEntry : Entity
{
    public Guid WorkerId { get; set; }
    public PayEntryKind Kind { get; set; }
    public Paise Amount { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>The pay month (year*100+month) a Salary or Recovery belongs to; 0 for an Advance.</summary>
    public int PayMonth { get; set; }
    public string? Note { get; set; }
}

/// <param name="HalfDays">Paid half-days: a present day counts 2, a half day 1.</param>
/// <param name="AdvanceBalance">Advances given and not yet recovered, before this slip.</param>
/// <param name="Deduction">The part of the balance taken from this salary (never more than the pay earned).</param>
public sealed record Payslip(
    Guid WorkerId, string WorkerName, int Year, int Month, PayType PayType, Paise Rate,
    int PresentDays, int HalfDayCount, int AbsentDays, int HalfDays,
    Paise Earned, Paise AdvanceBalance, Paise Deduction, Paise NetPay, bool AlreadyPaid)
{
    public Paise BalanceAfter => AdvanceBalance - Deduction;
}

/// <summary>Whole-paise arithmetic only: no floating point anywhere in pay.</summary>
public static class PayrollCalculator
{
    /// <summary>Pay for the paid half-days. Monthly: rate x halfDays / (2 x days in month). Daily: rate x halfDays / 2. Rounded half up.</summary>
    public static Paise Earned(PayType type, Paise rate, int halfDays, int daysInMonth)
    {
        if (halfDays <= 0 || rate.Value <= 0) return Paise.Zero;
        var denominator = type == PayType.Monthly ? 2L * daysInMonth : 2L;
        var numerator = checked(rate.Value * halfDays);
        return new Paise((numerator + denominator / 2) / denominator);
    }

    public static Payslip Compute(Worker worker, int year, int month, IEnumerable<AttendanceEntry> marks, Paise advanceBalance, bool alreadyPaid)
    {
        var days = DateTime.DaysInMonth(year, month);
        int present = 0, half = 0;
        // One mark per day: the last one wins if the data ever holds two.
        foreach (var m in marks.Where(m => m.WorkerId == worker.Id && m.Day.Year == year && m.Day.Month == month)
                     .GroupBy(m => m.Day).Select(g => g.Last()))
        {
            if (m.Mark == AttendanceMark.Present) present++;
            else if (m.Mark == AttendanceMark.HalfDay) half++;
        }
        var halfDays = present * 2 + half;
        var earned = Earned(worker.PayType, worker.Rate, halfDays, days);
        var balance = advanceBalance.Value > 0 ? advanceBalance : Paise.Zero;
        var deduction = balance < earned ? balance : earned;
        // Days with no mark are counted absent only for a monthly slip's information; a daily worker is simply not paid for them.
        var absent = Math.Max(0, days - present - half);
        return new Payslip(worker.Id, worker.Name, year, month, worker.PayType, worker.Rate, present, half, absent, halfDays,
            earned, balance, deduction, earned - deduction, alreadyPaid);
    }
}
