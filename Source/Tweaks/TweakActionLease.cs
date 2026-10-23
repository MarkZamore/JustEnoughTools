using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace JustEnoughTools.Tweaks
{
    // Copies in different folders/sessions must not change the same Windows
    // preferences concurrently. No registry or filesystem state is persisted.
    internal sealed class TweakActionLease : IDisposable
    {
        const string Name = @"Global\JustEnoughTools.Tweaks";
        readonly Mutex mutex;
        TweakActionLease(Mutex mutex) { this.mutex = mutex; }
        internal static TweakActionLease Take(TweakId id)
        {
            if(!TweakCatalog.IsValid(id))throw new ArgumentOutOfRangeException(nameof(id));
            string name=Name+"."+(int)id;
            Mutex mutex;
            try { mutex = Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); }
            catch (WaitHandleCannotBeOpenedException)
            {
                var security = new MutexSecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
                using (var identity = WindowsIdentity.GetCurrent()) security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                try { mutex = new Mutex(false, name, out created, security); }
                catch (UnauthorizedAccessException) { mutex = Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); }
            }
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new InvalidOperationException("Эта настройка уже выполняется другим экземпляром приложения."); }
            return new TweakActionLease(mutex);
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
