using System;
using System.Collections.Generic;
using System.Linq;
using JustEnoughTools.Tweaks;

namespace JustEnoughTools.Tests
{
    public static class WindowsUpdateTests
    {
        public static int RunAll()
        {
            int count = 0;
            Action<bool, string> check = (ok, message) => { if (!ok) throw new InvalidOperationException(message); count++; };
            var empty = new FakeBatch(); var result = WindowsUpdateOperation.Run(empty);
            check(result.Found == 0 && result.Errors.Count == 0 && empty.Calls.SequenceEqual(new[] { "search" }), "An empty search never starts download/install.");
            check(WindowsUpdateOperation.Criteria == "IsInstalled=0 and IsHidden=0", "Optional software and drivers must not be filtered out.");
            var optional = new FakeItem("optional"); var driver = new FakeItem("driver"); var cached = new FakeItem("cached") { Downloaded = true, EulaAccepted = true };
            var all = new FakeBatch(optional, driver, cached); result = WindowsUpdateOperation.Run(all);
            check(result.Found == 3 && result.Downloaded == 3 && result.Installed == 3 && result.Errors.Count == 0, "Every offered update, including optional and driver, is installed.");
            check(optional.EulaCalls == 1 && driver.EulaCalls == 1 && cached.EulaCalls == 0, "EULAs are accepted before download without reaccepting existing approval.");
            check(!all.Calls.Contains("download:cached"), "Cached updates are installed without redundant download.");
            check(all.Calls.SequenceEqual(new[] { "search", "download:optional", "download:driver", "install:optional", "install:driver", "install:cached" }), "Downloads precede individual installations.");
            var broken = new FakeItem("broken") { FailEula = true }; var failure = new FakeItem("download-failure") { FailDownload = true }; var good = new FakeItem("good");
            var partial = new FakeBatch(broken, failure, good); result = WindowsUpdateOperation.Run(partial);
            check(result.Installed == 1 && result.Errors.Count == 2, "EULA/download failure cannot stop another update.");
            check(!partial.Calls.Contains("install:broken") && !partial.Calls.Contains("install:download-failure"), "Never install packages that failed preparation.");
            var installFailure = new FakeItem("bad-install") { FailInstall = true }; var restart = new FakeItem("restart") { Reboot = true };
            result = WindowsUpdateOperation.Run(new FakeBatch(installFailure, restart, new FakeItem("last")));
            check(result.Installed == 2 && result.Errors.Count == 1 && result.RebootRequired, "An install failure must not stop later updates; report restart requirement.");
            check(result.Describe().Contains("автоматически она не запускается"), "The result explains that no restart was initiated.");
            var lying = new FakeItem("not-cached") { LieDownload = true }; result = WindowsUpdateOperation.Run(new FakeBatch(lying));
            check(result.Installed == 0 && result.Errors.Count == 1, "A successful HRESULT is insufficient if content is not cached.");
            var warning = new FakeItem("warning") { WarningDownload = true }; result = WindowsUpdateOperation.Run(new FakeBatch(warning));
            check(result.Installed == 0 && result.Errors.Count == 1, "SucceededWithErrors must not be counted as successful download.");
            var thrown = new FakeItem("throw") { ThrowInstall = true }; result = WindowsUpdateOperation.Run(new FakeBatch(thrown, new FakeItem("next")));
            check(result.Installed == 1 && result.Errors.Count == 1, "Thrown installer errors are isolated per update.");
            result=WindowsUpdateOperation.Run(new FakeBatch(new FakeItem(new string('x',10000)){FailEula=true}));
            check(result.Errors.Count==1&&result.Describe().Length<8192,"Update reports must fit the authenticated IPC result limit.");
            return count;
        }
        sealed class FakeItem : IWindowsUpdateItem
        {
            internal FakeItem(string title) { Title = title; }
            public string Title { get; private set; }
            public bool Downloaded { get; set; }
            public bool EulaAccepted { get; set; }
            internal bool FailEula, FailDownload, FailInstall, Reboot, LieDownload, WarningDownload, ThrowInstall;
            internal int EulaCalls;
            public void AcceptEula() { EulaCalls++; if (FailEula) throw new InvalidOperationException("eula"); EulaAccepted = true; }
        }
        sealed class FakeBatch : IWindowsUpdateBatch
        {
            readonly FakeItem[] items;
            internal readonly List<string> Calls = new List<string>();
            internal FakeBatch(params FakeItem[] items) { this.items = items; }
            public IList<IWindowsUpdateItem> Search() { Calls.Add("search"); return items.Cast<IWindowsUpdateItem>().ToList(); }
            public WindowsUpdateStepResult Download(IWindowsUpdateItem value)
            {
                var item = (FakeItem)value; Calls.Add("download:" + item.Title);
                item.Downloaded = !item.FailDownload && !item.LieDownload;
                return new WindowsUpdateStepResult { Code = item.FailDownload ? 4 : item.WarningDownload ? 3 : 2, HResult = item.FailDownload ? -1 : 0 };
            }
            public WindowsUpdateStepResult Install(IWindowsUpdateItem value)
            {
                var item = (FakeItem)value; Calls.Add("install:" + item.Title);
                if (item.ThrowInstall) throw new InvalidOperationException("install");
                return new WindowsUpdateStepResult { Code = item.FailInstall ? 4 : 2, HResult = item.FailInstall ? -1 : 0, RebootRequired = item.Reboot };
            }
            public void Dispose() { }
        }
    }
}
