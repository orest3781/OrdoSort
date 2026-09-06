namespace OrdoSort.Wpf.Services;

/// <summary>The modals the box-label maker actually opens.
///
/// It is a separate, smaller interface from OrdoSort's <c>IDialogService</c>
/// because BoxLabels.exe has to satisfy it, and OrdoSort's version cannot
/// travel: it also promises a password prompt and a batch-date prompt, both
/// of which are windows that stay in OrdoSort. Asking a standalone label
/// printer to implement ten members so it can call three was the alternative.
///
/// <c>IDialogService</c> extends this, so OrdoSort's existing DialogService
/// and every test double already satisfy it with no change.</summary>
public interface ILabelDialogs
{
    void Warn(string message, string title);

    /// <summary>Something the user needs to read, but nothing is wrong.
    ///
    /// Here rather than only on OrdoSort's IDialogService because the
    /// standalone's very first screen is an explanation of what the shared
    /// box-labels file is — and shown through Warn, it greeted a brand-new
    /// user with a warning triangle before they had done anything at
    /// all.</summary>
    void Info(string message, string title);

    bool Confirm(string message, string title);

    /// <summary>A question whose buttons say what they DO — "Remove"/"Keep"
    /// rather than "Yes"/"No". Every question this app asks has a destructive
    /// answer, and a generic Yes forces the user to re-read the sentence to
    /// work out which way round it is.
    ///
    /// Defaulted rather than abstract because only the implementations that
    /// render real buttons care about the labels; the fakes, recorders and
    /// scripted stubs inherit a correct fallback instead of each carrying a
    /// throwaway override. The fallback deliberately drops the labels — a
    /// recording double cares which question was asked, not what the buttons
    /// said.</summary>
    bool Confirm(string message, string title, string yesLabel, string noLabel) =>
        Confirm(message, title);

    string? AskSaveFile(string filter, string suggestedName);
}
