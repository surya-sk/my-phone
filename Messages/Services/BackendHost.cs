using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Messages.Services;

public sealed class BackendHost : IDisposable
{
    public const int Port = 17077;
    // MYPHONE_DATA_DIR points a test copy at a separate folder so it can't touch the real pairing.
    public static string DataDirectory => Environment.GetEnvironmentVariable("MYPHONE_DATA_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeMessages");
    public static string SessionPath => Path.Combine(DataDirectory, "session.json");
    public static string TokenPath => Path.Combine(DataDirectory, "control.token");
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "Backend", "openmessage.exe");

    private Process? _server;
    private readonly StringBuilder _serverLog = new();

    public bool HasSession => File.Exists(SessionPath);

    public async Task PairAsync(string cookies, Action<string> onEmoji, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("RCS backend is missing", ExecutablePath);
        Directory.CreateDirectory(DataDirectory);
        using var process = CreateProcess("pair --google-stdin");
        if (!process.Start()) throw new InvalidOperationException("Could not start account pairing.");
        ChildProcessJob.Attach(process);
        await process.StandardInput.WriteAsync(cookies);
        process.StandardInput.Close();
        var output = new StringBuilder();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            if (line.StartsWith("EMOJI: ", StringComparison.Ordinal)) onEmoji(line[7..].Trim());
        }
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await errorTask).Trim() is { Length: > 0 } error ? error : output.ToString());
        if (!HasSession) throw new InvalidOperationException("Pairing finished without a saved session.");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("RCS backend is missing", ExecutablePath);
        if (!HasSession) throw new InvalidOperationException("Pair your phone first.");
        Directory.CreateDirectory(DataDirectory);
        var process = CreateProcess("serve");
        _server = process;
        if (!process.Start()) throw new InvalidOperationException("Could not start RCS backend.");
        ChildProcessJob.Attach(process);
        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line) Record(line);
        });
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) Record(line);
        });
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_server.HasExited) throw new InvalidOperationException(ServerLog());
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{Port}/api/status", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var status = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    var dataDir = status.RootElement.GetProperty("auth").GetProperty("data_dir").GetString();
                    if (string.Equals(Path.GetFullPath(dataDir ?? ""), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
                        return;
                    throw new InvalidOperationException("Another service is using the Messages port.");
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("RCS backend did not start. " + ServerLog());
    }

    // Backend lines about which device gets live messages, and about the connection itself, go to
    // app.log so a silent PC can be diagnosed. Only these lines: others can carry numbers or text.
    private static readonly string[] SessionKeywords =
        { "inactive", "active session", "setactive", "browser", "logged out", "loggedout", "listen", "disconnect", "reconnect", "pair", "poll found" };

    private void Record(string line)
    {
        lock (_serverLog) _serverLog.AppendLine(line);
        var lower = line.ToLowerInvariant();
        if (SessionKeywords.Any(lower.Contains)) Log.Write("backend: " + (line.Length > 300 ? line[..300] : line));
    }

    private Process CreateProcess(string arguments)
    {
        var start = new ProcessStartInfo(ExecutablePath, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = DataDirectory
        };
        start.Environment["OPENMESSAGES_DATA_DIR"] = DataDirectory;
        start.Environment["OPENMESSAGES_HOST"] = "127.0.0.1";
        start.Environment["OPENMESSAGES_PORT"] = Port.ToString();
        if (BrowserAuth.SavedBrowser() is { } browser)
        {
            start.Environment["OPENMESSAGE_AUTH_BROWSER"] = browser;
            start.Environment["OPENMESSAGE_CHROME_PROFILE"] = BrowserAuth.Profile(browser);
        }
        return new Process { StartInfo = start };
    }

    private string ServerLog()
    {
        lock (_serverLog) return _serverLog.ToString();
    }

    public void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        _server?.Dispose();
        _server = null;
    }

    public void Dispose() => Stop();
}
