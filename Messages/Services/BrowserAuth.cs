using System.Diagnostics;

namespace Messages.Services;

/// <summary>The browser selected for pairing owns a dedicated persistent sign-in profile.</summary>
internal static class BrowserAuth
{
    private static string SelectionPath => Path.Combine(BackendHost.DataDirectory, "auth-browser.txt");

    public static string? SavedBrowser()
    {
        try
        {
            var browser = File.ReadAllText(SelectionPath).Trim().ToLowerInvariant();
            return browser is "edge" or "chrome" or "firefox" ? browser : null;
        }
        catch (IOException) { return null; }
    }

    public static string Profile(string browser) => Path.Combine(BackendHost.DataDirectory, "BrowserAuth", browser);

    public static bool IsInstalled(string browser) => Executable(browser) is not null;

    public static void SaveBrowser(string browser)
    {
        if (browser is not ("edge" or "chrome" or "firefox")) throw new ArgumentException("Unsupported browser", nameof(browser));
        Directory.CreateDirectory(BackendHost.DataDirectory);
        File.WriteAllText(SelectionPath, browser);
    }

    public static void OpenSignIn(string browser, string url)
    {
        var executable = Executable(browser) ?? throw new FileNotFoundException($"{browser} is not installed on this PC.");
        var profile = Profile(browser);
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (browser == "firefox")
        {
            start.ArgumentList.Add("-no-remote");
            start.ArgumentList.Add("-profile");
            start.ArgumentList.Add(profile);
        }
        else
        {
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add("--no-first-run");
        }
        start.ArgumentList.Add(url);
        _ = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {browser}.");
        SaveBrowser(browser);
    }

    private static string? Executable(string browser)
    {
        var paths = browser switch
        {
            "edge" => new[] { "Microsoft/Edge/Application/msedge.exe" },
            "chrome" => new[] { "Google/Chrome/Application/chrome.exe" },
            "firefox" => new[] { "Mozilla Firefox/firefox.exe" },
            _ => Array.Empty<string>()
        };
        foreach (var relative in paths)
        foreach (var basePath in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        })
        {
            var candidate = Path.Combine(basePath, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
