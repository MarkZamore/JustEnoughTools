using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;

namespace JustEnoughTools.Tests
{
    public static class TestContext
    {
        public static string ExecutablePath { get; internal set; }
    }
    internal static class TestProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass the tracked application EXE path.");
                TestContext.ExecutablePath = Path.GetFullPath(args[0]);
                DependencyBootstrap.Initialize();
                AppContext.SetSwitch("Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles", true);
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                int count = 0;
                foreach (var suite in typeof(TestProgram).Assembly.GetTypes().OrderBy(type => type.FullName))
                {
                    var method = suite.GetMethod("RunAll", BindingFlags.Public | BindingFlags.Static);
                    if (method == null || method.ReturnType != typeof(int) || method.GetParameters().Length != 0) continue;
                    int passed;
                    try { passed = (int)method.Invoke(null, null); }
                    catch (TargetInvocationException failure) { throw failure.InnerException ?? failure; }
                    if (passed < 1) throw new InvalidOperationException(suite.FullName + " did not run tests.");
                    count += passed;
                    Console.WriteLine("PASS " + suite.Name + ": " + passed);
                }
                if (count == 0) throw new InvalidOperationException("No tests ran.");
                Console.WriteLine("PASS total: " + count);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    public static class CoreTests
    {
        public static int RunAll()
        {
            var assembly = typeof(DependencyBootstrap).Assembly;
            var target = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
            Require(target != null && target.FrameworkName == ".NETFramework,Version=v4.8", "Application must target net48.");
            foreach (string name in new[] { "Theme", "Window", "AppIcon", "RunnerTemplate", "Changelog", "Dependencies.BsDiff.dll", "Dependencies.ICSharpCode.SharpZipLib.dll" })
                using (var resource = assembly.GetManifestResourceStream(name)) Require(resource != null && resource.Length > 0, "Missing embedded resource: " + name);
            var version = FileVersionInfo.GetVersionInfo(TestContext.ExecutablePath);
            int releaseNumber;
            Require(Int32.TryParse(version.FileVersion, out releaseNumber) && releaseNumber > 0 &&
                version.FileVersion == releaseNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                version.ProductVersion == version.FileVersion, "File and product versions must contain only the release number.");
            using (var process = Process.Start(new ProcessStartInfo(TestContext.ExecutablePath, "--self-test")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(TestContext.ExecutablePath)
            }))
            {
                if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException("Standalone embedded-dependency probe timed out."); }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                Require(process.ExitCode == 0 && output.Contains("Embedded dependencies OK"), "Standalone dependency probe failed: " + error + output);
            }
            return 10;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
