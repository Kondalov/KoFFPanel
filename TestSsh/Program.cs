using Renci.SshNet;
using System;

class Program {
    static void Main() {
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var ssh = new SshClient("103.71.22.166", "root", new[] { pk });
        try {
            ssh.Connect();
            Console.WriteLine("--- APPLYING NATIVE HYSTERIA2 FIX ---");
            string script = @"
if [ -f /etc/sing-box/config.json ]; then
    # Меняем listen_port у hysteria2 на 443 (вместо 10443)
    jq '(.inbounds[] | select(.type == ""hysteria2"")).listen_port = 443' /etc/sing-box/config.json > /tmp/config.json && mv /tmp/config.json /etc/sing-box/config.json
    
    # Очищаем костыль с NAT (удаляем все редиректы)
    iptables -t nat -F PREROUTING
    
    # Рестарт
    systemctl restart sing-box
    sleep 2
    systemctl status sing-box --no-pager
    
    echo '--- SINGBOX LOGS AFTER FIX ---'
    journalctl -u sing-box -n 20 --no-pager
else
    echo '/etc/sing-box/config.json not found'
fi
";
            Console.WriteLine(ssh.RunCommand(script.Replace("\r", "")).Result);
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
