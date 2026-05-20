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
using Microsoft.EntityFrameworkCore;
using System;

using System.Runtime.Versioning;

namespace KoFFPanel.Presentation;

[SupportedOSPlatform("windows")]
public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        // 1. Инфраструктура и Core-сервисы
        services.AddSingleton<IAppLogger, AppLogger>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddTransient<ISshService, SshService>();
        services.AddTransient<IServerMonitorService, ServerMonitorService>();
        services.AddTransient<IXrayCoreService, XrayCoreService>();
        services.AddHttpClient<IGitHubReleaseService, GitHubReleaseService>();
        services.AddTransient<ICoreDeploymentService, CoreDeploymentService>();
        services.AddTransient<IXrayConfiguratorService, XrayConfiguratorService>();
        services.AddTransient<IXrayUserManagerService, XrayUserManagerService>();

        // Настройка пути к БД с жестким разделением сред (Dev / Prod)
#if DEBUG
        string dbFileName = "koffpanel_users_dev.db";
        string configFolderName = "KoFFPanel_Dev";
#else
        string dbFileName = "koffpanel_users.db";
        string configFolderName = "KoFFPanel";
#endif

        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string basePath = System.IO.Path.Combine(appDataPath, configFolderName);

        if (!System.IO.Directory.Exists(basePath))
        {
            System.IO.Directory.CreateDirectory(basePath);
        }

        string dbPath = System.IO.Path.Combine(basePath, dbFileName);
        string dbPassword = KoFFPanel.Infrastructure.Services.MasterKeyService.Instance.GetMasterPassword();

        services.AddDbContext<Infrastructure.Data.AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath};Password={dbPassword};Pooling=True;"),
            ServiceLifetime.Transient);

        services.AddTransient<FraudScoringViewModel>();

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

        services.AddTransient<IServerSelectionService, ServerSelectionService>();
        services.AddTransient<ISmartPortValidator, SmartPortValidator>();
        services.AddTransient<KoFFPanel.Application.Services.ProtocolFactory>();

        services.AddSingleton<IClientAnalyticsService, ClientAnalyticsService>();
        services.AddTransient<IAntiFraudService, AntiFraudService>();

        // 2. Сервисы UI и Билдеры
        services.AddTransient<IFilePickerService, FilePickerService>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.VlessRealityBuilder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.Hysteria2Builder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.TrustTunnelBuilder>();
        services.AddTransient<FraudScoringWindow>();
        // ДОБАВЛЕНЫ НОВЫЕ ПРОТОКОЛЫ
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.TrojanBuilder>();
        services.AddTransient<KoFFPanel.Application.Interfaces.ProtocolBuilders.IProtocolBuilder, KoFFPanel.Infrastructure.Services.ProtocolBuilders.ShadowsocksBuilder>();

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
        services.AddTransient<FraudScoringWindow>();

        // 5. Pages / Components
        services.AddSingleton<DashboardView>();
        services.AddSingleton<ClientsView>();
        services.AddSingleton<BotView>();

        return services;
    }
}
