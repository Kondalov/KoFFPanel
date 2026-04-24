using Renci.SshNet;
using System;

class Program {
    static void Main() {
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var client = new SshClient("103.71.22.166", "root", new[] { pk });
        try {
            client.Connect();
            Console.WriteLine("==== SING-BOX STATUS ====");
            Console.WriteLine(client.CreateCommand("systemctl status sing-box --no-pager").Execute());
            Console.WriteLine("==== SING-BOX LOGS ====");
            Console.WriteLine(client.CreateCommand("journalctl -u sing-box -n 30 --no-pager").Execute());
            Console.WriteLine("==== PORTS (UDP) ====");
            Console.WriteLine(client.CreateCommand("ss -ulnp").Execute());
            Console.WriteLine("==== FIREWALL ====");
            Console.WriteLine(client.CreateCommand("iptables -L -n | grep -i udp").Execute());
            Console.WriteLine("==== SING-BOX CONFIG ====");
            Console.WriteLine(client.CreateCommand("cat /etc/sing-box/config.json").Execute());
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
