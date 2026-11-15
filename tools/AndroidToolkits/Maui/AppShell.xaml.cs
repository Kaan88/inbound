using Microsoft.Maui.Controls;

namespace AndroidTrial.MauiTk;

public partial class AppShell : Shell
{
    public AppShell() => InitializeComponent();
}

public sealed class PlaceholderPage : ContentPage
{
    public PlaceholderPage() => Content = new Label { Text = "Not in this trial", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
}
