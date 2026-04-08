using KoFFPanel.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;

namespace KoFFPanel.Presentation;

public partial class App : System.Windows.Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        var services = new ServiceCollection();

        // Магия чистого кода: подключаем все зависимости одной строкой!
        services.AddPresentationServices();

        Services = services.BuildServiceProvider();
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var mainWindow = Services.GetRequiredService<CabinetWindow>();
        mainWindow.Show();
    }
}