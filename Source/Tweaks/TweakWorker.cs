using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Updates;
using Microsoft.Win32.SafeHandles;

namespace JustEnoughTools.Tweaks
{
    internal static class TweakWorker
    {
        const string Argument = "--tweaks-worker", Protocol = "JET-TWEAKS-1|";
        internal static bool IsAdministrator()
        { using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        internal static bool MachineAction(TweakId id)
        { return id == TweakId.Indexing || id == TweakId.BitLocker || id == TweakId.WindowsUpdate || id == TweakId.Startup; }
        internal static List<TweakRow> Apply(TweakId[] ids)
        {
            ValidateIds(ids);
            if (IsAdministrator()) return Run(ids);
            string pipe = "JustEnoughTools.Tweaks." + Guid.NewGuid().ToString("N"), nonce = SecurityCoordinator.CreateNonce();
            using (var parent = Process.GetCurrentProcess())
            using (var server = SecurityCoordinator.CreateServer(pipe))
            using (var gateHandle = UpdateService.CreateOperationMutex(parent.MainModule.FileName))
            using (var scopeHandles = KeepScopeHandles(parent.MainModule.FileName, ids))
            {
                var start = SecurityCoordinator.CreateWorkerStartInfo(parent.MainModule.FileName,
                    Argument + " " + pipe + " " + nonce + " " + parent.Id.ToString(CultureInfo.InvariantCulture) + " " + parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), false, true);
                Process child;
                try { child = Process.Start(start); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
                { throw new InvalidOperationException("UAC отменён. Административные твики пропущены; пользовательские настройки сохранены."); }
                if (child == null) throw new InvalidOperationException("Не удалось запустить рабочий процесс твиков.");
                using (child)
                {
                    SecurityCoordinator.WaitForConnectionAsync(server, child).GetAwaiter().GetResult();
                    uint pid;
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out pid) || pid != (uint)child.Id || child.HasExited)
                        throw new InvalidDataException("Не подтверждён рабочий процесс. Административные изменения не начинались.");
                    var hello = SecurityCoordinator.ReadWithTimeoutAsync(server, 10000).GetAwaiter().GetResult();
                    if (hello.Kind != SecurityCoordinator.MessageKind.Hello || !Equal(hello.Payload, nonce)) throw new InvalidDataException("Неверное подтверждение рабочего процесса.");
                    SecurityCoordinator.WriteWithTimeoutAsync(server, SecurityCoordinator.MessageKind.Accepted,
                        EncodeRequest(ids)).GetAwaiter().GetResult();
                    // Disk processing and conversion can legitimately take hours.
                    // EOF is a failure, never a successful completion.
                    var result = SecurityCoordinator.ReadFrameAsync(server, CancellationToken.None).GetAwaiter().GetResult();
                    if (result.Kind != SecurityCoordinator.MessageKind.Result) throw new InvalidDataException("Неверный ответ рабочего процесса.");
                    return DecodeResult(result.Payload, ids);
                }
            }
        }
        private sealed class ScopeHandles : IDisposable
        {
            internal readonly List<Mutex> Handles = new List<Mutex>();
            public void Dispose() { foreach (Mutex handle in Handles) handle.Dispose(); }
        }
        private static IDisposable KeepScopeHandles(string executable, TweakId[] ids)
        {
            var handles = new ScopeHandles();
            try
            {
                foreach (TweakId id in ids)
                    handles.Handles.Add(UpdateService.CreateOperationScopeMutex(executable, "Tweak" + (int)id));
                return handles;
            }
            catch { handles.Dispose(); throw; }
        }
        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 || args[0] != Argument) return false;
            int parentPid; long ticks;
            if (args.Length != 5 || !Regex.IsMatch(args[1], "^JustEnoughTools\\.Tweaks\\.[0-9a-f]{32}$") ||
                !Regex.IsMatch(args[2], "^[0-9a-f]{64}$") || !int.TryParse(args[3], out parentPid) || parentPid <= 0 ||
                !long.TryParse(args[4], out ticks) || ticks <= 0 || !IsAdministrator()) { exitCode = 2; return true; }
            try
            {
                using (var parent = Process.GetProcessById(parentPid))
                using (var current = Process.GetCurrentProcess())
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(parent.MainModule.FileName, current.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Неверный процесс приложения.");
                    using (var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        pipe.Connect(45000); uint pid;
                        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) || pid != (uint)parentPid) throw new InvalidDataException("Неверный контроллер твиков.");
                        SecurityCoordinator.WriteWithTimeoutAsync(pipe, SecurityCoordinator.MessageKind.Hello, Encoding.ASCII.GetBytes(args[2])).GetAwaiter().GetResult();
                        var accepted = SecurityCoordinator.ReadWithTimeoutAsync(pipe, 10000).GetAwaiter().GetResult();
                        if (accepted.Kind != SecurityCoordinator.MessageKind.Accepted) throw new InvalidDataException("Запрос не подтверждён.");
                        var ids = DecodeRequest(accepted.Payload);
                        var rows = Run(ids);
                        SecurityCoordinator.WriteWithTimeoutAsync(pipe, SecurityCoordinator.MessageKind.Result, EncodeResult(rows)).GetAwaiter().GetResult();
                    }
                }
            }
            catch { exitCode = 1; }
            return true;
        }
        internal enum Phase { All, PrepareUpdates, InstallUpdates }
        static List<TweakRow> Run(TweakId[] ids)
        {
            ValidateIds(ids);
            string executable;
            using(var current=Process.GetCurrentProcess())executable=current.MainModule.FileName;
            return RunBatch(ids,(id,phase)=>
            {
                using(UpdateService.AcquireOperationLease(executable,"Tweak"+(int)id))
                using(TweakActionLease.Take(id))
                {
                    var engine=new TweakEngine(new WindowsTweakBackend(new WindowsTweakPlatform(true)));
                    return engine.Apply(new[]{id},TweakScope.Administrator,
                        deferUpdateSearch:phase==Phase.PrepareUpdates,updateSearchOnly:phase==Phase.InstallUpdates);
                }
            });
        }
        internal static List<TweakRow> RunBatch(TweakId[] ids,Func<TweakId,Phase,List<TweakRow>> execute)
        {
            ValidateIds(ids);
            Func<TweakId,Phase,List<TweakRow>> safe=(id,phase)=>
            {
                try{return execute(id,phase);}
                catch(Exception error){return new List<TweakRow>{new TweakRow{Id=id,Parts=new List<TweakPart>{
                    new TweakPart("operation-"+phase,TweakState.Failed,true,error.Message)}}};}
            };
            var results=new List<TweakRow>();
            // Finish quick machine settings before dispatching independent long jobs.
            foreach(var id in ids.Where(id=>id!=TweakId.Indexing&&id!=TweakId.BitLocker&&id!=TweakId.WindowsUpdate))
                results.AddRange(safe(id,Phase.All));
            if(ids.Contains(TweakId.WindowsUpdate))results.AddRange(safe(TweakId.WindowsUpdate,Phase.PrepareUpdates));
            var tasks=ids.Where(id=>id==TweakId.Indexing||id==TweakId.BitLocker||id==TweakId.WindowsUpdate)
                .Select(id=>Task.Run(()=>safe(id,id==TweakId.WindowsUpdate?Phase.InstallUpdates:Phase.All))).ToArray();
            Task.WaitAll(tasks);
            foreach(var task in tasks)results.AddRange(task.Result);
            return results;
        }
        internal static byte[] EncodeRequest(TweakId[] ids) { ValidateIds(ids); return Encoding.ASCII.GetBytes(Protocol + string.Join(",", ids.Select(id => ((int)id).ToString(CultureInfo.InvariantCulture)))); }
        internal static TweakId[] DecodeRequest(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 128) throw new InvalidDataException("Неверный запрос твиков.");
            string request = Encoding.ASCII.GetString(bytes);
            if (!request.StartsWith(Protocol, StringComparison.Ordinal)) throw new InvalidDataException("Неверный протокол твиков.");
            var ids = request.Substring(Protocol.Length).Split(',').Select(s =>
            { int n; if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new InvalidDataException("Неверный идентификатор."); return (TweakId)n; }).ToArray();
            ValidateIds(ids); return ids;
        }
        static void ValidateIds(TweakId[] ids)
        { if (ids == null || ids.Length == 0 || ids.Length > 4 || ids.Distinct().Count() != ids.Length || ids.Any(id => !MachineAction(id))) throw new InvalidDataException("Действие не разрешено административному процессу."); }
        static bool Equal(byte[] bytes, string expected)
        { byte[] right = Encoding.ASCII.GetBytes(expected); if (bytes.Length != right.Length) return false; int difference = 0; for (int i = 0; i < bytes.Length; i++) difference |= bytes[i] ^ right[i]; return difference == 0; }
        [DataContract] sealed class WireResult
        {
            [DataMember] public int Id;
            [DataMember] public string Key;
            [DataMember] public int State;
            [DataMember] public string Reason;
        }
        internal static byte[] EncodeResult(List<TweakRow> rows)
        {
            var items = rows.SelectMany(r => r.Parts.Select(p => new WireResult { Id = (int)r.Id, Key = p.Key, State = (int)p.State, Reason = p.Reason })).ToList();
            using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(List<WireResult>)).WriteObject(stream, items); if (stream.Length > SecurityCoordinator.MaximumFrameBytes) throw new InvalidDataException("Слишком большой отчёт твиков."); return stream.ToArray(); }
        }
        internal static List<TweakRow> DecodeResult(byte[] bytes, TweakId[] expected)
        {
            ValidateIds(expected);
            if (bytes == null || bytes.Length > SecurityCoordinator.MaximumFrameBytes) throw new InvalidDataException("Неверный отчёт твиков.");
            List<WireResult> items;
            using (var stream = new MemoryStream(bytes, false)) items = (List<WireResult>)new DataContractJsonSerializer(typeof(List<WireResult>)).ReadObject(stream);
            if (items == null || items.Count > 512 || items.Any(p => p == null || !expected.Contains((TweakId)p.Id) || string.IsNullOrEmpty(p.Key) || p.Key.Length > 1024 ||
                !Enum.IsDefined(typeof(TweakState), p.State) || p.Reason != null && p.Reason.Length > 8192) || items.GroupBy(p => p.Id + "/" + p.Key).Any(g => g.Count() != 1))
                throw new InvalidDataException("Отчёт содержит неизвестные действия.");
            return expected.Select(id => new TweakRow { Id = id, Parts = items.Where(p => p.Id == (int)id).Select(p => new TweakPart(p.Key, (TweakState)p.State, true, p.Reason)).ToList() }).ToList();
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    }
}
