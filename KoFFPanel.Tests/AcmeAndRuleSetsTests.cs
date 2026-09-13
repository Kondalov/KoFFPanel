using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using System.Threading.Tasks;
using Xunit;

namespace KoFFPanel.Tests;

public class AcmeAndRuleSetsTests
{
    private class FakeLogger : IAppLogger
    {
        public void Log(string module, string message) { }
    }

    private class DisconnectedSsh : ISshService
    {
        public bool IsConnected => false;
        public string? ServerHostKeyFingerprint => null;
        public Task<string> ConnectAsync(string ip, int port, string user, string password, string keyPath, string? expectedFingerprint = null) => Task.FromResult("Disconnected");
        public void Disconnect() { }
        public Task WriteToShellAsync(string command) => Task.CompletedTask;
        public Task<int> ReadShellOutputAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token) => Task.FromResult(0);
        public void ResizeTerminal(uint cols, uint rows) { }
        public Task<System.Collections.Generic.IEnumerable<(string Name, bool IsDir)>> ListDirectoryAsync(string path) => Task.FromResult(System.Linq.Enumerable.Empty<(string, bool)>());
        public Task DownloadFileAsync(string remotePath, System.IO.Stream localStream) => Task.CompletedTask;
        public void UploadFile(System.IO.Stream localStream, string remotePath) { }
        public Task<string> ExecuteCommandAsync(string commandText, System.TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> ExecuteSudoCommandAsync(string commandText, string password, System.TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public string GetWorkingDirectory() => "/root";
        public Renci.SshNet.ShellStream CreateShellStream(string terminalName, uint columns, uint rows, uint width, uint height, int bufferSize) => null!;
    }

    private class ConnectedSshMock : ISshService
    {
        public bool IsConnected => true;
        public string? ServerHostKeyFingerprint => "test-fingerprint";
        public Task<string> ConnectAsync(string ip, int port, string user, string password, string keyPath, string? expectedFingerprint = null) => Task.FromResult("Connected");
        public void Disconnect() { }
        public Task WriteToShellAsync(string command) => Task.CompletedTask;
        public Task<int> ReadShellOutputAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token) => Task.FromResult(0);
        public void ResizeTerminal(uint cols, uint rows) { }
        public Task<System.Collections.Generic.IEnumerable<(string Name, bool IsDir)>> ListDirectoryAsync(string path) => Task.FromResult(System.Linq.Enumerable.Empty<(string, bool)>());
        public Task DownloadFileAsync(string remotePath, System.IO.Stream localStream) => Task.CompletedTask;
        public void UploadFile(System.IO.Stream localStream, string remotePath) { }
        public Task<string> ExecuteCommandAsync(string commandText, System.TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default)
        {
            if (commandText.Contains("EUID"))
                return Task.FromResult("0");

            string decoded = commandText;
            if (commandText.Contains("base64 -d"))
            {
                var parts = commandText.Split('\'');
                if (parts.Length >= 2)
                {
                    try
                    {
                        var bytes = System.Convert.FromBase64String(parts[1]);
                        decoded = System.Text.Encoding.UTF8.GetString(bytes);
                    }
                    catch { }
                }
            }

            if (decoded.Contains("certbot") || decoded.Contains("koff/certs") || decoded.Contains("ACME_SUCCESS"))
                return Task.FromResult("ACME_SUCCESS");
            if (decoded.Contains("srs_build") || decoded.Contains("rule-set") || decoded.Contains("sing-box/rules") || decoded.Contains("SRS_SUCCESS"))
                return Task.FromResult("SRS_SUCCESS");
            return Task.FromResult("SUCCESS");
        }
        public Task<string> ExecuteSudoCommandAsync(string commandText, string password, System.TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public string GetWorkingDirectory() => "/root";
        public Renci.SshNet.ShellStream CreateShellStream(string terminalName, uint columns, uint rows, uint width, uint height, int bufferSize) => null!;
    }

    [Fact]
    public async Task EnsureCertificateAsync_DisconnectedSsh_ReturnsError()
    {
        var service = new AcmeCertificateService(new FakeLogger());
        var (isSuccess, _, _, msg) = await service.EnsureCertificateAsync(new DisconnectedSsh(), "1.2.3.4", "vpn.example.com");

        Assert.False(isSuccess);
        Assert.Equal("Нет подключения по SSH", msg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task EnsureCertificateAsync_EmptyDomain_ReturnsFallback(string? domain)
    {
        var service = new AcmeCertificateService(new FakeLogger());
        var (isSuccess, _, _, msg) = await service.EnsureCertificateAsync(new ConnectedSshMock(), "1.2.3.4", domain);

        Assert.False(isSuccess);
        Assert.Contains("Домен не указан", msg);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("8.8.8.8")]
    public async Task EnsureCertificateAsync_IpAddressInsteadOfDomain_RejectsWithFQDNRequirement(string ip)
    {
        var service = new AcmeCertificateService(new FakeLogger());
        var (isSuccess, _, _, msg) = await service.EnsureCertificateAsync(new ConnectedSshMock(), "1.2.3.4", ip);

        Assert.False(isSuccess);
        Assert.Contains("FQDN", msg);
    }

    [Fact]
    public async Task EnsureCertificateAsync_ValidDomain_ExecutesAndSucceeds()
    {
        var service = new AcmeCertificateService(new FakeLogger());
        var (isSuccess, certPath, keyPath, msg) = await service.EnsureCertificateAsync(new ConnectedSshMock(), "1.2.3.4", "https://sub.myvpn.net:8443/");

        Assert.True(isSuccess);
        Assert.Contains("sub.myvpn.net.crt", certPath);
        Assert.Contains("sub.myvpn.net.key", keyPath);
        Assert.Contains("успешно выпущен", msg);
    }

    [Fact]
    public async Task CompileAndDeployRuleSetsAsync_DisconnectedSsh_ReturnsError()
    {
        var service = new SingBoxConfiguratorService(new FakeLogger());
        var (isSuccess, msg) = await service.CompileAndDeployRuleSetsAsync(new DisconnectedSsh());

        Assert.False(isSuccess);
        Assert.Equal("Нет подключения по SSH", msg);
    }

    [Fact]
    public async Task CompileAndDeployRuleSetsAsync_ConnectedSsh_DeploysSuccessfully()
    {
        var service = new SingBoxConfiguratorService(new FakeLogger());
        var (isSuccess, msg) = await service.CompileAndDeployRuleSetsAsync(new ConnectedSshMock());

        Assert.True(isSuccess);
        Assert.Contains("успешно", msg);
    }
}
