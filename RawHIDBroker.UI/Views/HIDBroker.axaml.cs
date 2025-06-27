using Avalonia.Controls;
using RawHIDBroker.UI.ViewModels.DesignData;

namespace RawHIDBroker.UI.Views;

public partial class HIDBroker : UserControl
{
    public HIDBroker()
    {
        if (Design.IsDesignMode)
        {
            Design.SetDataContext(this, DesignHIDBrokerViewModel.Create());
        }
        InitializeComponent();
    }
}
