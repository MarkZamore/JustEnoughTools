using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JustEnoughTools.Updates
{
    /// <summary>Hands a verified adjacent package to the system PowerShell after exit.</summary>
    internal static class UpdateInstaller
    {
        private const int HandoffMilliseconds = 12000;
        private const int RestartAcknowledgementMilliseconds = 6000;

        internal static bool TrySchedule(string executablePath, string packagePath, int currentRelease,
            bool restart, out string error)
        {
            error = string.Empty;
            EventWaitHandle cancel = null;
            Mutex operation = null;
            try
            {
                var executable = Path.GetFullPath(executablePath);
                var package = Path.GetFullPath(packagePath);
                if (string.Equals(executable, package, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(executable), Path.GetDirectoryName(package),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Пакет обновления должен находиться рядом с программой.");

                if (IsActive(executable))
                {
                    if (!restart || RequestRestartIfActive(executable)) return true;
                    throw new InvalidOperationException("Установщик не подтвердил запрос перезапуска.");
                }

                // Keep the shared object alive until the helper has opened it.
                // Its ownership, rather than a separate busy check, serializes
                // the final replacement with the elevated operation worker.
                operation = UpdateService.CreateOperationMutex(executable);
                bool operationAvailable;
                try { operationAvailable = operation.WaitOne(0); }
                catch (AbandonedMutexException) { operationAvailable = true; }
                if (!operationAvailable)
                    throw new InvalidOperationException("Дождитесь завершения текущей операции. Обновление сохранено.");
                operation.ReleaseMutex();

                PreparedUpdate prepared;
                if (!UpdatePackage.TryRead(package, currentRelease, out prepared, out error)) return false;
                if (!UpdatePackage.IsViableExecutable(executable))
                    throw new InvalidOperationException("Текущий исполняемый файл не найден или повреждён.");

                var originalHash = HashFile(executable);
                var names = NamesFor(executable);
                var attempt = Guid.NewGuid().ToString("N");
                var readyName = names + ".Handoff." + attempt;
                var intentName = names + ".Intent." + attempt;
                var cancelName = names + ".Cancel." + attempt;
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
                using (var intent = new EventWaitHandle(false, EventResetMode.ManualReset, intentName))
                using (cancel = new EventWaitHandle(false, EventResetMode.ManualReset, cancelName))
                using (var source = Process.GetCurrentProcess())
                {
                    var script = BuildScript(executable, package, names, readyName, intentName, cancelName,
                        source.Id, source.StartTime.ToUniversalTime().Ticks, prepared.PayloadLength,
                        prepared.Manifest.Sha256, prepared.Manifest.ReleaseNumber, currentRelease,
                        originalHash, restart);
                    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var systemDirectory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                        ? "Sysnative" : "System32";
                    var powershell = Path.Combine(windows, systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                    if (!File.Exists(powershell))
                        throw new FileNotFoundException("Системный Windows PowerShell недоступен.");

                    script = Regex.Replace(script, @"(?m)^[ \t]+", string.Empty);
                    var arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                    if (powershell.Length + arguments.Length + 4 >= 32767)
                        throw new InvalidOperationException("Команда установщика превышает ограничение Windows.");

                    var start = new ProcessStartInfo(powershell, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(executable)
                    };
                    using (var helper = Process.Start(start))
                    {
                        if (helper == null) throw new InvalidOperationException("Не удалось запустить установщик.");
                        var deadline = Stopwatch.StartNew();
                        while (deadline.ElapsedMilliseconds < HandoffMilliseconds)
                        {
                            if (ready.WaitOne(50))
                            {
                                // No helper may modify a file until this final commitment.
                                // There are deliberately no fallible operations after Set().
                                intent.Set();
                                return true;
                            }
                            if (helper.HasExited)
                                throw new InvalidOperationException(UpdateService.IsOperationInProgress(executable)
                                    ? "Дождитесь завершения текущей операции. Обновление сохранено."
                                    : "Установщик завершился до подготовки обновления.");
                        }
                        cancel.Set();
                        error = "Установщик не подтвердил готовность. Программа продолжит работу.";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                try { if (cancel != null) cancel.Set(); } catch (Exception) { }
                error = ex.Message;
                return false;
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        internal static bool IsActive(string executablePath)
        {
            try
            {
                var names = NamesFor(Path.GetFullPath(executablePath));
                if (!IsMutexOwned(names + ".Lock")) return false;
                using (var ready = EventWaitHandle.OpenExisting(names + ".Ready")) return ready.WaitOne(0);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is WaitHandleCannotBeOpenedException)
            { return false; }
        }

        internal static bool RequestRestartIfActive(string executablePath)
        {
            try
            {
                var names = NamesFor(Path.GetFullPath(executablePath));
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < RestartAcknowledgementMilliseconds)
                {
                    if (!IsMutexOwned(names + ".Lock")) return false;
                    try
                    {
                        using (var ready = EventWaitHandle.OpenExisting(names + ".Ready"))
                        using (var request = EventWaitHandle.OpenExisting(names + ".Restart"))
                        using (var acknowledgement = EventWaitHandle.OpenExisting(names + ".RestartAccepted"))
                        {
                            if (!ready.WaitOne(50)) continue;
                            request.Set();
                            while (deadline.ElapsedMilliseconds < RestartAcknowledgementMilliseconds)
                            {
                                if (acknowledgement.WaitOne(50)) return true;
                                if (!IsMutexOwned(names + ".Lock")) return false;
                            }
                            return false;
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // The owning helper creates its events just after taking
                        // the mutex. A second launch may arrive in that interval.
                        Thread.Sleep(25);
                    }
                }
                return false;
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException || ex is IOException ||
                                       ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException)
            { return false; }
        }

        private static bool IsMutexOwned(string name)
        {
            Mutex mutex;
            try { mutex = Mutex.OpenExisting(name); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return true;
                mutex.ReleaseMutex();
                return false;
            }
        }

        private static string NamesFor(string executable)
        {
            return @"Local\JustEnoughTools.Update." + UpdateService.GetInstanceKey(executable);
        }

        private static string HashFile(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(input));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static string Encoded(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        private static string BuildScript(string executable, string package, string names, string handoff,
            string intent, string cancel, int sourcePid, long sourceStartTicks, long payloadLength,
            string payloadHash, int targetRelease, int currentRelease, string originalHash, bool restart)
        {
            return HelperScript.Replace("@TARGET@", Encoded(executable)).Replace("@PACKAGE@", Encoded(package))
                .Replace("@OPERATION@", Encoded(UpdateService.GetOperationMutexName(executable)))
                .Replace("@NAMES@", Encoded(names)).Replace("@HANDOFF@", Encoded(handoff))
                .Replace("@INTENT@", Encoded(intent)).Replace("@CANCEL@", Encoded(cancel))
                .Replace("@PID@", sourcePid.ToString(CultureInfo.InvariantCulture))
                .Replace("@START@", sourceStartTicks.ToString(CultureInfo.InvariantCulture))
                .Replace("@LENGTH@", payloadLength.ToString(CultureInfo.InvariantCulture))
                .Replace("@HASH@", payloadHash.ToLowerInvariant())
                .Replace("@ORIGINAL@", originalHash)
                .Replace("@RELEASE@", targetRelease.ToString(CultureInfo.InvariantCulture))
                .Replace("@CURRENT@", currentRelease.ToString(CultureInfo.InvariantCulture))
                .Replace("@RESTART@", restart ? "$true" : "$false");
        }

        private const string HelperScript = """
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
function Decode([string]$s) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) }
$target = Decode '@TARGET@'
$package = Decode '@PACKAGE@'
$names = Decode '@NAMES@'
$operationName = Decode '@OPERATION@'
$sourcePid = [int]@PID@
$sourceStart = [long]@START@
$expectedLength = [long]@LENGTH@
$expectedHash = '@HASH@'
$originalHash = '@ORIGINAL@'
$expectedRelease = [int]@RELEASE@
$currentRelease = [int]@CURRENT@
$script:restart = @RESTART@
$owned = $false
$operationOwned = $false
$committed = $false
$sourceExited = $false
$installed = $false
$exitCode = 1
$lock = $null
$activeReady = $null
$restartRequest = $null
$restartAccepted = $null
$handoff = $null
$intent = $null
$cancel = $null
$operation = $null
function Read-Exactly([IO.Stream]$stream, [byte[]]$bytes) {
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $count = $stream.Read($bytes, $offset, $bytes.Length - $offset)
        if ($count -le 0) { throw 'Unexpected end of package.' }
        $offset += $count
    }
}
function Get-StreamHash([IO.Stream]$stream, [long]$length) {
    $stream.Position = 0
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $buffer = New-Object byte[] 65536
        $remaining = $length
        while ($remaining -gt 0) {
            $count = $stream.Read($buffer, 0, [int][Math]::Min([long]$buffer.Length, $remaining))
            if ($count -le 0) { throw 'Unexpected end of executable.' }
            $null = $hash.TransformBlock($buffer, 0, $count, $buffer, 0)
            $remaining -= $count
        }
        $null = $hash.TransformFinalBlock([byte[]]@(), 0, 0)
        return [BitConverter]::ToString($hash.Hash).Replace('-', '').ToLowerInvariant()
    } finally { $hash.Dispose() }
}
function Assert-Package([IO.Stream]$stream) {
    if ($expectedLength -le 0 -or $expectedLength -gt 67108864 -or $stream.Length -lt 25) { throw 'Invalid package size.' }
    $stream.Position = $stream.Length - 24
    $footer = New-Object byte[] 24
    Read-Exactly $stream $footer
    $payloadLength = [BitConverter]::ToInt64($footer, 8)
    $metadataLength = [BitConverter]::ToInt32($footer, 16)
    if ([Text.Encoding]::ASCII.GetString($footer, 0, 8) -cne 'JETUPD02' -or
        [BitConverter]::ToInt32($footer, 20) -ne 2 -or $payloadLength -ne $expectedLength -or
        $metadataLength -le 0 -or $metadataLength -gt 65536 -or
        $stream.Length -ne ($payloadLength + [long]$metadataLength + 24)) { throw 'Invalid package footer.' }
    $stream.Position = $payloadLength
    $metadataBytes = New-Object byte[] $metadataLength
    Read-Exactly $stream $metadataBytes
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    $metadata = ConvertFrom-Json -InputObject $utf8.GetString($metadataBytes)
    if ([int]$metadata.schemaVersion -ne 2 -or [long]$metadata.sizeBytes -ne $expectedLength -or
        [int]$metadata.releaseNumber -ne $expectedRelease -or $expectedRelease -le $currentRelease -or
        ([string]$metadata.sha256).Trim().ToLowerInvariant() -cne $expectedHash) { throw 'Package metadata changed.' }
    if ((Get-StreamHash $stream $expectedLength) -cne $expectedHash) { throw 'Package checksum mismatch.' }
}
function Test-Raw([string]$path) {
    $stream = $null
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -ne $expectedLength) { return $false }
        return (Get-StreamHash $stream $expectedLength) -ceq $expectedHash
    } catch { return $false } finally { if ($null -ne $stream) { $stream.Dispose() } }
}
function Test-Viable([string]$path) {
    $stream = $null
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        if ($stream.Length -lt 64) { return $false }
        $header = New-Object byte[] 64
        Read-Exactly $stream $header
        if ($header[0] -ne 77 -or $header[1] -ne 90) { return $false }
        $pe = [BitConverter]::ToInt32($header, 60)
        if ($pe -lt 64 -or $pe -gt ($stream.Length - 4)) { return $false }
        $stream.Position = $pe
        $signature = New-Object byte[] 4
        Read-Exactly $stream $signature
        return $signature[0] -eq 80 -and $signature[1] -eq 69 -and $signature[2] -eq 0 -and $signature[3] -eq 0
    } catch { return $false } finally { if ($null -ne $stream) { $stream.Dispose() } }
}
function Accept-Restart {
    if ($null -ne $restartRequest -and $restartRequest.WaitOne(0)) {
        $script:restart = $true
        $null = $restartAccepted.Set()
    }
}
function Clear-Hidden {
    $attributes = [int][IO.File]::GetAttributes($target)
    [IO.File]::SetAttributes($target, [IO.FileAttributes]($attributes -band (-bnot [int][IO.FileAttributes]::Hidden)))
}
try {
    $handoff = [Threading.EventWaitHandle]::OpenExisting((Decode '@HANDOFF@'))
    $intent = [Threading.EventWaitHandle]::OpenExisting((Decode '@INTENT@'))
    $cancel = [Threading.EventWaitHandle]::OpenExisting((Decode '@CANCEL@'))
    $lock = New-Object Threading.Mutex($false, ($names + '.Lock'))
    try { $owned = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) {
        $activeReady = [Threading.EventWaitHandle]::OpenExisting($names + '.Ready')
        $restartRequest = [Threading.EventWaitHandle]::OpenExisting($names + '.Restart')
        $restartAccepted = [Threading.EventWaitHandle]::OpenExisting($names + '.RestartAccepted')
        if (-not $activeReady.WaitOne(10000) -or $cancel.WaitOne(0)) { throw 'Active installer is not ready.' }
        if ($restart) {
            $null = $restartRequest.Set()
            if (-not $restartAccepted.WaitOne(6000)) { throw 'Restart request was not accepted.' }
        }
        $null = $handoff.Set()
        $exitCode = 0
    } else {
        $rights = [Security.AccessControl.MutexRights]::Synchronize -bor [Security.AccessControl.MutexRights]::Modify
        $operation = [Threading.Mutex]::OpenExisting($operationName, $rights)
        try { $operationOwned = $operation.WaitOne(0) } catch [Threading.AbandonedMutexException] { $operationOwned = $true }
        if (-not $operationOwned) { throw 'A protection operation owns the shared lease.' }
        $activeReady = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, ($names + '.Ready'))
        $restartRequest = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, ($names + '.Restart'))
        $restartAccepted = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, ($names + '.RestartAccepted'))
        $null = $activeReady.Reset()
        $null = $restartRequest.Reset()
        $null = $restartAccepted.Reset()
        $stream = [IO.File]::Open($package, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { Assert-Package $stream } finally { $stream.Dispose() }
        if ($cancel.WaitOne(0)) { throw 'Handoff cancelled.' }
        $null = $handoff.Set()
        $decision = [Threading.WaitHandle]::WaitAny([Threading.WaitHandle[]]@($intent, $cancel), 15000)
        if ($decision -ne 0 -or $cancel.WaitOne(0)) { throw 'Handoff was not committed.' }
        $committed = $true
        $null = $activeReady.Set()
        $source = $null
        try {
            try {
                $source = [Diagnostics.Process]::GetProcessById($sourcePid)
                if ($source.StartTime.ToUniversalTime().Ticks -ne $sourceStart) { $source.Dispose(); $source = $null }
            } catch {
                $baseError = $_.Exception.GetBaseException()
                $alreadyGone = $baseError -is [ArgumentException]
                if (-not $alreadyGone -and $null -ne $source) {
                    try { $alreadyGone = $source.HasExited } catch { }
                }
                if (-not $alreadyGone) { throw }
                if ($null -ne $source) { $source.Dispose() }
                $source = $null
            }
            if ($null -ne $source) {
                while (-not $source.WaitForExit(100)) { Accept-Restart }
            }
        } finally { if ($null -ne $source) { $source.Dispose() } }
        $sourceExited = $true
        Accept-Restart
        $stream = [IO.File]::Open($package, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Assert-Package $stream
            $stream.SetLength($expectedLength)
            $stream.Flush($true)
            if ((Get-StreamHash $stream $expectedLength) -cne $expectedHash) { throw 'Truncated payload checksum mismatch.' }
        } finally { $stream.Dispose() }
        $replacementAttempted = $false
        for ($attempt = 0; $attempt -lt 80; $attempt++) {
            Accept-Restart
            try {
                if (Test-Raw $target) {
                    Clear-Hidden
                    if ([IO.File]::Exists($package)) { [IO.File]::Delete($package) }
                    $installed = $true
                    break
                }
                if (-not (Test-Raw $package)) { throw 'The replacement is unavailable or invalid.' }
                if ([IO.File]::Exists($target)) {
                    if (-not $replacementAttempted) {
                        $old = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                        try { $oldHash = Get-StreamHash $old $old.Length } finally { $old.Dispose() }
                        if ($oldHash -cne $originalHash) { throw 'The target changed while the installer waited.' }
                    }
                    $replacementAttempted = $true
                    [IO.File]::Replace($package, $target, [NullString]::Value)
                } else {
                    $replacementAttempted = $true
                    [IO.File]::Move($package, $target)
                }
                if (-not (Test-Raw $target)) { throw 'Installed executable checksum mismatch.' }
                Clear-Hidden
                $installed = $true
                break
            } catch {
                if ($attempt -eq 79) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
        if (-not $installed) { throw 'Replacement did not finish.' }
        $exitCode = 0
    }
} catch {
    # In particular, do not delete the package here: ReplaceFile may have
    # removed the old target before reporting a failed rename.
    if ($owned -and $committed -and $sourceExited -and -not [IO.File]::Exists($target) -and (Test-Raw $package)) {
        try {
            [IO.File]::Move($package, $target)
            if (Test-Raw $target) { Clear-Hidden; $installed = $true; $exitCode = 0 }
        } catch { }
    }
} finally {
    if ($owned) {
        try { Accept-Restart } catch { }
        try { if ($null -ne $activeReady) { $null = $activeReady.Reset() } } catch { }
        if ($operationOwned) { try { $operation.ReleaseMutex() } catch { } }
        try { $lock.ReleaseMutex() } catch { }
    }
    foreach ($handle in @($cancel, $intent, $handoff, $restartAccepted, $restartRequest, $activeReady, $operation, $lock)) {
        if ($null -ne $handle) { $handle.Dispose() }
    }
}
if ($owned -and $committed -and $sourceExited -and $restart -and (Test-Viable $target)) {
    try {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $target
        $start.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
        $start.UseShellExecute = $false
        if (-not $installed) { $start.Arguments = '--skip-prestart-update' }
        $newProcess = [Diagnostics.Process]::Start($start)
        if ($null -ne $newProcess) { $newProcess.Dispose() }
    } catch { $exitCode = 1 }
}
exit $exitCode
""";
    }
}
