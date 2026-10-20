using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Events;
using Angadi.Domain.Notes;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Notes;

internal sealed class SaveNoteValidator : IValidator<SaveNoteCommand>
{
    public Error? Validate(SaveNoteCommand c) =>
        string.IsNullOrWhiteSpace(c.Text) ? new Error(ErrorCodes.NoteTextRequired)
        : c.Text.Trim().Length > Note.MaxLength ? new Error(ErrorCodes.NoteTooLong)
        : null;
}

internal sealed class SendFeedbackValidator : IValidator<SendFeedbackCommand>
{
    public Error? Validate(SendFeedbackCommand c) =>
        string.IsNullOrWhiteSpace(c.Message) ? new Error(ErrorCodes.FeedbackTextRequired)
        : c.Message.Trim().Length > Note.MaxLength ? new Error(ErrorCodes.NoteTooLong)
        : null;
}

internal sealed class NotesHandlers(INoteRepository notes, IOutbox outbox, IClock clock) :
    IRequestHandler<SaveNoteCommand, Result<Guid>>,
    IRequestHandler<SetNoteDoneCommand, Result>,
    IRequestHandler<DeleteNoteCommand, Result>,
    IRequestHandler<ListNotesQuery, IReadOnlyList<Note>>
{
    public async Task<Result<Guid>> Handle(SaveNoteCommand command, CancellationToken ct)
    {
        Note note;
        if (command.Id is { } id)
        {
            if (await notes.GetAsync(id, ct) is not { } existing) return Result.Failure<Guid>(ErrorCodes.NoteNotFound);
            note = existing;
        }
        else note = new Note();

        note.Text = command.Text.Trim();
        await notes.SaveAsync(note, ct);
        await outbox.AppendAsync("Note", new NoteChanged(note.Id, clock.Now, Deleted: false), ct);
        return Result.Success(note.Id);
    }

    public async Task<Result> Handle(SetNoteDoneCommand command, CancellationToken ct)
    {
        if (await notes.GetAsync(command.Id, ct) is not { } note) return Result.Failure(ErrorCodes.NoteNotFound);
        if (note.IsDone == command.Done) return Result.Success();
        note.IsDone = command.Done;
        await notes.SaveAsync(note, ct);
        await outbox.AppendAsync("Note", new NoteChanged(note.Id, clock.Now, Deleted: false), ct);
        return Result.Success();
    }

    public async Task<Result> Handle(DeleteNoteCommand command, CancellationToken ct)
    {
        if (await notes.GetAsync(command.Id, ct) is null) return Result.Failure(ErrorCodes.NoteNotFound);
        await notes.DeleteAsync(command.Id, ct);
        await outbox.AppendAsync("Note", new NoteChanged(command.Id, clock.Now, Deleted: true), ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<Note>> Handle(ListNotesQuery q, CancellationToken ct) =>
        (await notes.ListAsync(ct)).OrderBy(n => n.IsDone).ThenByDescending(n => n.UpdatedAt).ToList();
}

internal sealed class SendFeedbackHandler(
    IReceiptShare share, IShopProfileRepository shop, ILogExporter logs, ILocalizer text, IOptions<SupportOptions> support)
    : IRequestHandler<SendFeedbackCommand, Result>
{
    public async Task<Result> Handle(SendFeedbackCommand command, CancellationToken ct)
    {
        var profile = await shop.GetAsync(ct);
        var body = text.Format("feedback.message", profile?.Name ?? "", command.Message.Trim());

        if (command.AttachLogs)
        {
            var zip = await logs.ExportAsync(ct);
            if (zip.IsFailure) return Result.Failure(zip.Error);
            var shared = await share.ShareFileAsync(zip.Value, text["feedback.title"], ct);
            if (shared.IsFailure) return shared;
        }

        var number = PhoneNumbers.Normalize(support.Value.WhatsAppNumber);
        return number is not null
            ? await share.OpenWhatsAppAsync(number, body, ct)
            : await share.ShareTextAsync(body, text["feedback.title"], ct);
    }
}
