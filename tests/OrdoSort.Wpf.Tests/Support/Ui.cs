using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrdoSort.TestSupport;

/// <summary>Walking and settling real WPF windows in tests: the helpers each
/// test file used to copy for itself. Every pump here has a ceiling, so a
/// window that never settles fails the test instead of hanging the run
/// (docs/testing.md).</summary>
public static class Ui
{
    /// <summary>How long a pump may run before the test fails.</summary>
    public static readonly TimeSpan PumpCeiling = TimeSpan.FromSeconds(30);

    /// <summary>Every element under <paramref name="root"/> in the visual
    /// tree, depth first.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Every <typeparamref name="T"/> under <paramref name="root"/>
    /// in the visual tree, depth first.</summary>
    public static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject =>
        Descendants(root).OfType<T>().ToList();

    /// <summary>Same as <see cref="Descendants{T}"/>; the name several suites use.</summary>
    public static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root);

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/>,
    /// depth first, or null.</summary>
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject =>
        Descendants(root).OfType<T>().FirstOrDefault();

    /// <summary>Sets UIElement.IsMouseOver, which is read-only and normally
    /// flipped only by a real mouse, so a hover style can be tested.</summary>
    public static void ForceMouseOver(UIElement element, bool value)
    {
        var field = typeof(UIElement).GetField("IsMouseOverPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UIElement has no private static IsMouseOverPropertyKey field");
        element.SetValue((DependencyPropertyKey)field.GetValue(null)!, value);
    }

    /// <summary>Every <typeparamref name="T"/> under <paramref name="root"/> in
    /// the logical tree (works before a window is shown or templated).</summary>
    public static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit) yield return hit;
            foreach (var deeper in LogicalDescendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>Runs queued dispatcher work down to <paramref name="priority"/>
    /// (Loaded runs layout and Loaded handlers; Background also runs bindings
    /// and ordinary posted work). Bounded by <see cref="PumpCeiling"/>.</summary>
    public static void Pump(DispatcherPriority priority = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        var reached = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() =>
        {
            reached = true;
            frame.Continue = false;
        }));
        var ceiling = new DispatcherTimer(DispatcherPriority.Send) { Interval = PumpCeiling };
        ceiling.Tick += (_, _) => frame.Continue = false;
        ceiling.Start();
        Dispatcher.PushFrame(frame);
        ceiling.Stop();
        if (!reached)
            Assert.Fail($"the UI thread never reached {priority} priority within {PumpCeiling.TotalSeconds}s");
    }

    /// <summary>Layout and Loaded handlers run; what the copies called
    /// PumpRender.</summary>
    public static void PumpRender() => Pump(DispatcherPriority.Loaded);

    /// <summary>A window (or element) after <c>Show()</c>: laid out, its
    /// Loaded handlers run and its posted work done, so a test drives what a
    /// person would see.</summary>
    public static void Settle(UIElement element)
    {
        element.UpdateLayout();
        Pump(DispatcherPriority.Background);
        element.UpdateLayout();
    }

    /// <summary>Pumps the dispatcher until <paramref name="done"/> is true,
    /// failing with <paramref name="because"/> after <see cref="PumpCeiling"/>
    /// (or <paramref name="ceiling"/>).</summary>
    public static void PumpUntil(Func<bool> done, string because, TimeSpan? ceiling = null)
    {
        var limit = ceiling ?? PumpCeiling;
        var deadline = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"never happened within {limit.TotalSeconds}s: {because}");
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>Pumps until <paramref name="task"/> completes, then surfaces
    /// its exception if it failed. For awaiting UI-thread work from a test
    /// running on the UI thread.</summary>
    public static void PumpUntilComplete(Task task, TimeSpan? ceiling = null)
    {
        PumpUntil(() => task.IsCompleted, "the awaited UI work should finish", ceiling);
        task.GetAwaiter().GetResult();
    }
}
