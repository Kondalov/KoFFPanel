using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using KoFFPanel.Presentation.Services;
using KoFFPanel.Presentation.ViewModels;
using KoFFPanel.Presentation.Views;
using KoFFPanel.Presentation.Views.Pages;
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

        // ИСПРАВЛЕНИЕ: Вычистили ClientConfigViewModel и ClientConfigWindow
        services.AddTransient<ISingBoxConfiguratorService, SingBoxConfiguratorService>();
        services.AddTransient<ISingBoxUserManagerService, SingBoxUserManagerService>();
        services.AddTransient<ITrustTunnelUserManagerService, TrustTunnelUserManagerService>();
        services.AddTransient<KoFFPanel.Presentation.ViewModels.ClientProtocolsViewModel>();
        services.AddTransient<KoFFPanel.Presentation.Views.ClientProtocolsWindow>();
        services.AddTransient<IServerSelectionService, ServerSelectionService>();

        // РЕГИСТРАЦИЯ МОЗГА
        services.AddTransient<ISmartPortValidator, SmartPortValidator>();
        services.AddTransient<KoFFPanel.Application.Services.ProtocolFactory>();

        // РЕГИСТРАЦИЯ АНАЛИТИКИ
        services.AddSingleton<IClientAnalyticsService, ClientAnalyticsService>();
        services.AddTransient<ClientAnalyticsViewModel>();
        services.AddTransient<ClientAnalyticsWindow>();

        // 2. Сервисы UI и Билдеры
        services.AddTransient<IFilePickerService, FilePickerService>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.VlessRealityBuilder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.Hysteria2Builder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.TrustTunnelBuilder>();

        // 3. ViewModels
        services.AddTransient<CabinetViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<CustomConfigViewModel>();
        services.AddTransient<BotViewModel>();
        services.AddSingleton<BotViewModel>();

        // 4. Views (Окна)
        services.AddTransient<CabinetWindow>();
        services.AddTransient<TerminalWindow>();
        services.AddTransient<AddServerWindow>();
        services.AddTransient<CustomConfigWindow>();

        // 5. Pages
        services.AddTransient<DashboardView>();
        services.AddTransient<ClientsView>();
        services.AddTransient<Views.Pages.BotView>();

        return services;
    }
}