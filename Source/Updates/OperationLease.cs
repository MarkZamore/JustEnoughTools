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
            string name = NameFor(executablePath);
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

        internal static bool IsBusy(string executablePath)
        {
            try
            {
                using (Mutex mutex = Open(executablePath))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        return !acquired;
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
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
