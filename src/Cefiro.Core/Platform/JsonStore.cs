using System.Text.Json;
using Cefiro.Ipc;
using Microsoft.Extensions.Logging;

namespace Cefiro.Core.Platform;

/// <summary>
/// Guarda y carga ficheros JSON de configuración de forma atómica (se escribe en un temporal y se
/// sustituye), para que un corte de luz nunca deje un fichero a medias.
/// </summary>
public sealed class JsonStore
{
    private readonly string _directory;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    public JsonStore(string directory, ILogger? logger = null)
    {
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;

    public string PathOf(string fileName) => Path.Combine(_directory, fileName);

    public T Load<T>(string fileName, Func<T> createDefault)
    {
        var path = PathOf(fileName);
        lock (_gate)
        {
            if (!File.Exists(path))
                return createDefault();

            try
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, CefiroJson.Options) ?? createDefault();
            }
            catch (Exception ex)
            {
                // Fichero dañado: se aparta para poder revisarlo y se usan los valores por defecto.
                var backup = path + $".dañado-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(path, backup, overwrite: true); } catch { /* lo intentamos */ }
                _logger?.LogError(ex, "No se pudo leer {Path}; se guarda una copia en {Backup} y se usan valores por defecto", path, backup);
                return createDefault();
            }
        }
    }

    public void Save<T>(string fileName, T value)
    {
        var path = PathOf(fileName);
        var temp = path + ".tmp";
        lock (_gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CefiroJson.Indented);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
    }
}
