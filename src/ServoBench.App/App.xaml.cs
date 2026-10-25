using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Extensions.DependencyInjection;
using ServoBench.Presentation;

namespace ServoBench.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last line of defence: report anything unexpected instead of letting the window vanish.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ServoBench", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // WPF formats bindings as en-US unless told otherwise; follow the user's regional settings.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        var services = new ServiceCollection();
        services.AddSingleton<IDriveClientFactory, ModbusDriveClientFactory>();
        services.AddSingleton<IProfileFileService, WpfProfileFileService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<IDriveClientFactory>(),
            sp.GetRequiredService<IProfileFileService>(),
            sp.GetRequiredService<TimeProvider>(),
            CultureInfo.CurrentCulture));
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            // Dispose off the UI thread: awaiting the socket shutdown on the dispatcher while
            // blocking it would deadlock. Give it a moment, then let the process end regardless.
            var services = _services;
            Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
        }

        base.OnExit(e);
    }
}
