using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BsDiff;
using JustEnoughTools.Updates;

namespace JustEnoughTools.Tests
{
    public static class UpdateTests
    {
        private static int _assertions;

        public static int RunAll()
        {
            _assertions = 0;
            PackageRoundTripAndTamper();
            InvalidPackageNeverDeletesTheOnlyExecutable();
            ManifestBoundsAndAssetIdentity();
            HttpBoundariesAndIdentity();
            ProgressLinesKeepTheirBudget();
            FullDownloadUsesOnlyOnePackage().GetAwaiter().GetResult();
            SameAndOlderReleasesAreIgnored().GetAwaiter().GetResult();
            FailedNewDownloadKeepsPreparedUpdate().GetAwaiter().GetResult();
            DeltaRoundTripAndFallback().GetAwaiter().GetResult();
            ChangedBaseCannotBePatched();
            CancellationDoesNotPublish().GetAwaiter().GetResult();
            _assertions += HttpBodyTests.RunAll();
            _assertions += InstallerTests.RunAll();
            return _assertions;
        }

        private static void PackageRoundTripAndTamper()
        {
            using (var fixture = new Fixture())
            {
                byte[] payload = Exe(2);
                UpdateManifest manifest = Manifest(payload, 2);
                UpdatePackage.Write(fixture.Package, payload, manifest);
                PreparedUpdate prepared;
                string error;
                Check(UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), error);
                Check(prepared.PayloadLength == payload.Length, "Payload boundary was lost.");
                Check(prepared.Manifest.ReleaseNumber == 2, "Release was lost.");
                Check((File.GetAttributes(fixture.Package) & FileAttributes.Hidden) != 0, "Package must be hidden.");
                UpdatePackage.Write(fixture.Package, payload, manifest);
                Check(UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), "An existing hidden package must be replaceable.");
                Check(!UpdatePackage.TryRead(fixture.Package, 2, out prepared, out error), "A cached equal version must not install.");
                Check(!UpdatePackage.TryRead(fixture.Package, 3, out prepared, out error), "A cached older version must not install.");
                using (var stream = new FileStream(fixture.Package, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Check(stream.ReadByte() == 'M' && stream.ReadByte() == 'Z', "EXE must precede metadata.");
                    stream.Position = 150;
                    stream.WriteByte(0xff);
                }
                Check(!UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), "A tampered payload was accepted.");
            }
        }

        private static void InvalidPackageNeverDeletesTheOnlyExecutable()
        {
            using (var fixture = new Fixture())
            {
                File.WriteAllBytes(fixture.Package, Exe(9)); // Interrupted after footer truncation.
                File.Delete(fixture.Executable);
                UpdatePackage.DeleteInvalidIfSafe(fixture.Package, fixture.Executable);
                Check(File.Exists(fixture.Package), "The only executable copy was deleted.");
                File.WriteAllText(fixture.Executable, "broken");
                UpdatePackage.DeleteInvalidIfSafe(fixture.Package, fixture.Executable);
                Check(File.Exists(fixture.Package), "A broken original cannot justify deleting a pending payload.");
                File.Copy(typeof(UpdateService).Assembly.Location, fixture.Executable, true);
                UpdatePackage.DeleteInvalidIfSafe(fixture.Package, fixture.Executable);
                Check(!File.Exists(fixture.Package), "An invalid package should be removed when the original is viable.");
            }
        }

        private static void ManifestBoundsAndAssetIdentity()
        {
            byte[] payload = Exe(2);
            UpdateManifest manifest = Manifest(payload, 2);
            UpdateService.ValidateManifest(manifest);
            Check(true, "Valid schema2 manifest.");
            manifest.AssetName = "../JustEnoughTools.exe";
            Throws<InvalidDataException>(() => UpdateService.ValidateManifest(manifest));
            manifest = Manifest(payload, 2);
            manifest.SizeBytes = UpdatePackage.MaxPayloadBytes + 1L;
            Throws<InvalidDataException>(() => UpdateService.ValidateManifest(manifest));
            manifest = Manifest(payload, 2);
            manifest.Sha256 = new string('z', 64);
            Throws<InvalidDataException>(() => UpdateService.ValidateManifest(manifest));
            manifest = Manifest(payload, 2);
            manifest.DeltaPatches.Add(Delta(new byte[] { 1 }, "../JustEnoughTools.bsdiff"));
            Throws<InvalidDataException>(() => UpdateService.ValidateManifest(manifest));
            manifest = Manifest(payload, 2);
            manifest.DeltaPatches.Add(Delta(new byte[] { 1 }));
            manifest.DeltaPatches.Add(Delta(new byte[] { 2 }, "JustEnoughTools.from-1.bsdiff"));
            Throws<InvalidDataException>(() => UpdateService.ValidateManifest(manifest));
            Throws<InvalidDataException>(() => UpdatePackage.DeserializeManifest(new byte[UpdatePackage.MaxManifestBytes + 1]));
        }

        private static void HttpBoundariesAndIdentity()
        {
            UpdateService.ValidateDownloadUri(new Uri("https://release-assets.githubusercontent.com/release.exe"));
            Check(true, "GitHub release CDN must work.");
            Throws<InvalidDataException>(() => UpdateService.ValidateDownloadUri(new Uri("http://github.com/a")));
            Throws<InvalidDataException>(() => UpdateService.ValidateDownloadUri(new Uri("https://github.com.evil.invalid/a")));
            Throws<InvalidDataException>(() => UpdateService.ValidateDownloadUri(new Uri("https://user@github.com/a")));
            string path = Path.Combine(Path.GetTempPath(), "JET", "JustEnoughTools.exe");
            Check(UpdateService.GetInstanceKey(path) == UpdateService.GetInstanceKey(path.ToUpperInvariant()), "Windows path identity must ignore casing.");
            Check(UpdatePackage.GetPath(path) == Path.Combine(Path.GetDirectoryName(path), "JustEnoughTools.update"), "Package escaped the EXE directory.");
        }

        private static async Task FullDownloadUsesOnlyOnePackage()
        {
            using (var fixture = new Fixture())
            {
                byte[] payload = Exe(2);
                var handler = new FakeHandler(Manifest(payload, 2), payload, null);
                using (var client = new HttpClient(handler))
                using (var service = new UpdateService(fixture.Executable, 1, client))
                {
                    var statuses = new List<UpdateStatus>();
                    service.StatusChanged += statuses.Add;
                    await service.CheckAndPrepareAsync(CancellationToken.None);
                    Check(service.HasPreparedUpdate, "Full update was not prepared.");
                    Check(handler.ExecutableDownloads == 1 && handler.PatchDownloads == 0, "Unexpected download route.");
                    Check(statuses.Last().Ready, "Ready status was not published.");
                    Check(Directory.GetFiles(fixture.Root).Length == 2 && Directory.GetDirectories(fixture.Root).Length == 0,
                        "Updater created additional files or folders.");
                    PreparedUpdate prepared;
                    string error;
                    Check(UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), error);
                }
            }
        }

        private static void ProgressLinesKeepTheirBudget()
        {
            Check(UpdateProgressLine.UpToDate(12) == "Вы на последней версии (12)", "The original up-to-date wording changed.");
            Check(UpdateProgressLine.Ready(12) == "Обновление 12 готово к установке", "The original ready wording changed.");
            Check(UpdateProgressLine.Applying(12) == "Применение обновления 12", "The original delta wording changed.");
            Check(UpdateProgressLine.Checking() == "Проверка обновлений", "The checking wording changed.");
            Check(UpdateProgressLine.CheckFailed() == "Не удалось проверить обновления", "A failed check must not claim success.");
            Check(UpdateProgressLine.DownloadFailed(12).Contains("12"), "A download error must name its release.");
            Check(TransferProgressLine.Remaining(TimeSpan.FromSeconds(3)) == "ещё ≈ 5 с", "ETA must not promise an immediate arrival.");
            Check(TransferProgressLine.Remaining(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(20)) == "ещё ≈ 3 мин 20 с", "ETA formatting changed.");
            Check(UpdateProgressLine.Estimate(0, 100, 0) == null, "A stalled download has no trustworthy ETA.");
            Check(UpdateProgressLine.Estimate(50, 100, 10) == TimeSpan.FromSeconds(5), "ETA arithmetic is incorrect.");
            foreach (int release in new[] { 1, 9999, int.MaxValue })
            foreach (double speed in new[] { 0d, 1024d, 600d * 1024 * 1024 })
            foreach (TimeSpan? remaining in new TimeSpan?[] { null, TimeSpan.FromSeconds(200), TimeSpan.FromHours(23) + TimeSpan.FromMinutes(55) })
            {
                string text = UpdateProgressLine.Downloading(release, 50 * 1024 * 1024, 64 * 1024 * 1024, speed, remaining);
                Check(text.Length <= UpdateProgressLine.Budget, "The update line exceeds the 68-character budget: " + text);
                Check(text.Contains(release.ToString()), "Progress lost the release number.");
                if (remaining.HasValue) Check(text.Contains("ещё ≈"), "A long line dropped ETA instead of speed.");
            }
        }

        private static async Task SameAndOlderReleasesAreIgnored()
        {
            foreach (int remote in new[] { 1, 2 })
            {
                using (var fixture = new Fixture())
                {
                    var handler = new FakeHandler(Manifest(Exe(remote), remote), Exe(remote), null);
                    using (var client = new HttpClient(handler))
                    using (var service = new UpdateService(fixture.Executable, 2, client))
                    {
                        await service.CheckAndPrepareAsync(CancellationToken.None);
                        Check(!service.HasPreparedUpdate && !File.Exists(fixture.Package), "A non-newer release was prepared.");
                        Check(handler.ExecutableDownloads == 0, "A non-newer EXE was downloaded.");
                    }
                }
            }
        }

        private static async Task DeltaRoundTripAndFallback()
        {
            foreach (bool corrupt in new[] { false, true })
            {
                using (var fixture = new Fixture())
                {
                    byte[] original = File.ReadAllBytes(fixture.Executable);
                    byte[] payload = Exe(2);
                    byte[] patch;
                    using (var stream = new MemoryStream())
                    {
                        BinaryPatch.Create(original, payload, stream);
                        patch = stream.ToArray();
                    }
                    if (corrupt) patch = new byte[] { 1, 2, 3, 4 }; // Its hash is correct; its patch format is not.
                    UpdateManifest manifest = Manifest(payload, 2);
                    DeltaManifest delta = Delta(patch);
                    delta.BaseSha256 = UpdatePackage.HashBytes(original);
                    manifest.DeltaPatches.Add(delta);
                    var handler = new FakeHandler(manifest, payload, patch);
                    using (var client = new HttpClient(handler))
                    using (var service = new UpdateService(fixture.Executable, 1, client))
                    {
                        await service.CheckAndPrepareAsync(CancellationToken.None);
                        Check(service.HasPreparedUpdate, "Delta/fallback did not produce a valid package.");
                        Check(handler.PatchDownloads == 1, "Matching delta was not attempted.");
                        Check(handler.ExecutableDownloads == (corrupt ? 1 : 0), "Incorrect full download fallback.");
                        PreparedUpdate prepared;
                        string error;
                        Check(UpdatePackage.TryRead(fixture.Package, 1, out prepared, out error), error);
                    }
                }
            }
        }

        private static async Task FailedNewDownloadKeepsPreparedUpdate()
        {
            using (var fixture = new Fixture())
            {
                byte[] older = Exe(2);
                UpdatePackage.Write(fixture.Package, older, Manifest(older, 2));
                byte[] before = File.ReadAllBytes(fixture.Package);
                byte[] newer = Exe(3);
                // Correct length but different bytes: integrity fails after the
                // full network transfer, while an older valid package exists.
                var handler = new FakeHandler(Manifest(newer, 3), Exe(4), null);
                using (var client = new HttpClient(handler))
                using (var service = new UpdateService(fixture.Executable, 1, client))
                {
                    UpdateStatus final = null;
                    service.StatusChanged += value => final = value;
                    await service.CheckAndPrepareAsync(CancellationToken.None);
                    Check(service.HasPreparedUpdate, "A failed new download discarded the cached update.");
                    Check(File.ReadAllBytes(fixture.Package).SequenceEqual(before), "Network failure rewrote the cached package.");
                    Check(final != null && final.Ready && final.Text == UpdateProgressLine.Ready(2), "The cached release was not retained in the UI.");
                }
            }
        }

        private static void ChangedBaseCannotBePatched()
        {
            using (var fixture = new Fixture())
            {
                byte[] original = File.ReadAllBytes(fixture.Executable);
                string hash = UpdatePackage.HashBytes(original);
                File.WriteAllBytes(fixture.Executable, Exe(7));
                Throws<InvalidDataException>(() => UpdateService.ApplyDelta(fixture.Executable, new byte[] { 0 }, hash,
                    Manifest(Exe(2), 2), CancellationToken.None));
                Check(!File.Exists(fixture.Package), "A failed patch published a package.");
            }
        }

        private static async Task CancellationDoesNotPublish()
        {
            using (var fixture = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                var handler = new FakeHandler(Manifest(Exe(2), 2), Exe(2), null) { BlockExecutable = true };
                using (var client = new HttpClient(handler))
                using (var service = new UpdateService(fixture.Executable, 1, client))
                {
                    Task preparation = service.CheckAndPrepareAsync(cancellation.Token);
                    await handler.ExecutableStarted.Task;
                    cancellation.Cancel();
                    await preparation;
                    Check(!service.HasPreparedUpdate && !File.Exists(fixture.Package), "Cancelled download was published.");
                    Check(Directory.GetFiles(fixture.Root).Length == 1, "Cancelled download left an artifact.");
                }
            }
        }

        private static UpdateManifest Manifest(byte[] payload, int release)
        {
            return new UpdateManifest
            {
                SchemaVersion = 2, ReleaseNumber = release, AssetName = "JustEnoughTools.exe",
                Sha256 = UpdatePackage.HashBytes(payload), SizeBytes = payload.Length,
                Version = release.ToString(), DeltaPatches = new List<DeltaManifest>()
            };
        }

        private static DeltaManifest Delta(byte[] patch, string assetName = "JustEnoughTools.bsdiff")
        {
            return new DeltaManifest
            {
                Algorithm = "bsdiff", AlgorithmVersion = 1, BaseReleaseNumber = 1,
                BaseSha256 = new string('a', 64), AssetName = assetName,
                Sha256 = UpdatePackage.HashBytes(patch), SizeBytes = patch.Length
            };
        }

        // A minimal PE-shaped fixture. Never executed or passed to the helper.
        private static byte[] Exe(int marker)
        {
            byte[] payload = new byte[512];
            payload[0] = (byte)'M'; payload[1] = (byte)'Z'; payload[0x3c] = 0x80;
            payload[0x80] = (byte)'P'; payload[0x81] = (byte)'E';
            for (int i = 160; i < payload.Length; i++) payload[i] = (byte)marker;
            return payload;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _assertions++;
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { _assertions++; return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private sealed class Fixture : IDisposable
        {
            internal Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "JET-update-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Executable = Path.Combine(Root, "JustEnoughTools.exe");
                File.WriteAllBytes(Executable, Exe(1));
                Package = UpdatePackage.GetPath(Executable);
            }
            internal string Root { get; }
            internal string Executable { get; }
            internal string Package { get; }
            public void Dispose() { Directory.Delete(Root, true); }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly UpdateManifest _manifest;
            private readonly byte[] _exe;
            private readonly byte[] _patch;
            internal FakeHandler(UpdateManifest manifest, byte[] exe, byte[] patch) { _manifest = manifest; _exe = exe; _patch = patch; }
            internal int ExecutableDownloads;
            internal int PatchDownloads;
            internal bool BlockExecutable;
            internal readonly TaskCompletionSource<bool> ExecutableStarted = new TaskCompletionSource<bool>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                byte[] content;
                if (request.RequestUri.AbsolutePath.EndsWith("/update.json", StringComparison.Ordinal)) content = UpdatePackage.SerializeManifest(_manifest);
                else if (request.RequestUri.AbsolutePath.EndsWith(".bsdiff", StringComparison.Ordinal)) { PatchDownloads++; content = _patch; }
                else
                {
                    ExecutableDownloads++;
                    ExecutableStarted.TrySetResult(true);
                    if (BlockExecutable) await Task.Delay(Timeout.Infinite, token);
                    content = _exe;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content ?? new byte[0]) };
            }
        }
    }
}
