namespace Angadi.Maui.Areas.Reports;

// Display rows: every value is already formatted (IndianFormat + localised labels) in the ViewModel,
// so the XAML only binds strings. Records keep compiled bindings trivial.

/// <summary>One label/value line (day-end figures, GST totals).</summary>
public sealed record LabelValueRow(string Label, string Value);

/// <summary>FR-28: one GST slab.</summary>
public sealed record GstRow(string Slab, string Taxable, string Tax);

/// <summary>FR-29: one top item.</summary>
public sealed record TopItemRow(int Rank, string Name, string Quantity, string Value);

/// <summary>FR-30: one customer with dues.</summary>
public sealed record DueRow(string Name, string Phone, string Balance);

/// <summary>FR-31: one reorder forecast.</summary>
public sealed record ReorderRow(string Headline, string Detail, bool IsUrgent);

/// <summary>FR-32: one dead-stock item.</summary>
public sealed record DeadStockRow(string Name, string Detail, string Value);
