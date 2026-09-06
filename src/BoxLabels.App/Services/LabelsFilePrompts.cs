using OrdoSort.Wpf.Windows;

namespace BoxLabelsApp.Services;

/// <summary>The wording and iconography for each
/// <see cref="LabelsFilePrompt"/>.
///
/// Separate from <see cref="LabelsFileSettings"/> so that stays free of any UI
/// reference, and a pure function rather than two inline dialog calls so the
/// choice of icon is testable. It was not, and the first-run explanation
/// shipped wearing a warning triangle: the first thing a new user ever saw
/// said something was wrong before they had done anything.</summary>
internal static class LabelsFilePrompts
{
    /// <summary>Message and icon for a prompt. Never called with
    /// <see cref="LabelsFilePrompt.None"/> — there is nothing to say when
    /// there is nothing to ask.</summary>
    public static (string Message, MessageKind Kind) PromptFor(LabelsFilePrompt prompt, string path) =>
        prompt switch
        {
            LabelsFilePrompt.FirstRun => (
                "Box Labels needs to know where the shared box labels file is.\n\n" +
                "This is the file every station prints from — it holds the client list and the " +
                "running box number, so they all stay in step. It is usually on a shared drive " +
                "and is named box-labels.json.\n\n" +
                "Choose it on the next screen.",
                MessageKind.Info),

            LabelsFilePrompt.Unreachable => (
                $"The box labels file couldn't be reached:\n\n{path}\n\n" +
                "The drive or shared folder it lives on may be disconnected. Reconnect it and " +
                "start Box Labels again, or choose the file's new location.\n\n" +
                "Nothing has been changed.",
                MessageKind.Warning),

            _ => throw new ArgumentOutOfRangeException(nameof(prompt), prompt,
                "None has no message — the caller should not be prompting."),
        };
}
