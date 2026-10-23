using System;
using System.Runtime.InteropServices;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools.Tests
{
    public static class NativeWindowsUpdateScanTests
    {
        public static int RunAll()
        {
            int checks = 0;
            var probe = new Fake();
            Require(NativeWindowsUpdateScan.IsAvailable(() => probe) && probe.Reads == 1 && probe.Starts == 0 && probe.Disposed,
                "Availability must only inspect the manager and release it without starting updates."); checks++;
            Require(!NativeWindowsUpdateScan.IsAvailable(() => { throw new COMException("Unavailable", unchecked((int)0x80040154)); }),
                "An unavailable WinRT factory must allow the caller's fallback."); checks++;
            var failedProbe = new Fake { ReadError = true };
            Require(!NativeWindowsUpdateScan.IsAvailable(() => failedProbe) && failedProbe.Disposed && failedProbe.Starts == 0,
                "Failed discovery must release the manager and never start an operation."); checks++;
            var idle = new Fake();
            Require(NativeWindowsUpdateScan.Start(idle).Contains("запущена") && idle.Starts == 1 && idle.UserInitiated,
                "The native launch must request exactly one user-initiated scan."); checks++;
            var scanning = new Fake { Scanning = true };
            Require(NativeWindowsUpdateScan.Start(scanning).Contains("уже") && scanning.Starts == 0,
                "An existing scan must not receive a duplicate launch."); checks++;
            var working = new Fake { Working = true };
            NativeWindowsUpdateScan.Start(working);
            Require(working.Starts == 1 && working.UserInitiated,
                "Other orchestrator work alone must not be misreported as an already running scan."); checks++;
            var failure = new Fake { StartError = true };
            bool propagated = false;
            try { NativeWindowsUpdateScan.Start(failure); } catch (COMException) { propagated = true; }
            Require(propagated && failure.Starts == 1, "A rejected launch must propagate so the caller can choose fallback."); checks++;
            var unknown = new Fake { Missing = true };
            propagated = false;
            try { NativeWindowsUpdateScan.Start(unknown); } catch (InvalidOperationException) { propagated = true; }
            Require(propagated && unknown.Starts == 0, "Unknown preflight must not launch a scan."); checks++;
            return checks;
        }

        sealed class Fake : IWindowsUpdateScanSession
        {
            public bool Scanning, Working, ReadError, StartError, Missing, UserInitiated, Disposed;
            public int Reads, Starts;
            public WindowsUpdateScanSnapshot Read()
            {
                Reads++; if (ReadError) throw new COMException("Read failed");
                return Missing ? null : new WindowsUpdateScanSnapshot { IsScanning = Scanning, IsWorking = Working };
            }
            public void StartScan(bool userInitiated)
            {
                Starts++; UserInitiated = userInitiated;
                if (StartError) throw new COMException("Launch failed", unchecked((int)0x80070005));
            }
            public void Dispose() { Disposed = true; }
        }
        static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}
