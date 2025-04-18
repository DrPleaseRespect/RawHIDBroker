using Avalonia.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RawHIDBroker.EventLoop;
using RawHIDBroker.Messaging;
using Serilog;
using System;
using System.Diagnostics;
namespace RawHIDBroker.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private byte hue = 0;
    [ObservableProperty]
    private byte sat = 0;
    [ObservableProperty]
    private byte val = 0;
    [ObservableProperty]
    private byte speed = 0;
    DeviceLoop deviceloop = new DeviceLoop(0x3434, 0x0321);


    public MainViewModel()
    {
        var loggerFactory = new LoggerConfiguration().WriteTo.Debug(outputTemplate: "{Timestamp:HH:MM:ss} [{Level}] ({SourceContext:l}) {Message}{NewLine}{Exception}");
        #if DEBUG
        loggerFactory = loggerFactory.MinimumLevel.Debug();
        #endif
        Log.Logger = loggerFactory.CreateLogger();
        var iloggerfactory = LoggerFactory.Create(LoggerFactory =>
        {
            LoggerFactory.AddSerilog();
        });
        deviceloop.SetLogger(iloggerfactory);
        deviceloop.Start();
    }

    void ChangeRGB()
    {
        byte[] data = new byte[6];
        data[0] = (byte)Hue;
        data[1] = (byte)Sat;
        data[2] = (byte)Val;
        data[3] = (byte)Speed;
        data[4] = 0x00;
        data[5] = 0x00;
        var message = new Message(4, data, 6);

        deviceloop.Write(message);
    }

    partial void OnHueChanged(byte value)
    {
        ChangeRGB();
    }
    partial void OnSatChanged(byte value)
    {
        ChangeRGB();
    }
    partial void OnValChanged(byte value)
    {
        ChangeRGB();
    }
    partial void OnSpeedChanged(byte value)
    {
        ChangeRGB();
    }
}   
