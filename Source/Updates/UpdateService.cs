using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BsDiff;

namespace JustEnoughTools.Updates
{
    public sealed class UpdateService : IDisposable
    {
        internal const string ExecutableAssetName = "JustEnoughTools.exe";
        private const string ReleaseBase = "https://github.com/MarkZamore/JustEnoughTools/releases/download/latest/";
        private readonly string _executablePath;
        private readonly string _packagePath;
        private readonly int _releaseNumber;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly TimeSpan _downloadTimeout;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _gate = new object();
        private bool _checking;
        private bool _disposed;
        private bool _installScheduled;
        private bool _hasPrepared;

        public UpdateService(string executablePath, int releaseNumber)
            : this(executablePath, releaseNumber, CreateHttpClient(), true) { }

        internal UpdateService(string executablePath, int releaseNumber, HttpClient client, bool ownsClient = false, TimeSpan? downloadTimeout = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("Не указан путь к программе.", nameof(executablePath));
            if (releaseNumber < 1) throw new ArgumentOutOfRangeException(nameof(releaseNumber));
            _executablePath = Path.GetFullPath(executablePath);
            _packagePath = UpdatePackage.GetPath(_executablePath);
            _releaseNumber = releaseNumber;
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
            _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
            if (_downloadTimeout <= TimeSpan.Zero || _downloadTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
            PreparedUpdate prepared;
            string error;
            _hasPrepared = UpdatePackage.TryRead(_packagePath, _releaseNumber, out prepared, out error);
        }

        public event Action<UpdateStatus> StatusChanged;

        public bool HasPreparedUpdate
        {
            get { lock (_gate) return _hasPrepared; }
        }

        public static string GetInstanceKey(string executablePath)
        {
            string canonical = Path.GetFullPath(executablePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (var sha = SHA256.Create()) return UpdatePackage.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        public static bool TryRequestActiveInstallationRestart(string executablePath)
        {
            return UpdateInstaller.RequestRestartIfActive(Path.GetFullPath(executablePath));
        }

        public static string GetOperationMutexName(string executablePath) { return OperationLease.NameFor(executablePath); }

        // The caller must acquire/release this handle on the same thread. Both
        // the elevated worker and installer use it for their complete operation.
        public static Mutex CreateOperationMutex(string executablePath) { return OperationLease.Open(executablePath); }

        public static bool IsOperationInProgress(string executablePath) { return OperationLease.IsBusy(executablePath); }

        // No network calls here: a prepared package is applied before the UI is
        // shown, and an already-running helper receives a restart request.
        public static bool TryApplyPreparedBeforeStartup(string executablePath, int releaseNumber)
        {
            string fullPath = Path.GetFullPath(executablePath);
            if (UpdateInstaller.RequestRestartIfActive(fullPath)) return true;
            if (IsOperationInProgress(fullPath)) return false;
            if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--skip-prestart-update", StringComparison.Ordinal))) return false;
            string packagePath = UpdatePackage.GetPath(fullPath);
            PreparedUpdate prepared;
            string error;
            if (!UpdatePackage.TryRead(packagePath, releaseNumber, out prepared, out error))
            {
                UpdatePackage.DeleteInvalidIfSafe(packagePath, fullPath);
                return false;
            }
            return UpdateInstaller.TrySchedule(fullPath, packagePath, releaseNumber, true, out error);
        }

        public async Task CheckAndPrepareAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_disposed || _installScheduled || _checking) return;
                _checking = true;
            }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
            {
                CancellationToken cancel = linked.Token;
                PreparedUpdate cached = null;
                int? downloadingRelease = null;
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    string ignored;
                    lock (_gate)
                    {
                        if (UpdatePackage.TryRead(_packagePath, _releaseNumber, out cached, out ignored)) _hasPrepared = true;
                        else
                        {
                            _hasPrepared = false;
                            if (!UpdateInstaller.IsActive(_executablePath)) UpdatePackage.DeleteInvalidIfSafe(_packagePath, _executablePath);
                        }
                    }
                    if (cached != null) ReportReady(cached.Manifest.ReleaseNumber);
                    else Report(UpdateProgressLine.Checking());
                    UpdateManifest manifest = await CheckManifestAsync(cached != null, cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    if (manifest.ReleaseNumber <= _releaseNumber)
                    {
                        if (cached == null) Report(UpdateProgressLine.UpToDate(_releaseNumber), 0);
                        return;
                    }
                    if (cached != null && manifest.ReleaseNumber <= cached.Manifest.ReleaseNumber)
                    {
                        ReportReady(cached.Manifest.ReleaseNumber);
                        return;
                    }
                    downloadingRelease = manifest.ReleaseNumber;
                    Report(UpdateProgressLine.Starting(manifest.ReleaseNumber), 0);
                    byte[] payload = await PreparePayloadAsync(manifest, cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (_disposed || _installScheduled) return;
                        if (UpdateInstaller.IsActive(_executablePath)) throw new IOException("Установка обновления уже выполняется.");
                        UpdatePackage.Write(_packagePath, payload, manifest);
                        PreparedUpdate written;
                        string validationError;
                        if (!UpdatePackage.TryRead(_packagePath, _releaseNumber, out written, out validationError))
                            throw new InvalidDataException(validationError);
                        _hasPrepared = true;
                    }
                    ReportReady(manifest.ReleaseNumber);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                catch (Exception)
                {
                    PreparedUpdate stillReady;
                    string validationError;
                    bool ready;
                    lock (_gate)
                    {
                        ready = UpdatePackage.TryRead(_packagePath, _releaseNumber, out stillReady, out validationError);
                        _hasPrepared = ready;
                        if (!ready && !UpdateInstaller.IsActive(_executablePath)) UpdatePackage.DeleteInvalidIfSafe(_packagePath, _executablePath);
                    }
                    if (ready) ReportReady(stillReady.Manifest.ReleaseNumber);
                    else Report(downloadingRelease.HasValue ? UpdateProgressLine.DownloadFailed(downloadingRelease.Value) : UpdateProgressLine.CheckFailed());
                }
                finally
                {
                    lock (_gate)
                    {
                        _checking = false;
                        if (_disposed) _lifetime.Dispose();
                    }
                }
            }
        }

        public bool TryScheduleInstall(bool restart, out string error)
        {
            lock (_gate)
            {
                error = "";
                if (_disposed) { error = "Сервис обновлений уже остановлен."; return false; }
                if (_installScheduled)
                {
                    if (restart && !UpdateInstaller.RequestRestartIfActive(_executablePath))
                    {
                        error = "Установщик не подтвердил запрос перезапуска.";
                        return false;
                    }
                    return true;
                }
                if (UpdateInstaller.IsActive(_executablePath))
                {
                    if (restart && !UpdateInstaller.RequestRestartIfActive(_executablePath))
                    {
                        error = "Установщик не подтвердил запрос перезапуска.";
                        return false;
                    }
                    _lifetime.Cancel();
                    _installScheduled = true;
                    return true;
                }
                if (IsOperationInProgress(_executablePath))
                {
                    error = "Обновление отложено: дождитесь завершения операции с защитой Windows.";
                    ReportBlocked();
                    return false;
                }
                PreparedUpdate prepared;
                if (!UpdatePackage.TryRead(_packagePath, _releaseNumber, out prepared, out error))
                {
                    _hasPrepared = false;
                    UpdatePackage.DeleteInvalidIfSafe(_packagePath, _executablePath);
                    return false;
                }
                // Prevent a download that was still in flight from publishing a
                // new package after the helper has verified the previous one.
                _lifetime.Cancel();
                if (!UpdateInstaller.TrySchedule(_executablePath, _packagePath, _releaseNumber, restart, out error))
                {
                    if (!UpdateInstaller.IsActive(_executablePath) && IsOperationInProgress(_executablePath)) ReportBlocked();
                    return false;
                }
                _installScheduled = true;
                return true;
            }
        }

        private async Task<UpdateManifest> CheckManifestAsync(bool cached, CancellationToken token)
        {
            int attempts = cached ? 1 : 3;
            Exception last = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(cached ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(20));
                    try
                    {
                        var uri = new Uri(ReleaseBase + "update.json?cache=" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        byte[] bytes = await DownloadBytesAsync(uri, UpdatePackage.MaxManifestBytes, null, null, null, timeout.Token).ConfigureAwait(false);
                        return UpdatePackage.DeserializeManifest(bytes);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { last = ex; }
                }
                if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false);
            }
            throw new IOException("Сервер обновлений недоступен.", last);
        }

