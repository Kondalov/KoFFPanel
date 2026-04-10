using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace KoFFPanel.Application.Interfaces;

public interface ISshService
{
    bool IsConnected { get; }
    Task<string> ConnectAsync(string ip, int port, string user, string password, string keyPath);
    void Disconnect();
    Task WriteToShellAsync(string command);
    Task<int> ReadShellOutputAsync(byte[] buffer, int offset, int count, CancellationToken token);
    void ResizeTerminal(uint cols, uint rows);
    Task<IEnumerable<(string Name, bool IsDir)>> ListDirectoryAsync(string path);
    Task DownloadFileAsync(string remotePath, Stream localStream);
    void UploadFile(Stream localStream, string remotePath);

    // ИСПРАВЛЕНИЕ: Добавлены гибкие таймауты и токен отмены
    Task<string> ExecuteCommandAsync(string commandText, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    string GetWorkingDirectory();
    Renci.SshNet.ShellStream CreateShellStream(string terminalName, uint columns, uint rows, uint width, uint height, int bufferSize);
}