using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Snacka.Server.Services;

namespace Snacka.Server.Tests.Services;

/// <summary>Link previews may only reach the public internet, however the address is spelled (#18).</summary>
[TestClass]
public class SsrfGuardTests
{
    [TestMethod]
    [DataRow("8.8.8.8", true)]
    [DataRow("2606:4700:4700::1111", true)]
    [DataRow("10.1.2.3", false)]
    [DataRow("172.16.0.1", false)]
    [DataRow("192.168.1.1", false)]
    [DataRow("127.0.0.1", false)]
    [DataRow("169.254.169.254", false)]
    [DataRow("100.64.0.1", false)]
    [DataRow("0.0.0.0", false)]
    [DataRow("224.0.0.1", false)]
    [DataRow("255.255.255.255", false)]
    [DataRow("::1", false)]
    [DataRow("::", false)]
    [DataRow("fe80::1", false)]
    [DataRow("fd00::1", false)]
    [DataRow("ff02::1", false)]
    [DataRow("::ffff:169.254.169.254", false)]
    [DataRow("::ffff:8.8.8.8", true)]
    [DataRow("64:ff9b::a9fe:a9fe", false)]
    [DataRow("64:ff9b::808:808", true)]
    [DataRow("2002:a9fe:a9fe::1", false)]
    [DataRow("::127.0.0.1", false)]
    [DataRow("2001:db8::1", false)]
    public void IsPublic(string address, bool expected) =>
        Assert.AreEqual(expected, SsrfGuard.IsPublic(IPAddress.Parse(address)), address);

    /// <summary>A local web server on IPv4 and IPv6 loopback; /secret records that it was reached.</summary>
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int SecretHits;
        public int Port { get; }

        private LocalServer(WebApplication app, int port) => (_app, Port) = (app, port);

        public static async Task<LocalServer> StartAsync()
        {
            var port = FreePort();
            var app = WebApplication.CreateSlimBuilder();
            var built = app.Build();
            var server = new LocalServer(built, port);
            built.Urls.Add($"http://127.0.0.1:{port}");
            built.Urls.Add($"http://[::1]:{port}");
            built.MapGet("/secret", () => { Interlocked.Increment(ref server.SecretHits); return "internal"; });
            built.MapGet("/to-ipv6", () => Results.Redirect($"http://[::1]:{port}/secret"));
            await built.StartAsync();
            return server;
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:{0}/secret")]
    [DataRow("http://localhost:{0}/secret")]
    [DataRow("http://[::1]:{0}/secret")]
    [DataRow("http://[::ffff:127.0.0.1]:{0}/secret")]
    public async Task InternalAddresses_AreNeverDialled(string url)
    {
        await using var server = await LocalServer.StartAsync();
        using var client = new HttpClient(SsrfGuard.CreateHandler());

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            SsrfGuard.SendAsync(client, new Uri(string.Format(url, server.Port)), null, CancellationToken.None));
        Assert.AreEqual(0, server.SecretHits);
    }

    [TestMethod]
    public async Task Redirects_AreCheckedLikeTheFirstRequest()
    {
        await using var server = await LocalServer.StartAsync();
        // Pretend IPv4 loopback is the public internet; IPv6 loopback stays internal.
        using var client = new HttpClient(SsrfGuard.CreateHandler(ip => ip.Equals(IPAddress.Loopback)));

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            SsrfGuard.SendAsync(client, new Uri($"http://127.0.0.1:{server.Port}/to-ipv6"), null, CancellationToken.None));
        Assert.AreEqual(0, server.SecretHits);

        // The allowed address itself still works.
        using var ok = await SsrfGuard.SendAsync(client, new Uri($"http://127.0.0.1:{server.Port}/secret"), null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
    }
}
