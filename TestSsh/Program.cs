using Renci.SshNet;
using System;

class Program {
    static void Main() {
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var ssh = new SshClient("103.71.22.166", "root", new[] { pk });
        try {
            ssh.Connect();
            Console.WriteLine(ssh.RunCommand("rm -rf /opt/trusttunnel2/vpn.toml /opt/trusttunnel2/hosts.toml /opt/trusttunnel2/rules.toml").Result);
            Console.WriteLine(ssh.RunCommand("cd /opt/trusttunnel2 && ./setup_wizard -m non-interactive -a 0.0.0.0:5443 -c KOLA:01983 -n vpn.endpoint --lib-settings vpn.toml --hosts-settings hosts.toml --cert-type self-signed").Result);
            Console.WriteLine("\n==== VPN.TOML ====");
            Console.WriteLine(ssh.RunCommand("cat /opt/trusttunnel2/vpn.toml").Result);
            Console.WriteLine("\n==== HOSTS.TOML ====");
            Console.WriteLine(ssh.RunCommand("cat /opt/trusttunnel2/hosts.toml").Result);
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}