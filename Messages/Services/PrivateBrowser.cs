using System.Diagnostics;
using Microsoft.Win32;

namespace Messages.Services;

/// <summary>Opens a URL in a private window of the user's default browser.</summary>
internal static class PrivateBrowser
{
    public static void Open(string url)
    {
        var progId = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice", "ProgId", null) as string ?? "";
        var command = Registry.GetValue($@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", "", null) as string;
        var exe = ExecutableFrom(command);
        if (exe is null) throw new InvalidOperationException("Couldn't find your default browser. Open a private window yourself and go to messages.google.com/web.");

        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        var flag = name switch
        {
            "msedge" => "--inprivate",
            "firefox" or "librewolf" or "waterfox" => "-private-window",
            "opera" or "launcher" => "--private",
            _ => "--incognito"  // Chrome, Brave, Vivaldi, Chromium and most others.
        };
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { flag, url }, UseShellExecute = false });
    }

    private static string? ExecutableFrom(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        var path = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
        return File.Exists(path) ? path : null;
    }
}
