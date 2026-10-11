using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskFrz;

internal static class FeatureTests
{
    public static void Transfer(Action<bool, string> assert)
    {
        Display Screen(string id, int x, int width, int height = 500, bool primary = true, int scale = 100) =>
            new(id, id, id, primary, new(x, 0, width, height), new(x, 0, width, height), scale, 1, 1);
        Snapshot Make(List<Display> screens, List<DesktopIcon> icons, Position? spacing = null, uint flags = 0) =>
            new(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                Fingerprint.Configuration(screens, spacing ?? new(100, 100), 48, flags), screens, icons,
                spacing ?? new(100, 100), new(0, 0), 48, flags);
        var old = Make([Screen("small", 0, 500)], [new("a", "a", "small", 26, 2), new("b", "b", "small", 226, 102)]);
        var current = Make([Screen("small", -500, 500, primary: false), Screen("large", 0, 1000, 700)],
            old.Icons.Select(i => i with { MonitorId = "large", X = 426, Y = i.Y + 200 }).ToList());
        var plan = TransferPlanner.Build(old, current, "large");
        assert(plan.BlockedReason is null && plan.Moves.All(m => m.MonitorId == "large"), "single-screen icons move to new primary");
        assert(plan.Moves[1].X - plan.Moves[0].X == 200 && plan.Moves[1].Y - plan.Moves[0].Y == 100,
            "translation preserves exact pairwise displacement");
        var density = Make([Screen("large", 0, 1000, 700, scale: 150)], current.Icons, new(120, 120));
        var scaledDpi = TransferPlanner.Build(old, density, "large");
        assert(scaledDpi.Moves[1].X - scaledDpi.Moves[0].X == 200 && scaledDpi.Moves[1].Y - scaledDpi.Moves[0].Y == 100,
            "DPI and anisotropic spacing cannot stretch the layout");
        var touching = old with { Icons = [old.Icons[0], old.Icons[1] with { X = 126, Y = 2 }] };
        var crowdedDpi = TransferPlanner.Build(touching, density, "large", true);
        assert(crowdedDpi.BlockedReason is not null && crowdedDpi.Moves.Count == 0,
            "larger target icon footprints block overlap rather than insert empty cells");
        var edge = old with { Icons = old.Icons.Select(i => i with { X = i.X + 500 }).ToList() };
        var shifted = TransferPlanner.Build(edge, Make([Screen("large", 0, 500)], current.Icons), "large");
        assert(shifted.BlockedReason is null && shifted.Moves[1].X - shifted.Moves[0].X == 200,
            "edge overflow translates the whole block without distortion");
        var negative = Make([Screen("other", -1000, 1000)], current.Icons.Select(i => i with { MonitorId = "other" }).ToList());
        assert(TransferPlanner.Build(old, negative, "other").Moves[0].X == -974, "negative target origin preserves translation");
        var wide = Make([Screen("small", 0, 1000)], [new("a", "a", "small", 26, 2), new("b", "b", "small", 326, 152), new("c", "c", "small", 726, 302)]);
        var narrow = Make([Screen("large", 0, 500)], wide.Icons.Select(i => i with { MonitorId = "large" }).ToList());
        var resizeGate = TransferPlanner.Build(wide, narrow, "large");
        assert(resizeGate.BlockedReason == "需要整体等比例缩小布局。" && resizeGate.Moves.Count == 0,
            "uniform shrink needs explicit consent without partial moves");
        var resized = TransferPlanner.Build(wide, narrow, "large", true);
        assert(resized.BlockedReason is null && resized.Transfer!.ScaleFactor < 1, "whole block shrinks to fit");
        var factor = resized.Transfer!.ScaleFactor;
        assert(resized.Moves.Select((m, i) => Math.Abs(m.X - resized.Moves[0].X - (wide.Icons[i].X - wide.Icons[0].X) * factor) <= 1
            && Math.Abs(m.Y - resized.Moves[0].Y - (wide.Icons[i].Y - wide.Icons[0].Y) * factor) <= 1).All(v => v),
            "every displacement uses the same scale on both axes, within pixel rounding");
        var packed = Make([Screen("small", 0, 1000)], Enumerable.Range(0, 26).Select(i => new DesktopIcon(i.ToString(), i.ToString(), "small", 26 + i / 5 * 100, 2 + i % 5 * 100)).ToList());
        var noRoom = TransferPlanner.Build(packed, Make([Screen("large", 0, 500)], packed.Icons), "large", true);
        assert(noRoom.BlockedReason is not null && noRoom.Moves.Count == 0, "dense shrink blocks instead of reflowing individual icons");
        var multiple = Make([Screen("left", -500, 500, primary: false), Screen("right", 0, 500)],
            [new("a", "a", "left", -474, 2), new("b", "b", "right", 26, 2)]);
        assert(TransferPlanner.Build(multiple, current, "large", true).BlockedReason!.Contains("多个屏幕"), "multi-screen icons are never merged");
        var singleOnDual = multiple with { Icons = old.Icons.Select(i => i with { MonitorId = "right" }).ToList() };
        assert(TransferPlanner.Build(singleOnDual, current, "large").BlockedReason is null, "two connected screens with icons on only one screen can migrate");
        var newcomer = current with { Icons = [..current.Icons, new("new", "new", "large", 26, 2)] };
        assert(TransferPlanner.Build(old, newcomer, "large").BlockedReason!.Contains("新增"), "newcomer conflict cannot scatter original layout");
        var safeNewcomer = current with { Icons = [..current.Icons, new("new", "new", "large", 826, 502)] };
        assert(TransferPlanner.Build(old, safeNewcomer, "large").Moves.All(m => m.Id != "new"), "nonconflicting newcomers retain positions");
        assert(TransferPlanner.Build(old, current with { Icons = [current.Icons[0]] }, "large").MissingCount == 1, "deleted icons are skipped");
        assert(TransferPlanner.Build(old, current, "absent").BlockedReason is not null, "disconnected target is rejected");
        assert(TransferPlanner.Build(old, current with { FolderFlags = 1 }, "large").BlockedReason is not null, "auto arrange pauses migration");
        assert(TransferPlanner.Build(old, current with { Displays = [current.Displays[0], current.Displays[1] with { TargetCount = 2 }] }, "large").BlockedReason is not null, "clone mode pauses migration");
        foreach (int targetScale in new[] { 100, 125, 150, 175, 200, 225, 250, 300 })
        {
            var matrixSource = Make([Screen("source-any-brand", 0, 2200, 1800)],
                Enumerable.Range(0, 15).Select(n => new DesktopIcon("p" + n, "p" + n, "source-any-brand", 76 + n % 5 * 200, 2 + n / 5 * 220)).ToList(), new(200, 220), 4);
            var targetScreen = Screen("target-any-brand", -1800, 1800, 2400, scale: targetScale) with {
                Bounds = new(-1800, -400, 1800, 2400), WorkArea = new(-1800, -400, 1800, 2340) };
            var matrixCurrent = Make([Screen("source-any-brand", 0, 2200, 1800, primary: false), targetScreen,
                Screen("third-empty", 2200, 1920, 1080, primary: false)], matrixSource.Icons,
                new(2 * targetScale, (int)Math.Round(2.2 * targetScale)), 4);
            var result = TransferPlanner.Build(matrixSource, matrixCurrent, targetScreen.Id, true);
            assert(result.BlockedReason is null && result.Moves.Count == 15
                && result.Moves.Select((m, n) => m.X - result.Moves[0].X == matrixSource.Icons[n].X - matrixSource.Icons[0].X
                    && m.Y - result.Moves[0].Y == matrixSource.Icons[n].Y - matrixSource.Icons[0].Y).All(v => v)
                && result.Moves.All(m => targetScreen.WorkArea.Contains(new(m.X, m.Y))),
                $"three-screen portrait negative-origin shape invariance at {targetScale}% DPI");
        }
        var tracker = new ScreenSwitchTracker(); tracker.Observe(old); tracker.Observe(current); tracker.Observe(current);
        assert(tracker.Source?.Id == old.Id, "first use captures pre-switch positions without a saved record and retains source across polls");
        tracker.Clear(current); tracker.Observe(current);
        assert(tracker.Source is null, "completed migration clears switch prompt");
        var connected = old with { Displays = [old.Displays[0], Screen("large", 500, 1000, primary: false)] };
        var connectionTracker = new ScreenSwitchTracker(); connectionTracker.Observe(old); connectionTracker.Observe(connected);
        assert(connectionTracker.Source is null, "connecting a secondary display cannot trigger migration");
        var splitBefore = connected with { Icons = [old.Icons[0], old.Icons[1] with { MonitorId = "large", X = 526 }] };
        connectionTracker.Observe(splitBefore); connectionTracker.Observe(current);
        assert(connectionTracker.Source?.Icons.Select(i => i.MonitorId).Distinct().Count() == 2,
            "latest pre-switch layout detects icons distributed after connecting second screen");
        var after = current with { Icons = current.Icons.Select(i => { var m = plan.Moves.Single(m => m.Id == i.Id); return i with { MonitorId = m.MonitorId, X = m.X, Y = m.Y }; }).ToList() };
        assert(Verifier.Matches(current, plan, after), "native verifier accepts rigid block migration");
    }

