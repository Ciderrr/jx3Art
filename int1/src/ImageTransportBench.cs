using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Int1;

internal static class ImageTransportBench
{
    internal sealed class DecodeProbe : IDisposable
    {
        private readonly Process _process;
        public DecodeProbe()
        {
            string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "decode_probe.py"));
            _process = new Process { StartInfo = new ProcessStartInfo("py") {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) } };
            _process.StartInfo.ArgumentList.Add(script);
            _process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
            try
            {
                _process.Start(); _process.BeginErrorReadLine();
                using var ready = Read();
                if (!ready.RootElement.GetProperty("ready").GetBoolean()) throw new IOException("decode probe not ready");
            }
            catch { Dispose(); throw; }
        }
        private JsonDocument Read()
        {
            var task = _process.StandardOutput.ReadLineAsync();
            if (!task.Wait(20000)) throw new TimeoutException("offline decode probe timed out");
            return JsonDocument.Parse(task.Result ?? throw new IOException("decode probe EOF"));
        }
        public JsonDocument Decode(string path)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(new { path = Path.GetFullPath(path) }));
            _process.StandardInput.Flush(); return Read();
        }
        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            _process.Dispose();
        }
    }

    internal static (byte[] Data, int Width, int Height) DecodeSource(string path)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit();
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return (bytes, converted.PixelWidth, converted.PixelHeight);
    }
    internal static string BgrHash(byte[] bgra)
    {
        var bgr = new byte[bgra.Length / 4 * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        { bgr[j] = bgra[i]; bgr[j+1] = bgra[i+1]; bgr[j+2] = bgra[i+2]; }
        return Convert.ToHexString(SHA256.HashData(bgr)).ToLowerInvariant();
    }
    public static int Run(string output, string sourceDir)
    {
        Directory.CreateDirectory(output);
        string temp = Path.Combine(output, "transport-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var variants = new (string Name, string Extension, Action<byte[],int,int,string> Save)[] {
            ("legacy_png_reference", ".png", MonitorCore.SavePng),
            ("production_bmp_codec", ".bmp", BitmapFrameTransport.SaveBmp),
        };
        using var probe = new DecodeProbe(); var records = new List<object>(); bool all = true;
        foreach (string frame in new[] { "f000001.png", "f000011.png", "f000031.png" })
        {
            var (data,w,h) = DecodeSource(Path.Combine(sourceDir, frame)); string expected = BgrHash(data);
            foreach (var variant in variants)
            for (int iteration = 0; iteration < 3; iteration++)
            {
                string path = Path.Combine(temp, variant.Name + variant.Extension);
                try
                {
                    long start = Stopwatch.GetTimestamp(); variant.Save(data,w,h,path);
                    double encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long bytes = new FileInfo(path).Length;
                    using var decoded = probe.Decode(path); var value = decoded.RootElement;
                    if (!value.TryGetProperty("ok",out var ok) || !ok.GetBoolean())
                        throw new IOException("decode probe refused frame: " + value.GetRawText());
                    bool exact = value.GetProperty("width").GetInt32() == w
                        && value.GetProperty("height").GetInt32() == h && value.GetProperty("bgr_sha256").GetString() == expected;
                    all &= exact;
                    records.Add(new { frame, variant = variant.Name, iteration, warmup = iteration == 0,
                        encodeMs, decodeMs = value.GetProperty("decode_ms").GetDouble(), bytes, pixelsExactlyEqual = exact });
                    Console.WriteLine($"{frame} {variant.Name} #{iteration} encode={encodeMs:F1}ms bytes={bytes} exact={exact}");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        }
        File.WriteAllText(Path.Combine(output,"transport_benchmark.json"), JsonSerializer.Serialize(new {
            mode = "offline saved-frame benchmark; encoding includes file write, decode includes file read; OS cache not controlled; not live WGC latency",
            sourceDir = Path.GetFullPath(sourceDir), pixelCheckAllPassed = all, records
        }, new JsonSerializerOptions { WriteIndented = true }));
        // Only the exact empty folder created by this run is removed; never recursive.
        Directory.Delete(temp);
        return all ? 0 : 9;
    }
}
