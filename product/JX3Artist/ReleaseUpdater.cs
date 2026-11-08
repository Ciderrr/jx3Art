using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JX3Artist;

internal sealed record UpdateAsset(string Version, string Name, long Size, string Digest, Uri Url, bool Installer, long GitHubId = 0);

internal sealed record UpdateLaunch(string Path, bool Installer)
{
    internal void Start(string previousDirectory)
    {
        var start = new ProcessStartInfo(Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(Path)! };
        if (Installer)
        {
            foreach (string arg in new[] { "/SP-", "/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/JX3UPDATE", "/DIR=" + previousDirectory.TrimEnd('\\', '/') })
                start.ArgumentList.Add(arg);
        }
        if (Process.Start(start) == null) throw new IOException("未能打开新版，请在发布页重新下载。");
        if (!Installer)
        {
            try { IconChoices.SyncShortcut(System.IO.Path.Combine(start.WorkingDirectory, "Assets", "AppIcon.ico"), Path); }
            catch (Exception ex) { System.Windows.MessageBox.Show("已打开新版。桌面快捷方式未更新，可在新版设置中重新创建。\n" + ex.Message, "艺人助手"); }
        }
    }
}

internal static class ReleaseUpdater
{
    internal const string Releases = "https://github.com/Ciderrr/jx3Art/releases";
    const string Api = "https://api.github.com/repos/Ciderrr/jx3Art/releases";
    const long MaxDownload = 512L * 1024 * 1024;
    const long MaxExpanded = 1024L * 1024 * 1024;
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "install-mode.txt"));
    internal static string InstallDirectory => Installed && Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) == "app"
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "艺人助手.exe"))
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")) : AppContext.BaseDirectory;
    internal static string EntryPath => InstallDirectory == AppContext.BaseDirectory
        ? Path.Combine(AppContext.BaseDirectory, "JX3Artist.exe") : Path.Combine(InstallDirectory, "艺人助手.exe");

    internal static async Task<UpdateAsset?> Check(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Get(new Uri(Api + "/latest"), false, timeout.Token);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        for (int n; (n = await input.ReadAsync(buffer, timeout.Token)) > 0;)
        {
            if (data.Length + n > 1024 * 1024) throw new IOException("发行信息超出大小限制。");
            data.Write(buffer, 0, n);
        }
        using var json = JsonDocument.Parse(data.ToArray());
        return ReadAsset(json.RootElement, typeof(ReleaseUpdater).Assembly.GetName().Version!, Installed);
    }

    internal static UpdateAsset? ReadAsset(JsonElement release, Version current, bool installer)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"\Av[0-9]{1,5}\.[0-9]{1,5}(\.[0-9]{1,5})?\z") || !Version.TryParse(tag[1..], out var version))
            throw new IOException("无法识别正式版版本号。");
        if (new Version(version.Major, version.Minor, Math.Max(0, version.Build), 0) <= current) return null;
        string name = "JX3Artist-" + tag[1..] + (installer ? "-Setup.exe" : "-win-x64.zip");
        string expected = Releases + "/download/" + tag + "/" + name;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            string digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? "" : "";
            long size = asset.GetProperty("size").GetInt64();
            if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("browser_download_url").GetString() != expected ||
                !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z") || size <= 0 || size > MaxDownload)
                throw new IOException("发行文件的地址、大小或校验信息异常。");
            long id = asset.TryGetProperty("id", out var assetId) && assetId.ValueKind == JsonValueKind.Number &&
                assetId.TryGetInt64(out long value) && value > 0 ? value : 0;
            return new(tag[1..], name, size, digest[7..], new Uri(expected), installer, id);
        }
        throw new IOException("这个版本没有与你的安装方式对应的更新包。");
    }

    static async Task<HttpResponseMessage> Get(Uri uri, bool binary, CancellationToken cancellation)
    {
        for (int hops = 0; hops < 6; hops++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new IOException("更新链接不在允许的 GitHub 地址范围内。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("JX3Artist-Update/1.0");
            if (binary) request.Headers.Accept.ParseAdd("application/octet-stream");
            HttpResponseMessage response;
            try { response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
            catch (HttpRequestException ex) { throw new HttpRequestException($"无法连接 {uri.Host}。请确认代理可访问该地址。", ex); }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location;
                response.Dispose();
                uri = next != null ? new Uri(uri, next) : throw new IOException("GitHub 下载链接不完整。");
                continue;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.OK) return response;
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"{uri.Host} 返回 HTTP {(int)status}。", null, status);
        }
        throw new IOException("GitHub 下载重定向次数过多。");
    }

    internal static async Task<HttpResponseMessage> OpenAsset(UpdateAsset asset, CancellationToken cancellation,
        Func<Uri, bool, CancellationToken, Task<HttpResponseMessage>> fetch)
    {
        async Task<HttpResponseMessage> Binary(Uri uri, bool api)
        {
            var response = await fetch(uri, api, cancellation);
            string? content = response.Content.Headers.ContentType?.MediaType;
            if (content == "text/html" || content == "application/json" || content?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Dispose();
                throw new HttpRequestException($"{uri.Host} 未返回更新文件。");
            }
            return response;
        }

        HttpRequestException? apiError = null;
        if (asset.GitHubId > 0)
        {
            try { return await Binary(new Uri(Api + "/assets/" + asset.GitHubId), true); }
            catch (HttpRequestException ex) { apiError = ex; }
        }
        cancellation.ThrowIfCancellationRequested();
        try { return await Binary(asset.Url, false); }
        catch (HttpRequestException ex) when (apiError != null)
        {
            throw new HttpRequestException($"两种 GitHub 下载方式均未成功。{apiError.Message} {ex.Message} 可重试或在发布页下载。", ex);
        }
    }

    internal static async Task<UpdateLaunch> Download(UpdateAsset asset, string dataRoot, IProgress<int> progress, CancellationToken cancellation,
        Func<Uri, CancellationToken, Task<HttpResponseMessage>>? testDownload = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        string work = Path.Combine(dataRoot, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string file = Path.Combine(work, asset.Name);
        try
        {
            using var response = testDownload == null
                ? await OpenAsset(asset, timeout.Token, Get)
                : await testDownload(asset.Url, timeout.Token);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int previous = -1;
            var buffer = new byte[128 * 1024];
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                for (int n; (n = await input.ReadAsync(buffer, timeout.Token)) > 0;)
                {
                    total += n;
                    if (total > asset.Size) throw new IOException("下载内容超过发行文件大小。");
                    hash.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token);
                    int percent = (int)(total * 100 / asset.Size);
                    if (percent != previous) { progress.Report(percent); previous = percent; }
                }
            }
            if (total != asset.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Digest, StringComparison.OrdinalIgnoreCase))
                throw new IOException("下载文件校验失败，请重新下载。");
            if (asset.Installer) return new(file, true);
            string directory = await Task.Run(() => Extract(file, work, "JX3Artist-" + asset.Version + "-win-x64", timeout.Token), timeout.Token);
            string exe = Path.Combine(directory, "JX3Artist.exe");
            string? version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
            if (!Version.TryParse(version, out var found) || !Version.TryParse(asset.Version, out var wanted) ||
                found.Major != wanted.Major || found.Minor != wanted.Minor || Math.Max(0, found.Build) != Math.Max(0, wanted.Build))
                throw new IOException("新版程序的版本与发行信息不一致。");
            EngineBundle.Validate(Path.Combine(directory, "engine"));
            if (!File.Exists(Path.Combine(directory, "python", "python.exe")) || !File.Exists(Path.Combine(directory, "web", "index.html")))
                throw new IOException("新版程序的界面或识别环境不完整。");
            File.Delete(file);
            return new(exe, false);
        }
        catch
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            throw;
        }
    }

    internal static string Extract(string zipPath, string work, string rootName, CancellationToken cancellation)
    {
        string root = Path.GetFullPath(Path.Combine(work, rootName));
        if (Directory.Exists(root)) throw new IOException("新版目录已存在。");
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > MaxExpanded) throw new IOException("更新包超出解压预算。");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<(ZipArchiveEntry Entry,string Destination,bool Directory)>();
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            bool isDirectory = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts[0] != rootName || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                    p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase)) ||
                (entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new IOException("更新包包含不安全的文件路径。");
            string destination = Path.GetFullPath(Path.Combine(work, Path.Combine(parts)));
            if (destination == root && !isDirectory || destination != root && !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !destinations.Add(destination)) throw new IOException("更新包路径冲突。");
            paths.Add((entry,destination,isDirectory));
        }
        foreach (var (entry,destination,isDirectory) in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (isDirectory) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew);
                input.CopyToAsync(output, cancellation).GetAwaiter().GetResult();
            }
        }
        return root;
    }
}
