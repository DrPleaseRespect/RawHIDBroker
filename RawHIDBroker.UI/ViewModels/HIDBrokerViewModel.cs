using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RawHIDBroker.EventLoop;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RawHIDBroker.UI.ViewModels
{

    public class HexOrIntegerRangeAttribute : ValidationAttribute
    {
        private readonly int _min;
        private readonly int _max;

        public HexOrIntegerRangeAttribute(int min, int max)
        {
            _max = max;
            _min = min;
        }

        public override bool IsValid(object? value)
        {
            if (value == null)
            {
                return false; // Null values are not valid
            }
            if (value is string strValue)
            {
                // Check if the string is a valid hex or decimal number
                if ((strValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
                {
                    // Hexadecimal format
                    if (ushort.TryParse(strValue.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hexValue))
                    {
                        return hexValue >= _min && hexValue <= _max;
                    }
                }
                else
                {
                    // Decimal format
                    if (ushort.TryParse(strValue, out ushort decimalValue))
                    {
                        return decimalValue >= _min && decimalValue <= _max;
                    }
                }
            }
            return false;
        }
    }


    public partial class HIDBrokerViewModel : ObservableValidator, IDisposable
    {
        private DispatcherTimer _reloadTimer;
        public ILogger Logger;


        [ObservableProperty]
        protected HashSet<DeviceInformation> _deviceIDs;

        protected DeviceInformation NewDevice {
            get {
                ushort vid;
                ushort pid;
                if (Vid.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                    vid = ushort.Parse(Vid.Substring(2), System.Globalization.NumberStyles.HexNumber);
                } else
                {
                    vid = ushort.Parse(Vid, System.Globalization.NumberStyles.Integer);
                }
                if (Pid.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    pid = ushort.Parse(Pid.Substring(2), System.Globalization.NumberStyles.HexNumber);
                }
                else
                {
                    pid = ushort.Parse(Pid, System.Globalization.NumberStyles.Integer);
                }
                return new DeviceInformation(vid, pid);
            }
            set
            {
                if (value != null)
                {
                    Vid = "0x" + value.VID.ToString("X4");
                    Pid = "0x" + value.PID.ToString("X4");
                }
            }
        }
            

        [ObservableProperty]
        [NotifyDataErrorInfo]
        [Required(ErrorMessage = "VID is required.")]
        [HexOrIntegerRange(0, 65535, ErrorMessage = "VID must be between 0 and 65535")]
        protected string _vid = "0";

        [ObservableProperty]
        [NotifyDataErrorInfo]
        [Required(ErrorMessage = "PID is required.")]
        [HexOrIntegerRange(0, 65535, ErrorMessage = "PID must be between 0 and 65535")]
        protected string _pid = "0";

        protected ServerLoop server;
        private bool disposedValue;

        public HIDBrokerViewModel(ServerLoop serverloop, ILogger<HIDBrokerViewModel> logger)
        {
            Logger = logger;
            server = serverloop;
            _deviceIDs = server.Devices;
            // Initialize DispatcherTimer to reload device list every 5 seconds
            _reloadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _reloadTimer.Tick += (s, e) =>
            {
                Logger.LogDebug("Reloading device list...");
                // Reload the device list from the server
                DeviceIDs = server.Devices;
            };
        }

        [RelayCommand]
        private async Task AddDevice(DeviceInformation? device = null)
        {
            if (device == null)
            {
                // If no device is specified, use the NewDevice property
                try
                {
                    device = NewDevice;
                }
                catch (FormatException e)
                {
                    // If the format is invalid, do not add the device
                    Logger.LogDebug("Invalid device format: " + e.Message);
                    var dialog = ErrorAsync(e);
                    await dialog.ShowAsync();
                    return;
                }
            }
            if (device != null)
            {
                Logger.LogDebug("Adding device: " + device.ToString());
                // Add the device to the server
                try
                {
                    server.AddDevice(device.ToString());
                }
                catch (HIDDeviceNotFoundException e)
                {
                    var dialog = ErrorAsync(e);
                    await dialog.ShowAsync();
                }
                catch (HIDDeviceAlreadyExistsException e)
                { 
                    var dialog = ErrorAsync(e);
                    await dialog.ShowAsync();
                }
                ;
                // Update the list of device IDs
                DeviceIDs = server.Devices;
            }
        }

        [RelayCommand]
        private void RemoveDevice(DeviceInformation device)
        {
            Logger.LogDebug("Removing device: " + device.ToString());
            if (device != null)
            {

                // Remove the device from the server
                try
                {
                    server.RemoveDevice(device.ToString());
                }
                catch (HIDDeviceNotFoundException e)
                {
                    var dialog = ErrorAsync(e);
                    dialog.ShowAsync();
                };
                // Update the list of device IDs
                DeviceIDs = server.Devices;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        protected ContentDialog ErrorAsync(Exception exc, string title="An Exception Occurred!")
        {
            // Create ContentDialog
            var dialog = new ContentDialog
            {
                Title = title,
                Content = exc.Message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            return dialog;
        }

        ~HIDBrokerViewModel()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
