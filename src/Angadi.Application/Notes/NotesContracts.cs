using Angadi.Application.Common;
using Angadi.Domain.Notes;

namespace Angadi.Application.Notes;

/// <summary>Add (Id null) or edit a shop note.</summary>
public sealed record SaveNoteCommand(Guid? Id, string Text) : ICommand<Result<Guid>>;
public sealed record SetNoteDoneCommand(Guid Id, bool Done) : ICommand<Result>;
public sealed record DeleteNoteCommand(Guid Id) : ICommand<Result>;
/// <summary>Open notes first (newest first), then done ones.</summary>
public sealed record ListNotesQuery : IQuery<IReadOnlyList<Note>>;

/// <summary>
/// In-app feedback to the Angadi team: goes to the support WhatsApp number when one is configured, otherwise
/// the share sheet. With <paramref name="AttachLogs"/>, the log zip is shared too (phone numbers are never logged).
/// </summary>
public sealed record SendFeedbackCommand(string Message, bool AttachLogs = false) : ICommand<Result>, INoTransaction;
