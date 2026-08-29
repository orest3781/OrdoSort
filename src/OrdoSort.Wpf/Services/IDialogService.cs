using OrdoSort.Core;

namespace OrdoSort.Wpf.Services;

/// <summary>Every modal the app can show, behind an interface so view models
/// stay testable and the smoke harness can record instead of block.</summary>
public interface IDialogService
{
    void Warn(string message, string title);
    void Info(string message, string title);
    bool Confirm(string message, string title);

    /// <summary>A question whose buttons say what they DO — "Remove"/"Keep"
    /// rather than "Yes"/"No". Every question this app asks has a destructive
    /// answer, and a generic Yes forces the user to re-read the sentence to
    /// work out which way round it is.
    ///
    /// Defaulted rather than abstract for the same reason
    /// <see cref="AskOpenFiles"/> is: fourteen classes implement this
    /// interface and only the real one renders buttons, so the fakes,
    /// recorders and scripted stubs inherit a correct fallback instead of each
    /// carrying a throwaway override. The fallback deliberately drops the
    /// labels — a recording double cares which question was asked, not what
    /// the buttons said.</summary>
    bool Confirm(string message, string title, string yesLabel, string noLabel) =>
        Confirm(message, title);
    string? AskSaveFile(string filter, string suggestedName);
    string? AskOpenFile(string filter);

    /// <summary>Pick one or more existing files. Empty when cancelled, never
    /// null. Defaulted rather than abstract on purpose: ten classes implement
    /// this interface and only two care about multi-select, so the rest inherit
    /// a correct single-file fallback instead of each carrying a throwaway
    /// override.</summary>
    string[] AskOpenFiles(string filter) =>
        AskOpenFile(filter) is { } one ? new[] { one } : Array.Empty<string>();

    /// <summary>Pick a file that may or may not exist yet (the history DB
    /// path). An open-style dialog — never the "replace it?" save prompt.</summary>
    string? AskFilePath(string filter, string suggestedName);
    string? BrowseFolder(string? startAt);

    /// <summary>Ask for one locked item's password, mid-run. Null is a skip:
    /// that item is reported as needing a password and the batch moves on.
    /// Defaulted rather than abstract for the same reason
    /// <see cref="AskOpenFiles"/> is: fourteen classes implement this
    /// interface and only the real one shows a window, so the fakes,
    /// recorders and scripted stubs inherit "skip" instead of each carrying a
    /// throwaway override — and a scenario that never expected a prompt
    /// fails on the needs_password row it produces, not on a missing
    /// method.</summary>
    string? AskPassword(PasswordRequest request) => null;
}
