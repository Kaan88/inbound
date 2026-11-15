using Avalonia;
using Application = Avalonia.Application;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace AndroidTrial.AvaloniaTk;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = new MainView();
        ActualThemeVariantChanged += (_, _) => Apply();
        Apply();
        base.OnFrameworkInitializationCompleted();
    }

    public void Apply()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var o = MainActivity.Options;
        var s = o.Dynamic && MainActivity.Current is { } act ? Scheme.Dynamic(act, dark) : Scheme.Own(dark);
        void B(string k, string hex) => Resources[k] = new SolidColorBrush(Color.Parse(hex));
        B("Bg", s.Bg); B("Card", s.Card); B("Text", s.Text); B("Muted", s.Muted); B("Accent", s.Accent); B("OnAccent", s.OnAccent); B("Track", s.Track);
        B("Done", s.Done); B("Failed", s.Failed); B("Waiting", s.Waiting); B("Container", s.Container);
    }
}
