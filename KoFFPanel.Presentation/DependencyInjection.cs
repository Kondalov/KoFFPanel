using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using KoFFPanel.Presentation.Services;
using KoFFPanel.Presentation.Features.Bot;
using KoFFPanel.Presentation.Features.Terminal;
using KoFFPanel.Presentation.Features.Cabinet;
using KoFFPanel.Presentation.Features.Deploy;
using KoFFPanel.Presentation.Features.Analytics;
using KoFFPanel.Presentation.Features.Management;
using KoFFPanel.Presentation.Features.Config;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace KoFFPanel.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        // 1. Ð˜Ð½Ñ„Ñ€Ð°ÑÑ‚Ñ€ÑƒÐºÑ‚ÑƒÑ€Ð° Ð¸ Core-ÑÐµÑ€Ð²Ð¸ÑÑ‹
        services.AddSingleton<IAppLogger, AppLogger>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddTransient<ISshService, SshService>();
        services.AddTransient<IServerMonitorService, ServerMonitorService>();
        services.AddTransient<IXrayCoreService, XrayCoreService>();
        services.AddHttpClient<IGitHubReleaseService, GitHubReleaseService>();
        services.AddTransient<ICoreDeploymentService, CoreDeploymentService>();
        services.AddTransient<IXrayConfiguratorService, XrayConfiguratorService>();
        services.AddTransient<IXrayUserManagerService, XrayUserManagerService>();
        services.AddDbContext<Infrastructure.Data.AppDbContext>(ServiceLifetime.Transient);

        // 2026 MODERNIZATION: Регистрация новых сервисов БД
        services.AddSingleton<LogBufferService>();
        services.AddHostedService(sp => sp.GetRequiredService<LogBufferService>());
        
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<IDatabaseBackupService>(sp => sp.GetRequiredService<DatabaseBackupService>());
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackupService>());

        services.AddSingleton<ISubscriptionService, SubscriptionService>();

        services.AddHttpClient("BotApiClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddTransient<ISingBoxConfiguratorService, SingBoxConfiguratorService>();
        services.AddTransient<ISingBoxUserManagerService, SingBoxUserManagerService>();
        services.AddTransient<ITrustTunnelUserManagerService, TrustTunnelUserManagerService>();
        services.AddTransient<IMarzbanMigrationService, MarzbanMigrationService>();
        
        services.AddTransient<IServerSelectionService, ServerSelectionService>();
        services.AddTransient<ISmartPortValidator, SmartPortValidator>();
        services.AddTransient<KoFFPanel.Application.Services.ProtocolFactory>();

        services.AddSingleton<IClientAnalyticsService, ClientAnalyticsService>();

        // 2. Ð¡ÐµÑ€Ð²Ð¸ÑÑ‹ UI Ð¸ Ð‘Ð¸Ð»Ð´ÐµÑ€Ñ‹
        services.AddTransient<IFilePickerService, FilePickerService>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.VlessRealityBuilder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.Hysteria2Builder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.TrustTunnelBuilder>();

        // 3. ViewModels
        services.AddSingleton<CabinetViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<AddClientViewModel>();
        services.AddTransient<CustomConfigViewModel>();
        services.AddTransient<DeployWizardViewModel>();
        services.AddTransient<ClientAnalyticsViewModel>();
        services.AddSingleton<BotViewModel>();
        services.AddTransient<EditorViewModel>();
        services.AddTransient<ClientProtocolsViewModel>();

        // 4. Views / Windows
        services.AddTransient<CabinetWindow>();
        services.AddTransient<TerminalWindow>();
        services.AddTransient<AddServerWindow>();
        services.AddTransient<AddClientWindow>();
        services.AddTransient<CustomConfigWindow>();
        services.AddTransient<DeployWizardWindow>();
        services.AddTransient<ClientAnalyticsWindow>();
        services.AddTransient<EditorWindow>();
        services.AddTransient<ServerSelectionWindow>();
        services.AddTransient<ClientProtocolsWindow>();
        services.AddTransient<InstallationSuccessWindow>();

        // 5. Pages / Components
        services.AddTransient<DashboardView>();
        services.AddTransient<ClientsView>();
        services.AddTransient<BotView>();

        return services;
    }
}
