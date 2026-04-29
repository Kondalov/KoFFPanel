using KoFFPanel.Presentation.Features.Cabinet;
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
        services.AddPresentationServices();
        Services = services.BuildServiceProvider();
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // 1. Инициализация базы данных и оптимизация (WAL, Integrity Check, Migrations)
        using (var scope = Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
            dbContext.InitializeDatabaseOptimization();
        }

        // 2. Запуск фоновых сервисов (Бэкап и Буфер логов)
        var hostedServices = Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(System.Threading.CancellationToken.None);
        }

        var mainWindow = Services.GetRequiredService<CabinetWindow>();
        mainWindow.Show();
    }
}
