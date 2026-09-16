using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Presentation.Messages;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel
{
    public async void Receive(CoreDeployedMessage message)
    {
        _logger.Log("USER-SYNC", "Сигнал синхронизации получен!");

        if (SelectedServer == null || message.Server == null || message.Server.Id != SelectedServer.Id)
        {
            _logger.Log("USER-SYNC", "ОТМЕНА: Несовпадение серверов или сервер не выбран.");
            return;
        }

        SelectedServer = message.Server;
        ISshService? ssh = _currentMonitoringSsh;
        bool isTempSsh = false;

        if (ssh == null || !ssh.IsConnected)
        {
            _logger.Log("USER-SYNC", "Мониторинг недоступен. Создаю временное SSH подключение...");
            ssh = _sshServiceFactory();
            string connRes = await ssh.ConnectAsync(SelectedServer.IpAddress ?? "", SelectedServer.Port, SelectedServer.Username ?? "root", SelectedServer.Password ?? "", SelectedServer.KeyPath ?? "");
            if (connRes != "SUCCESS")
            {
                _logger.Log("USER-SYNC", "КРИТИЧЕСКАЯ ОТМЕНА: Не удалось создать временный SSH!");
                System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = "Ошибка синхронизации: Нет SSH");
                return;
            }
            isTempSsh = true;
        }

        bool isSingBox = SelectedServer.CoreType == "sing-box";
        string activeCoreName = isSingBox ? "Sing-box" : "Xray-core";

        System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = $"Синхронизация БД с {activeCoreName}...");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
        string ip = SelectedServer.IpAddress ?? "";
        var dbUsers = dbContext.Clients.Where(c => c.ServerIp == ip).ToList();

        // Авто-активация вновь развернутых на сервере протоколов для существующих пользователей
        var serverProtocols = SelectedServer.Inbounds.Select(i => i.Protocol.ToLowerInvariant()).ToHashSet();
        bool anyChanged = false;
        foreach (var u in dbUsers)
        {
            if (serverProtocols.Contains("vless") && !u.IsVlessEnabled) { u.IsVlessEnabled = true; anyChanged = true; }
            if (serverProtocols.Contains("hysteria2") && !u.IsHysteria2Enabled) { u.IsHysteria2Enabled = true; anyChanged = true; }
            if (serverProtocols.Contains("tuic") && !u.IsTuicEnabled) { u.IsTuicEnabled = true; anyChanged = true; }
            if (serverProtocols.Contains("trojan") && !u.IsTrojanEnabled) { u.IsTrojanEnabled = true; anyChanged = true; }
        }
        if (anyChanged) await dbContext.SaveChangesAsync();

        System.Windows.Application.Current.Dispatcher.Invoke(() => SyncClientsCollection(dbUsers));

        try
        {
            bool coreSyncSuccess = isSingBox ? await _singBoxUserManager.SyncUsersToCoreAsync(ssh, Clients) :
                                   await _userManager.SyncUsersToCoreAsync(ssh, Clients);

            if (coreSyncSuccess)
            {
                using var freshScope = _serviceProvider.CreateScope();
                var freshContext = freshScope.ServiceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                var updatedUsers = freshContext.Clients.AsNoTracking().Where(c => c.ServerIp == ip).ToList();

                foreach (var client in updatedUsers)
                {
                    var links = new List<string>();

                    if (client.IsVlessEnabled && !string.IsNullOrEmpty(client.VlessLink) && client.VlessLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) links.Add(client.VlessLink);
                    if (client.IsHysteria2Enabled && !string.IsNullOrEmpty(client.Hysteria2Link) && (client.Hysteria2Link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase) || client.Hysteria2Link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase))) links.Add(client.Hysteria2Link);
                    if (client.IsTuicEnabled && !string.IsNullOrEmpty(client.TuicLink) && client.TuicLink.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase)) links.Add(client.TuicLink);
                    if (client.IsTrojanEnabled && !string.IsNullOrEmpty(client.TrojanLink) && client.TrojanLink.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) links.Add(client.TrojanLink);

                    await _subscriptionService.UpdateUserSubscriptionAsync(ssh, client.Uuid ?? "", links);
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    SyncClientsCollection(updatedUsers);
                    ServerStatus = $"Онлайн (Синхронизировано {Clients.Count})";
                });
            }
        }
        catch (Exception ex) { _logger.Log("USER-SYNC", $"Ошибка: {ex.Message}"); }
        finally
        {
            if (isTempSsh) ssh.Disconnect();
        }
    }
}