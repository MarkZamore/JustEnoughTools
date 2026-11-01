using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace JustEnoughTools.Platform
{
    // Fixed application scripts travel over an anonymous pipe, never through a
    // command-line payload or a temporary script file. The complete script is
    // checked and parsed before any of its statements can execute.
    internal static class WindowsPowerShell
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static ProcessStartInfo CreateStartInfo(string script, string workingDirectory, bool captureOutput)
        {
            if (script == null) throw new ArgumentNullException("script");
            // Reject invalid UTF-16 before starting a process, rather than
            // silently replacing characters while transferring the script.
            Utf8.GetByteCount(script);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            string executable = Path.Combine(windows, system + @"\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Системный Windows PowerShell не найден.", executable);
            string bootstrap = "[Console]::InputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false, $true); " +
                "$jetScript=[Console]::In.ReadToEnd(); if ($jetScript.Length -ne " + script.Length.ToString(CultureInfo.InvariantCulture) +
                ") { throw 'Incomplete script input.' }; & ([ScriptBlock]::Create($jetScript))";
            var start = new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -Command \"" + bootstrap + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                WorkingDirectory = workingDirectory ?? windows
            };
            if (captureOutput)
            {
                start.StandardOutputEncoding = Utf8;
                start.StandardErrorEncoding = Utf8;
            }
            return start;
        }

        internal static Process Start(string script, string workingDirectory, bool captureOutput)
        {
            ProcessStartInfo start = CreateStartInfo(script, workingDirectory, captureOutput);
            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Windows не вернула процесс PowerShell.");
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        internal static async Task SendScriptAsync(Process process, string script)
        {
            if (process == null) throw new ArgumentNullException("process");
            Stream input = process.StandardInput.BaseStream;
            try
            {
                if (script == null) throw new ArgumentNullException("script");
                byte[] bytes = Utf8.GetBytes(script);
                await input.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                // The framework's StandardInput writer uses the parent's code
                // page. Closing that unused writer can append its own BOM.
                // Close the byte stream directly to send an unambiguous EOF.
                input.Dispose();
            }
        }

        internal static string Explain(Exception error, string stage = null)
        {
            if (error == null) throw new ArgumentNullException("error");
            Win32Exception native = FindWin32(error);
            string code = native == null ? "HRESULT=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) :
                "Win32=" + native.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + "; HRESULT=0x" +
                native.HResult.ToString("X8", CultureInfo.InvariantCulture);
            return (String.IsNullOrWhiteSpace(stage) ? "" : stage + ": ") + error.Message + " (" + code + ").";
        }

        internal static bool IsAccessDenied(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                var native = current as Win32Exception;
                if (current is UnauthorizedAccessException || current is SecurityException ||
                    (native != null && native.NativeErrorCode == 5)) return true;
            }
            return false;
        }

        private static Win32Exception FindWin32(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                var native = current as Win32Exception;
                if (native != null) return native;
            }
            return null;
        }
    }
}
