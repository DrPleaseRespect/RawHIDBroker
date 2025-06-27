using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using RawHIDBroker.EventLoop;
using RawHIDBroker.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using RawHIDBroker.UI.Views;

namespace RawHIDBroker.UI;

public partial class App : Application
{
    

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Line below is needed to remove Avalonia data validation.
        // Without this line you will get duplicate validations from both Avalonia and CT
        BindingPlugins.DataValidators.RemoveAt(0);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug().
            CreateLogger();
        ServiceCollection services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSerilog();
        });




        // Register the server loop in the service collection
        services.AddSingleton<ServerLoop>();
        services.AddSingleton<ApplicationViewModel>();
        services.AddTransient<MainWindow>();
        services.AddTransient<HIDBrokerViewModel>();



        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            services.AddSingleton<IApplicationLifetime>(desktop);
            ServiceProvider serviceProvider = services.BuildServiceProvider();

            serviceProvider.GetRequiredService<ServerLoop>().Start();

            DataContext = serviceProvider.GetRequiredService<ApplicationViewModel>();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime).Exit += (s, e) =>
            {
                var server = serviceProvider.GetRequiredService<ServerLoop>();
                server.Stop();
                server.Dispose();
            };

        }

        base.OnFrameworkInitializationCompleted();
    }

    
}
