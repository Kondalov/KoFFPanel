using KoFFPanel.Application.Interfaces;
using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SshService : ISshService, IDisposable
{
    private SshClient? _sshClient;
    private SftpClient? _sftpClient;
    private ShellStream? _shellStream;
    private readonly IAppLogger _logger;

    public SshService(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _sshClient?.IsConnected == true;

    public async Task<string> ConnectAsync(string ip, int port, string user, string password, string keyPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                ConnectionInfo connInfo;
                if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
                {
                    PrivateKeyFile privateKey;
                    try
                    {
                        privateKey = string.IsNullOrEmpty(password) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, password);
                    }
                    catch (SshPassPhraseNullOrEmptyException) { return "KEY_LOCKED"; }
                    catch (Exception ex) { return $"KEY_ERROR|{ex.Message}"; }

                    connInfo = new ConnectionInfo(ip, port, user, new PrivateKeyAuthenticationMethod(user, privateKey));
                }
                else
                {
                    if (string.IsNullOrEmpty(password)) return "NO_AUTH";
                    connInfo = new ConnectionInfo(ip, port, user, new PasswordAuthenticationMethod(user, password));
                }

                connInfo.Timeout = TimeSpan.FromSeconds(15);

                _sshClient = new SshClient(connInfo) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
                _sshClient.Connect();

                _sftpClient = new SftpClient(connInfo) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
                _sftpClient.Connect();

                _shellStream = _sshClient.CreateShellStream("xterm-256color", 120, 40, 1200, 600, 1024);
                return "SUCCESS";
            }
            catch (Exception ex) { return $"ERROR|{ex.Message}"; }
        });
    }

    public void Disconnect()
    {
        _shellStream?.Dispose();
        _shellStream = null;

        if (_sftpClient != null)
        {
            try { if (_sftpClient.IsConnected) _sftpClient.Disconnect(); } catch { }
            _sftpClient.Dispose();
            _sftpClient = null;
        }

        if (_sshClient != null)
        {
            try
            {
                if (_sshClient.IsConnected)
                {
                    _sshClient.Disconnect();
                }
            }
            catch (ObjectDisposedException) { /* Игнорируем */ }

            _sshClient.Dispose();
            _sshClient = null;
        }
    }

    public async Task WriteToShellAsync(string command)
    {
        if (_shellStream != null)
        {
            await Task.Run(() => _shellStream.Write(command));
        }
    }

    public Renci.SshNet.ShellStream CreateShellStream(string terminalName, uint columns, uint rows, uint width, uint height, int bufferSize)
    {
        if (_sshClient == null || !_sshClient.IsConnected)
            throw new InvalidOperationException("SSH клиент не подключен.");

        return _sshClient.CreateShellStream(terminalName, columns, rows, width, height, bufferSize);
    }

    public async Task<int> ReadShellOutputAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        if (_shellStream != null)
        {
            return await _shellStream.ReadAsync(buffer, offset, count, token);
        }
        return 0;
    }

    public void ResizeTerminal(uint cols, uint rows)
    {
        if (_shellStream == null) return;

        var channelField = _shellStream.GetType().GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (channelField != null)
        {
            var channel = channelField.GetValue(_shellStream);
            if (channel != null)
            {
                var method = channel.GetType().GetMethod("SendWindowChangeRequest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                method?.Invoke(channel, new object[] { cols, rows, 0u, 0u });
            }
        }
    }

    public async Task<IEnumerable<(string Name, bool IsDir)>> ListDirectoryAsync(string path)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
            throw new InvalidOperationException("SFTP is not connected.");

        var files = await Task.Run(() => _sftpClient.ListDirectory(path));
        return files.Where(f => f.Name != ".")
                    .OrderByDescending(f => f.Name == "..")
                    .ThenByDescending(f => f.IsDirectory)
                    .ThenBy(f => f.Name)
                    .Select(f => (f.Name, f.IsDirectory))
                    .ToList();
    }

    public async Task DownloadFileAsync(string remotePath, Stream localStream)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        _logger.Log("SSH-LIB-TRACE", $"[SFTP] Запуск нативного метода DownloadFile: {remotePath}");
        await Task.Run(() => _sftpClient.DownloadFile(remotePath, localStream));
        _logger.Log("SSH-LIB-TRACE", $"[SFTP] Нативный метод DownloadFile завершен: {remotePath}");
    }

    public void UploadFile(Stream localStream, string remotePath)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;
        _sftpClient.UploadFile(localStream, remotePath);
    }

    // ИСПРАВЛЕНИЕ: Точное совпадение сигнатуры с ISshService
    public async Task<string> ExecuteCommandAsync(string commandText, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (_sshClient == null || !_sshClient.IsConnected) return string.Empty;

        TimeSpan actualTimeout = timeout ?? TimeSpan.FromSeconds(15);

        _logger.Log("SSH-CMD-TRACE", $"[СТАРТ] Запрос команды: {commandText.Substring(0, Math.Min(commandText.Length, 50))}... (Таймаут: {actualTimeout.TotalSeconds}с)");
        long startTick = Environment.TickCount64;

        try
        {
            var tcs = new TaskCompletionSource<string>();
            var cmd = _sshClient.CreateCommand(commandText);
            cmd.CommandTimeout = actualTimeout;

            using var ctr = cancellationToken.Register(() =>
            {
                try { cmd.CancelAsync(); } catch { }
                tcs.TrySetCanceled();
            });

            cmd.BeginExecute(ar =>
            {
                try { tcs.TrySetResult(cmd.EndExecute(ar)); }
                catch (Exception ex)
                {
                    _logger.Log("SSH-CMD-ERROR", $"Ошибка внутри BeginExecute: {ex.Message}");
                    tcs.TrySetResult(string.Empty);
                }
                finally { cmd.Dispose(); }
            }, null);

            var executeTask = tcs.Task;
            var delayTask = Task.Delay(actualTimeout, cancellationToken);

            if (await Task.WhenAny(executeTask, delayTask) == executeTask)
            {
                long duration = Environment.TickCount64 - startTick;
                _logger.Log("SSH-CMD-TRACE", $"[УСПЕХ] Команда выполнена за {duration} мс");
                return await executeTask;
            }
            else
            {
                _logger.Log("SSH-TIMEOUT", $"[ТАЙМАУТ] Команда зависла дольше {actualTimeout.TotalSeconds} секунд!");
                try { cmd.CancelAsync(); } catch { }
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.Log("SSH-CRASH", $"[КРАШ] Исключение в ExecuteCommandAsync: {ex.Message}");
            return string.Empty;
        }
    }

    public string GetWorkingDirectory()
    {
        return _sftpClient?.WorkingDirectory ?? "/root";
    }

    public void Dispose()
    {
        Disconnect();
    }
}