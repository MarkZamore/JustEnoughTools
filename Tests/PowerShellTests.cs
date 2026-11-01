using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using JustEnoughTools.Platform;

namespace JustEnoughTools.Tests
{
    // These children only write to their standard streams. No protection
    // providers, setters, application entrypoints or temporary files are used.
    public static class PowerShellTests
    {
        private sealed class Output
        {
            internal int ExitCode;
            internal string Text, Errors;
        }

        public static int RunAll()
        {
            int checks = 0;
            Require(Environment.Version.Major == 4, "PowerShell transport tests must run in the .NET Framework parent."); checks++;
            string unicode = "Привет 中文 \U0001F600";
            Output result = Run("[Console]::Out.Write('" + unicode + "')");
            Require(result.ExitCode == 0 && result.Text == unicode && result.Errors.Length == 0,
                "The stdin transport changed Unicode or added prompts/BOMs."); checks++;

            string full = "[Console]::Out.Write('PREFIX-RAN'); [Console]::Out.Write('TAIL-RAN')";
            foreach (byte[] incomplete in new[] { new byte[0], Encoding.UTF8.GetBytes("[Console]::Out.Write('PREFIX-RAN')") })
            {
                result = Run(full, incomplete);
                Require(result.ExitCode != 0 && result.Text.Length == 0 && result.Errors.Contains("Incomplete script input."),
                    "An incomplete but syntactically valid input executed a statement."); checks++;
            }
            result = Run("'AA'", new byte[] { 0x27, 0xC0, 0xAF, 0x27 });
            Require(result.ExitCode != 0 && result.Text.Length == 0 && result.Errors.Length > 0,
                "Invalid UTF-8 was replaced and executed."); checks++;

            result = Run("[Console]::Out.Write('PREFIX-RAN'); if (");
            Require(result.ExitCode != 0 && result.Text.Length == 0 && result.Errors.Length > 0,
                "A script with a syntax error executed preceding statements."); checks++;
            result = Run("[Console]::Out.Write('done'); exit 7");
            Require(result.ExitCode == 7 && result.Text == "done", "An explicit PowerShell exit code was lost."); checks++;
            result = Run("throw 'Ошибка 中文'");
            Require(result.ExitCode == 1 && result.Errors.Contains("Ошибка 中文"),
                "A terminating error lost its exit code or Unicode error text."); checks++;

            result = Run("[Console]::Out.Write('O' * 131072); [Console]::Error.Write('E' * 131072)");
            Require(result.ExitCode == 0 && result.Text == new string('O', 131072) && result.Errors == new string('E', 131072),
                "Concurrent stdout/stderr draining blocked or lost output."); checks++;

            string large = "# " + new string('x', 100000) + "\r\n[Console]::Out.Write('LARGE-SCRIPT-RAN')";
            ProcessStartInfo start = WindowsPowerShell.CreateStartInfo(large, null, true);
            Require(start.Arguments.Length < 600 && !start.Arguments.Contains("LARGE-SCRIPT-RAN") &&
                start.Arguments.IndexOf("EncodedCommand", StringComparison.OrdinalIgnoreCase) < 0 &&
                !start.UseShellExecute && start.RedirectStandardInput && start.CreateNoWindow,
                "The command line contains the script payload or the child can show a console."); checks++;
            result = Run(large);
            Require(result.ExitCode == 0 && result.Text == "LARGE-SCRIPT-RAN", "A script larger than the process argument limit failed."); checks++;

            using (Process child = WindowsPowerShell.Start("exit 0", null, false))
            {
                WindowsPowerShell.SendScriptAsync(child, "exit 0").GetAwaiter().GetResult();
                Require(child.WaitForExit(15000) && child.ExitCode == 0 && !child.StartInfo.RedirectStandardOutput &&
                    !child.StartInfo.RedirectStandardError, "The output-free installer transport did not finish."); checks++;
            }

            Exception denied = new TargetInvocationException(new Win32Exception(5, "Отказано в доступе"));
            string explanation = WindowsPowerShell.Explain(denied, "Запуск проверки");
            Require(WindowsPowerShell.IsAccessDenied(denied) && explanation.Contains("Win32=5") &&
                explanation.StartsWith("Запуск проверки:", StringComparison.Ordinal), "An inner native access error lost its code/stage."); checks++;
            Require(WindowsPowerShell.IsAccessDenied(new UnauthorizedAccessException()) &&
                WindowsPowerShell.IsAccessDenied(new SecurityException()) &&
                !WindowsPowerShell.IsAccessDenied(new Win32Exception(2)) &&
                WindowsPowerShell.Explain(new InvalidOperationException("test")).Contains("HRESULT=0x"),
                "Native error classification conflates missing files with denied access."); checks++;
            return checks;
        }

        private static Output Run(string script, byte[] suppliedInput = null)
        {
            using (Process child = WindowsPowerShell.Start(script, null, true))
            {
                try
                {
                    Task<string> output = child.StandardOutput.ReadToEndAsync();
                    Task<string> errors = child.StandardError.ReadToEndAsync();
                    Task sending = suppliedInput == null ? WindowsPowerShell.SendScriptAsync(child, script) : SendBytesAsync(child, suppliedInput);
                    Require(sending.Wait(15000) && child.WaitForExit(15000), "A harmless PowerShell child timed out.");
                    return new Output { ExitCode = child.ExitCode, Text = output.GetAwaiter().GetResult(), Errors = errors.GetAwaiter().GetResult() };
                }
                finally
                {
                    // Only these harmless test children may be stopped here.
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
                }
            }
        }

        private static async Task SendBytesAsync(Process child, byte[] bytes)
        {
            Stream input = child.StandardInput.BaseStream;
            try
            {
                await input.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
            finally { input.Dispose(); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
