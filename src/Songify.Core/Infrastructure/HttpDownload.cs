namespace Songify.Core.Infrastructure;

/// <summary>Streams a URL to a file, with throttled progress.</summary>
public static class HttpDownload
{
    private const long Megabyte = 1_048_576;

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="path"/> through a ".download" file that is moved into place
    /// only when complete. <paramref name="progress"/> (bytes done, total if known) is called on the downloading thread
    /// when the whole percent changes, or per megabyte without a known size.
    /// </summary>
    /// <param name="maxBytes">Stop with an error if the download grows past this (a known expected size).</param>
    public static async Task ToFileAsync(
        HttpClient http, string url, string path, Action<long, long?>? progress, CancellationToken ct,
        long? maxBytes = null)
    {
        var partial = path + ".download";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? maxBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0, lastStep = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    if (done > maxBytes) throw new InvalidDataException("The download is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    var step = total > 0 ? done * 100 / total.Value : done / Megabyte;
                    if (step != lastStep)
                    {
                        lastStep = step;
                        progress?.Invoke(done, total);
                    }
                }
            }
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } // only still there if the download failed
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
