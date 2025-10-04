using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RawHIDBroker.UI.ViewModels;
using RawHIDBroker.UI.ViewModels.DesignData;
using System;

namespace RawHIDBroker.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow(HIDBrokerViewModel hidvm)
    {
        InitializeComponent();
        var _hidBroker = hidvm;
        HIDBroker.DataContext = _hidBroker;
    }
    public MainWindow()
    {
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            Design.SetDataContext(this, DesignHIDBrokerViewModel.Create());
            return;
        }
        else
        {
            throw new InvalidOperationException("MainWindow must be initialized with a valid DesignHIDBrokerViewModel instance.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
        DataContext = null;
        if (HIDBroker.DataContext is IDisposable hidDisposable)
        {
            hidDisposable.Dispose();
        }
        HIDBroker.DataContext = null;
    }
}