using System.Diagnostics;

namespace Messages.Services;

/// <summary>Small diagnostic log in the data folder. Never pass message text, numbers or credentials.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string PathName => System.IO.Path.Combine(BackendHost.DataDirectory, "app.log");

    public static void Write(string text)
    {
        Debug.WriteLine(text);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(BackendHost.DataDirectory);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 512 * 1024) File.Move(PathName, PathName + ".old", overwrite: true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
