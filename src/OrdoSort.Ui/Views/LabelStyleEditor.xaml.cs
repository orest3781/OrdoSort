using System.Windows.Controls;

namespace OrdoSort.Wpf.Views;

/// <summary>The label style editor: layout, leading zeros, date lines and a
/// live preview. Its DataContext is a
/// <see cref="ViewModels.LabelStyleEditorViewModel"/>.</summary>
public partial class LabelStyleEditor : UserControl
{
    public LabelStyleEditor() => InitializeComponent();
}
