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
        bool isTrustTunnel = SelectedServer.CoreType == "trusttunnel";
        string activeCoreName = isSingBox ? "Sing-box" : (isTrustTunnel ? "TrustTunnel" : "Xray-core");

        System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = $"Синхронизация БД с {activeCoreName}...");

        var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
        string ip = SelectedServer.IpAddress ?? "";
        var dbUsers = dbContext.Clients.Where(c => c.ServerIp == ip).ToList();

        System.Windows.Application.Current.Dispatcher.Invoke(() => SyncClientsCollection(dbUsers));

        try
        {
            bool coreSyncSuccess = isSingBox ? await _singBoxUserManager.SyncUsersToCoreAsync(ssh, Clients) :
                                   (isTrustTunnel ? await _trustTunnelUserManager.SyncUsersToCoreAsync(ssh, Clients) :
                                   await _userManager.SyncUsersToCoreAsync(ssh, Clients));

            bool hasTrustTunnelExtra = SelectedServer.Inbounds.Any(i => i.Protocol.ToLower() == "trusttunnel");
            if (hasTrustTunnelExtra && !isTrustTunnel)
            {
                await _trustTunnelUserManager.SyncUsersToCoreAsync(ssh, Clients);
            }

            if (coreSyncSuccess)
            {
                var freshContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                var updatedUsers = freshContext.Clients.AsNoTracking().Where(c => c.ServerIp == ip).ToList();

                foreach (var client in updatedUsers)
                {
                    var links = new List<string>();

                    // ДОБАВЛЕНЫ НОВЫЕ ПРОТОКОЛЫ ДЛЯ ОТПРАВКИ В HTTPS-ПОДПИСКУ
                    if (client.IsVlessEnabled && !string.IsNullOrEmpty(client.VlessLink) && client.VlessLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) links.Add(client.VlessLink);
                    if (client.IsHysteria2Enabled && !string.IsNullOrEmpty(client.Hysteria2Link) && client.Hysteria2Link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) links.Add(client.Hysteria2Link);
                    if (client.IsTrojanEnabled && !string.IsNullOrEmpty(client.TrojanLink) && client.TrojanLink.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) links.Add(client.TrojanLink);
                    if (client.IsShadowsocksEnabled && !string.IsNullOrEmpty(client.ShadowsocksLink) && client.ShadowsocksLink.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) links.Add(client.ShadowsocksLink);
                    if (client.IsTrustTunnelEnabled && !string.IsNullOrEmpty(client.TrustTunnelLink) && client.TrustTunnelLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) links.Add(client.TrustTunnelLink);

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