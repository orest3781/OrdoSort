using BoxLabelsApp.Services;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The first thing anyone ever sees in BoxLabels.exe is the prompt
/// explaining what the shared box-labels file is. It shipped through Warn, so
/// it arrived with a warning triangle — telling a brand-new user something was
/// wrong before they had done anything at all.
///
/// The icon is a decision now, not a side effect of which method was handy, so
/// it gets a test.</summary>
public class BoxLabelsAppPromptTests
{
    [Fact]
    public void TheFirstRunExplanationIsInformation_NotAWarning()
    {
        var (message, kind) = LabelsFilePrompts.PromptFor(LabelsFilePrompt.FirstRun, "");

        Assert.Equal(MessageKind.Info, kind);
        Assert.Contains("box-labels.json", message);
    }

    /// <summary>The unreachable case genuinely is a warning, and must name the
    /// path — "it couldn't be reached" without saying what "it" was leaves the
    /// user nothing to reconnect.</summary>
    [Fact]
    public void AnUnreachableStoreIsAWarningAndNamesThePath()
    {
        var (message, kind) = LabelsFilePrompts.PromptFor(
            LabelsFilePrompt.Unreachable, @"\\dead\share\box-labels.json");

        Assert.Equal(MessageKind.Warning, kind);
        Assert.Contains(@"\\dead\share\box-labels.json", message);
    }

    /// <summary>None means "open it and go". Asking for its message is a bug in
    /// the caller, so it throws rather than returning something plausible that
    /// would be shown to a user for no reason.</summary>
    [Fact]
    public void ThereIsNoMessageWhenThereIsNothingToAsk()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LabelsFilePrompts.PromptFor(LabelsFilePrompt.None, ""));
    }
}
