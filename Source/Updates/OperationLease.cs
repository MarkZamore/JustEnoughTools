using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace JustEnoughTools.Updates
{
    internal static class OperationLease
    {
        internal static string NameFor(string executablePath)
        {
            return @"Local\JustEnoughTools.Operation." + UpdateService.GetInstanceKey(executablePath);
        }

        internal static Mutex Open(string executablePath)
        {
            return OpenName(NameFor(executablePath));
        }

        private static Mutex OpenName(string name)
        {
            try { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); }
            catch (WaitHandleCannotBeOpenedException) { }

            // A worker launched with another administrator's credentials must
            // still coordinate with the original medium-integrity UI. Limited
            // existing handles avoid requiring that UI to request FullControl.
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User != null)
                    security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
            }
            try
            {
                bool created;
                return new Mutex(false, name, out created, security);
            }
            catch (UnauthorizedAccessException)
            {
                // Another process can create the same object after OpenExisting
                // failed. Its ACL intentionally grants only these two rights.
                return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
            }
        }

        internal static readonly string[] Scopes = {
            "Security", "Taskbar", "Tweak1", "Tweak2", "Tweak3", "Tweak4", "Tweak5", "Tweak6", "Tweak7",
            "Tweak8", "Tweak9", "Tweak10", "Tweak11", "Tweak12", "Tweak13"
        };

        internal static Mutex OpenScope(string executablePath, string scope)
        {
            if (Array.IndexOf(Scopes, scope) < 0) throw new ArgumentOutOfRangeException("scope");
            return OpenName(NameFor(executablePath) + "." + scope);
        }

        // The installer owns the admission gate for its full lifetime. Actions
        // hold it only while entering one fixed scope, so independent actions
        // can proceed together without racing an installer handoff.
        internal static IDisposable Acquire(string executablePath, string scope)
        {
            using (Mutex gate = Open(executablePath))
            {
                if (!Take(gate, 1000)) throw new InvalidOperationException("Устанавливается обновление приложения.");
                try
                {
                    Mutex action = OpenScope(executablePath, scope);
                    try
                    {
                        if (!Take(action)) throw new InvalidOperationException("Эта операция уже выполняется.");
                        return new Owned(action);
                    }
                    catch { action.Dispose(); throw; }
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private sealed class Owned : IDisposable
        {
            private Mutex _mutex;
            internal Owned(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                Mutex mutex = _mutex;
                if (mutex == null) return;
                mutex.ReleaseMutex();
                _mutex = null;
                mutex.Dispose();
            }
        }

        private static bool Take(Mutex mutex, int milliseconds = 0)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        internal static bool IsScopeBusy(string executablePath, string scope)
        {
            // Check only this row. Another category or a short admission-gate
            // holder must not make an unrelated row appear to be running.
            if (Array.IndexOf(Scopes, scope) < 0) throw new ArgumentOutOfRangeException("scope");
            try
            {
                using (Mutex mutex = OpenScope(executablePath, scope))
                {
                    if (!Take(mutex)) return true;
                    mutex.ReleaseMutex();
                    return false;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException || ex is WaitHandleCannotBeOpenedException)
            { return true; }
        }

        internal static bool IsBusy(string executablePath)
        {
            try
            {
                using (Mutex gate = Open(executablePath))
                {
                    if (!Take(gate)) return true;
                    try
                    {
                        foreach (string scope in Scopes)
                        {
                            using (Mutex mutex = OpenScope(executablePath, scope))
                            {
                                if (!Take(mutex)) return true;
                                mutex.ReleaseMutex();
                            }
                        }
                        return false;
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException || ex is WaitHandleCannotBeOpenedException)
            {
                // An inaccessible lease is not evidence that an operation has
                // stopped. Preserve the prepared package and refuse to install.
                return true;
            }
        }
    }
}
