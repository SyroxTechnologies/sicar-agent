using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace StockandriaAgent.Services;

/// <summary>
/// Provee un identificador estable de la instalación (la PC física donde
/// corre el agente). Permite al backend dedupear cuando el agente reinstala
/// perdiendo su config.dat — la misma PC sigue mapeando al mismo Agent.
///
/// Estrategia por SO:
///   - Windows: HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid
///     (generado al instalar Windows, persiste hasta reinstalación del SO).
///   - Linux:   /etc/machine-id (con fallback a /var/lib/dbus/machine-id).
///   - macOS y otros: hash de hostname como fallback (no persistente entre
///     renombrados, pero suficiente para dev local).
/// </summary>
public static class InstallationIdProvider
{
    public static string Get(ILogger? logger = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var windowsId = ReadWindowsMachineGuid();
                if (!string.IsNullOrWhiteSpace(windowsId)) return windowsId;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var linuxId = ReadLinuxMachineId();
                if (!string.IsNullOrWhiteSpace(linuxId)) return linuxId;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "No se pudo leer el identificador estable del SO. Usando fallback por hostname.");
        }

        return HostnameFallback();
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    private static string? ReadLinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static string HostnameFallback()
    {
        var hostname = Environment.MachineName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"hostname:{hostname}"));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