        private async Task<byte[]> PreparePayloadAsync(UpdateManifest manifest, CancellationToken token)
        {
            DeltaManifest delta = null;
            if (manifest.DeltaPatches != null)
            {
                foreach (DeltaManifest candidate in manifest.DeltaPatches)
                {
                    if (candidate.BaseReleaseNumber != _releaseNumber) continue;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (new FileInfo(_executablePath).Length > UpdatePackage.MaxPayloadBytes) break;
                        string actual = await Task.Run(() => UpdatePackage.HashFile(_executablePath), token).ConfigureAwait(false);
                        if (string.Equals(actual, candidate.BaseSha256, StringComparison.OrdinalIgnoreCase)) { delta = candidate; break; }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (IOException) { break; }
                    catch (UnauthorizedAccessException) { break; }
                }
            }
            if (delta != null)
            {
                try
                {
                    byte[] patch = await DownloadBytesAsync(BuildAssetUri(delta.AssetName), UpdatePackage.MaxPayloadBytes, delta.SizeBytes,
                        delta.Sha256, manifest.ReleaseNumber, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Report(UpdateProgressLine.Applying(manifest.ReleaseNumber), null);
                    byte[] patched = await Task.Run(() => ApplyDelta(_executablePath, patch, delta.BaseSha256, manifest, token), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return patched;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    Report(UpdateProgressLine.Starting(manifest.ReleaseNumber), 0);
                }
            }
            byte[] payload = await DownloadBytesAsync(BuildAssetUri(manifest.AssetName), UpdatePackage.MaxPayloadBytes,
                manifest.SizeBytes, manifest.Sha256, manifest.ReleaseNumber, token).ConfigureAwait(false);
            UpdatePackage.ValidatePayload(payload, manifest);
            return payload;
        }

        internal static byte[] ApplyDelta(string originalPath, byte[] patch, string baseSha256, UpdateManifest manifest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateManifest(manifest);
            if (patch == null || patch.Length == 0 || patch.Length > UpdatePackage.MaxPayloadBytes) throw new InvalidDataException("Недопустимый размер патча.");
            using (var original = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new LimitedMemoryStream(checked((int)manifest.SizeBytes), token))
            {
                if (original.Length > UpdatePackage.MaxPayloadBytes) throw new InvalidDataException("Базовый EXE слишком большой.");
                if (!string.Equals(UpdatePackage.HashStream(original, original.Length), baseSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Исходная программа изменилась перед применением патча.");
                original.Position = 0;
                BinaryPatch.Apply(original, () => new MemoryStream(patch, false), output);
                token.ThrowIfCancellationRequested();
                byte[] payload = output.ToArray();
                UpdatePackage.ValidatePayload(payload, manifest);
                return payload;
            }
        }

        private async Task<byte[]> DownloadBytesAsync(Uri uri, int limit, long? expectedSize, string expectedHash, int? release, CancellationToken token)
        {
            // With ResponseHeadersRead, HttpClient.Timeout stops covering the
            // body. Framework's ConnectStream can also ignore a ReadAsync token;
            // close the response/stream on cancellation to interrupt that read.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(_downloadTimeout);
                CancellationToken cancel = deadline.Token;
                HttpResponseMessage response = null;
                Stream input = null;
                try
                {
                    response = await SendWithRedirectsAsync(uri, cancel).ConfigureAwait(false);
                    using (cancel.Register(() =>
                    {
                        try { if (input != null) input.Dispose(); } catch { }
                        try { response.Dispose(); } catch { }
                    }))
                    {
                        cancel.ThrowIfCancellationRequested();
                        response.EnsureSuccessStatusCode();
                        long? contentLength = response.Content.Headers.ContentLength;
                        if (contentLength.HasValue && (contentLength.Value > limit || contentLength.Value < 0 || (expectedSize.HasValue && contentLength.Value != expectedSize.Value)))
                            throw new InvalidDataException("Размер ответа не совпадает с манифестом.");
                        int capacity = expectedSize.HasValue ? checked((int)expectedSize.Value) : 4096;
                        input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        cancel.ThrowIfCancellationRequested();
                        using (var output = new MemoryStream(capacity))
                        {
                            var buffer = new byte[65536];
                            var watch = Stopwatch.StartNew();
                            var rate = new TransferRateTracker();
                            rate.Update(0, uri.AbsoluteUri);
                            long lastReport = -100;
                            while (true)
                            {
                                int read = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false);
                                if (read == 0) break;
                                if (output.Length + read > limit || (expectedSize.HasValue && output.Length + read > expectedSize.Value))
                                    throw new InvalidDataException("Обновление превышает допустимый размер.");
                                output.Write(buffer, 0, read);
                                if (release.HasValue && watch.ElapsedMilliseconds - lastReport >= 100)
                                {
                                    lastReport = watch.ElapsedMilliseconds;
                                    double total = expectedSize ?? contentLength ?? limit;
                                    double speed = rate.Update(output.Length, uri.AbsoluteUri);
                                    Report(UpdateProgressLine.Downloading(release.Value, output.Length, (long)total, speed,
                                            UpdateProgressLine.Estimate(output.Length, (long)total, speed)),
                                        Math.Min(1d, output.Length / total));
                                }
                            }
                            cancel.ThrowIfCancellationRequested();
                            byte[] result = output.ToArray();
                            if (expectedSize.HasValue && result.LongLength != expectedSize.Value) throw new InvalidDataException("Загрузка обновления не завершена.");
                            if (expectedHash != null && !string.Equals(UpdatePackage.HashBytes(result), expectedHash, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Контрольная сумма загрузки не совпала.");
                            return result;
                        }
                    }
                }
                catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
                finally
                {
                    if (input != null) input.Dispose();
                    if (response != null) response.Dispose();
                }
            }
        }

        private async Task<HttpResponseMessage> SendWithRedirectsAsync(Uri initialUri, CancellationToken token)
        {
            Uri uri = initialUri;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                ValidateDownloadUri(uri);
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                    HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    int code = (int)response.StatusCode;
                    if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) return response;
                    Uri location = response.Headers.Location;
                    response.Dispose();
                    if (location == null) throw new InvalidDataException("Сервер вернул перенаправление без адреса.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                }
            }
            throw new InvalidDataException("Слишком много перенаправлений обновления.");
        }

        internal static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || manifest.SchemaVersion != 2 || manifest.ReleaseNumber < 1)
                throw new InvalidDataException("Неизвестный формат манифеста обновления.");
            if (!string.Equals(manifest.AssetName, ExecutableAssetName, StringComparison.Ordinal))
                throw new InvalidDataException("Манифест содержит другое имя программы.");
            ValidateSizeAndHash(manifest.SizeBytes, manifest.Sha256);
            if (manifest.DeltaPatches == null) manifest.DeltaPatches = new List<DeltaManifest>();
            if (manifest.DeltaPatches.Count > 2) throw new InvalidDataException("В манифесте слишком много патчей.");
            var bases = new HashSet<int>();
            foreach (DeltaManifest delta in manifest.DeltaPatches)
            {
                if (delta == null || delta.Algorithm != "bsdiff" || delta.AlgorithmVersion != 1 || delta.BaseReleaseNumber < 1 ||
                    delta.BaseReleaseNumber >= manifest.ReleaseNumber || !bases.Add(delta.BaseReleaseNumber) || !IsSafeDeltaName(delta.AssetName))
                    throw new InvalidDataException("Манифест содержит недопустимый патч.");
                ValidateSizeAndHash(delta.SizeBytes, delta.Sha256);
                ValidateHash(delta.BaseSha256);
            }
        }

        private static void ValidateSizeAndHash(long size, string hash)
        {
            if (size <= 0 || size > UpdatePackage.MaxPayloadBytes) throw new InvalidDataException("Недопустимый размер обновления.");
            ValidateHash(hash);
        }

        private static void ValidateHash(string hash)
        {
            if (hash == null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("Недопустимая контрольная сумма.");
        }

        private static bool IsSafeDeltaName(string name)
        {
            return !string.IsNullOrEmpty(name) && Path.GetFileName(name) == name &&
                Regex.IsMatch(name, @"\AJustEnoughTools(?:\.from-[1-9][0-9]*)?\.bsdiff\z", RegexOptions.CultureInvariant);
        }

        internal static void ValidateDownloadUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                throw new InvalidDataException("Адрес обновления должен использовать HTTPS.");
            string host = uri.Host.ToLowerInvariant();
            if (host != "github.com" && host != "objects.githubusercontent.com" && host != "github-releases.githubusercontent.com" && host != "release-assets.githubusercontent.com")
                throw new InvalidDataException("Недопустимый сервер обновлений.");
        }

        private static Uri BuildAssetUri(string asset) { return new Uri(ReleaseBase + Uri.EscapeDataString(asset)); }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JustEnoughTools/1.0");
            return client;
        }

