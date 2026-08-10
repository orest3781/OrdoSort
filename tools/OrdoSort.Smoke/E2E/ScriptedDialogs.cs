using OrdoSort.Wpf.Services;

namespace OrdoSort.Smoke.E2E;

/// <summary>An IDialogService that answers from per-scenario queues instead
/// of showing modals — a shown modal would block the message loop and hang
/// the harness.
///
/// This answers the USER's side of a prompt (which path to save to, whether
/// to confirm). It never stands in for the app's work: the view models'
/// zipper/extractor/merger/counter/unlocker/plan seams stay at their
/// defaults in every scenario, which is what makes this suite a
/// demonstration rather than a mock theatre.</summary>
public sealed class ScriptedDialogs : IDialogService
{
    private readonly Queue<string?> _saveFile = new();
    private readonly Queue<string?> _openFile = new();
    private readonly Queue<string?> _filePath = new();
    private readonly Queue<string?> _folder = new();
    private readonly Queue<bool> _confirm = new();

    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();

    public ScriptedDialogs QueueSaveFile(params string?[] paths) { foreach (var p in paths) _saveFile.Enqueue(p); return this; }
    public ScriptedDialogs QueueOpenFile(params string?[] paths) { foreach (var p in paths) _openFile.Enqueue(p); return this; }
    public ScriptedDialogs QueueFilePath(params string?[] paths) { foreach (var p in paths) _filePath.Enqueue(p); return this; }
    public ScriptedDialogs QueueFolder(params string?[] paths) { foreach (var p in paths) _folder.Enqueue(p); return this; }
    public ScriptedDialogs QueueConfirm(params bool[] answers) { foreach (var a in answers) _confirm.Enqueue(a); return this; }

    public void Warn(string message, string title) => Warnings.Add(message);
    public void Info(string message, string title) => Infos.Add(message);

    // An empty confirm queue answers true: the overwhelmingly common case is
    // "yes, proceed", and a scenario that cares queues its own answer.
    public bool Confirm(string message, string title) => _confirm.Count > 0 ? _confirm.Dequeue() : true;

    // An empty path queue answers null — the user cancelled. Several
    // scenarios exercise cancellation deliberately, so this must not throw.
    public string? AskSaveFile(string filter, string suggested) => _saveFile.Count > 0 ? _saveFile.Dequeue() : null;
    public string? AskOpenFile(string filter) => _openFile.Count > 0 ? _openFile.Dequeue() : null;
    public string? AskFilePath(string filter, string suggested) => _filePath.Count > 0 ? _filePath.Dequeue() : null;
    public string? BrowseFolder(string? startAt) => _folder.Count > 0 ? _folder.Dequeue() : null;

    /// <summary>Queues with answers left over. A leftover means the scenario
    /// never reached the prompt it was written for.</summary>
    public IReadOnlyList<string> Unconsumed
    {
        get
        {
            var left = new List<string>();
            if (_saveFile.Count > 0) left.Add($"AskSaveFile ({_saveFile.Count})");
            if (_openFile.Count > 0) left.Add($"AskOpenFile ({_openFile.Count})");
            if (_filePath.Count > 0) left.Add($"AskFilePath ({_filePath.Count})");
            if (_folder.Count > 0) left.Add($"BrowseFolder ({_folder.Count})");
            if (_confirm.Count > 0) left.Add($"Confirm ({_confirm.Count})");
            return left;
        }
    }
}
