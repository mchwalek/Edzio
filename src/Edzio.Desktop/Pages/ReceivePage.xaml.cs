using Edzio.Desktop.ViewModels;
namespace Edzio.Desktop.Pages;

public partial class ReceivePage : ContentPage
{
    private readonly ReceiveViewModel _vm;
    private CancellationTokenSource? _cts;

    public ReceivePage(ReceiveViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_vm.IsInstantMode) return; // IncomingTransferCoordinator is already driving this transfer.
        _cts = new CancellationTokenSource();
        _ = _vm.StartAsync(_cts.Token);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cts?.Cancel();
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e) => _vm.OpenFolder();
}
