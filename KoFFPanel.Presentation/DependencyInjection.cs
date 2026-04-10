using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using KoFFPanel.Presentation.Services;
using KoFFPanel.Presentation.ViewModels;
using KoFFPanel.Presentation.Views;
using KoFFPanel.Presentation.Views.Pages; // Подключаем папку с будущими страницами
using Microsoft.Extensions.DependencyInjection;

namespace KoFFPanel.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        // 1. Инфраструктура
        services.AddSingleton<IAppLogger, AppLogger>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddTransient<ISshService, SshService>();
        services.AddTransient<IServerMonitorService, ServerMonitorService>();
        services.AddTransient<IXrayCoreService, XrayCoreService>();
        services.AddTransient<DeployWizardViewModel>();
        services.AddTransient<DeployWizardWindow>();
        services.AddHttpClient<IGitHubReleaseService, GitHubReleaseService>();
        services.AddTransient<ICoreDeploymentService, CoreDeploymentService>();
        services.AddTransient<IXrayConfiguratorService, XrayConfiguratorService>();
        services.AddTransient<IXrayUserManagerService, XrayUserManagerService>();
        services.AddTransient<ViewModels.AddClientViewModel>();
        services.AddTransient<Views.AddClientWindow>();
        services.AddDbContext<Infrastructure.Data.AppDbContext>(ServiceLifetime.Transient);
        services.AddTransient<ViewModels.TerminalViewModel>();
        services.AddTransient<Views.TerminalWindow>();
        services.AddTransient<IDatabaseBackupService, DatabaseBackupService>();
        services.AddTransient<ISubscriptionService, SubscriptionService>();
        services.AddTransient<ClientConfigViewModel>();
        services.AddTransient<ClientConfigWindow>();
        services.AddTransient<ISingBoxConfiguratorService, SingBoxConfiguratorService>();
        services.AddTransient<ISingBoxUserManagerService, SingBoxUserManagerService>();

        // РЕГИСТРАЦИЯ АНАЛИТИКИ
        services.AddSingleton<IClientAnalyticsService, ClientAnalyticsService>();
        services.AddTransient<ClientAnalyticsViewModel>();
        services.AddTransient<ClientAnalyticsWindow>();

        // 2. Сервисы UI
        services.AddTransient<IFilePickerService, FilePickerService>();

        // 3. ViewModels
        services.AddTransient<CabinetViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<CustomConfigViewModel>(); // <-- ДОБАВЛЕНО: ViewModel Своей конфигурации

        // 4. Views (Окна)
        services.AddTransient<CabinetWindow>();
        services.AddTransient<TerminalWindow>();
        services.AddTransient<AddServerWindow>();
        services.AddTransient<CustomConfigWindow>(); // <-- ДОБАВЛЕНО: Окно Своей конфигурации

        // 5. Pages (НАШИ НОВЫЕ СТРАНИЦЫ НАВИГАЦИИ)
        services.AddTransient<DashboardView>();
        services.AddTransient<ClientsView>();

        return services;
    }
}