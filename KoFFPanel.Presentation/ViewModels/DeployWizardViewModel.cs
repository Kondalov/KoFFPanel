using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class DeployWizardViewModel : ObservableObject
{
    private readonly ISshService _sshService;
    private readonly IGitHubReleaseService _gitHubService;
    private readonly ICoreDeploymentService _deploymentService;
    public Action<string>? OnInstallRequested { get; set; }

    public Action? CloseAction { get; set; }

    [ObservableProperty] private VpnProfile? _targetServer;

    // Состояние выбора
    [ObservableProperty] private bool _isXraySelected = true;
    [ObservableProperty] private bool _isSingBoxSelected = false;
    [ObservableProperty] private bool _isCustomSelected = false;

    // Умное обновление
    [ObservableProperty] private bool _isAutoUpdateEnabled = false;

    // Версии
    [ObservableProperty] private string _xrayCurrentVersion = "Загрузка...";
    [ObservableProperty] private string _xrayLatestVersion = "Загрузка...";
    [ObservableProperty] private string _singBoxCurrentVersion = "Загрузка...";
    [ObservableProperty] private string _singBoxLatestVersion = "Загрузка...";

    // Статусы UI
    [ObservableProperty] private string _statusMessage = "Анализ сервера...";
    [ObservableProperty] private bool _isInstalling = false;
    [ObservableProperty] private bool _isNotInstalling = false; // Блокируем кнопки до окончания проверок

    public DeployWizardViewModel(
        ISshService sshService,
        IGitHubReleaseService gitHubService,
        ICoreDeploymentService deploymentService)
    {
        _sshService = sshService;
        _gitHubService = gitHubService;
        _deploymentService = deploymentService;
    }

    public async Task InitializeAsync(VpnProfile server)
    {
        TargetServer = server;
        IsNotInstalling = false;

        // 1. Подключаемся к серверу
        var connResult = await _sshService.ConnectAsync(server.IpAddress, server.Port, server.Username, server.Password, server.KeyPath);
        if (connResult != "SUCCESS")
        {
            StatusMessage = $"❌ Ошибка подключения: {connResult}";
            return;
        }

        // 2. УМНАЯ ЗАЩИТА: Pre-flight checks
        var (isReady, checkMsg) = await _deploymentService.RunPreFlightChecksAsync(_sshService);
        if (!isReady)
        {
            StatusMessage = $"⛔ Отказ: {checkMsg}";
            _sshService.Disconnect();
            return;
        }

        StatusMessage = "Сервер прошел проверку. Получение версий...";

        // 3. Параллельно запрашиваем версии (С сервера и с GitHub)
        var xrayTask = _deploymentService.GetInstalledXrayVersionAsync(_sshService);
        var singBoxTask = _deploymentService.GetInstalledSingBoxVersionAsync(_sshService);
        var gitXrayTask = _gitHubService.GetLatestReleaseVersionAsync("XTLS/Xray-core");
        var gitSingBoxTask = _gitHubService.GetLatestReleaseVersionAsync("SagerNet/sing-box");

        await Task.WhenAll(xrayTask, singBoxTask, gitXrayTask, gitSingBoxTask);

        XrayCurrentVersion = xrayTask.Result;
        XrayLatestVersion = gitXrayTask.Result;

        SingBoxCurrentVersion = singBoxTask.Result;
        SingBoxLatestVersion = gitSingBoxTask.Result;

        StatusMessage = "Готов к установке.";
        IsNotInstalling = true; // Разблокируем UI
    }

    [RelayCommand]
    private void StartInstall() // Теперь это синхронный метод!
    {
        if (TargetServer == null || !_sshService.IsConnected) return;

        string installCmd = "";

        // Формируем команду в зависимости от выбора ( \r - это имитация нажатия Enter )
        if (IsXraySelected)
        {
            installCmd = "bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install\r";
        }
        else if (IsSingBoxSelected)
        {
            installCmd = "bash <(curl -fsSL https://sing-box.app/install.sh)\r";
        }
        else if (IsCustomSelected)
        {
            StatusMessage = "Загрузка кастомного конфига пока в разработке!";
            return;
        }

        // Если включено умное автообновление, "приклеиваем" команду добавления в cron
        if (IsAutoUpdateEnabled && IsXraySelected)
        {
            installCmd += "echo '0 3 * * * root /bin/bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install' > /etc/cron.d/xray_updater\r";
        }

        // Отключаем фоновый SSH мастера, чтобы терминал мог нормально подключиться
        _sshService.Disconnect();

        CloseAction?.Invoke(); // Закрываем Мастера

        // Вызываем событие, передавая сформированную команду!
        OnInstallRequested?.Invoke(installCmd);
    }

    [RelayCommand]
    private void Cancel()
    {
        _sshService.Disconnect();
        CloseAction?.Invoke();
    }
}