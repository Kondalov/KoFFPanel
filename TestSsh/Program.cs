using Renci.SshNet;
using System;
using System.IO;

class Program {
    static void Main(string[] args) {
        var keyFile = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        var connectionInfo = new ConnectionInfo("185.94.167.194", "root", new PrivateKeyAuthenticationMethod("root", keyFile));
        using (var ssh = new SshClient(connectionInfo)) {
            ssh.Connect();
            if (args.Length > 0) {
                Console.WriteLine(ssh.RunCommand(args[0]).Result);
            }
        }
    }
}
