using System.ComponentModel;
using System.Windows;
using BoxLabelsApp.Services;
using OrdoSort.Wpf.Windows;

namespace BoxLabelsApp.Windows;

/// <summary>Box Labels' Settings: General (theme, text size and font, the
/// labels file) and Label style. The window only edits; the app saves what
/// OK accepted (<see cref="BoxLabelsSettingsSaver"/>) and applies it.</summary>
public partial class BoxLabelsSettingsWindow : Window
{
    private readonly BoxLabelsSettingsViewModel _vm;

    private readonly Func<bool> _confirmDiscard;

    /// <param name="pickLabelsFile">The app's own file picker and checks; null
    /// when the person cancelled it.</param>
    /// <param name="confirmDiscard">Asks whether to throw unsaved edits away
    /// on close; true discards. Defaults to the app's Discard / Keep editing
    /// question, as OrdoSort's Settings asks.</param>
    public BoxLabelsSettingsWindow(BoxLabelsSettingsViewModel vm, Func<string?> pickLabelsFile,
        Func<bool>? confirmDiscard = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _confirmDiscard = confirmDiscard ?? (() => MessageWindow.Confirm(this,
            "Discard the changes you made in Settings?", Title, "Discard", "Keep editing"));
        Closing += OnClosing;
        ChangeFileButton.Click += (_, _) =>
        {
            if (pickLabelsFile() is { } picked) vm.LabelsFile = picked;
        };
    }

    /// <summary>What OK last refused over, for tests.</summary>
    internal string ProblemText => ProblemLine.Text;

    /// <summary>OK's check: refuses while anything is wrong, saying what.</summary>
    internal bool TryAccept()
    {
        var problems = _vm.Problems();
        ProblemLine.Text = string.Join("\n", problems);
        return problems.Count == 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryAccept()) DialogResult = true;
    }

    /// <summary>Cancel, Esc or the title-bar X with unsaved edits asks first.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DialogResult == true || !_vm.IsChanged) return;
        if (!_confirmDiscard()) e.Cancel = true;
    }
}
