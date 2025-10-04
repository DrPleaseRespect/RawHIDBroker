using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RawHIDBroker.EventLoop;
using RawHIDBroker.UI.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RawHIDBroker.UI.ViewModels
{
    public partial class ApplicationViewModel : ObservableObject
    {
        private MainWindow? _mainWindow;
        private HIDBrokerServer _server;
        private IApplicationLifetime _lifetime;
        private bool _opened = false;
        private ILogger _logger;
        private IServiceProvider Services;

        public ApplicationViewModel(IServiceProvider services)
        {
            Services = services;
            _server = services.GetRequiredService<HIDBrokerServer>();
            _lifetime = services.GetRequiredService<IApplicationLifetime>();
            _logger = services.GetRequiredService<ILogger<ApplicationViewModel>>();
        }

        private void MainWindowClosed(object? sender, EventArgs e)
        {
            if (_mainWindow != null)
            {
                _mainWindow.Closed -= MainWindowClosed; // Unsubscribe from the event
                _mainWindow = null; // Clear the reference when the window is closed
                _opened = false; // Reset the opened state
                GC.Collect(); // Force garbage collection to clean up resources
            }
        }

        [RelayCommand]
        public void ShowMainWindow()
        {
            if (_mainWindow == null && _opened == false)
            {
                _opened = true;
                _mainWindow = Services.GetRequiredService<MainWindow>();
                _mainWindow.Closed += MainWindowClosed;
            }
            if (_mainWindow == null)
            {
                return; // If the main window is still null, do nothing
            }
            _mainWindow.Show();
        }

        [RelayCommand]
        public void ExitApplication()
        {
            if (_mainWindow != null)
            {
                _mainWindow.Close();
            }

            _server.Dispose();
            if (_lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
            }
            else if (_lifetime is ISingleViewApplicationLifetime singleView)
            {
                Environment.Exit(0);
            }
            else
            {
                Environment.Exit(0);
            }

        }
    }
}
