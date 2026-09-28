using System.Windows.Input;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One destination button on the Processing screen: label with
/// suffix + hotkey, config color with a WCAG-picked foreground, disabled with
/// a readable reason when the destination is unusable.</summary>
public sealed class RouteButtonViewModel : ObservableObject
{
    /// <summary>True on the one button Enter would press right now — the
    /// last-used route, starting at the first, or always the first, per
    /// enter_commits — shown as a ⏎ badge.</summary>
    private bool _isEnterTarget;
    public bool IsEnterTarget { get => _isEnterTarget; internal set => Set(ref _isEnterTarget, value); }

    /// <summary>True on the route the user pressed most recently — the lit
    /// trail rule. Unlike <see cref="IsEnterTarget"/> this does not depend on
    /// enter_commits. It is not undone by a later Undo: it tracks the last
    /// button press, not where a document currently sits, and Enter's target
    /// on the keystroke path relies on that staying true.</summary>
    private bool _isLastUsed;
    public bool IsLastUsed { get => _isLastUsed; internal set => Set(ref _isLastUsed, value); }

    public int Index { get; }

    /// <summary>The destination this button files into, captured when the
    /// session started. Filing goes through THIS, never a fresh index lookup
    /// in the live config: a mid-session save (Match &amp; merge, a merge-type
    /// toggle) re-reads the shared destinations from disk, and a peer that
    /// reordered or removed them must not turn the "Tax" button into a
    /// different folder.</summary>
    public Route Route { get; }

    public string Label { get; }
    public bool Enabled { get; }
    public string? DisabledReason { get; }
    public Rgb Back { get; }
    public Rgb Fore { get; }
    public KeyGesture? Gesture { get; }

    /// <param name="problem">Result of <see cref="Config.ValidateRoute"/> for
    /// this route, gathered off the UI thread — the probe touches the
    /// destination folder, a network round trip on SMB shares.</param>
    public RouteButtonViewModel(int index, Route route, ThemePalette palette, string problem)
    {
        Index = index;
        Route = route;

        Gesture = GestureFor(index, route);
        var gestureText = Gesture is null ? "" : HotkeyParser.Display(Gesture);

        Enabled = problem.Length == 0;
        DisabledReason = Enabled ? null : problem;

        Label = route.Label
            + (route.AppendSuffix && route.Suffix.Length > 0 ? $"   ·   {route.Suffix}" : "")
            + (gestureText.Length > 0 ? $"   ·   {gestureText}" : "")
            + (Enabled ? "" : "   (unavailable)");

        var back = ThemePalette.ParseColor(route.Color);
        Back = back ?? palette.Surface;
        Fore = back is { } b ? ThemePalette.IdealForeground(b) : palette.Text;
    }

    /// <summary>The key a destination's button answers to: its configured
    /// hotkey when that parses, else the classic Ctrl+1-9 by position.</summary>
    public static KeyGesture? GestureFor(int index, Route route) =>
        HotkeyParser.ToGesture(route.Hotkey)
        ?? (index < 9 ? new KeyGesture(Key.D1 + index, ModifierKeys.Control) : null);

    /// <summary>For each destination, "" or why it can't have its key:
    /// another destination answers to the same one (Q2-35). Settings refuses
    /// this, but a hand-edited config.json can still hold it, and a key bound
    /// twice files to whichever binding wins, silently. Both are refused so
    /// the key does nothing until one is changed.</summary>
    public static IReadOnlyList<string> KeyClashes(IReadOnlyList<Route> routes)
    {
        var problems = new string[routes.Count];
        var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < routes.Count; i++)
        {
            problems[i] = "";
            if (GestureFor(i, routes[i]) is not { } gesture) continue;
            var key = HotkeyParser.Display(gesture);
            if (!byKey.TryGetValue(key, out var indexes)) byKey[key] = indexes = new List<int>();
            indexes.Add(i);
        }
        foreach (var (key, indexes) in byKey)
        {
            if (indexes.Count < 2) continue;
            foreach (var i in indexes)
            {
                var others = string.Join(", ", indexes.Where(j => j != i).Select(j => $"\"{routes[j].Label}\""));
                problems[i] = $"{key} is also the key for {others}. Change one in Settings.";
            }
        }
        return problems;
    }
}
