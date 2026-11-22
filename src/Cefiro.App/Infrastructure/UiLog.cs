using System.IO;
using System.Text;

namespace Cefiro.App.Infrastructure;

/// <summary>Registro de errores de la interfaz (%APPDATA%\Cefiro\logs\interfaz-AAAAMMDD.log).</summary>
public static class UiLog
{
    private static readonly object Gate = new();
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cefiro", "logs");

    public static string DirectoryPath => Directory_;

    public static void Info(string message) => Write("INF", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Directory_);
                var file = Path.Combine(Directory_, $"interfaz-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}";
                File.AppendAllText(file, line, new UTF8Encoding(true));
            }
        }
        catch
        {
            // Si no se puede escribir el registro, no se hace nada más.
        }
    }
}
