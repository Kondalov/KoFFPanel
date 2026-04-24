using Renci.SshNet;
using System;

class Program {
    static void Main() {
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var client = new SshClient("103.71.22.166", "root", new[] { pk });
        try {
            client.Connect();
            Console.WriteLine("==== REGEX TEST ====");
            var outStr = client.CreateCommand("/usr/local/bin/xray x25519 -i 2G8IL6mxs0U09hb7p88ZPZhJ25xjQrDlhnp4Z1Z2RXc").Execute();
            var m = System.Text.RegularExpressions.Regex.Match(outStr, @"(?i)PublicKey[)]?\s*:\s*(\S+)");
            Console.WriteLine("REGEX MATCHED: " + m.Success);
            if (m.Success) Console.WriteLine("PUBKEY: " + m.Groups[1].Value);
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
