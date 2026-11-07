using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// File and folder pickers from the Windows App SDK. The older <c>Windows.Storage.Pickers</c> ones fail to open in an
/// app running as administrator, which is what "The file picker couldn't open" was.
/// </summary>
internal static class Pickers
{
    /// <summary>The chosen files' paths, none when cancelled.</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(IntPtr window, IEnumerable<string> types, bool multiple, PickerLocationId start = PickerLocationId.Downloads)
    {
        var picker = new FileOpenPicker(Win32Interop.GetWindowIdFromWindow(window))
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = start,
        };
        foreach (var type in types) picker.FileTypeFilter.Add(type);
        if (multiple)
        {
            var results = await picker.PickMultipleFilesAsync();
            return results is null ? [] : results.Select(result => result.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        }

        var one = await picker.PickSingleFileAsync();
        return one is null || string.IsNullOrWhiteSpace(one.Path) ? [] : [one.Path];
    }

    /// <summary>The chosen folder's path, null when cancelled.</summary>
    public static async Task<string?> PickFolderAsync(IntPtr window, bool startInDownloads = true)
    {
        var picker = new FolderPicker(Win32Interop.GetWindowIdFromWindow(window)) { ViewMode = PickerViewMode.List };
        if (startInDownloads) picker.SuggestedStartLocation = PickerLocationId.Downloads;
        var folder = await picker.PickSingleFolderAsync();
        return string.IsNullOrWhiteSpace(folder?.Path) ? null : folder.Path;
    }
}
