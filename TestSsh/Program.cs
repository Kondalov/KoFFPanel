using Renci.SshNet;
using System;
using System.Linq;

class Program {
    static void Main(string[] args) {
        if (args.Length == 0) {
            Console.WriteLine("Usage: TestSsh <command>");
            return;
        }
        string command = string.Join(" ", args);
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var ssh = new SshClient("185.94.167.194", "root", new[] { pk });
        try {
            ssh.Connect();
            var cmd = ssh.CreateCommand(command);
            var result = cmd.Execute();
            Console.Write(result);
            if (!string.IsNullOrEmpty(cmd.Error)) {
                Console.Error.Write(cmd.Error);
            }
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
