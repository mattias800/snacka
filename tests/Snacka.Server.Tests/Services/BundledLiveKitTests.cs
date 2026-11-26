using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Snacka.Server.Services;

namespace Snacka.Server.Tests.Services;

/// <summary>When the server runs LiveKit itself, and what clients are told to connect to (#53).</summary>
[TestClass]
public class BundledLiveKitTests
{
    private string _dataDirectory = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dataDirectory = Path.Combine(Path.GetTempPath(), $"snacka-livekit-{Guid.NewGuid():N}"));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dataDirectory, recursive: true);

    private BundledLiveKitOptions? Resolve(Dictionary<string, string?> settings, bool development = false) =>
        BundledLiveKitOptions.Resolve(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _dataDirectory, development);

    [TestMethod]
    public void ExplicitLiveKit_IsUsedAsIs() =>
        Assert.IsNull(Resolve(new() { ["LiveKit:Url"] = "wss://voice.example", ["LiveKit:ApiSecret"] = "s", ["LiveKit:Binary"] = "/usr/bin/livekit-server" }));

    [TestMethod]
    public void NoBinary_NoBundle() => Assert.IsNull(Resolve(new()));

    [TestMethod]
    public void Binary_RunsWithKeysThatSurviveRestarts()
    {
        var first = Resolve(new() { ["LiveKit:Binary"] = "/opt/livekit-server" });
        var second = Resolve(new() { ["LiveKit:Binary"] = "/opt/livekit-server" });

        Assert.IsNotNull(first);
        Assert.AreEqual("/opt/livekit-server", first.Binary);
        Assert.IsTrue(first.ApiSecret.Length >= 32);
        Assert.AreEqual(first.ApiSecret, second!.ApiSecret);
        Assert.IsTrue(first.UseExternalIp, "a real host announces its public IP");
        Assert.IsFalse(Resolve(new() { ["LiveKit:Binary"] = "/opt/livekit-server" }, development: true)!.UseExternalIp);
    }

    [TestMethod]
    [DataRow("https", "friends.example.com", "wss://friends.example.com/livekit")]
    [DataRow("http", "192.168.1.20:5117", "ws://192.168.1.20:5117/livekit")]
    public void ClientsConnectThroughTheAddressTheyUsedForTheServer(string scheme, string host, string expected)
    {
        var service = new LiveKitTokenService(Options.Create(new LiveKitSettings { ApiKey = "snacka", ApiSecret = "secret", ViaServer = true }));
        var request = new DefaultHttpContext().Request;
        request.Scheme = scheme;
        request.Host = new HostString(host);

        Assert.AreEqual(expected, service.UrlFor(request));
    }
}
