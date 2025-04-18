using Avalonia.Controls;

namespace RawHIDBroker.UI.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        this.DataContext = new ViewModels.MainViewModel();
    }
}