        private void ReportReady(int release)
        {
            if (!UpdateInstaller.IsActive(_executablePath) && IsOperationInProgress(_executablePath)) ReportBlocked();
            else Report(UpdateProgressLine.Ready(release), 1d, true);
        }

        private void ReportBlocked() { Report("Обновление отложено до завершения операции с защитой Windows", 1d, true); }

        private void Report(string text, double? fraction = null, bool ready = false)
        {
            Action<UpdateStatus> handler;
            lock (_gate)
            {
                if (_disposed || _installScheduled) return;
                handler = StatusChanged;
            }
            if (handler != null) handler(new UpdateStatus(text, fraction, ready));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                if (_ownsClient) _client.Dispose();
                // An in-flight check releases the source in its own finally.
                if (!_checking) _lifetime.Dispose();
            }
        }

        private sealed class LimitedMemoryStream : MemoryStream
        {
            private readonly long _limit;
            private readonly CancellationToken _token;
            internal LimitedMemoryStream(int limit, CancellationToken token) : base(limit) { _limit = limit; _token = token; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _token.ThrowIfCancellationRequested();
                if (Position + count > _limit) throw new InvalidDataException("Патч превышает заявленный размер обновления.");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                _token.ThrowIfCancellationRequested();
                if (Position >= _limit) throw new InvalidDataException("Патч превышает заявленный размер обновления.");
                base.WriteByte(value);
            }
        }
    }
}
