using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Messages.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.BadgeNotifications;

namespace Messages;

public partial class App : Application
{
    private MainWindow? _window;
    private bool _notificationsRegistered;

    public App() => InitializeComponent();

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
            Log.Write($"Notifications registered; setting = {AppNotificationManager.Default.Setting}.");
        }
        catch (Exception ex) { Log.Write($"Notification registration failed: {ex}"); }

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            SetBadge(0);
            // Later toast clicks start a fresh process instead of targeting this one.
            if (_notificationsRegistered) AppNotificationManager.Default.Unregister();
        };
        _window.Activate();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs launch)
            Dispatch(launch);
    }

    /// <summary>A second launch of the app was redirected here (see Program).</summary>
    internal void OnRedirected(AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification)
                Dispatch(notification);
            else _window.BringToFront();
        });
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        _window?.DispatcherQueue.TryEnqueue(() => Dispatch(args));

    private void Dispatch(AppNotificationActivatedEventArgs args)
    {
        if (_window is null) return;
        if (!args.Arguments.TryGetValue("conversationId", out var id) || string.IsNullOrWhiteSpace(id))
        {
            _window.BringToFront();
            return;
        }
        args.Arguments.TryGetValue("action", out var action);
        args.UserInput.TryGetValue("reply", out var reply);
        _ = _window.HandleNotificationAsync(action ?? "", id, reply);
    }

    internal void ShowMessageToast(string conversationId, string messageId, string title, string body, Uri? avatar, Uri? image)
    {
        if (!_notificationsRegistered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("conversationId", conversationId)
                .SetTag(ShortHash(messageId))
                .SetGroup(ShortHash(conversationId))
                .AddText(title, new AppNotificationTextProperties().SetMaxLines(1))
                .AddText(string.IsNullOrWhiteSpace(body) ? "New message" : body);
            if (avatar is not null) builder.SetAppLogoOverride(avatar, AppNotificationImageCrop.Default);
            if (image is not null) builder.SetInlineImage(image);
            builder.AddTextBox("reply", "Reply", "")
                .AddButton(new AppNotificationButton("Send")
                    .AddArgument("action", "reply")
                    .AddArgument("conversationId", conversationId)
                    .SetInputId("reply"))
                .AddButton(new AppNotificationButton("Mark as read")
                    .AddArgument("action", "read")
                    .AddArgument("conversationId", conversationId));
            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Log.Write($"Toast shown (id {notification.Id}).");
        }
        catch (Exception ex) { Log.Write($"Notification failed: {ex}"); }
    }

    internal void ShowErrorToast(string title, string body, string conversationId)
    {
        if (!_notificationsRegistered) return;
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddArgument("conversationId", conversationId)
                .AddText(title)
                .AddText(body)
                .BuildNotification());
        }
        catch (Exception ex) { Log.Write($"Notification failed: {ex}"); }
    }

    internal void ClearConversationNotifications(string conversationId)
    {
        if (!_notificationsRegistered) return;
        try { _ = AppNotificationManager.Default.RemoveByGroupAsync(ShortHash(conversationId)); }
        catch (Exception ex) { Log.Write($"Clearing notifications failed: {ex}"); }
    }

    /// <summary>The native taskbar badge. Windows only offers it to apps with package identity (see PackageIdentity).</summary>
    internal void SetBadge(int count)
    {
        if (!PackageIdentity.HasIdentity) return;
        try
        {
            if (count > 0) BadgeNotificationManager.Current.SetBadgeAsCount((uint)count);
            else BadgeNotificationManager.Current.ClearBadge();
        }
        catch (Exception ex) { Log.Write($"Badge update failed: {ex.Message}"); }
    }

    // Toast tags and groups are limited to 64 characters; conversation IDs can be longer.
    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
