using Edzio.Desktop.ViewModels;

namespace Edzio.Desktop.Pages;

public partial class SendPage : ContentPage
{
    private readonly SendViewModel _vm;

    public SendPage(SendViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    /// <summary>
    /// Handles files dragged from Windows Explorer onto the page. Adds
    /// dropped files (not folders) to the current selection via
    /// <see cref="SendViewModel.AddPaths"/>. Safely no-ops if the native
    /// Windows drag args aren't present.
    /// </summary>
    private async void OnDrop(object? sender, DropEventArgs e)
    {
        var windowsArgs = e.PlatformArgs?.DragEventArgs;
        if (windowsArgs is null) return;

        if (!windowsArgs.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await windowsArgs.DataView.GetStorageItemsAsync();
        var filePaths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path);
        _vm.AddPaths(filePaths);
    }
}
