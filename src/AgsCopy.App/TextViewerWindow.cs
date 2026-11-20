using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgsCopy.App;

/// <summary>Read-only text, used for robocopy logs.</summary>
public sealed class TextViewerWindow : Window
{
    public TextViewerWindow(string title, string text)
    {
        Title = title;
        Width = 1000;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
        };
    }
}
