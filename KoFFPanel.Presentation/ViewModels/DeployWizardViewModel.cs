using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Application.Services;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Services;
using KoFFPanel.Presentation.Messages;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class ProtocolSetupItem : ObservableObject
{
    public IProtocolBuilder Builder { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private bool _isValid = true;
    [ObservableProperty] private string _validationMessage = "Ожидание...";

    public ProtocolSetupItem(IProtocolBuilder builder)
    {
        Builder = builder;
    }
}

public partial class DeployWizardViewModel : ObservableObject
{
    private readonly ISshService _ssh;
    private readonly ProtocolFactory _protocolFactory;
    private readonly ISmartPortValidator _portValidator;
    private readonly IAppLogger _logger;
    private readonly ICoreDeploymentService _deploymentService;
    private VpnProfile _server = null!;

    public Action? CloseAction { get; set; }
    public Action<string>? OnInstallRequested { get; set; }

    [ObservableProperty] private bool _isXraySelected = true;
    [ObservableProperty] private bool _isSingBoxSelected;
    [ObservableProperty] private bool _isTrustTunnelSelected; // ИСПРАВЛЕНИЕ: Добавлено ядро TrustTunnel
    [ObservableProperty] private bool _isCustomSelected;

    [ObservableProperty] private bool _isNotInstalling = true;
    [ObservableProperty] private string _statusMessage = "Выберите ядро и нужные протоколы для установки.";

    public ObservableCollection<ProtocolSetupItem> AvailableProtocols { get; } = new();

    public DeployWizardViewModel(
        ISshService ssh,
        ProtocolFactory protocolFactory,
        ISmartPortValidator portValidator,
        IAppLogger logger,
        ICoreDeploymentService deploymentService)
    {
        _ssh = ssh;
        _protocolFactory = protocolFactory;
        _portValidator = portValidator;
        _logger = logger;
        _deploymentService = deploymentService;
    }

    public async Task InitializeAsync(VpnProfile server)
    {
        _logger.Log("WIZARD-TRACE", $"[START] Инициализация мастера для сервера {server.IpAddress}");
        _server = server;

        try
        {
            if (!_ssh.IsConnected)
            {
                _logger.Log("WIZARD-TRACE", "[INFO] SSH не подключен. Начинаем подключение...");
                StatusMessage = "Подключение к серверу для умного сканирования портов...";

                var connectResult = await _ssh.ConnectAsync(server.IpAddress!, server.Port, server.Username!, server.Password!, server.KeyPath!);
                _logger.Log("WIZARD-TRACE", $"[SSH] Результат подключения: {connectResult}");
            }

            LoadProtocolsForCurrentCore();
            StatusMessage = "Успешно! Выберите ядро и отметьте протоколы.";
            _logger.Log("WIZARD-TRACE", "[SUCCESS] Мастер загружен.");
        }
        catch (Exception ex)
        {
            _logger.Log("WIZARD-ERROR", $"[ОШИБКА] {ex.Message}");
            StatusMessage = $"Ошибка подключения: {ex.Message}";
        }
    }

    partial void OnIsXraySelectedChanged(bool value)
    {
        if (value) { IsSingBoxSelected = false; IsTrustTunnelSelected = false; LoadProtocolsForCurrentCore(); }
    }

    partial void OnIsSingBoxSelectedChanged(bool value)
    {
        if (value) { IsXraySelected = false; IsTrustTunnelSelected = false; LoadProtocolsForCurrentCore(); }
    }

    partial void OnIsTrustTunnelSelectedChanged(bool value)
    {
        if (value) { IsXraySelected = false; IsSingBoxSelected = false; LoadProtocolsForCurrentCore(); }
    }

    private string GetSelectedCoreType()
    {
        if (IsSingBoxSelected) return CoreTypes.SingBox;
        if (IsTrustTunnelSelected) return CoreTypes.TrustTunnel;
        return CoreTypes.Xray;
    }

    private void LoadProtocolsForCurrentCore()
    {
        foreach (var p in AvailableProtocols) p.PropertyChanged -= OnProtocolPropertyChanged;
        AvailableProtocols.Clear();

        if (IsCustomSelected) return;

        var builders = _protocolFactory.GetAvailableProtocols(GetSelectedCoreType());
        foreach (var builder in builders)
        {
            var item = new ProtocolSetupItem(builder);

            if (_server?.Inbounds != null)
            {
                var existing = _server.Inbounds.FirstOrDefault(i => i.Protocol.Equals(builder.ProtocolType, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    item.IsSelected = false;
                    item.PortText = existing.Port.ToString();
                    item.IsValid = true;
                    item.ValidationMessage = "Установлен (Включите для переустановки)";
                }
                // Для TrustTunnel задаем порт 443 по умолчанию
                else if (builder.ProtocolType.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase))
                {
                    item.PortText = "443";
                }
            }

            item.PropertyChanged += OnProtocolPropertyChanged;
            AvailableProtocols.Add(item);
        }
    }

    private async void OnProtocolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ProtocolSetupItem item)
        {
            if (e.PropertyName == nameof(ProtocolSetupItem.IsSelected))
            {
                if (item.IsSelected)
                {
                    item.ValidationMessage = "Сканирование...";
                    item.IsValid = true;

                    int bestPort = int.TryParse(item.PortText, out int parsedPort) ? parsedPort :
                        await _portValidator.SuggestBestPortAsync(_ssh, _server.Id, item.Builder.ProtocolType);

                    string newPortText = bestPort.ToString();

                    if (item.PortText == newPortText)
                    {
                        item.ValidationMessage = "Проверка...";
                        var (isValid, msg) = await _portValidator.ValidatePortAsync(_ssh, _server.Id, bestPort, item.Builder.ProtocolType);

                        var duplicateInUI = AvailableProtocols.FirstOrDefault(p => p != item && p.IsSelected && p.PortText == newPortText && p.Builder.TransportType == item.Builder.TransportType);
                        if (duplicateInUI != null)
                        {
                            isValid = false;
                            msg = $"Конфликт UI: Порт уже выбран для {duplicateInUI.Builder.DisplayName}!";
                        }

                        item.IsValid = isValid;
                        item.ValidationMessage = msg;
                    }
                    else
                    {
                        item.PortText = newPortText;
                    }
                }
                else
                {
                    item.PortText = "";
                    item.ValidationMessage = "ОТКЛЮЧЕН";
                }
            }
            else if (e.PropertyName == nameof(ProtocolSetupItem.PortText) && item.IsSelected)
            {
                if (int.TryParse(item.PortText, out int port))
                {
                    item.ValidationMessage = "Проверка...";
                    var (isValid, msg) = await _portValidator.ValidatePortAsync(_ssh, _server.Id, port, item.Builder.ProtocolType);

                    var duplicateInUI = AvailableProtocols.FirstOrDefault(p => p != item && p.IsSelected && p.PortText == item.PortText && p.Builder.TransportType == item.Builder.TransportType);
                    if (duplicateInUI != null)
                    {
                        isValid = false;
                        msg = $"Конфликт UI: Порт уже выбран для {duplicateInUI.Builder.DisplayName}!";
                    }

                    item.IsValid = isValid;
                    item.ValidationMessage = msg;
                }
                else
                {
                    item.IsValid = false;
                    item.ValidationMessage = "Введите число от 1 до 65535";
                }
            }
        }
    }

    [RelayCommand]
    private void Cancel() => CloseAction?.Invoke();

    [RelayCommand]
    private async Task StartInstallAsync()
    {
        _logger.Log("WIZARD-TRACE", "[ACTION] Старт установки");

        if (IsCustomSelected) return;

        var selectedItems = AvailableProtocols.Where(p => p.IsSelected).ToList();
        if (!selectedItems.Any())
        {
            StatusMessage = "ОШИБКА: Выберите протоколы!";
            return;
        }
        if (selectedItems.Any(p => !p.IsValid))
        {
            StatusMessage = "ОШИБКА: Исправьте конфликты портов!";
            return;
        }

        StatusMessage = "🚀 Развертывание ядра и портов... (Подождите)";
        IsNotInstalling = false;

        var protocolsToInstall = selectedItems.Select(p => (p.Builder, int.Parse(p.PortText))).ToList();

        // ИСПРАВЛЕНИЕ: Передаем строковый идентификатор ядра вместо bool isSingBox
        var (success, log) = await _deploymentService.DeployFullStackAsync(_ssh, _server, GetSelectedCoreType(), protocolsToInstall);

        if (success)
        {
            StatusMessage = "✅ УСТАНОВКА УСПЕШНА! Синхронизация клиентов...";
            _logger.Log("WIZARD-TRACE", "[SUCCESS] Отправка сигнала CabinetViewModel для вшивки клиентов.");

            WeakReferenceMessenger.Default.Send(new CoreDeployedMessage(_server));

            await Task.Delay(2000);
            CloseAction?.Invoke();
        }
        else
        {
            StatusMessage = "❌ ОШИБКА. Смотрите логи.";
            _logger.Log("WIZARD-ERROR", $"[DEPLOY-FAIL] {log}");
            IsNotInstalling = true;
        }
    }
}