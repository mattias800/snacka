using System.Reflection;
using System.Runtime.InteropServices;

namespace Snacka.Client.ViewModels;

public class AboutSettingsViewModel : ViewModelBase
{
    public AboutSettingsViewModel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(0, 1, 0);

        Version = $"{version.Major}.{version.Minor}.{version.Build}";
        FullVersion = version.ToString();
        DotNetVersion = RuntimeInformation.FrameworkDescription;
        OperatingSystem = RuntimeInformation.OSDescription;
        Architecture = RuntimeInformation.OSArchitecture.ToString();
        RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier;
    }

    public string Version { get; }
    public string FullVersion { get; }
    public string DotNetVersion { get; }
    public string OperatingSystem { get; }
    public string Architecture { get; }
    public string RuntimeIdentifier { get; }
}
