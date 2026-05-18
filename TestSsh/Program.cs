using Renci.SshNet;
using System;

class Program {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: dotnet run <ip> <command>");
            return;
        }
        string ip = args[0];
        string command = args[1];
        var keyFile = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        var connectionInfo = new ConnectionInfo(ip, "root", new PrivateKeyAuthenticationMethod("root", keyFile));
        using (var ssh = new SshClient(connectionInfo)) {
            try {
                ssh.Connect();
                var cmd = ssh.RunCommand(command);
                Console.WriteLine(cmd.Result);
                if (!string.IsNullOrEmpty(cmd.Error)) {
                    Console.Error.WriteLine("Error: " + cmd.Error);
                }
            } catch (Exception ex) {
                Console.Error.WriteLine("Connection error: " + ex.Message);
            }
        }
    }
}
