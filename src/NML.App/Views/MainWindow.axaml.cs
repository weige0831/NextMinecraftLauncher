using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using NML.App.ViewModels;

namespace NML.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Multi-value converter bound to a nav item: receives <c>[shell.CurrentPage, item]</c> and
/// returns true when the item is the active page — drives both the selection pill and the
/// vertical accent indicator.
/// </summary>
public class NavActiveClassConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is { Count: >= 2 })
        {
            return ReferenceEquals(values[0], values[1]);
        }
        return false;
    }
}
