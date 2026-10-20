using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JustEnoughTools.Updates;

namespace JustEnoughTools.Tests
{
    internal static class HttpBodyTests
    {
        // A real Framework ConnectStream: the server sends headers and then no
        // bytes. A fake handler stalled before headers does not cover this bug.
        internal static int RunAll()
        {
            return CancelAfterHeaders(true).GetAwaiter().GetResult() + CancelAfterHeaders(false).GetAwaiter().GetResult();
        }

        private static async Task<int> CancelAfterHeaders(bool cancelCaller)
        {
            string root = Path.Combine(Path.GetTempPath(), "JET-body-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            var serverStop = new CancellationTokenSource();
            Task server = null;
            try
            {
                string executable = Path.Combine(root, "JustEnoughTools.exe");
                File.Copy(typeof(UpdateService).Assembly.Location, executable);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                server = Task.Run(async () =>
                {
                    try
                    {
                        using (TcpClient connection = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (NetworkStream stream = connection.GetStream())
                        {
                            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 512\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                            await Task.Delay(Timeout.Infinite, serverStop.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
                    catch (ObjectDisposedException) when (serverStop.IsCancellationRequested) { }
                    catch (SocketException) when (serverStop.IsCancellationRequested) { }
                });
                var manifest = new UpdateManifest
                {
                    SchemaVersion = 2, ReleaseNumber = 2, AssetName = "JustEnoughTools.exe", SizeBytes = 512,
                    Sha256 = new string('a', 64), DeltaPatches = new List<DeltaManifest>()
                };
                using (var caller = new CancellationTokenSource())
                using (var handler = new BodyHandler(manifest, port))
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) })
                using (var service = new UpdateService(executable, 1, http, false,
                    cancelCaller ? TimeSpan.FromMinutes(10) : TimeSpan.FromMilliseconds(400)))
                {
                    Task preparation = service.CheckAndPrepareAsync(caller.Token);
                    Task headersSeen = handler.HeadersReceived.Task;
                    if (await Task.WhenAny(headersSeen, Task.Delay(5000)).ConfigureAwait(false) != headersSeen)
                        throw new InvalidOperationException("The loopback server did not reach the response body.");
                    await headersSeen.ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                    if (preparation.IsCompleted) throw new InvalidOperationException("The response body was not stalled.");
                    Task cancellation = cancelCaller ? Task.Run(() => caller.Cancel()) : Task.FromResult(true);
                    if (await Task.WhenAny(preparation, Task.Delay(3000)).ConfigureAwait(false) != preparation)
                        throw new InvalidOperationException(cancelCaller ? "Cancellation did not interrupt Framework's stalled response body." : "The download deadline did not interrupt the stalled response body.");
                    await preparation.ConfigureAwait(false);
                    await cancellation.ConfigureAwait(false);
                    if (File.Exists(UpdatePackage.GetPath(executable)) || service.HasPreparedUpdate)
                        throw new InvalidOperationException("The stalled response published an update.");
                    return 3;
                }
            }
            finally
            {
                serverStop.Cancel();
                listener.Stop();
                if (server != null) { try { await server.ConfigureAwait(false); } catch (SocketException) { } }
                serverStop.Dispose();
                Directory.Delete(root, true);
            }
        }

        private sealed class BodyHandler : HttpMessageHandler
        {
            private readonly byte[] _manifest;
            private readonly Uri _bodyUri;
            private readonly HttpMessageInvoker _transport;
            internal readonly TaskCompletionSource<bool> HeadersReceived = new TaskCompletionSource<bool>();
            internal BodyHandler(UpdateManifest manifest, int port)
            {
                _manifest = UpdatePackage.SerializeManifest(manifest);
                _bodyUri = new Uri("http://127.0.0.1:" + port + "/body");
                _transport = new HttpMessageInvoker(new HttpClientHandler { UseProxy = false });
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/update.json", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifest) };
                // Only this test transport substitutes a local endpoint. The
                // production service still validates its public GitHub URI.
                using (var local = new HttpRequestMessage(HttpMethod.Get, _bodyUri))
                {
                    HttpResponseMessage response = await _transport.SendAsync(local, token).ConfigureAwait(false);
                    HeadersReceived.TrySetResult(true);
                    return response;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _transport.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
