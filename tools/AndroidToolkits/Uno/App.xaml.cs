using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AndroidTrial.UnoTk;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var o = Droid.MainActivity.Options;
        var s = o.Dynamic && Droid.MainActivity.Current is { } act ? Scheme.Dynamic(act, dark) : Scheme.Own(dark);
        static Color C(string hex) => Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
        void B(string k, string hex) => Resources[k] = new SolidColorBrush(C(hex));
        B("Bg", s.Bg); B("Card", s.Card); B("Text", s.Text); B("Muted", s.Muted); B("Accent", s.Accent); B("OnAccent", s.OnAccent); B("Track", s.Track);
        B("Done", s.Done); B("Failed", s.Failed); B("Waiting", s.Waiting); B("Container", s.Container);

        MainWindow = new Window();
        var root = new MainPage();
        MainWindow.Content = root;
        MainWindow.Activate();
    }
}
