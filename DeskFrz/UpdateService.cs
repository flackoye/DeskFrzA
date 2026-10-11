using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskFrz;

public record UpdateInfo(string CurrentVersion, string? LatestVersion, string Status, string Notes,
    bool CanInstall, bool Ready, string ReleaseUrl);
public record UpdateRelease(string Version, string Tag, Uri Installer, Uri Checksums, string ReleaseUrl,
    string Notes, string? Digest);

/// <summary>Only public stable releases from the project's fixed GitHub repository are trusted.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "flackoye/DeskFrzA";
    public const string InstallerName = "DeskFrzA-Setup.exe";
    const long MaxInstallerBytes = 250L * 1024 * 1024;
    readonly HttpClient http;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string directory, appDirectory, dataDirectory;
    readonly bool canInstall;
    readonly Version current;
    UpdateRelease? release;
    string? installerPath, installerHash;
    UpdateInfo info;
    public UpdateInfo Status => info;

    public UpdateService(string dataDirectory, bool native, HttpMessageHandler? handler = null, string? version = null, string? applicationDirectory = null)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        directory = Path.Combine(this.dataDirectory, "updates");
        appDirectory = (applicationDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        canInstall = native && File.Exists(Path.Combine(appDirectory, "unins000.exe"));
        string running = version ?? Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
        current = Version.Parse(running);
        info = new(running, null, "unchecked", "", canInstall, false, $"https://github.com/{Repository}/releases");
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskFrzA/{running}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                release = null; installerPath = installerHash = null;
                return info = info with { LatestVersion = null, Status = "unpublished", Notes = "仓库尚未公开，或尚无正式 Release。", Ready = false };
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("GitHub 暂时限制检查频率，请稍后重试。");
            response.EnsureSuccessStatusCode();
            var next = ParseRelease(await ReadBounded(response, 1024 * 1024, timeout.Token));
            if (release != next) { installerPath = installerHash = null; }
            release = next;
            bool newer = Version.Parse(next.Version) > current;
            return info = info with { LatestVersion = next.Version, Status = newer ? "available" : "current",
                Notes = next.Notes, Ready = newer && installerPath is not null, ReleaseUrl = next.ReleaseUrl };
        }
        catch
        {
            release = null; installerPath = installerHash = null;
            info = info with { Status = "failed", Ready = false };
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<UpdateInfo> DownloadAsync(string expectedVersion, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        string? temporary = null;
        try
        {
            var next = release ?? throw new InvalidOperationException("请先检查更新。");
            if (!canInstall) throw new InvalidOperationException("便携版请从 Releases 下载新便携包，解压后替换原程序。");
            if (next.Version != expectedVersion || Version.Parse(next.Version) <= current)
                throw new InvalidOperationException("更新版本已改变，请重新检查。");
            installerPath = installerHash = null; info = info with { Ready = false };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var sums = await http.GetAsync(next.Checksums, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            sums.EnsureSuccessStatusCode();
            string hash = ParseChecksum(Encoding.UTF8.GetString(await ReadBounded(sums, 64 * 1024, timeout.Token)));
            if (next.Digest is not null && !string.Equals(next.Digest, "sha256:" + hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Release 校验信息不一致，已取消更新。");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $"download-{Guid.NewGuid():N}.tmp");
            using var response = await http.GetAsync(next.Installer, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxInstallerBytes) throw new InvalidDataException("安装包超出大小限制。");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes) throw new InvalidDataException("安装包超出大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }
            await VerifyFile(temporary, hash, timeout.Token);
            var final = Path.Combine(directory, $"DeskFrzA-{next.Version}-Setup.exe");
            File.Move(temporary, final, true); temporary = null;
            installerPath = final; installerHash = hash;
            return info = info with { Ready = true };
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    public async Task<string> PrepareInstallAsync(string expectedVersion, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (!canInstall || !info.Ready || info.LatestVersion != expectedVersion || installerPath is null || installerHash is null)
                throw new InvalidOperationException("安装包尚未准备好，请重新检查更新。");
            await VerifyFile(installerPath, installerHash, cancellation);
            return installerPath;
        }
        catch
        {
            installerPath = installerHash = null;
            info = info with { Ready = false };
            throw;
        }
        finally { gate.Release(); }
    }

    public void LaunchInstaller(string verifiedPath)
    {
        if (verifiedPath != installerPath || !canInstall) throw new InvalidOperationException("更新安装路径无效。");
        var start = new System.Diagnostics.ProcessStartInfo(verifiedPath) { UseShellExecute = true };
        foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/DESKFRZAUPDATE=1", "/DESKFRZADATA=" + dataDirectory, "/DIR=" + appDirectory })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动更新安装程序。");
    }

    public static UpdateRelease ParseRelease(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("自动更新仅接受已发布的正式版本。");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var parsed))
            throw new InvalidDataException("Release 版本号格式无效。");
        JsonElement Asset(string name) => root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
        var installer = Asset(InstallerName);
        Uri AssetUri(JsonElement asset, string name)
        {
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            var expected = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
            if (uri != expected || uri.UserInfo.Length != 0) throw new InvalidDataException("更新文件不属于项目的 GitHub Release。");
            return uri;
        }
        string? digest = installer.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        return new(parsed.ToString(3), tag, AssetUri(installer, InstallerName), AssetUri(Asset("SHA256SUMS.txt"), "SHA256SUMS.txt"),
            $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}", notes[..Math.Min(notes.Length, 6000)], digest);
    }
    public static string ParseChecksum(string text)
    {
        var matches = Regex.Matches(text.TrimStart('\uFEFF'), @"^([a-fA-F0-9]{64})[ \t]+\*?DeskFrzA-Setup\.exe\r?$", RegexOptions.Multiline);
        if (matches.Count != 1) throw new InvalidDataException("缺少唯一有效的安装包 SHA256 校验值。");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
    static async Task VerifyFile(string path, string hash, CancellationToken cancellation)
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验失败，已取消更新。");
        file.Position = 0;
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("下载文件不是 Windows 安装程序。");
    }
    static async Task<byte[]> ReadBounded(HttpResponseMessage response, int maxBytes, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException("更新信息超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
