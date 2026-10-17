using System.Text.Json;
using Messages.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace Messages.Views;

/// <summary>
/// Google sign-in in a private (InPrivate) browser window inside the app. Once Google lands on
/// messages.google.com, the window reads the session cookies, closes, and hands them to pairing, the
/// same thing the copy-a-request steps did by hand. Nothing is kept: the browser profile is a fresh
/// temporary folder, deleted when the window closes.
/// </summary>
public sealed class SignInWindow : Window
{
    private const string StartUrl = "https://accounts.google.com/AccountChooser?continue=https%3A%2F%2Fmessages.google.com%2Fweb%2Fconfig";
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 10, 16, 10), Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "MyPhone-SignIn-" + Guid.NewGuid().ToString("N"));
    private bool _done;

    private SignInWindow()
    {
        Title = "Sign in to Google";
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status.Text = "Sign in with the Google account your phone's Google Messages uses. This window is private and closes by itself.";
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Resize(new SizeInt32(520, 760));
        Closed += (_, _) => Finish(null);
    }

    /// <summary>Shows the window and returns the Google cookies as a JSON object, or null if it was closed.</summary>
    public static async Task<string?> SignInAsync()
    {
        var window = new SignInWindow();
        window.Activate();
        try { await window.StartAsync(); }
        catch (Exception ex)
        {
            Log.Write($"Sign-in window failed: {ex.Message}");
            window.Close();
            throw new InvalidOperationException("The sign-in window couldn't open. Use sign in with a browser instead.");
        }
        return await window._result.Task;
    }

    private async Task StartAsync()
    {
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _profile, new CoreWebView2EnvironmentOptions());
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await _web.EnsureCoreWebView2Async(environment, options);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationCompleted += async (_, _) => await CheckSignedInAsync();
        core.Navigate(StartUrl);
    }

    private async Task CheckSignedInAsync()
    {
        if (_done || _web.CoreWebView2 is not { } core) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var url) || url.Host != "messages.google.com") return;
        var cookies = await core.CookieManager.GetCookiesAsync("https://messages.google.com/web/config");
        var map = new Dictionary<string, string>();
        foreach (var cookie in cookies) map[cookie.Name] = cookie.Value;
        // Pairing needs the signed-in account cookies; without them Google hasn't finished signing in.
        if (!map.ContainsKey("SAPISID") && !map.ContainsKey("__Secure-1PAPISID")) return;
        _status.Text = "Signed in. Pairing with your phone…";
        Finish(JsonSerializer.Serialize(map));
        Close();
    }

    private void Finish(string? cookies)
    {
        if (_done) return;
        _done = true;
        _result.TrySetResult(cookies);
        _ = Task.Run(async () =>
        {
            // The browser process lets go of its folder a moment after the window closes.
            for (var i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(_profile)) Directory.Delete(_profile, recursive: true); return; }
                catch (IOException) { await Task.Delay(1000); }
                catch (UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }
}
