namespace OrdoSort.Wpf.Services;

/// <summary>Every modal the app can show, behind an interface so view models
/// stay testable and the smoke harness can record instead of block.</summary>
public interface IDialogService
{
    void Warn(string message, string title);
    void Info(string message, string title);
    bool Confirm(string message, string title);
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
}
