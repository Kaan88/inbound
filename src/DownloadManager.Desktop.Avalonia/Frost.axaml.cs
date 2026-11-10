using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DownloadManager.AvaloniaUi;

/// <summary>Frost.axaml as a compiled class, so it loads under Native AOT (a ResourceInclude by URI does not).</summary>
public sealed partial class FrostTokens : ResourceDictionary
{
    public FrostTokens() => AvaloniaXamlLoader.Load(this);
}
