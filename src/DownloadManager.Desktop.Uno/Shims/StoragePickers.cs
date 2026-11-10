// Windows App SDK 1.8+ pickers (Microsoft.Windows.Storage.Pickers) that Uno does not have, mapped onto the older
// Windows.Storage.Pickers that Uno implements on its Skia desktop heads.
using Microsoft.UI;

namespace Microsoft.Windows.Storage.Pickers;

public enum PickerLocationId { DocumentsLibrary, ComputerFolder, Desktop, Downloads, HomeGroup, MusicLibrary, PicturesLibrary, VideosLibrary, Objects3D, Unspecified }

public enum PickerViewMode { List, Thumbnail }

public sealed record PickFileResult(string Path);

public sealed record PickFolderResult(string Path);

public sealed class FileOpenPicker(WindowId window)
{
    public WindowId Window { get; } = window;
    public PickerViewMode ViewMode { get; set; }
    public PickerLocationId SuggestedStartLocation { get; set; } = PickerLocationId.Unspecified;
    public IList<string> FileTypeFilter { get; } = new List<string>();

    private global::Windows.Storage.Pickers.FileOpenPicker Create()
    {
        var picker = new global::Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Map(SuggestedStartLocation) };
        foreach (var type in FileTypeFilter) picker.FileTypeFilter.Add(type);
        if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
        return picker;
    }

    internal static global::Windows.Storage.Pickers.PickerLocationId Map(PickerLocationId id) =>
        (global::Windows.Storage.Pickers.PickerLocationId)(int)id;

    public async Task<PickFileResult?> PickSingleFileAsync()
    {
        var file = await Create().PickSingleFileAsync();
        return file is null ? null : new PickFileResult(file.Path);
    }

    public async Task<IReadOnlyList<PickFileResult>?> PickMultipleFilesAsync()
    {
        var files = await Create().PickMultipleFilesAsync();
        return files?.Select(file => new PickFileResult(file.Path)).ToList();
    }
}

public sealed class FileSavePicker(WindowId window)
{
    public WindowId Window { get; } = window;
    public PickerLocationId SuggestedStartLocation { get; set; } = PickerLocationId.Unspecified;
    public string SuggestedFileName { get; set; } = "";
    public IDictionary<string, IList<string>> FileTypeChoices { get; } = new Dictionary<string, IList<string>>();

    public async Task<PickFileResult?> PickSaveFileAsync()
    {
        var picker = new global::Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = FileOpenPicker.Map(SuggestedStartLocation),
            SuggestedFileName = SuggestedFileName,
        };
        foreach (var (name, types) in FileTypeChoices) picker.FileTypeChoices.Add(name, types);
        var file = await picker.PickSaveFileAsync();
        return file is null ? null : new PickFileResult(file.Path);
    }
}

public sealed class FolderPicker(WindowId window)
{
    public WindowId Window { get; } = window;
    public PickerViewMode ViewMode { get; set; }
    public PickerLocationId SuggestedStartLocation { get; set; } = PickerLocationId.Unspecified;

    public async Task<PickFolderResult?> PickSingleFolderAsync()
    {
        var picker = new global::Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = FileOpenPicker.Map(SuggestedStartLocation) };
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder is null ? null : new PickFolderResult(folder.Path);
    }
}
