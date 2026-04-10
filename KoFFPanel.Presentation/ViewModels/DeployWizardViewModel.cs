using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Strategies;
using KoFFPanel.Domain.Entities;
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
            var (success, msg, resultObj) = await strategy.ExecuteFullInstall(
                _sshService, TargetServer.IpAddress, 443, "www.microsoft.com",
                TargetServer.Uuid ?? "", TargetServer.PrivateKey ?? "",
                TargetServer.PublicKey ?? "", TargetServer.ShortId ?? ""
            );

            StatusMessage = msg;

            // ВАЖНО: Универсальная проверка для обоих ядер
            if (success && resultObj != null)
            {
                string newUuid = "", newPriv = "", newPub = "", newSid = "", newSni = "";
                int newPort = 443;

                if (resultObj is XrayInstallResult xrayRes)
                {
                    newUuid = xrayRes.Uuid; newPriv = xrayRes.PrivateKey; newPub = xrayRes.PublicKey;
                    newSid = xrayRes.ShortId; newPort = xrayRes.Port; newSni = xrayRes.Sni;
                }
                else if (resultObj is SingBoxInstallResult sbRes)
                {
                    newUuid = sbRes.Uuid; newPriv = sbRes.PrivateKey; newPub = sbRes.PublicKey;
                    newSid = sbRes.ShortId; newPort = sbRes.Port; newSni = sbRes.Sni;
                }

                TargetServer.PrivateKey = newPriv; TargetServer.PublicKey = newPub;
                TargetServer.Uuid = newUuid; TargetServer.ShortId = newSid;
                TargetServer.VpnPort = newPort; TargetServer.Sni = newSni;

                _profileRepository.UpdateProfile(TargetServer);

                // Даем команду дашборду: "Эй, ядро сменилось, обнови карточки и ссылки!"
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Messages.CoreDeployedMessage(TargetServer));

                StatusMessage = "✅ Готово! Сохранение конфигурации...";

                // Открываем окно с ключами
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var successWin = new Views.InstallationSuccessWindow();
                    successWin.DataContext = resultObj; // Биндим наши ссылки из шаблона

                    if (System.Windows.Application.Current.MainWindow != null)
                        successWin.Owner = System.Windows.Application.Current.MainWindow;

                    successWin.ShowDialog();
                });

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