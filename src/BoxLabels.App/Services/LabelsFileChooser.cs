using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Windows;

namespace BoxLabelsApp.Services;

/// <summary>Works out which box-labels file this run opens, and refuses any
/// path that is not one — whether the user picked it, typed it after
/// "--file", or it was remembered from last time.
///
/// Every path goes through <see cref="Accept"/>. Only picked files used to:
/// a "--file" path or a remembered one was opened unchecked, so
/// <c>--file ...\config.json</c> wrote label_clients into OrdoSort's own
/// config, and a remembered store that had been renamed or deleted silently
/// started a new list at 1 — the same box numbers a second time.
///
/// Out of App so the rules can be tested: the file picker and the "remember
/// this" step are handed in, the dialogs are the ILabelDialogs the label
/// maker already uses.</summary>
internal sealed class LabelsFileChooser
{
    private readonly ILabelDialogs _dialogs;
    private readonly string _title;
    private readonly Func<string?> _askForFile;

    /// <param name="dialogs">Warnings and confirmations.</param>
    /// <param name="title">Heads every dialog.</param>
    /// <param name="askForFile">Shows the file picker; null means the user cancelled.</param>
    public LabelsFileChooser(ILabelDialogs dialogs, string title, Func<string?> askForFile)
    {
        _dialogs = dialogs;
        _title = title;
        _askForFile = askForFile;
    }

    /// <summary>The store for this run, or null when the user cancelled and
    /// the app should close.
    ///
    /// The where-to-look rules live in <see cref="LabelsFileSettings.Decide"/>;
    /// this adds the what-is-there check and the dialogs around it.</summary>
    /// <param name="remember">Saves a replacement the user picked for next
    /// launch. Called only when the run's rules allow it — never for a
    /// "--file" run, which must leave the remembered path as it was.</param>
    public string? Resolve(string[] args, string settingsPath, Func<string, bool> folderReachable,
        Action<string> remember)
    {
        var decision = LabelsFileSettings.Decide(args, settingsPath, folderReachable);
        var persistChoice = decision.PersistChoice;

        if (decision.Prompt == LabelsFilePrompt.None)
        {
            if (Accept(decision.Path)) return decision.Path;
            // A remembered file that turned out to be wrong is replaced by
            // whatever the user picks now — they have just told us where the
            // store really is. A "--file" run stays a one-off.
            persistChoice = LabelsFileSettings.FromArgs(args) is null;
        }
        else
        {
            var (message, kind) = LabelsFilePrompts.PromptFor(decision.Prompt, decision.Path);
            if (kind == MessageKind.Info) _dialogs.Info(message, _title);
            else _dialogs.Warn(message, _title);
        }

        var chosen = PickAndCheck();
        if (chosen is null) return null;
        if (persistChoice) remember(chosen);
        return chosen;
    }

    /// <summary>Pick a box-labels file and refuse the ones that are not one.
    /// Null means the user cancelled.
    ///
    /// Loops rather than closing on a bad pick: being told "that isn't it" and
    /// then having the app exit would leave someone with no way forward but to
    /// start it again and guess better.</summary>
    public string? PickAndCheck()
    {
        while (true)
        {
            var chosen = _askForFile();
            if (chosen is null) return null;
            if (Accept(chosen)) return chosen;
        }
    }

    /// <summary>True when <paramref name="path"/> is a box-labels store the
    /// app may open. Anything else is explained to the user and refused;
    /// a missing file is accepted only once the user confirms a new list.</summary>
    internal bool Accept(string path)
    {
        switch (LabelsFileCheck.Classify(path))
        {
            case LabelsFileKind.Store:
            case LabelsFileKind.EmptyObject:
                return true;

            // Legitimate for the first station ever to set this up, and
            // also exactly how someone starts a private list by mistake.
            // The buttons say which is which; a plain Yes/No here would
            // make the safe answer the one you have to think about.
            case LabelsFileKind.Missing:
                return _dialogs.Confirm(
                    $"There is no file there yet:\n\n{path}\n\n" +
                    "Box Labels will start a NEW, empty box-number list.\n\n" +
                    "If you meant to join the list the other stations already print from, " +
                    "choose that file instead — two separate lists will eventually put the " +
                    "same number on two different boxes.",
                    _title, "Start a new list", "Choose another file");

            case LabelsFileKind.NotAStore:
                _dialogs.Warn(
                    $"That does not look like a box labels file:\n\n{path}\n\n" +
                    "It is a settings file of some kind, but it has no client list in it — " +
                    "it may belong to another program. Using it would write box-label " +
                    "settings into it.\n\n" +
                    "Choose the shared box-labels.json instead.",
                    _title);
                return false;

            case LabelsFileKind.Unreadable:
                _dialogs.Warn(
                    $"That file cannot be read as a box labels file:\n\n{path}\n\n" +
                    "It may be damaged, empty, or not a settings file at all. If it is the " +
                    "right file and it is damaged, restore it from a backup before using it " +
                    "— the running box numbers live in it.",
                    _title);
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path,
                    "unhandled box-labels file kind");
        }
    }
}
