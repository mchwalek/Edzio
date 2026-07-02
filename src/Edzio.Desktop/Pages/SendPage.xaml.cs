using Edzio.Desktop.ViewModels;
namespace Edzio.Desktop.Pages;

public partial class SendPage : ContentPage
{
    public SendPage(SendViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
