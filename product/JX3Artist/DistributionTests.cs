using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace JX3Artist;

internal static class DistributionTests
{
    internal static void Add(Action<string, Action> test)
    {
        test("updates select matching stable GitHub assets and reject unexpected download targets", () =>
        {
            using var release = JsonDocument.Parse(ReleaseJson("v1.1", "JX3Artist-1.1-win-x64.zip", false));
            if (ReleaseUpdater.ReadAsset(release.RootElement, new Version(1,0,0,0), false)?.Version != "1.1") throw new Exception("new ZIP not selected");
            if (ReleaseUpdater.ReadAsset(release.RootElement, new Version(1,1,0,0), false) != null) throw new Exception("same version selected");
            using var installer = JsonDocument.Parse(ReleaseJson("v1.1", "JX3Artist-1.1-Setup.exe", false));
            if (ReleaseUpdater.ReadAsset(installer.RootElement, new Version(1,0,0,0), true)?.Installer != true) throw new Exception("installer not selected");
            using var prerelease = JsonDocument.Parse(ReleaseJson("v1.1", "JX3Artist-1.1-win-x64.zip", true));
            if (ReleaseUpdater.ReadAsset(prerelease.RootElement, new Version(1,0,0,0), false) != null) throw new Exception("pre-release selected");
            using var unsafeUrl = JsonDocument.Parse(ReleaseJson("v1.1", "JX3Artist-1.1-win-x64.zip", false).Replace("https://github.com/", "https://other.example/"));
            Reject(() => ReleaseUpdater.ReadAsset(unsafeUrl.RootElement, new Version(1,0,0,0), false));
        });
        test("portable update validates the whole archive before writing and refuses an existing version directory", () =>
        {
            string work = Path.Combine(Path.GetTempPath(), "jx3-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            const string root = "JX3Artist-1.1-win-x64";
            string zip = Path.Combine(work, "update.zip");
            try
            {
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    Write(archive, root + "/JX3Artist.exe");
                    Write(archive, root + "/../outside.txt");
                }
                Reject(() => ReleaseUpdater.Extract(zip, work, root, CancellationToken.None));
                if (Directory.Exists(Path.Combine(work, root)) || File.Exists(Path.Combine(work, "outside.txt"))) throw new Exception("invalid archive wrote files");
                File.Delete(zip);
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) Write(archive, root + "/JX3Artist.exe");
                string installed = ReleaseUpdater.Extract(zip, work, root, CancellationToken.None);
                if (File.ReadAllText(Path.Combine(installed, "JX3Artist.exe")) != "fixture") throw new Exception("safe extraction failed");
                Reject(() => ReleaseUpdater.Extract(zip, work, root, CancellationToken.None));
            }
            finally
            {
                string exe = Path.Combine(work, root, "JX3Artist.exe");
                if (File.Exists(exe)) File.Delete(exe);
                if (Directory.Exists(Path.Combine(work, root))) Directory.Delete(Path.Combine(work, root));
                if (File.Exists(zip)) File.Delete(zip);
                Directory.Delete(work);
            }
        });
        test("startup distinguishes missing bundled recognition files from a complete runtime manifest", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "jx3-start-test-" + Guid.NewGuid().ToString("N"));
            var missing = StartupEnvironment.MissingFiles(root, true);
            if (!missing.Contains("python/python.exe") || !missing.Contains("web/index.html") || missing.Count(f => f.StartsWith("engine/")) != EngineBundle.Files.Length)
                throw new Exception("incomplete bundle was misclassified");
            if (StartupEnvironment.MissingFiles(root, false).Contains("python/python.exe")) throw new Exception("developer build requires bundled Python");
        });
        test("update payload must match the release size and SHA256 before it can be launched", () =>
        {
            string work = Path.Combine(Path.GetTempPath(), "jx3-download-test-" + Guid.NewGuid().ToString("N"));
            byte[] correct = {77,90,1,2,3};
            var asset = new UpdateAsset("1.1", "JX3Artist-1.1-Setup.exe", correct.Length,
                Convert.ToHexString(SHA256.HashData(correct)), new Uri(ReleaseUpdater.Releases), true);
            UpdateLaunch Fetch(byte[] bytes) => ReleaseUpdater.Download(asset, work, new Progress<int>(), CancellationToken.None,
                (_,_) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })).GetAwaiter().GetResult();
            try
            {
                Reject(() => Fetch(new byte[]{77,90,1,2,4}));
                Reject(() => Fetch(new byte[]{77,90,1}));
                var launch = Fetch(correct);
                if (!launch.Installer || !File.ReadAllBytes(launch.Path).SequenceEqual(correct)) throw new Exception("verified setup was not staged");
                File.Delete(launch.Path);
            }
            finally
            {
                string updates = Path.Combine(work, "updates");
                foreach (string dir in Directory.GetDirectories(updates)) Directory.Delete(dir);
                Directory.Delete(updates);
                Directory.Delete(work);
            }
        });
    }

    static string ReleaseJson(string tag, string asset, bool prerelease) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease,
        assets = new[] { new { name = asset, size = 200L, state = "uploaded", digest = "sha256:"+new string('a',64), browser_download_url = ReleaseUpdater.Releases+"/download/"+tag+"/"+asset } }
    });

    static void Write(ZipArchive archive, string name)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write("fixture");
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("unsafe update was accepted");
    }
}
