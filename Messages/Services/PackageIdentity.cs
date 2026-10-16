using System.Diagnostics;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace Messages.Services;

/// <summary>
/// Gives the unpackaged app package identity through the sparse package in Identity\AppxManifest.xml,
/// which Windows requires for the native taskbar badge. Identity is assigned when a process starts,
/// so after a first registration the app relaunches itself once. A newer manifest version is
/// re-registered and takes effect on the next start.
/// </summary>
internal static class PackageIdentity
{
    private const string RelaunchMarker = "NATIVEMESSAGES_IDENTITY_RELAUNCH";

    public static bool HasIdentity
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current is not null; }
            catch { return false; }
        }
    }

    /// <summary>Returns true when a relaunched copy has started and this process should exit.</summary>
    public static bool EnsureOrRelaunch(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        var root = AppContext.BaseDirectory;
        var manifest = Path.Combine(root, "Identity", "AppxManifest.xml");
        if (!File.Exists(manifest)) return false;

        var hasIdentity = HasIdentity;
        if (hasIdentity && RegisteredVersion() >= ManifestVersion(manifest)) return false;
        // Never loop: if identity didn't stick after one relaunch, run without it.
        if (!hasIdentity && Environment.GetEnvironmentVariable(RelaunchMarker) == "1")
        {
            Log.Write("Package identity still missing after registration; running without it.");
            return false;
        }
        try
        {
            var options = new RegisterPackageOptions
            {
                ExternalLocationUri = new Uri(root),
                DeveloperMode = true,
                ForceUpdateFromAnyVersion = true,
                // This process is using the package; apply the update when it exits.
                DeferRegistrationWhenPackagesAreInUse = hasIdentity
            };
            var result = Register(manifest, options);
            if (result.ExtendedErrorCode is { } error)
            {
                Log.Write($"Identity registration failed: {result.ErrorText} ({error.HResult:X8})");
                return false;
            }
            if (hasIdentity)
            {
                Log.Write("Newer identity package registered; it applies on the next start.");
                return false;
            }
            Log.Write("Identity package registered; relaunching.");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment[RelaunchMarker] = "1";
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            // Needs Developer Mode (or a signed package); without it the app simply has no badge.
            Log.Write($"Identity registration failed: {ex.Message}");
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private static DeploymentResult Register(string manifest, RegisterPackageOptions options) =>
        Task.Run(() => new PackageManager().RegisterPackageByUriAsync(new Uri(manifest), options).AsTask()).Result;

    private static Version RegisteredVersion()
    {
        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        return new Version(v.Major, v.Minor, v.Build, v.Revision);
    }

    private static Version ManifestVersion(string path)
    {
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var value = XDocument.Load(path).Root?.Element(ns + "Identity")?.Attribute("Version")?.Value;
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }
}
