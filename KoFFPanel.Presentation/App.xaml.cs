using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using KoFFPanel.Presentation.Services;
using KoFFPanel.Presentation.ViewModels;
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
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private void ConfigureServices(IServiceCollection services)
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

        // 2. Сервисы UI (наш диалог выбора файлов)
        services.AddTransient<IFilePickerService, FilePickerService>();

        // 3. ViewModels
        services.AddTransient<CabinetViewModel>();
        services.AddTransient<TerminalViewModel>();
        services.AddTransient<AddServerViewModel>();

        // 4. Views
        services.AddTransient<CabinetWindow>();
        services.AddTransient<TerminalWindow>();
        services.AddTransient<AddServerWindow>();
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var mainWindow = Services.GetRequiredService<CabinetWindow>();
        mainWindow.Show();
    }
}