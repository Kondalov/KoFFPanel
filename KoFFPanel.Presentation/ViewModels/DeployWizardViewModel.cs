using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Strategies;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class DeployWizardViewModel : ObservableObject
{
    private readonly ISshService _sshService;
    private readonly IGitHubReleaseService _gitHubService;
    private readonly ICoreDeploymentService _deploymentService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IProfileRepository _profileRepository;

    public Action<string>? OnInstallRequested { get; set; }
    public Action? CloseAction { get; set; }

    [ObservableProperty] private VpnProfile? _targetServer;

    [ObservableProperty] private bool _isXraySelected = true;
    [ObservableProperty] private bool _isSingBoxSelected = false;
    [ObservableProperty] private bool _isCustomSelected = false;

    [ObservableProperty] private string _xrayCurrentVersion = "Загрузка...";
    [ObservableProperty] private string _xrayLatestVersion = "Загрузка...";
    [ObservableProperty] private string _singBoxCurrentVersion = "Загрузка...";
    [ObservableProperty] private string _singBoxLatestVersion = "Загрузка...";

    [ObservableProperty] private string _statusMessage = "Анализ сервера...";
    [ObservableProperty] private bool _isInstalling = false;
    [ObservableProperty] private bool _isNotInstalling = false;

    public DeployWizardViewModel(
        ISshService sshService,
        IGitHubReleaseService gitHubService,
        ICoreDeploymentService deploymentService,
        IServiceProvider serviceProvider,
        IProfileRepository profileRepository)
    {
        _sshService = sshService;
        _gitHubService = gitHubService;
        _deploymentService = deploymentService;
        _serviceProvider = serviceProvider;
        _profileRepository = profileRepository;
    }

    public async Task InitializeAsync(VpnProfile server)
    {
        TargetServer = server;
        IsNotInstalling = false;

        var connResult = await _sshService.ConnectAsync(server.IpAddress, server.Port, server.Username, server.Password, server.KeyPath ?? "");
        if (connResult != "SUCCESS")
        {
            StatusMessage = $"❌ Ошибка: {connResult}";
            return;
        }

        StatusMessage = "Получение актуальных версий...";

        var xrayTask = _deploymentService.GetInstalledXrayVersionAsync(_sshService);
        var singBoxTask = _deploymentService.GetInstalledSingBoxVersionAsync(_sshService);
        var gitXrayTask = _gitHubService.GetLatestReleaseVersionAsync("XTLS/Xray-core");
        var gitSingBoxTask = _gitHubService.GetLatestReleaseVersionAsync("SagerNet/sing-box");

        await Task.WhenAll(xrayTask, singBoxTask, gitXrayTask, gitSingBoxTask);

        XrayCurrentVersion = xrayTask.Result;
        XrayLatestVersion = gitXrayTask.Result;
        SingBoxCurrentVersion = singBoxTask.Result;
        SingBoxLatestVersion = gitSingBoxTask.Result;

        StatusMessage = "Готов к умной установке.";
        IsNotInstalling = true;
    }

    [RelayCommand]
    private async Task StartInstallAsync()
    {
        if (TargetServer == null || !_sshService.IsConnected) return;

        if (IsCustomSelected)
        {
            _sshService.Disconnect();
            CloseAction?.Invoke();
            var customConfigWindow = _serviceProvider.GetRequiredService<Views.CustomConfigWindow>();
            customConfigWindow.ShowDialog();
            return;
        }

        IsNotInstalling = false;
        IsInstalling = true;
        StatusMessage = "🔍 Проверка конфигурации...";

        ICoreInstallStrategy? strategy = null;
        if (IsXraySelected) strategy = new XrayInstallStrategy();
        else if (IsSingBoxSelected) strategy = new SingBoxInstallStrategy();

        if (strategy != null)
        {
            // ПЕРЕДАЕМ СТАРЫЕ КЛЮЧИ (ЕСЛИ ЕСТЬ), ЧТОБЫ НЕ СБРАСЫВАТЬ ПОЛЬЗОВАТЕЛЕЙ
            var (success, msg, resultObj) = await strategy.ExecuteFullInstall(
                _sshService,
                TargetServer.IpAddress,
                443,
                "www.microsoft.com",
                TargetServer.Uuid ?? "",
                TargetServer.PrivateKey ?? "",
                TargetServer.PublicKey ?? "",
                TargetServer.ShortId ?? ""
            );

            StatusMessage = msg;

            if (success && resultObj is XrayInstallResult result)
            {
                TargetServer.PrivateKey = result.PrivateKey;
                TargetServer.PublicKey = result.PublicKey;
                TargetServer.Uuid = result.Uuid;
                TargetServer.ShortId = result.ShortId;
                TargetServer.VpnPort = result.Port;
                TargetServer.Sni = result.Sni;

                _profileRepository.UpdateProfile(TargetServer);

                StatusMessage = "✅ Готово! Сохранение конфигурации...";

                var successWin = new Views.InstallationSuccessWindow();
                successWin.DataContext = result;
                successWin.ShowDialog();

                _sshService.Disconnect();
                CloseAction?.Invoke();
            }
            else
            {
                IsNotInstalling = true;
                IsInstalling = false;
            }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _sshService.Disconnect();
        CloseAction?.Invoke();
    }
}