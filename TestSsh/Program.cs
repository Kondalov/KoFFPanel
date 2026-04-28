using Renci.SshNet;
using System;

class Program {
    static void Main() {
        var pk = new PrivateKeyFile(@"C:\Users\Nikolay\.ssh\id_ed25519", "01983");
        using var ssh = new SshClient("103.71.22.166", "root", new[] { pk });
        try {
            ssh.Connect();
            Console.WriteLine("--- READING LOG ---");
            Console.WriteLine(ssh.RunCommand("cat /tmp/sb_pure.log").Result);
            Console.WriteLine("--- TEST XRAY ---");
            string script = @"
cat << 'EOF' > /tmp/xray_pure.json
{
  ""inbounds"": [
    {
      ""protocol"": ""vless"",
      ""listen"": ""0.0.0.0"",
      ""port"": 4443,
      ""settings"": { ""clients"": [] },
      ""streamSettings"": {
        ""network"": ""tcp"",
        ""security"": ""reality"",
        ""realitySettings"": { ""show"": false, ""dest"": ""bing.com:443"", ""serverNames"": [""bing.com""], ""privateKey"": """", ""shortIds"": [] }
      }
    }
  ],
  ""outbounds"": [ { ""protocol"": ""freedom"", ""tag"": ""direct"" } ]
}
EOF
/usr/local/bin/xray -config /tmp/xray_pure.json > /tmp/xray.log 2>&1 &
PID=$!
sleep 2
kill -9 $PID
cat /tmp/xray.log
";
            Console.WriteLine(ssh.RunCommand(script).Result);
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
