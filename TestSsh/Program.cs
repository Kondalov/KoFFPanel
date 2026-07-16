using Renci.SshNet;
using System;
using System.IO;

class Program {
    static void Main(string[] args) {
        if (args.Length < 3) {
            Console.WriteLine("Usage: dotnet run <ip> <mode: cmd|upload> <arg> [remotePath]");
            return;
        }
        string ip = args[0];
        string mode = args[1];
        string arg = args[2];
        
        var keyFile = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        var connectionInfo = new ConnectionInfo(ip, "root", new PrivateKeyAuthenticationMethod("root", keyFile));

        if (mode == "cmd") {
            using (var ssh = new SshClient(connectionInfo)) {
                try {
                    ssh.Connect();
                    var cmd = ssh.RunCommand(arg);
                    Console.Write(cmd.Result);
                    if (!string.IsNullOrEmpty(cmd.Error)) Console.Error.Write("Error: " + cmd.Error);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Connection error: " + ex.Message);
                }
            }
        } else if (mode == "upload") {
            string remotePath = args.Length > 3 ? args[3] : "/tmp/" + Path.GetFileName(arg);
            using (var scp = new ScpClient(connectionInfo)) {
                try {
                    scp.Connect();
                    using (var fs = File.OpenRead(arg)) {
                        scp.Upload(fs, remotePath);
                    }
                    Console.WriteLine($"Uploaded {arg} to {remotePath}");
                } catch (Exception ex) {
                    Console.Error.WriteLine("Upload error: " + ex.Message);
                }
            }
        }
    }
}
