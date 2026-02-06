using System.Net.Sockets;

namespace Snacka.Client.Services;

public record DetectedPort(int Port, string? ServerType);

public interface IPortDetectionService
{
    Task<IReadOnlyList<DetectedPort>> DetectOpenPortsAsync();
}

public class PortDetectionService : IPortDetectionService
{
    // Common dev server ports and their likely server types
    private static readonly (int port, string label)[] CommonPorts =
    [
        (5173, "Vite"),
        (5174, "Vite"),
        (3000, "Dev server"),
        (3001, "Dev server"),
        (8080, "Dev server"),
        (8000, "Dev server"),
        (4200, "Angular"),
        (6006, "Storybook"),
        (4321, "Astro"),
        (5000, "Dev server"),
        (5001, "Dev server"),
        (8888, "Dev server"),
        (1234, "Parcel"),
        (4000, "Dev server"),
        (9000, "Dev server"),
    ];

    private const int ConnectTimeoutMs = 200;

    public async Task<IReadOnlyList<DetectedPort>> DetectOpenPortsAsync()
    {
        var tasks = CommonPorts.Select(async p =>
        {
            var isOpen = await IsPortOpenAsync(p.port);
            return isOpen ? new DetectedPort(p.port, p.label) : null;
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<DetectedPort>().ToList();
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(ConnectTimeoutMs);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
