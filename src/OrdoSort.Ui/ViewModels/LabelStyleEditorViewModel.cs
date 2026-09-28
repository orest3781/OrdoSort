using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Edits one label style for a Settings page: Box Labels' Label
/// style tab and OrdoSort's Box labels tab. Radio pairs bind to the bool
/// properties; the preview binds <see cref="Style"/>.</summary>
public sealed class LabelStyleEditorViewModel : ObservableObject
{
    private readonly BoxLabels.LabelStyle _original;
    private BoxLabels.LabelStyle _style;

    /// <param name="original">The style stored now; <see cref="IsChanged"/>
    /// compares against it.</param>
    public LabelStyleEditorViewModel(BoxLabels.LabelStyle original)
    {
        _original = original.Normalized();
        _style = _original;
    }

    /// <summary>The style as edited so far.</summary>
    public BoxLabels.LabelStyle Style => _style;

    /// <summary>True when OK should write the style back.</summary>
    public bool IsChanged => _style != _original;

    /// <summary>What the preview card shows: a realistic mid-range number.</summary>
    public BoxLabels.Item SampleItem { get; } =
        new("ABCD00004200", new DateTime(2026, 9, 28), new DateTime(2033, 9, 26));

    public bool LayoutStandard
    {
        get => _style.Layout == BoxLabels.LayoutStandard;
        set { if (value) Change(_style with { Layout = BoxLabels.LayoutStandard }); }
    }

    public bool LayoutBig
    {
        get => _style.Layout == BoxLabels.LayoutBig;
        set { if (value) Change(_style with { Layout = BoxLabels.LayoutBig }); }
    }

    public bool LayoutHuge
    {
        get => _style.Layout == BoxLabels.LayoutHuge;
        set { if (value) Change(_style with { Layout = BoxLabels.LayoutHuge }); }
    }

    public bool LeadingZeros
    {
        get => _style.LeadingZeros;
        set => Change(_style with { LeadingZeros = value });
    }

    public bool DatesBars
    {
        get => _style.DateStyle == BoxLabels.DateStyleBars;
        set { if (value) Change(_style with { DateStyle = BoxLabels.DateStyleBars }); }
    }

    public bool DatesPlain
    {
        get => _style.DateStyle == BoxLabels.DateStylePlain;
        set { if (value) Change(_style with { DateStyle = BoxLabels.DateStylePlain }); }
    }

    private void Change(BoxLabels.LabelStyle next)
    {
        if (next == _style) return;
        _style = next;
        foreach (var name in new[]
                 {
                     nameof(Style), nameof(IsChanged), nameof(LayoutStandard), nameof(LayoutBig),
                     nameof(LayoutHuge), nameof(LeadingZeros), nameof(DatesBars), nameof(DatesPlain),
                 })
            Raise(name);
    }
}
