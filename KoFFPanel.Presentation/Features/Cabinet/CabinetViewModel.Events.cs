using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Presentation.Messages;
using KoFFPanel.Domain.Entities;
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

        var ssh = _currentMonitoringSsh;
        if (ssh == null || !ssh.IsConnected)
        {
            _logger.Log("USER-SYNC", "КРИТИЧЕСКАЯ ОТМЕНА: Нет активного SSH!");
            System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = "Ошибка: Нет SSH");
            return;
        }

        bool isSingBox = SelectedServer.CoreType == "sing-box";
        bool isTrustTunnel = SelectedServer.CoreType == "trusttunnel";
        string activeCoreName = isSingBox ? "Sing-box" : (isTrustTunnel ? "TrustTunnel" : "Xray-core");

        System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = $"Синхронизация БД с {activeCoreName}...");

        var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
        string ip = SelectedServer.IpAddress ?? "";
        var dbUsers = dbContext.Clients.Where(c => c.ServerIp == ip).ToList();

        System.Windows.Application.Current.Dispatcher.Invoke(() => {
            Clients.Clear();
            foreach (var u in dbUsers) Clients.Add(u);
        });

        try
        {
            bool syncSuccess = isSingBox ? await _singBoxUserManager.SyncUsersToCoreAsync(ssh, Clients) :
                              (isTrustTunnel ? await _trustTunnelUserManager.SyncUsersToCoreAsync(ssh, Clients) : await _userManager.SyncUsersToCoreAsync(ssh, Clients));

            if (syncSuccess)
            {
                var freshContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                var updatedUsers = freshContext.Clients.AsNoTracking().Where(c => c.ServerIp == ip).ToList();

                foreach (var client in Clients)
                {
                    var links = new List<string>();
                    if (client.IsTrustTunnelEnabled && !string.IsNullOrEmpty(client.TrustTunnelLink) && client.TrustTunnelLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) links.Add(client.TrustTunnelLink);
                    if (client.IsVlessEnabled && !string.IsNullOrEmpty(client.VlessLink) && client.VlessLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) links.Add(client.VlessLink);
                    if (client.IsHysteria2Enabled && !string.IsNullOrEmpty(client.Hysteria2Link) && client.Hysteria2Link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) links.Add(client.Hysteria2Link);
                    await _subscriptionService.UpdateUserSubscriptionAsync(ssh, client.Uuid ?? "", links);
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    Clients.Clear(); foreach (var u in updatedUsers) Clients.Add(u);
                    ServerStatus = $"Онлайн (Синхронизировано {Clients.Count})";
                });
            }
        }
        catch (Exception ex) { _logger.Log("USER-SYNC", $"Ошибка: {ex.Message}"); }
    }
}
