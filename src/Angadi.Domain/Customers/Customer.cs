using Angadi.Domain.Common;

namespace Angadi.Domain.Customers;

/// <summary>FR-21. Balance is denormalised; DuesEntry rows are the ledger (FR-22).</summary>
public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }   // E.164 without '+', e.g. 919876543210, for wa.me
    public Paise DuesBalance { get; set; }   // positive = customer owes the shop

    /// <summary>Customer account details the owner keeps (optional).</summary>
    public string? Address { get; set; }
    public string? Notes { get; set; }

    /// <summary>"Will pay on Sunday": the day the customer promised to clear udhaar/appu. Drives reminders.</summary>
    public DateOnly? PromisedPayDate { get; set; }

    /// <summary>When the last dues reminder was sent, so the same customer is not nagged every day.</summary>
    public DateTimeOffset? LastRemindedAt { get; set; }
}

/// <summary>Udhaar ledger. Charge from an udhaar bill, Payment when the customer pays, Reversal on void,
/// WriteOff when the owner forgives bad debt (SEC-5: never disguised as a payment).</summary>
public sealed class DuesEntry : Entity
{
    public Guid CustomerId { get; set; }
    public DuesEntryKind Kind { get; set; }
    public Paise Amount { get; set; }   // always positive; Kind gives direction
    public Guid? BillId { get; set; }
    public string? Note { get; set; }
    /// <summary>H9-EXTENSION: UPI reference when a dues payment came by UPI.</summary>
    public string? UpiRef { get; set; }
}

// FIX(SEC-5, 2026-10-20, FIX-4): Flaw: there was no WriteOff kind, so forgiving bad debt required a fake
// Payment — collections reports overstated real money, and genuine payment fraud could hide in the same
// bucket. Fix: WriteOff = 3 (stored as int in DuesEntryRow.Kind, so this is schema-additive — no migration).
// Why a distinct kind rather than a Note on a payment: reports and aging must be able to tell "money that
// arrived" from "money forgiven" mechanically, forever. Logged in docs/AGENT_FIX_LOG.md.
public enum DuesEntryKind { Charge = 0, Payment = 1, Reversal = 2, WriteOff = 3 }
