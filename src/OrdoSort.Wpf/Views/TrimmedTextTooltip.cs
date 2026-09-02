using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrdoSort.Wpf.Views;

/// <summary>Attached behaviour (table-rules, Rule 4): gives a TextBlock a
/// ToolTip of its own full <see cref="TextBlock.Text"/>, but only once
/// <c>TextTrimming="CharacterEllipsis"</c> has actually trimmed it visible —
/// a tooltip that repeats fully-visible text is noise, and the owner
/// explicitly rejected exactly that once already (requirements.md: "a
/// tooltip that repeats fully-visible text is noise"). Wired app-wide
/// through Theme/Styles.xaml's <c>GridCellText</c> Style Setter
/// (<c>views:TrimmedTextTooltip.Enabled="True"</c>) rather than per column,
/// the same "SHARED, not per-window" rule every other piece of this feature
/// follows. A column that needs something OTHER than its own text in the
/// tooltip — ZipToolsWindow/MergePdfsWindow/PageCountsWindow's Item/File,
/// whose ToolTip shows the row's full PATH, not a repeat of the trimmed
/// filename, and FilenameListWindow's Pages column, whose ToolTip shows the
/// count failure's reason — opts back out with its own, more-derived
/// <c>Enabled="False"</c> Setter; this class then never touches that
/// TextBlock's ToolTip at all (see <see cref="OnEnabledChanged"/>), so its
/// own Setter keeps working exactly as it did before this class existed —
/// ordinary WPF Style precedence, no runtime detection needed.
///
/// VERIFIED, NOT ASSUMED. WPF's TextBlock carries no IsTextTrimmed property
/// at all — confirmed absent, byte for byte, from this app's own
/// net8.0-windows build's PresentationFramework.dll REFERENCE ASSEMBLY
/// (Microsoft.WindowsDesktop.App.Ref\8.0.27\ref\net8.0) before writing this
/// class: a raw scan of that file's bytes for the ASCII string
/// "IsTextTrimmed" found nothing. That name exists only on an unrelated
/// type of the same name — WinUI/UWP's Windows.UI.Xaml.Controls.TextBlock —
/// which this app, built against net8.0-windows WPF, does not use.
/// Trimmed-ness is instead measured the same way
/// <see cref="DataGridColumnCap"/> already measures a cell's natural
/// content width: FormattedText against the element's own font, compared
/// here to the element's rendered ActualWidth rather than to a cap.
///
/// SizeChanged, not a Binding to ActualWidth: FrameworkElement.ActualWidth
/// changes do not reliably drive a WPF Binding refresh — the same reason
/// DataGridColumnCap itself recomputes off SizeChanged/LayoutUpdated rather
/// than a width Binding (see that class's own doc comment). A DataGrid's
/// row virtualization can also hand a RECYCLED TextBlock new bound Text at
/// the SAME ActualWidth as the row it used to display — no resize, so no
/// SizeChanged — which is why Text is watched separately, through a
/// DependencyPropertyDescriptor, rather than trusting SizeChanged alone to
/// catch every case that can change what "trimmed" means for a cell.
///
/// LOADED/UNLOADED, NOT A PERMANENT HOOK (fix round 1, the HIGH this class
/// shipped with): <see cref="DependencyPropertyDescriptor.AddValueChanged"/>
/// stores its per-object handler in a table the STATIC descriptor instance
/// holds for the life of the process — a strong reference from that
/// process-lifetime static straight through to every TextBlock ever handed
/// to it, and the string each one is bound to, unless something calls
/// <see cref="DependencyPropertyDescriptor.RemoveValueChanged"/> back. With
/// <c>Enabled=True</c> arriving via GridCellText's own Setter, that is
/// effectively every cell in every grid window in the app: open and close a
/// tool window over a working day and the heap would grow monotonically,
/// every realized cell TextBlock ever shown still pinned by a table nothing
/// ever unhooks. Hooking/unhooking both watches (SizeChanged too, for the
/// same reason) from Loaded/Unloaded instead — rather than once, for good,
/// from OnEnabledChanged — means a TextBlock is only ever watched while it
/// is actually in the visual tree: a DataGrid recycling a virtualized
/// container unloads it while off-screen (freeing both watches) and reloads
/// it once reused (re-establishing them, and refreshing immediately — see
/// <see cref="Hook"/> — since the recycled container may already carry its
/// final text and size by the time Loaded fires), and a closed window
/// unloads its whole tree the same way.</summary>
internal static class TrimmedTextTooltip
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(TrimmedTextTooltip),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(TextBlock element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(TextBlock element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>Resolved once and shared by every attached TextBlock —
    /// DependencyPropertyDescriptor.FromProperty already returns the SAME
    /// cached instance for the same (property, owner type) pair on every
    /// call, so this is a convenience, not a distinct cache of its own.</summary>
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>Fires only on a genuine value CHANGE — WPF's own
    /// DependencyProperty machinery never invokes this for a Style Setter
    /// whose value matches the property's existing effective value, which is
    /// exactly what lets an opt-out column's own <c>Enabled="False"</c>
    /// Setter (equal to this property's <c>false</c> default) stay a true
    /// no-op: this callback never runs for it at all, so this class never
    /// wires anything to that TextBlock and never touches its ToolTip.
    ///
    /// Subscribes to Loaded/Unloaded here, not SizeChanged/TextDescriptor
    /// directly — see the class doc's own LOADED/UNLOADED paragraph for why.
    /// The immediate <c>if (text.IsLoaded)</c> hook covers the one case
    /// Loaded itself cannot: Enabled becoming True on an element already in
    /// the tree (never happens from a Style Setter, which resolves before
    /// Loaded fires, but this attached property's own contract does not
    /// assume that of every future caller).</summary>
    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        if ((bool)e.NewValue)
        {
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
            if (text.IsLoaded) Hook(text);
        }
        else
        {
            // Dead in this app today — every consumer sets Enabled once,
            // from a Style, and a DataGridColumn.ElementStyle never changes
            // after the column is built, so a live TextBlock never actually
            // sees a True-to-False transition. Kept anyway so the attached
            // property's own contract is correct on its own terms, not only
            // for how this app happens to use it today.
            text.Loaded -= OnLoaded;
            text.Unloaded -= OnUnloaded;
            Unhook(text);
            text.ClearValue(FrameworkElement.ToolTipProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Hook((TextBlock)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Unhook((TextBlock)sender);

    /// <summary>Unhooks first, unconditionally: WPF can raise Loaded more
    /// than once for the same element without an intervening Unloaded in
    /// some reparenting scenarios, and both SizeChanged's <c>+=</c> and
    /// AddValueChanged would otherwise double-subscribe, firing Refresh
    /// twice per real change — not a leak on its own (Unloaded still cleans
    /// up whatever is currently subscribed), but a needless extra
    /// measurement per cell per pass this idempotent order avoids for free.
    /// Refreshes immediately rather than waiting for the first SizeChanged/
    /// TextChanged: see the class doc's own recycled-container paragraph
    /// for why a freshly re-hooked cell cannot simply wait.</summary>
    private static void Hook(TextBlock text)
    {
        Unhook(text);
        text.SizeChanged += Refresh;
        TextDescriptor.AddValueChanged(text, Refresh);
        Refresh(text, EventArgs.Empty);
    }

    /// <summary>Safe to call on a TextBlock that is not currently hooked —
    /// both <c>-=</c> on an unsubscribed event and RemoveValueChanged for an
    /// unregistered handler are documented no-ops — which is what lets
    /// <see cref="Hook"/> call this unconditionally before it (re-)subscribes,
    /// and what lets OnUnloaded fire this safely even for an element that
    /// somehow unloads without ever having loaded.</summary>
    private static void Unhook(TextBlock text)
    {
        text.SizeChanged -= Refresh;
        TextDescriptor.RemoveValueChanged(text, Refresh);
    }

    private static void Refresh(object? sender, EventArgs e)
    {
        var text = (TextBlock)sender!;
        text.ToolTip = IsTrimmed(text) ? text.Text : null;
    }

    /// <summary>The same measurement <see cref="DataGridColumnCap"/>'s own
    /// ContentWidths.TextWidthOf performs, for the same reason (no
    /// IsTextTrimmed to read) — applied here to the LIVE rendered element's
    /// ActualWidth rather than to a cap this class has no access to.</summary>
    private static bool IsTrimmed(TextBlock text)
    {
        if (string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0) return false;
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var formatted = new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection, typeface, text.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        // ActualWidth is net of this element's own Margin (a layout-slot
        // concept the element itself has no opinion on) but still INCLUDES
        // its own Padding, which FormattedText's measurement knows nothing
        // about — netting it out here is the mirror image of
        // ContentWidths.TextWidthOf ADDING Padding to a raw measurement to
        // get a cell's wanted width, so both halves of this feature agree
        // on what "the text's own width" means.
        var available = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        return formatted.WidthIncludingTrailingWhitespace > available + 0.5;
    }
}
