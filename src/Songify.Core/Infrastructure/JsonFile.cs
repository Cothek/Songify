using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songify.Core.Infrastructure;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not read {path}; starting fresh. The bad file was kept as .corrupt", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>Writes via a temp file so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, value, Options);
        File.Move(tmp, path, overwrite: true);
    }
}
