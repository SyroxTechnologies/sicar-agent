using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public class ConfigStorage : IConfigStorage
{
    private const string FileName = "config.dat";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("StockandriaAgent.v1");

    private readonly ILogger<ConfigStorage> _logger;
    private readonly string _directory;
    private bool _devModeWarningLogged;

    public ConfigStorage(ILogger<ConfigStorage> logger)
    {
        _logger = logger;
        _directory = ResolveStorageDirectory();
        Directory.CreateDirectory(_directory);
        ApplyDirectoryPermissions(_directory);
    }

    public string StoragePath => Path.Combine(_directory, FileName);

    public async Task<AgentConfig?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(StoragePath))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(StoragePath, ct);
            var plain = Decrypt(bytes);
            var cfg = JsonSerializer.Deserialize<AgentConfig>(plain, JsonOptions);
            return cfg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo leer la configuración en {Path}", StoragePath);
            return null;
        }
    }

    public async Task SaveAsync(AgentConfig config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var cipher = Encrypt(json);
        var tmp = StoragePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, cipher, ct);
        File.Move(tmp, StoragePath, overwrite: true);
        ApplyFilePermissions(StoragePath);
        _logger.LogInformation("Configuración persistida en {Path}", StoragePath);
    }

    private static string ResolveStorageDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "StockandriaAgent");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(home, "StockandriaAgent");
    }

    private byte[] Encrypt(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ProtectedData.Protect(bytes, DpapiEntropy, DataProtectionScope.LocalMachine);
        }
        LogDevModeWarningOnce();
        return bytes;
    }

    private string Decrypt(byte[] cipher)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        LogDevModeWarningOnce();
        return Encoding.UTF8.GetString(cipher);
    }

    private void LogDevModeWarningOnce()
    {
        if (_devModeWarningLogged) return;
        _devModeWarningLogged = true;
        _logger.LogWarning(
            "⚠️  MODO DEV: ConfigStorage usa plaintext (OS {Os}, DPAPI solo en Windows). " +
            "No usar en producción — solo desarrollo local.",
            RuntimeInformation.OSDescription);
    }

    private static void ApplyDirectoryPermissions(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // best-effort: si el FS no soporta chmod, seguimos.
        }
    }

    private static void ApplyFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}