    public static async Task Updates(string directory, Action<bool, string> assert)
    {
        var payload = Encoding.ASCII.GetBytes("MZ-test-installer");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        byte[] Release(string tag = "v9.0.0", string owner = UpdateService.Repository, bool prerelease = false, bool draft = false) => JsonSerializer.SerializeToUtf8Bytes(new {
            tag_name = tag, draft, prerelease, body = "测试更新内容", assets = new[] {
                new { name = UpdateService.InstallerName, browser_download_url = $"https://github.com/{owner}/releases/download/{tag}/{UpdateService.InstallerName}", digest = "sha256:" + hash },
                new { name = "SHA256SUMS.txt", browser_download_url = $"https://github.com/{owner}/releases/download/{tag}/SHA256SUMS.txt", digest = "" }
            }
        });
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        assert(UpdateService.ParseRelease(Release()).Version == "9.0.0", "parse stable GitHub release");
        assert(Reject(() => UpdateService.ParseRelease(Release(prerelease: true))) && Reject(() => UpdateService.ParseRelease(Release(draft: true))), "reject draft and prerelease updates");
        assert(Reject(() => UpdateService.ParseRelease(Release(owner: "attacker/app"))), "reject foreign installer URL");
        assert(Reject(() => UpdateService.ParseRelease(Release(tag: "v9.0.0-beta"))), "reject invalid update version");
        assert(UpdateService.ParseChecksum("\uFEFF" + hash + "  DeskFrzA-Setup.exe\r\n") == hash, "parse published BOM and CRLF checksum");
        assert(Reject(() => UpdateService.ParseChecksum(hash + "  other.exe")) && Reject(() => UpdateService.ParseChecksum($"{hash}  DeskFrzA-Setup.exe\n{hash}  DeskFrzA-Setup.exe")), "reject missing or duplicate installer checksums");
        var testDirectory = Path.Combine(directory, "updates-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            File.WriteAllText(Path.Combine(testDirectory, "unins000.exe"), "test marker only, never executed");
            bool corrupt = false;
            HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var handler = new FakeHttp(request => request.RequestUri!.Host == "api.github.com" ? Response(Release())
                : request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt") ? Response(Encoding.UTF8.GetBytes(hash + "  DeskFrzA-Setup.exe\n"))
                : Response(corrupt ? Encoding.ASCII.GetBytes("MZ-tampered") : payload));
            using var service = new UpdateService(testDirectory, true, handler, "0.1.6", testDirectory);
            assert((await service.CheckAsync()).Status == "available", "new version becomes available");
            assert((await service.DownloadAsync("9.0.0")).Ready, "downloaded installer must pass release digest and checksum");
            var path = await service.PrepareInstallAsync("9.0.0");
            File.AppendAllText(path, "changed");
            bool tampered = false;
            try { await service.PrepareInstallAsync("9.0.0"); } catch (InvalidDataException) { tampered = true; }
            assert(tampered && !service.Status.Ready, "reverify installer immediately before launch and allow clean redownload");
            corrupt = true;
            bool refused = false;
            try { await service.DownloadAsync("9.0.0"); } catch (InvalidDataException) { refused = true; }
            assert(refused && !service.Status.Ready && !Directory.GetFiles(Path.Combine(testDirectory, "updates"), "*.tmp").Any(), "corrupt download is rejected and temporary file cleaned");
            using var portable = new UpdateService(testDirectory, false, new FakeHttp(_ => Response(Release())), "0.1.6", testDirectory);
            await portable.CheckAsync();
            bool portableRefused = false;
            try { await portable.DownloadAsync("9.0.0"); } catch (InvalidOperationException) { portableRefused = true; }
            assert(!portable.Status.CanInstall && portableRefused, "portable build offers manual update instead of installer overwrite");
            using var empty = new UpdateService(testDirectory, false, new FakeHttp(_ => new(HttpStatusCode.NotFound)), "0.1.6");
            assert((await empty.CheckAsync()).Status == "unpublished", "unpublished repository is reported without claiming latest version");
            using var latest = new UpdateService(testDirectory, false, new FakeHttp(_ => Response(Release("v0.1.6"))), "0.1.6");
            assert((await latest.CheckAsync()).Status == "current", "equal release version is not offered as update");
        }
        finally { Directory.Delete(testDirectory, true); }
    }

    sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
