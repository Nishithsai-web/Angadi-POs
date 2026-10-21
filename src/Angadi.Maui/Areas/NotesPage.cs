using Angadi.Application.Common;
using Angadi.Application.Notes;
using Angadi.Application.Platform;
using Angadi.Domain.Notes;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// The shop's own notes (to-dos, supplier orders, promises) plus feedback to the Angadi team.
/// Notes live on the device and travel in backups; feedback leaves only when the shopkeeper taps Send.
/// </summary>
public sealed class NotesPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Entry _newNote;
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };
    private readonly Editor _feedback;
    private readonly CheckBox _attachLogs = new() { Color = Theme.Primary };

    public NotesPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["settings.notes"];

        _newNote = Ui.Entry(text["notes.add.placeholder"]);
        _newNote.MaxLength = Note.MaxLength;
        _newNote.Completed += SafeUi.On(async () => await AddAsync());
        var add = Ui.Button(text["notes.add"]);
        add.Clicked += SafeUi.On(async () => await AddAsync());
        var addRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        addRow.Add(_newNote, 0, 0);
        addRow.Add(add, 1, 0);

        _feedback = new Editor
        {
            Placeholder = text["feedback.placeholder"], AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 96,
            MaxLength = Note.MaxLength, BackgroundColor = Theme.Surface, FontFamily = Theme.Font, FontSize = 16,
        };
        var logsRow = new HorizontalStackLayout { Spacing = 6, Children = { _attachLogs, Ui.Body(text["feedback.logs"], 14) } };
        var send = Ui.Button(text["feedback.send"], Theme.Success);
        send.Clicked += SafeUi.On(async () => await SendFeedbackAsync());

        Content = Ui.Scroll(
            Ui.Title(text["notes.title"]),
            addRow,
            _list,
            new BoxView { HeightRequest = 1, Color = Theme.Border, Margin = new Thickness(0, 12) },
            Ui.Title(text["feedback.title"]),
            _feedback,
            logsRow,
            send);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: unwrapped async void OnAppearing on a ContentPage — a failed load
        // escaped SafeUi (no toast, half-loaded page). Fix: run the load through SafeUi.Run like the other 7
        // secondary pages. Why: "never-fail UI" must hold on every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () => await ReloadAsync());
    }

    private async Task ReloadAsync()
    {
        var notes = await _sender.Send(new ListNotesQuery());
        _list.Children.Clear();
        if (notes.Count == 0) { _list.Add(Ui.Caption(_text["notes.empty"])); return; }
        foreach (var note in notes) _list.Add(Row(note));
    }

    private View Row(Note note)
    {
        var done = new CheckBox { IsChecked = note.IsDone, Color = Theme.Primary, VerticalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(done, _text["notes.done"]);
        done.CheckedChanged += SafeUi.On<CheckedChangedEventArgs>(async e =>
        {
            var r = await _sender.Send(new SetNoteDoneCommand(note.Id, e.Value));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
            await ReloadAsync();
        });

        var body = new Label
        {
            Text = note.Text, FontSize = 16, LineBreakMode = LineBreakMode.WordWrap,
            TextColor = note.IsDone ? Theme.TextSecondary : Theme.TextPrimary,
            TextDecorations = note.IsDone ? TextDecorations.Strikethrough : TextDecorations.None,
        };
        var when = Ui.Caption(note.UpdatedAt.ToLocalTime().ToString("dd MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture));

        var delete = new Button { Text = "✕", WidthRequest = 48, BackgroundColor = Theme.PrimarySoft, TextColor = Theme.Danger, Padding = 0 };
        SemanticProperties.SetDescription(delete, _text["notes.delete"]);
        delete.Clicked += SafeUi.On(async () =>
        {
            if (!await _dialogs.ConfirmAsync(_text["notes.delete"], _text["notes.delete.confirm"], _text["common.yes"], _text["common.no"])) return;
            var r = await _sender.Send(new DeleteNoteCommand(note.Id));
            if (r.IsFailure) await _dialogs.ToastAsync(_text.Error(r.Error));
            await ReloadAsync();
        });

        var g = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        g.Add(done, 0, 0);
        g.Add(new VerticalStackLayout { Spacing = 2, Children = { body, when } }, 1, 0);
        g.Add(delete, 2, 0);
        return Ui.Card(g, 8);
    }

    private async Task AddAsync()
    {
        var r = await _sender.Send(new SaveNoteCommand(null, _newNote.Text ?? ""));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _newNote.Text = "";
        await ReloadAsync();
    }

    private async Task SendFeedbackAsync()
    {
        var r = await _sender.Send(new SendFeedbackCommand(_feedback.Text ?? "", _attachLogs.IsChecked));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _feedback.Text = "";
        await _dialogs.ToastAsync(_text["feedback.thanks"]);
    }
}
