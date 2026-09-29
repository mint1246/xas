using System.Text;
using Xas.Core.Configuration;

namespace Xas.Daemon;

/// <summary>Best-effort durable logging for automatic Windows KVM handoff diagnostics.</summary>
internal static class KvmDiagnosticLog
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxMessageCharacters = 4096;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try { Console.Error.WriteLine(message); }
        catch { /* A service process may not have a usable stderr handle. */ }

        try
        {
            if (message.Length > MaxMessageCharacters)
                message = message[..MaxMessageCharacters] + "…";
            var line = $"{DateTimeOffset.UtcNow:O} KVM {message}{Environment.NewLine}";
            var bytes = Utf8.GetBytes(line);
            lock (Gate)
            {
                var root = AppPaths.Root;
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "daemon.log");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaxFileBytes)
                {
                    var rotated = path + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(path, rotated);
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Diagnostics must never change monitor or input handoff behavior.
        }
    }
}
