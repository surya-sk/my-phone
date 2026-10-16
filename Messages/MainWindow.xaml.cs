using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Messages.Models;
using Messages.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using Path = System.IO.Path;

namespace Messages;

public sealed partial class MainWindow : Window
{
    private enum InboxFilter { All, Unread, Archived }

    private static readonly string[] QuickReactions = { "👍", "❤️", "😂", "😮", "😢", "😡" };
    private static readonly string SettingsPath = Path.Combine(BackendHost.DataDirectory, "notifications.off");

    private readonly BackendHost _backend = new();
    private readonly OpenMessageClient _client = new();
    private readonly MediaService _media;

    private readonly ObservableCollection<object> _inboxItems = new();
    private readonly SectionHeaderItem _peopleSearchHeader = new("people");
    private readonly ObservableCollection<ThreadItem> _threadItems = new();
    private readonly ObservableCollection<AttachmentItem> _attachments = new();
    private readonly Dictionary<string, ConversationItem> _conversationItems = new();
    private readonly Dictionary<string, ThreadItem> _threadItemCache = new();
    private readonly List<ChatMessage> _threadMessages = new();
    private readonly List<PendingSend> _pendingSends = new();
    private readonly Dictionary<string, string> _drafts = new();
    // RCS/SMS worked out from each opened thread's history; the backend's label can be stale (see Protocol).
    private readonly Dictionary<string, string> _resolvedProtocols = new();
    private readonly HashSet<string> _encryptedThreads = new();
    private IReadOnlyList<Conversation> _allConversations = Array.Empty<Conversation>();
    private List<object> _searchResultItems = new();

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    // Notification bookkeeping: only messages newer than this app session (and not already shown) alert.
    private readonly Dictionary<string, long> _knownConversationTimestamps = new();
    private readonly HashSet<string> _notifiedMessageIds = new();
    private readonly HashSet<string> _notificationScans = new();
    private long _notificationFloorMs;
    private bool _notificationsEnabled = !File.Exists(SettingsPath);

    private readonly List<Func<Task>> _readyActions = new();
    private Conversation? _selected;
    private InboxFilter _filter = InboxFilter.All;
    private string _searchQuery = "";
    private ChatMessage? _replyTo;
    private bool _inboxReady;
    private bool _refreshing;
    private bool _refreshQueued;
    private bool _queuedRefreshIncludesThread;
    private bool _threadRefreshing;
    private bool _threadRefreshQueued;
    private bool _windowActive = true;
    private bool _loadingOlder;
    private bool _hasMoreHistory;
    private bool _stickToBottom = true;
    // Google delivers live messages to one linked session at a time; see ActivateHereAsync.
    private bool _sessionInactive;
    private bool _activating;
    private DateTimeOffset _lastActivateAttempt;
    private int _settlingScroll;
    private bool _pagingArmed;
    private bool _narrow;
    private bool _suppressSelection;
    private int _threadVersion;
    private string? _pendingSelectId;
    private string? _pendingJumpMessageId;
    private CancellationTokenSource? _eventsCts;
    private DateTimeOffset _typingExpires;
    private ScrollViewer? _messageScroller;
    private MediaPlayer? _audioPlayer;
    private MessageItem? _audioItem;
    private (byte[] Data, string Mime, string Name)? _viewerMedia;
    private Func<Task>? _statusAction;

    public MainWindow()
    {
        InitializeComponent();
        _media = new MediaService(_client);
        ConversationList.ItemsSource = _inboxItems;
        MessageList.ItemsSource = _threadItems;
        AttachmentTray.ItemsSource = _attachments;
        NotificationsToggle.IsChecked = _notificationsEnabled;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 420;
            presenter.PreferredMinimumHeight = 520;
        }
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1200 * scale), (int)(820 * scale)));

        BuildPairingSteps();
        var savedBrowser = BrowserAuth.SavedBrowser();
        PairingBrowser.SelectedItem = PairingBrowser.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string?)item.Tag == savedBrowser)
            ?? PairingBrowser.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string browser && BrowserAuth.IsInstalled(browser));
        UpdateFilterHeaders();
        AddAccelerator(VirtualKey.N, (_, _) => NewChat_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.F, (_, _) => { if (InboxPanel.Visibility == Visibility.Visible) SearchBox.Focus(FocusState.Keyboard); });

        Activated += OnActivated;
        Closed += OnClosed;
        Root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        MessageList.Loaded += (_, _) => HookScroller();
        // The reply bar, attachment tray and typing row shrink the transcript; stay pinned to the newest message.
        MessageList.SizeChanged += (_, _) => { if (_stickToBottom && _threadItems.Count > 0) ScrollToBottom(); };
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(includeThread: true);
        _typingTimer.Tick += (_, _) => { if (DateTimeOffset.UtcNow > _typingExpires) HideTyping(); };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunMessageSearchAsync(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusBanner.Visibility = Visibility.Collapsed; };
        _ = InitializeAsync();
    }

    private void AddAccelerator(VirtualKey key, Action<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (s, e) => { e.Handled = true; handler(s, e); };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // =====================================================================
    // Startup, pairing, lifetime
    // =====================================================================

    private async Task InitializeAsync()
    {
        if (!_backend.HasSession)
        {
            SetConnection(null, "not paired");
            return;
        }
        PairStatus.Text = "Starting your paired phone connection…";
        try { await ShowInboxAsync(); }
        catch (Exception ex)
        {
            PairStatus.Text = ex.Message;
            SetConnection(false, "couldn't start");
        }
    }

    private async Task ShowInboxAsync()
    {
        _notificationFloorMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _knownConversationTimestamps.Clear();
        _notifiedMessageIds.Clear();
        SetConnection(null, "starting…");
        await _backend.StartAsync();
        _client.LoadLocalToken();
        PairingPanel.Visibility = Visibility.Collapsed;
        InboxPanel.Visibility = Visibility.Visible;
        ApplyLayout(Root.ActualWidth);
        _refreshTimer.Start();
        _eventsCts?.Cancel();
        _eventsCts = new CancellationTokenSource();
        _ = _client.WatchEventsAsync(OnBackendEvent, _eventsCts.Token);
        await RefreshAsync(includeThread: false);
        _inboxReady = true;
        var actions = _readyActions.ToArray();
        _readyActions.Clear();
        foreach (var action in actions) await action();
    }

    private void BuildPairingSteps()
    {
        var steps = new[]
        {
            "Choose your browser, then select open Google sign-in. Sign in with the account chosen in Google Messages on your phone. Keep this dedicated browser profile signed in so My Phone can renew its session.",
            "When the browser reaches messages.google.com/web/config, press F12 and open the Network tab.",
            "Press Ctrl+R to reload. Find the request named config whose URL contains messages.google.com/web/config.",
            "Right-click that request. Edge: Copy → Copy as cURL (bash). Firefox: Copy → Copy as cURL.",
            "Paste it below with Ctrl+V. It starts with curl and contains a Cookie header. Select pair phone.",
            "On your phone open Google Messages → account menu → Device pairing, and tap the emoji shown here."
        };
        for (var i = 0; i < steps.Length; i++)
        {
            var row = new Grid { ColumnSpacing = 18 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["MetroDisplayFont"],
                FontWeight = Microsoft.UI.Text.FontWeights.Light,
                FontSize = 34,
                LineHeight = 34,
                Style = (Style)Application.Current.Resources["PairingStepNumberStyle"],
                TextLineBounds = Microsoft.UI.Xaml.TextLineBounds.Tight
            });
            var text = new TextBlock { Text = steps[i], TextWrapping = TextWrapping.Wrap, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            PairingSteps.Items.Add(row);
        }
    }

    private void OpenSignIn_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://accounts.google.com/AccountChooser?continue=https://messages.google.com/web/config";
        try
        {
            if (PairingBrowser.SelectedItem is not ComboBoxItem { Tag: string browser })
                throw new InvalidOperationException("Choose an installed browser first.");
            BrowserAuth.OpenSignIn(browser, url);
        }
        catch (Exception ex) { PairStatus.Text = ex.Message; }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (PairingBrowser.SelectedItem is not ComboBoxItem { Tag: string browser })
        {
            PairStatus.Text = "Choose the browser you used for pairing first.";
            return;
        }
        var cookies = CookieInput.Text.Trim();
        if (cookies.Length == 0) { PairStatus.Text = "Paste the copied browser request first."; return; }
        CookieInput.Text = "";
        PairButton.IsEnabled = false;
        EmojiPanel.Visibility = Visibility.Collapsed;
        EmojiText.Text = "";
        PairProgress.IsActive = true;
        PairStatus.Text = "Waiting for Google Messages…";
        try
        {
            BrowserAuth.SaveBrowser(browser);
            await _backend.PairAsync(cookies, emoji => DispatcherQueue.TryEnqueue(() =>
            {
                EmojiText.Text = emoji;
                EmojiPanel.Visibility = Visibility.Visible;
                PairStatus.Text = "Tap the matching emoji on your phone.";
                PairingPanel.ChangeView(null, PairingPanel.ScrollableHeight, null);
            }));
            PairStatus.Text = "Paired. Starting My Phone…";
            await ShowInboxAsync();
        }
        catch (Exception ex) { PairStatus.Text = ex.Message; }
        finally { PairProgress.IsActive = false; PairButton.IsEnabled = true; }
    }

    private async void PairAgain_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "pair again?",
            Content = "My Phone disconnects from your phone until you finish pairing again.",
            PrimaryButtonText = "Pair again",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _inboxReady = false;
        _refreshTimer.Stop();
        _eventsCts?.Cancel();
        _backend.Stop();
        CloseThread();
        _allConversations = Array.Empty<Conversation>();
        _conversationItems.Clear();
        _inboxItems.Clear();
        _knownConversationTimestamps.Clear();
        _notifiedMessageIds.Clear();
        _media.Clear();
        CookieInput.Text = "";
        EmojiText.Text = "";
        EmojiPanel.Visibility = Visibility.Collapsed;
        PairStatus.Text = "Pair this PC with your phone again.";
        InboxPanel.Visibility = Visibility.Collapsed;
        PairingPanel.Visibility = Visibility.Visible;
        SetConnection(null, "not paired");
        UpdateBadge();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_windowActive && _sessionInactive) _ = ActivateHereAsync(automatic: true);
        if (_windowActive && _selected is not null)
        {
            App.Current.ClearConversationNotifications(_selected.Id);
            if (_selected.UnreadCount > 0) _ = MarkReadAsync(_selected);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _typingTimer.Stop();
        _eventsCts?.Cancel();
        _audioPlayer?.Dispose();
        ViewerPlayer.MediaPlayer?.Dispose();
        _client.Dispose();
        _backend.Dispose();
    }

    /// <summary>Restores and focuses the window, for toast clicks and second launches.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // =====================================================================
    // Notifications
    // =====================================================================

    /// <summary>Handles a toast click, inline reply, or "mark as read" button.</summary>
    public async Task HandleNotificationAsync(string action, string conversationId, string? replyText)
    {
        if (!_inboxReady)
        {
            if (action.Length == 0) BringToFront();
            _readyActions.Add(() => HandleNotificationAsync(action, conversationId, replyText));
            return;
        }
        var conversation = _allConversations.FirstOrDefault(c => c.Id == conversationId);
        switch (action)
        {
            case "reply" when !string.IsNullOrWhiteSpace(replyText):
                try
                {
                    await _client.SendTextAsync(conversationId, replyText.Trim(), null, Guid.NewGuid().ToString("N"));
                    if (conversation is not null) await MarkReadAsync(conversation);
                }
                catch (Exception ex)
                {
                    App.Current.ShowErrorToast(conversation?.DisplayName ?? "My Phone", $"Reply not sent: {ex.Message}", conversationId);
                }
                await RefreshAsync(includeThread: true);
                break;
            case "read":
                if (conversation is not null) await MarkReadAsync(conversation);
                break;
            default:
                BringToFront();
                OpenConversation(conversationId);
                break;
        }
    }

    public void OpenConversation(string conversationId)
    {
        _searchQuery = "";
        SearchBox.Text = "";
        var conversation = _allConversations.FirstOrDefault(c => c.Id == conversationId);
        if (conversation is null)
        {
            _pendingSelectId = conversationId;
            _ = RefreshAsync(includeThread: false);
            return;
        }
        SelectInList(conversation);
    }

    private void SelectInList(Conversation conversation)
    {
        var wanted = conversation.IsArchived ? InboxFilter.Archived
            : _filter == InboxFilter.Unread && conversation.UnreadCount > 0 ? InboxFilter.Unread
            : InboxFilter.All;
        if (_filter != wanted)
        {
            _filter = wanted;
            UpdateFilterHeaders();
            RebuildInbox();
        }
        if (_conversationItems.TryGetValue(conversation.Id, out var item) && _inboxItems.Contains(item))
            ConversationList.SelectedItem = item;
        else
            _ = SelectConversationAsync(conversation);
    }

    private void DetectNewMessages(IReadOnlyList<Conversation> conversations)
    {
        foreach (var conversation in conversations)
        {
            _knownConversationTimestamps.TryGetValue(conversation.Id, out var previous);
            if (conversation.TimestampMs > _notificationFloorMs && conversation.TimestampMs > previous)
                _ = ScanIncomingAsync(conversation, previous);
            _knownConversationTimestamps[conversation.Id] = Math.Max(previous, conversation.TimestampMs);
        }
    }

    private async Task ScanIncomingAsync(Conversation conversation, long previousTimestamp)
    {
        if (!_notificationScans.Add(conversation.Id)) return;
        try
        {
            var floor = Math.Max(_notificationFloorMs, previousTimestamp);
            var messages = await _client.GetMessagesAsync(conversation.Id, 20);
            var fresh = messages.Where(m => !m.IsFromMe && !m.IsSystem && m.TimestampMs > floor && m.Id.Length > 0
                                            && _notifiedMessageIds.Add($"{conversation.Id}:{m.Id}")).ToList();
            if (fresh.Count == 0) return;

            // Like Google Messages, a new incoming message brings an archived thread back to the inbox.
            if (conversation.IsArchived)
            {
                try { await _client.SetTabAsync(conversation.Id, ""); RequestRefresh(); }
                catch (Exception ex) { Log.Write($"Unarchive failed: {ex}"); }
            }

            if (_windowActive && _selected?.Id == conversation.Id) return;
            if (!_windowActive) FlashTaskbar();
            if (!_notificationsEnabled || conversation.IsMuted) return;
            foreach (var message in fresh.TakeLast(3)) await ShowToastAsync(conversation, message);
        }
        catch (Exception ex)
        {
            _knownConversationTimestamps.Remove(conversation.Id);
            Log.Write($"Incoming notification scan failed: {ex}");
        }
        finally { _notificationScans.Remove(conversation.Id); }
    }

    private async Task ShowToastAsync(Conversation conversation, ChatMessage message)
    {
        var sender = message.SenderName.Length > 0 ? message.SenderName : message.SenderNumber;
        var body = Ui.Snippet(message);
        if (conversation.IsGroup && sender.Length > 0) body = $"{sender}: {body}";
        var participant = conversation.IsGroup ? FindParticipant(conversation, message) : conversation.Others.FirstOrDefault();
        Uri? avatar = null;
        Uri? image = null;
        try { avatar = await _media.GetAvatarFileAsync(participant); } catch { }
        var mime = message.MimeType.ToLowerInvariant();
        if (mime is "image/jpeg" or "image/png" or "image/gif" && message.MediaId.Length > 0)
        {
            try
            {
                if (await _media.GetMediaAsync(message.Id) is { } media && media.Data.Length < 3 * 1024 * 1024)
                    image = await MediaService.WriteCacheFileAsync("toast", media.Data, MediaService.ExtensionFor(mime));
            }
            catch { }
        }
        App.Current.ShowMessageToast(conversation.Id, message.Id, conversation.DisplayName, body, avatar, image);
    }

    private void NotificationsToggle_Click(object sender, RoutedEventArgs e)
    {
        _notificationsEnabled = NotificationsToggle.IsChecked;
        try
        {
            if (_notificationsEnabled) File.Delete(SettingsPath);
            else { Directory.CreateDirectory(BackendHost.DataDirectory); File.WriteAllText(SettingsPath, ""); }
        }
        catch { }
    }

    private void UpdateBadge()
    {
        var total = _allConversations.Where(c => !c.IsArchived && !c.IsMuted).Sum(c => c.UnreadCount);
        Title = total > 0 ? $"My Phone ({total})" : "My Phone";
        App.Current.SetBadge(total);
    }

    // =====================================================================
    // Backend events and refresh
    // =====================================================================

    private void OnBackendEvent(BackendEvent evt) => DispatcherQueue.TryEnqueue(() =>
    {
        switch (evt.Type)
        {
            case "typing" when evt.ConversationId == _selected?.Id:
                if (evt.Typing) ShowTyping(evt.SenderName);
                else HideTyping();
                break;
            case "messages":
                if (_selected is not null && (evt.ConversationId.Length == 0 || evt.ConversationId == _selected.Id))
                {
                    HideTyping();
                    _ = RefreshThreadAsync();
                }
                RequestRefresh();
                break;
            case "conversations":
                RequestRefresh();
                break;
            case "status":
                RequestRefresh();
                break;
        }
    });

    private void RequestRefresh() => _ = RefreshAsync(includeThread: false);

    private async Task RefreshAsync(bool includeThread)
    {
        if (!_inboxReady && InboxPanel.Visibility != Visibility.Visible) return;
        if (_refreshing)
        {
            _refreshQueued = true;
            _queuedRefreshIncludesThread |= includeThread;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshQueued = false;
                includeThread |= _queuedRefreshIncludesThread;
                _queuedRefreshIncludesThread = false;
                PhoneStatus? status = null;
                try { status = await _client.GetStatusAsync(); }
                catch { SetConnection(false, "phone status unavailable"); }
                var conversations = await _client.GetConversationsAsync();
                if (status is not null) ApplyStatus(status);
                DetectNewMessages(conversations);
                _allConversations = conversations = conversations.Select(WithResolvedProtocol).ToArray();
                if (_selected is not null && conversations.FirstOrDefault(c => c.Id == _selected.Id) is { } fresh && !_selected.Equals(fresh))
                {
                    _selected = fresh;
                    UpdateThreadHeader(loadAvatar: false);
                }
                RebuildInbox();
                UpdateBadge();
                if (_pendingSelectId is { } pendingId && conversations.FirstOrDefault(c => c.Id == pendingId) is { } pending)
                {
                    _pendingSelectId = null;
                    SelectInList(pending);
                }
                if (includeThread && _selected is not null) await RefreshThreadAsync();
                if (_selected is { UnreadCount: > 0 } open && _windowActive) _ = MarkReadAsync(open);
                includeThread = false;
            } while (_refreshQueued);
        }
        catch (Exception ex)
        {
            SetConnection(false, "can't reach the local service");
            Log.Write($"Refresh failed: {ex.Message}");
        }
        finally { _refreshing = false; }
    }

    private void ApplyStatus(PhoneStatus status)
    {
        _sessionInactive = status.Connected && status.SessionInactive;
        if (_sessionInactive) SetConnection(false, "active on another device");
        else if (status.Connected) SetConnection(true, null);
        else if (status.SignInExpired) SetConnection(false, "Google sign-in expired");
        else SetConnection(false, status.PhoneResponding ? "reconnecting…" : "phone not connected");

        AuthBanner.Visibility = Ui.Show(status.SignInExpired || _sessionInactive);
        if (_sessionInactive)
        {
            AuthBannerText.Text = "Google Messages is active on another device, so new messages aren't arriving here.";
            AuthBannerAction.Content = "use here";
        }
        else
        {
            AuthBannerText.Text = "Messages couldn't renew this connection. Sign in using the selected browser, then pair again if needed.";
            AuthBannerAction.Content = "pair again";
        }
        ToolTipService.SetToolTip(ConnectionText, status.LastError.Length > 0 ? status.LastError : null);

        // Opening Messages on this PC means you want it here; take the session back.
        if (_sessionInactive && _windowActive) _ = ActivateHereAsync(automatic: true);
    }

    private void AuthBannerAction_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionInactive) _ = ActivateHereAsync(automatic: false);
        else PairAgain_Click(sender, e);
    }

    /// <summary>
    /// Takes live delivery back from another linked device, then reloads. The backend paces this
    /// (Google throttles accounts that keep switching), so automatic attempts are spaced out too.
    /// </summary>
    private async Task ActivateHereAsync(bool automatic)
    {
        if (_activating) return;
        if (automatic && DateTimeOffset.UtcNow - _lastActivateAttempt < TimeSpan.FromMinutes(2)) return;
        _activating = true;
        _lastActivateAttempt = DateTimeOffset.UtcNow;
        try
        {
            await _client.ActivateAsync();
            Log.Write($"Session activated on this device ({(automatic ? "window focused" : "user")}).");
            if (!automatic) ShowStatus("My Phone is active on this PC again. Catching up…");
        }
        catch (Exception ex)
        {
            Log.Write($"Activate failed: {ex.Message}");
            if (!automatic) ShowStatus($"Couldn't switch Google Messages to this PC: {ex.Message}");
        }
        finally { _activating = false; }
        await RefreshAsync(includeThread: true);
    }

    private void SetConnection(bool? connected, string? text)
    {
        ConnectionDot.Visibility = connected is null ? Visibility.Collapsed : Visibility.Visible;
        ConnectionDot.Fill = new SolidColorBrush(connected == true
            ? Windows.UI.Color.FromArgb(255, 0x60, 0xA9, 0x17)
            : Windows.UI.Color.FromArgb(255, 0xE5, 0x14, 0x00));
        ConnectionText.Text = text ?? (connected == true ? "phone connected" : "phone not connected");
    }

    /// <summary>
    /// A real refresh: take the Google session back for this PC (only one linked PC receives live
    /// messages at a time), then pull the last few days from the phone and reload.
    /// </summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus("Syncing with your phone…");
        try
        {
            // Take live delivery back if another device has it ("Use here"); this is a no-op cost when we already do.
            try { await _client.ActivateAsync(); }
            catch (Exception ex) { Log.Write($"Activate during refresh: {ex.Message}"); }
            var newest = _allConversations.Count > 0 ? _allConversations.Max(c => c.TimestampMs) : 0;
            var since = newest > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(newest).AddDays(-1)
                : DateTimeOffset.UtcNow.AddDays(-3);
            if (since < DateTimeOffset.UtcNow.AddDays(-14)) since = DateTimeOffset.UtcNow.AddDays(-14);
            await _client.SyncSinceAsync(since);
            Log.Write("Manual refresh: reconnected and requested phone sync.");
            ShowStatus("Synced with your phone. New messages appear as they arrive.");
        }
        catch (Exception ex)
        {
            Log.Write($"Manual refresh failed: {ex.Message}");
            ShowStatus($"Couldn't sync with your phone: {ex.Message}");
        }
        await RefreshAsync(includeThread: true);
    }

    // =====================================================================
    // Inbox
    // =====================================================================

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = (sender as FrameworkElement)?.Tag switch
        {
            "unread" => InboxFilter.Unread,
            "archived" => InboxFilter.Archived,
            _ => InboxFilter.All
        };
        UpdateFilterHeaders();
        if (SearchBox.Text.Length > 0) SearchBox.Text = ""; // rebuilds through TextChanged
        else RebuildInbox();
    }

    private void UpdateFilterHeaders()
    {
        FilterAll.Opacity = _filter == InboxFilter.All ? 1 : 0.42;
        FilterUnread.Opacity = _filter == InboxFilter.Unread ? 1 : 0.42;
        FilterArchived.Opacity = _filter == InboxFilter.Archived ? 1 : 0.42;
    }

    private void RebuildInbox()
    {
        IEnumerable<Conversation> list = _filter switch
        {
            InboxFilter.Unread => _allConversations.Where(c => !c.IsArchived && c.UnreadCount > 0),
            InboxFilter.Archived => _allConversations.Where(c => c.IsArchived),
            _ => _allConversations.Where(c => !c.IsArchived)
        };
        var query = _searchQuery;
        if (query.Length > 0)
        {
            var digits = new string(query.Where(char.IsDigit).ToArray());
            list = _allConversations.Where(c => c.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Participants.Any(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (digits.Length >= 3 && new string(p.Number.Where(char.IsDigit).ToArray()).Contains(digits))));
        }
        var ordered = (_filter == InboxFilter.All && query.Length == 0
            ? list.OrderByDescending(c => c.IsFavorite).ThenByDescending(c => c.TimestampMs)
            : list.OrderByDescending(c => c.TimestampMs)).ToList();

        var live = new HashSet<string>(_allConversations.Select(c => c.Id));
        foreach (var stale in _conversationItems.Keys.Where(id => !live.Contains(id)).ToList()) _conversationItems.Remove(stale);

        var desired = new List<object>();
        if (query.Length > 0 && _searchResultItems.Count > 0 && ordered.Count > 0) desired.Add(_peopleSearchHeader);
        foreach (var conversation in ordered)
        {
            if (!_conversationItems.TryGetValue(conversation.Id, out var item))
                _conversationItems[conversation.Id] = item = new ConversationItem(conversation);
            else item.Update(conversation);
            desired.Add(item);
        }
        if (query.Length > 0) desired.AddRange(_searchResultItems);

        _suppressSelection = true;
        try
        {
            Reconcile(_inboxItems, desired);
            object? selectedItem = _selected is not null && _conversationItems.TryGetValue(_selected.Id, out var sel) && _inboxItems.Contains(sel) ? sel : null;
            if (ConversationList.SelectedItem is SearchHitItem hit && _inboxItems.Contains(hit)) selectedItem = hit;
            if (!ReferenceEquals(ConversationList.SelectedItem, selectedItem)) ConversationList.SelectedItem = selectedItem;
        }
        finally { _suppressSelection = false; }

        var empty = desired.Count == 0;
        ListEmpty.Visibility = Ui.Show(empty);
        if (empty)
        {
            (ListEmptyTitle.Text, ListEmptyText.Text) = query.Length > 0
                ? ("no results", $"Nothing matches “{query}”.")
                : _filter switch
                {
                    InboxFilter.Unread => ("all caught up", "You have no unread conversations."),
                    InboxFilter.Archived => ("nothing archived", "Right-click a conversation and choose Archive to tidy your inbox."),
                    _ => ("no conversations", "Conversations from your phone will appear here.")
                };
        }
        FilterUnread.Content = _allConversations.Count(c => !c.IsArchived && c.UnreadCount > 0) is var unread and > 0 ? $"unread {unread}" : "unread";
    }

    /// <summary>Applies the desired order to an observable collection with the fewest changes, so lists don't flicker.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], want)) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], want)) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, want);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        switch (ConversationList.SelectedItem)
        {
            case ConversationItem item:
                _ = SelectConversationAsync(item.Model);
                break;
            case SearchHitItem hit when _allConversations.FirstOrDefault(c => c.Id == hit.Message.ConversationId) is { } conversation:
                _pendingJumpMessageId = hit.Message.Id;
                if (_selected?.Id == conversation.Id) _ = JumpToMessageAsync(hit.Message.Id);
                else _ = SelectConversationAsync(conversation);
                break;
        }
    }

    private void ConversationList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var header = args.Item is SectionHeaderItem;
        args.ItemContainer.IsHitTestVisible = !header;
        args.ItemContainer.IsTabStop = !header;
        if (args.Item is ConversationItem item) item.EnsureAvatar(_media);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text.Trim();
        _searchTimer.Stop();
        if (_searchQuery.Length < 2) _searchResultItems = new List<object>();
        else _searchTimer.Start();
        RebuildInbox();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { SearchBox.Text = ""; e.Handled = true; }
        else if (e.Key == VirtualKey.Down && _inboxItems.Count > 0) { ConversationList.Focus(FocusState.Keyboard); e.Handled = true; }
    }

    private async Task RunMessageSearchAsync()
    {
        var query = _searchQuery;
        if (query.Length < 2) return;
        try
        {
            var hits = await _client.SearchMessagesAsync(query);
            if (query != _searchQuery) return;
            var names = _allConversations.ToDictionary(c => c.Id, c => c.DisplayName);
            var items = new List<object>();
            var matches = hits.Where(h => names.ContainsKey(h.ConversationId)).Take(40)
                .Select(h => (object)new SearchHitItem(h, names[h.ConversationId])).ToList();
            if (matches.Count > 0)
            {
                items.Add(new SectionHeaderItem("messages"));
                items.AddRange(matches);
            }
            _searchResultItems = items;
            RebuildInbox();
        }
        catch (Exception ex) { Log.Write($"Search failed: {ex}"); }
    }

    private void Conversation_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConversationItem item) return;
        args.Handled = true;
        var flyout = BuildConversationMenu(item.Model);
        if (args.TryGetPosition(sender, out var point)) flyout.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else flyout.ShowAt((FrameworkElement)sender);
    }

    private MenuFlyout BuildConversationMenu(Conversation conversation)
    {
        var flyout = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { ShowStatus(ex.Message); }
                await RefreshAsync(includeThread: false);
            };
            flyout.Items.Add(item);
        }
        if (conversation.UnreadCount > 0) Add("Mark as read", "", () => MarkReadAsync(conversation));
        Add(conversation.IsFavorite ? "Unpin" : "Pin to top", conversation.IsFavorite ? "" : "",
            () => _client.SetFavoriteAsync(conversation.Id, !conversation.IsFavorite));
        Add(conversation.IsMuted ? "Turn on notifications" : "Mute notifications", conversation.IsMuted ? "" : "",
            () => _client.SetNotificationModeAsync(conversation.Id, conversation.IsMuted ? "all" : "muted"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(conversation.IsArchived ? "Unarchive" : "Archive", conversation.IsArchived ? "" : "",
            () => SetArchivedAsync(conversation, !conversation.IsArchived));
        var numbers = string.Join(", ", conversation.Others.Select(p => p.Number).Where(n => n.Length > 0));
        if (numbers.Length > 0)
        {
            var copy = new MenuFlyoutItem { Text = "Copy number", Icon = new FontIcon { Glyph = "" } };
            copy.Click += (_, _) => CopyText(numbers);
            flyout.Items.Add(copy);
        }
        return flyout;
    }

    private async Task SetArchivedAsync(Conversation conversation, bool archived)
    {
        await _client.SetTabAsync(conversation.Id, archived ? "archive" : "");
        if (archived && _selected?.Id == conversation.Id && _filter != InboxFilter.Archived) CloseThread();
        ShowStatus(archived ? $"{conversation.DisplayName} archived" : $"{conversation.DisplayName} moved to inbox",
            "undo", async () =>
            {
                await _client.SetTabAsync(conversation.Id, archived ? "" : "archive");
                await RefreshAsync(includeThread: false);
            });
    }

    private async Task MarkReadAsync(Conversation conversation)
    {
        App.Current.ClearConversationNotifications(conversation.Id);
        if (conversation.UnreadCount == 0) return;
        try
        {
            await _client.MarkReadAsync(conversation.Id);
            _allConversations = _allConversations.Select(c => c.Id == conversation.Id ? c with { UnreadCount = 0 } : c).ToArray();
            if (_selected?.Id == conversation.Id) _selected = _selected with { UnreadCount = 0 };
            RebuildInbox();
            UpdateBadge();
        }
        catch (Exception ex) { Log.Write($"Mark read failed: {ex}"); }
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        foreach (var conversation in _allConversations.Where(c => c.UnreadCount > 0).ToList())
            await MarkReadAsync(conversation);
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (InboxPanel.Visibility != Visibility.Visible) return;
        ContactEntry? chosen = null;
        var box = new AutoSuggestBox
        {
            PlaceholderText = "name or phone number",
            QueryIcon = new FontIcon { Glyph = "" },
            Width = 360
        };
        box.TextChanged += async (s, a) =>
        {
            if (a.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            chosen = null;
            var text = s.Text.Trim();
            if (text.Length < 2) { s.ItemsSource = null; return; }
            try
            {
                var contacts = await _client.GetContactsAsync(text);
                if (s.Text.Trim() == text) s.ItemsSource = contacts.Select(c => $"{c.DisplayName}  ·  {c.Number}").ToList();
                s.Tag = contacts;
            }
            catch { s.ItemsSource = null; }
        };
        box.SuggestionChosen += (s, a) =>
        {
            if (s.Tag is IReadOnlyList<ContactEntry> contacts && a.SelectedItem is string label)
                chosen = contacts.FirstOrDefault(c => $"{c.DisplayName}  ·  {c.Number}" == label);
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Google Messages on your phone decides whether the chat uses RCS or SMS.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "new message",
            Content = panel,
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var number = chosen?.Number ?? box.Text.Split('·').Last().Trim();
        if (string.IsNullOrWhiteSpace(number)) return;
        try
        {
            var id = await _client.NewConversationAsync(number);
            _searchQuery = "";
            SearchBox.Text = "";
            await RefreshAsync(includeThread: false);
            var conversation = _allConversations.FirstOrDefault(c => c.Id == id)
                ?? new Conversation(id, chosen?.DisplayName ?? number, "", 0, "", 0, false,
                    new[] { new Participant("", chosen?.Name ?? "", number, false, "") }, false, "", "");
            SelectInList(conversation);
            if (!_allConversations.Any(c => c.Id == id)) _pendingSelectId = id;
        }
        catch (Exception ex) { ShowStatus($"Couldn't start the conversation: {ex.Message}"); }
    }

    // =====================================================================
    // Thread
    // =====================================================================

    private async Task SelectConversationAsync(Conversation conversation)
    {
        if (_selected?.Id == conversation.Id)
        {
            ApplyLayout(Root.ActualWidth);
            if (_pendingJumpMessageId is { } jump) await JumpToMessageAsync(jump);
            return;
        }
        SaveDraft();
        _selected = conversation;
        var version = ++_threadVersion;
        _threadMessages.Clear();
        _threadItemCache.Clear();
        _threadItems.Clear();
        _hasMoreHistory = false;
        _pagingArmed = false;
        SetStickToBottom(true);
        CancelReply();
        _attachments.Clear();
        UpdateAttachmentTray();
        StopAudio();
        HideTyping();
        ThreadEmpty.Visibility = Visibility.Collapsed;
        ThreadHeader.Visibility = Visibility.Visible;
        ComposerPanel.Visibility = Visibility.Visible;
        UpdateThreadHeader(loadAvatar: true);
        Composer.Text = _drafts.TryGetValue(conversation.Id, out var draft) ? draft : "";
        UpdateComposerState();
        ApplyLayout(Root.ActualWidth);
        App.Current.ClearConversationNotifications(conversation.Id);

        try
        {
            var messages = await _client.GetMessagesAsync(conversation.Id, 100);
            if (version != _threadVersion) return;
            _threadMessages.AddRange(messages);
            _hasMoreHistory = messages.Count >= 60;
            RebuildThread(scrollToBottom: true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't load messages: {ex.Message}"); }

        if (version != _threadVersion) return;
        if (_pendingJumpMessageId is { } jumpTo) await JumpToMessageAsync(jumpTo);
        if (conversation.UnreadCount > 0 && _windowActive) await MarkReadAsync(conversation);
        if (!_narrow || _selected is not null) Composer.Focus(FocusState.Programmatic);
    }

    private void CloseThread()
    {
        SaveDraft();
        _selected = null;
        _threadVersion++;
        _threadMessages.Clear();
        _threadItemCache.Clear();
        _threadItems.Clear();
        CancelReply();
        _attachments.Clear();
        UpdateAttachmentTray();
        StopAudio();
        HideTyping();
        ThreadHeader.Visibility = Visibility.Collapsed;
        ComposerPanel.Visibility = Visibility.Collapsed;
        ThreadEmpty.Visibility = Visibility.Visible;
        _suppressSelection = true;
        ConversationList.SelectedItem = null;
        _suppressSelection = false;
        ApplyLayout(Root.ActualWidth);
    }

    private void SaveDraft()
    {
        if (_selected is null) return;
        if (Composer.Text.Trim().Length > 0) _drafts[_selected.Id] = Composer.Text;
        else _drafts.Remove(_selected.Id);
    }

    private async void UpdateThreadHeader(bool loadAvatar)
    {
        if (_selected is not { } conversation) return;
        ThreadTitle.Text = conversation.DisplayName;
        var protocol = conversation.ProtocolLabel switch
        {
            "RCS" when _encryptedThreads.Contains(conversation.Id) => "rcs chat · end-to-end encrypted",
            "RCS" => "rcs chat",
            "SMS" => "sms/mms",
            _ => ""
        };
        var who = conversation.IsGroup
            ? $"group · {conversation.Participants.Count(p => !p.IsMe) + 1} people"
            : conversation.Others.FirstOrDefault()?.Number ?? "";
        if (who == conversation.DisplayName) who = "";
        ThreadSubtitle.Text = string.Join("  ·  ", new[] { protocol, who }.Where(s => s.Length > 0));
        ThreadAvatarTile.Background = Ui.TileBrush(conversation.Id);
        ThreadAvatarInitials.Text = conversation.IsGroup ? "" : Ui.Initials(conversation.DisplayName);
        ThreadAvatarGroup.Visibility = Ui.Show(conversation.IsGroup);
        ThreadArchiveButton.Content = conversation.IsArchived ? "" : "";
        ToolTipService.SetToolTip(ThreadArchiveButton, conversation.IsArchived ? "Unarchive" : "Archive");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThreadArchiveButton, conversation.IsArchived ? "Unarchive" : "Archive");
        Composer.PlaceholderText = conversation.ProtocolLabel switch
        {
            "RCS" => "rcs message",
            "SMS" => "text message",
            _ => "type a message"
        };
        UpdateComposerState();
        if (!loadAvatar) return;
        ThreadAvatarImage.Source = null;
        var participant = conversation.IsGroup ? null : conversation.Others.FirstOrDefault();
        var image = await _media.GetAvatarImageAsync(participant);
        if (_selected?.Id == conversation.Id) ThreadAvatarImage.Source = image;
    }

    private async Task RefreshThreadAsync()
    {
        if (_threadRefreshing) { _threadRefreshQueued = true; return; }
        _threadRefreshing = true;
        try
        {
            do
            {
                _threadRefreshQueued = false;
                await RefreshThreadCoreAsync();
            } while (_threadRefreshQueued);
        }
        finally { _threadRefreshing = false; }
    }

    private async Task RefreshThreadCoreAsync()
    {
        if (_selected is not { } conversation) return;
        var version = _threadVersion;
        try
        {
            var latest = await _client.GetMessagesAsync(conversation.Id, 100);
            if (version != _threadVersion) return;
            if (latest.Count > 0)
            {
                // Within the window the latest page covers, it is authoritative (drops duplicates and deletions).
                var windowStart = latest[0].TimestampMs;
                var latestIds = new HashSet<string>(latest.Select(m => m.Id));
                _threadMessages.RemoveAll(m => m.TimestampMs >= windowStart && !latestIds.Contains(m.Id));
                var index = _threadMessages.Select((m, i) => (m.Id, i)).ToDictionary(p => p.Id, p => p.i);
                foreach (var message in latest)
                {
                    if (index.TryGetValue(message.Id, out var at)) _threadMessages[at] = message;
                    else _threadMessages.Add(message);
                }
                _threadMessages.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            }
            RebuildThread(scrollToBottom: false);
        }
        catch (Exception ex) { Log.Write($"Thread refresh failed: {ex}"); }
    }

    private async Task LoadOlderAsync()
    {
        if (_selected is not { } conversation || _loadingOlder || !_hasMoreHistory || _threadMessages.Count == 0) return;
        _loadingOlder = true;
        OlderProgress.Visibility = Visibility.Visible;
        var version = _threadVersion;
        try
        {
            var page = await _client.GetMessagesAsync(conversation.Id, 100, _threadMessages[0]);
            if (version != _threadVersion) return;
            var known = new HashSet<string>(_threadMessages.Select(m => m.Id));
            var added = page.Where(m => !known.Contains(m.Id)).ToList();
            _hasMoreHistory = added.Count > 0 && page.Count >= 60;
            _threadMessages.InsertRange(0, added);
            _threadMessages.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            RebuildThread(scrollToBottom: false);
        }
        catch (Exception ex) { Log.Write($"Loading older messages failed: {ex}"); }
        finally
        {
            _loadingOlder = false;
            OlderProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildThread(bool scrollToBottom)
    {
        if (_selected is not { } conversation) return;
        var lastKeyBefore = _threadItems.LastOrDefault()?.Key;

        // Retire optimistic sends once the phone's copy shows up; hand their bubble over so nothing flickers.
        var claimed = new HashSet<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var pending in _pendingSends.Where(p => p.ConversationId == conversation.Id).ToList())
        {
            if (pending.Failed) continue;
            var match = _threadMessages.FirstOrDefault(m => m.IsFromMe && !m.IsSystem && !claimed.Contains(m.Id)
                && m.TimestampMs >= pending.CreatedMs - 120_000
                && !_threadItemCache.ContainsKey(m.Id)
                && (pending.Attachment is null ? !m.HasMedia && m.Body.Trim() == pending.Body.Trim() : m.HasMedia));
            if (match is not null)
            {
                claimed.Add(match.Id);
                _pendingSends.Remove(pending);
                if (_threadItemCache.Remove("pending:" + pending.Key, out var handed)) _threadItemCache[match.Id] = handed;
            }
            else if (pending.Accepted && now - pending.CreatedMs > 10 * 60_000) _pendingSends.Remove(pending);
        }

        var all = _threadMessages.Select(m => (Message: m, Pending: (PendingSend?)null))
            .Concat(_pendingSends.Where(p => p.ConversationId == conversation.Id).Select(p => (Message: p.ToMessage(), Pending: (PendingSend?)p)))
            .OrderBy(x => x.Message.TimestampMs)
            .ToList();

        var byId = new Dictionary<string, ChatMessage>();
        foreach (var (message, _) in all)
        {
            byId.TryAdd(message.Id, message);
            if (message.SourceId.Length > 0) byId.TryAdd(message.SourceId, message);
        }

        var desired = new List<ThreadItem>();
        DateTime? day = null;
        for (var i = 0; i < all.Count; i++)
        {
            var (message, pending) = all[i];
            var date = Ui.Local(message.TimestampMs).Date;
            if (date != day)
            {
                var key = "day:" + date.ToString("yyyyMMdd");
                if (!_threadItemCache.TryGetValue(key, out var dayItem)) _threadItemCache[key] = dayItem = new DayItem(date);
                desired.Add(dayItem);
                day = date;
            }
            if (message.IsSystem)
            {
                if (!_threadItemCache.TryGetValue(message.Id, out var system) || system is not SystemItem)
                    _threadItemCache[message.Id] = system = new SystemItem(message);
                desired.Add(system);
                continue;
            }
            var previous = i > 0 ? all[i - 1].Message : null;
            var next = i + 1 < all.Count ? all[i + 1].Message : null;
            var first = previous is null || !Continues(previous, message);
            var last = next is null || !Continues(message, next);

            string replySender = "", replySnippet = "";
            if (message.ReplyToId.Length > 0 && byId.TryGetValue(message.ReplyToId, out var target))
            {
                replySender = target.IsFromMe ? "You" : SenderLabel(conversation, target);
                replySnippet = Ui.Snippet(target);
            }
            var chips = message.Reactions.Select(r => new ReactionChip(r, conversation.IsMine(r), ReactionTooltip(conversation, r), message)).ToList();

            if (!_threadItemCache.TryGetValue(message.Id, out var cached) || cached is not MessageItem item)
                _threadItemCache[message.Id] = item = new MessageItem(message, pending);
            item.Update(message, pending, conversation.IsGroup, first, last, replySender, replySnippet, chips);
            desired.Add(item);
        }

        var keep = new HashSet<ThreadItem>(desired);
        foreach (var stale in _threadItemCache.Where(p => !keep.Contains(p.Value)).Select(p => p.Key).ToList())
            _threadItemCache.Remove(stale);
        Reconcile(_threadItems, desired);

        ApplyProtocol(conversation.Id, _threadMessages);

        var appended = _threadItems.LastOrDefault()?.Key != lastKeyBefore;
        if (scrollToBottom || (appended && _stickToBottom)) ScrollToBottom();
    }

    private Conversation WithResolvedProtocol(Conversation conversation) =>
        _resolvedProtocols.TryGetValue(conversation.Id, out var protocol) && conversation.ProtocolLabel != protocol
            ? conversation with { Protocol = protocol }
            : conversation;

    private void ApplyProtocol(string conversationId, IEnumerable<ChatMessage> messages)
    {
        if (Protocol.Resolve(messages) is not { } result) return;
        var changed = !_resolvedProtocols.TryGetValue(conversationId, out var known) || known != result.Label
                      || _encryptedThreads.Contains(conversationId) != result.Encrypted;
        if (!changed) return;
        _resolvedProtocols[conversationId] = result.Label;
        if (result.Encrypted) _encryptedThreads.Add(conversationId);
        else _encryptedThreads.Remove(conversationId);
        _allConversations = _allConversations.Select(WithResolvedProtocol).ToArray();
        if (_selected?.Id == conversationId)
        {
            _selected = WithResolvedProtocol(_selected);
            UpdateThreadHeader(loadAvatar: false);
        }
    }

    private static bool Continues(ChatMessage previous, ChatMessage message) =>
        !previous.IsSystem && !message.IsSystem
        && previous.IsFromMe == message.IsFromMe
        && (message.IsFromMe || (previous.SenderNumber == message.SenderNumber && previous.SenderName == message.SenderName))
        && message.TimestampMs - previous.TimestampMs < 5 * 60_000
        && Ui.Local(previous.TimestampMs).Date == Ui.Local(message.TimestampMs).Date;

    private static string SenderLabel(Conversation conversation, ChatMessage message)
    {
        if (message.SenderName.Length > 0) return message.SenderName;
        if (!conversation.IsGroup) return conversation.DisplayName;
        return message.SenderNumber.Length > 0 ? message.SenderNumber : "Someone";
    }

    private static Participant? FindParticipant(Conversation conversation, ChatMessage message)
    {
        static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
        var number = Digits(message.SenderNumber);
        return conversation.Others.FirstOrDefault(p => number.Length > 0 && Digits(p.Number).EndsWith(number.Length > 10 ? number[^10..] : number))
            ?? conversation.Others.FirstOrDefault(p => p.Name.Length > 0 && p.Name == message.SenderName)
            ?? (conversation.IsGroup ? null : conversation.Others.FirstOrDefault());
    }

    private static string ReactionTooltip(Conversation conversation, Reaction reaction)
    {
        var names = reaction.Actors.Select(actor =>
        {
            if (actor.Equals("me", StringComparison.OrdinalIgnoreCase)) return "You";
            var participant = conversation.Participants.FirstOrDefault(p => p.Id == actor || p.Number == actor);
            if (participant is not null) return participant.IsMe ? "You" : participant.Name.Length > 0 ? participant.Name : participant.Number;
            return conversation.IsGroup ? "" : conversation.DisplayName;
        }).Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) return $"{reaction.Emoji} {reaction.Count}";
        return $"{reaction.Emoji}  {string.Join(", ", names)}";
    }

    private void MessageList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MessageItem item) return;
        item.EnsureMedia(_media, OnMediaLoaded);
        item.EnsurePreview(_media, OnMediaLoaded);
        if (_selected is { IsGroup: true } conversation && !item.Model.IsFromMe && item.IsFirst)
            item.EnsureSenderAvatar(_media, FindParticipant(conversation, item.Model));
    }

    private void OnMediaLoaded()
    {
        if (_stickToBottom) ScrollToBottom();
    }

    private void HookScroller()
    {
        if (_messageScroller is not null) return;
        _messageScroller = FindDescendant<ScrollViewer>(MessageList);
        if (_messageScroller is null) return;
        _messageScroller.ViewChanged += (_, _) =>
        {
            // Our own scrolls (and the layout passes they cause) must not page history or unpin the view.
            if (_settlingScroll > 0) return;
            var scroller = _messageScroller;
            SetStickToBottom(scroller.VerticalOffset >= scroller.ScrollableHeight - 80);
            if (_pagingArmed && scroller.VerticalOffset < 160 && _hasMoreHistory && !_loadingOlder && _threadItems.Count > 0)
                _ = LoadOlderAsync();
        };
    }

    /// <summary>While pinned, the panel keeps the newest message in view as images and previews load above it.</summary>
    private void SetStickToBottom(bool value)
    {
        _stickToBottom = value;
        if (MessageList.ItemsPanelRoot is ItemsStackPanel panel)
            panel.ItemsUpdatingScrollMode = value ? ItemsUpdatingScrollMode.KeepLastItemInView : ItemsUpdatingScrollMode.KeepItemsInView;
    }

    private async void ScrollToBottom()
    {
        SetStickToBottom(true);
        _settlingScroll++;
        try
        {
            // Item heights are estimates until realized, so re-aim a few times as layout converges.
            foreach (var delay in new[] { 0, 60, 180, 400 })
            {
                if (delay > 0) await Task.Delay(delay);
                if (_threadItems.Count == 0 || _messageScroller is null) return;
                MessageList.UpdateLayout();
                _messageScroller.ChangeView(null, _messageScroller.ScrollableHeight, null, true);
            }
        }
        finally
        {
            _settlingScroll--;
            if (_settlingScroll == 0 && _threadItems.Count > 0) _pagingArmed = true;
        }
    }

    private async Task JumpToMessageAsync(string messageId)
    {
        _pendingJumpMessageId = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var item = _threadItems.OfType<MessageItem>().FirstOrDefault(m => m.Model.Id == messageId || m.Model.SourceId == messageId);
            if (item is not null)
            {
                SetStickToBottom(false);
                MessageList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
                item.Highlighted = true;
                await Task.Delay(2200);
                item.Highlighted = false;
                return;
            }
            if (!_hasMoreHistory) break;
            await LoadOlderAsync();
        }
        ShowStatus("That message is too far back to show.");
    }

    private void Reply_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageItem item && item.Model.ReplyToId.Length > 0)
        {
            e.Handled = true;
            _ = JumpToMessageAsync(item.Model.ReplyToId);
        }
    }

    private void ShowTyping(string name)
    {
        TypingText.Text = $"{(string.IsNullOrWhiteSpace(name) ? _selected?.DisplayName ?? "Someone" : name)} is typing";
        if (TypingPanel.Visibility != Visibility.Visible)
        {
            TypingPanel.Visibility = Visibility.Visible;
            ((Storyboard)Root.Resources["TypingStoryboard"]).Begin();
        }
        _typingExpires = DateTimeOffset.UtcNow.AddSeconds(8);
        _typingTimer.Start();
    }

    private void HideTyping()
    {
        if (TypingPanel.Visibility == Visibility.Collapsed) return;
        TypingPanel.Visibility = Visibility.Collapsed;
        ((Storyboard)Root.Resources["TypingStoryboard"]).Stop();
        _typingTimer.Stop();
    }

    private void ThreadArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } conversation) return;
        _ = Run(async () =>
        {
            await SetArchivedAsync(conversation, !conversation.IsArchived);
            await RefreshAsync(includeThread: false);
        });
    }

    private void ThreadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } conversation) return;
        BuildConversationMenu(conversation).ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void Back_Click(object sender, RoutedEventArgs e) => CloseThread();

    // =====================================================================
    // Message actions: reactions, reply, copy, retry
    // =====================================================================

    private void Message_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageItem item || _selected is null) return;
        args.Handled = true;
        var message = item.Model;
        var resources = Application.Current.Resources;
        var flatButton = (Style)resources["FlatTextButtonStyle"];
        var menu = new StackPanel { Width = 312, Padding = new Thickness(0, 0, 0, 6) };
        var flyout = new Flyout
        {
            Content = menu,
            FlyoutPresenterStyle = (Style)resources["MetroFlyoutPresenterStyle"]
        };

        if (item.Pending is null)
        {
            var mine = message.Reactions.FirstOrDefault(r => _selected.IsMine(r))?.Emoji;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(6) };
            foreach (var emoji in QuickReactions)
            {
                var button = new Button
                {
                    Style = flatButton,
                    Width = 50,
                    Height = 50,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = emoji == mine ? ThemeBrush("MetroSelectedBrush") : new SolidColorBrush(Colors.Transparent),
                    Content = new TextBlock
                    {
                        Text = emoji,
                        FontFamily = (FontFamily)resources["EmojiFont"],
                        FontSize = 24,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                ToolTipService.SetToolTip(button, emoji == mine ? "Remove your reaction" : "React");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"React with {emoji}");
                button.Click += (_, _) => { flyout.Hide(); _ = ToggleReactionAsync(message, emoji); };
                row.Children.Add(button);
            }
            menu.Children.Add(row);
            menu.Children.Add(new Rectangle { Height = 1, Fill = (Brush)resources["DividerBrush"], Margin = new Thickness(0, 0, 0, 6) });
        }

        void Secondary(string label, string glyph, Action action)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15 });
            content.Children.Add(new TextBlock { Text = label, FontSize = 14 });
            var button = new Button
            {
                Style = flatButton,
                Content = content,
                Padding = new Thickness(18, 9, 18, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => { flyout.Hide(); action(); };
            menu.Children.Add(button);
        }

        if (item.Pending is null) Secondary("Reply", "", () => SetReply(message));
        if (message.Body.Trim().Length > 0) Secondary("Copy text", "", () => CopyText(message.Body));
        if (item.Kind != MediaKind.None) Secondary("Save attachment", "", () => _ = SaveMediaAsync(item));
        if (item.Pending is { Failed: true } failed)
        {
            Secondary("Try again", "", () => _ = RetryAsync(failed));
            Secondary("Delete", "", () => { _pendingSends.Remove(failed); RebuildThread(false); });
        }
        else if (message.IsFailed && item.Pending is null && message.Body.Trim().Length > 0)
        {
            Secondary("Send again", "", () => _ = SendMessagesAsync(message.Body.Trim(), Array.Empty<AttachmentItem>(), null));
        }
        Secondary("Details", "", () => ShowStatus(DescribeMessage(message)));

        var options = new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard };
        if (args.TryGetPosition(sender, out var point)) options.Position = point;
        flyout.ShowAt((FrameworkElement)sender, options);
    }

    private string DescribeMessage(ChatMessage message)
    {
        var time = Ui.Local(message.TimestampMs).ToString("f");
        var from = message.IsFromMe ? "You" : SenderLabel(_selected!, message);
        var status = message.IsFromMe && message.StatusDisplay.Length > 0 ? $" · {message.StatusDisplay}" : "";
        var via = _selected!.ProtocolLabel.Length > 0 ? $" · {_selected.ProtocolLabel}" : "";
        return $"{from} · {time}{via}{status}";
    }

    private async void ReactionChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReactionChip chip) return;
        await ToggleReactionAsync(chip.Message, chip.Reaction.Emoji);
    }

    private async Task ToggleReactionAsync(ChatMessage message, string emoji)
    {
        if (_selected is not { } conversation) return;
        var target = message.SourceId.Length > 0 ? message.SourceId : message.Id;
        var mine = message.Reactions.FirstOrDefault(conversation.IsMine);
        var action = mine is null ? "add" : mine.Emoji == emoji ? "remove" : "switch";
        try
        {
            await _client.ReactAsync(conversation.Id, target, emoji, action);
            ApplyReactionLocally(message.Id, emoji, action, mine?.Emoji);
        }
        catch (Exception ex) { ShowStatus($"Reaction not sent: {ex.Message}"); }
    }

    private void ApplyReactionLocally(string messageId, string emoji, string action, string? previous)
    {
        var index = _threadMessages.FindIndex(m => m.Id == messageId);
        if (index < 0) return;
        var reactions = _threadMessages[index].Reactions.ToList();
        void Remove(string icon)
        {
            var at = reactions.FindIndex(r => r.Emoji == icon);
            if (at < 0) return;
            var r = reactions[at];
            var actors = r.Actors.Where(a => _selected?.IsMyActor(a) != true).ToArray();
            if (r.Count <= 1) reactions.RemoveAt(at);
            else reactions[at] = r with { Count = r.Count - 1, Actors = actors };
        }
        if (action is "remove" or "switch" && (previous ?? emoji) is { } old) Remove(action == "remove" ? emoji : old);
        if (action is "add" or "switch")
        {
            var at = reactions.FindIndex(r => r.Emoji == emoji);
            if (at < 0) reactions.Add(new Reaction(emoji, 1, new[] { "me" }));
            else reactions[at] = reactions[at] with { Count = reactions[at].Count + 1, Actors = reactions[at].Actors.Append("me").ToArray() };
        }
        _threadMessages[index] = _threadMessages[index] with { Reactions = reactions };
        RebuildThread(scrollToBottom: false);
    }

    private void SetReply(ChatMessage message)
    {
        _replyTo = message;
        ReplyBarHeader.Text = message.IsFromMe ? "replying to your message" : $"replying to {SenderLabel(_selected!, message)}";
        ReplyBarSnippet.Text = Ui.Snippet(message);
        ReplyBar.Visibility = Visibility.Visible;
        Composer.Focus(FocusState.Programmatic);
    }

    private void CancelReply()
    {
        _replyTo = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e) => CancelReply();

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // =====================================================================
    // Composer and sending
    // =====================================================================

    private void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shift & CoreVirtualKeyStates.Down) != 0) return;
            e.Handled = true;
            _ = SendCurrentAsync();
        }
        else if (e.Key == VirtualKey.Escape && _replyTo is not null)
        {
            e.Handled = true;
            CancelReply();
        }
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposerState();

    private void UpdateComposerState()
    {
        SendButton.IsEnabled = _selected is not null && (Composer.Text.Trim().Length > 0 || _attachments.Count > 0);
        var length = Composer.Text.Trim().Length;
        ComposerHint.Text = _selected?.ProtocolLabel switch
        {
            "SMS" when length > 0 => length <= 160 ? $"sms  ·  {length}/160" : $"sms  ·  {(length + 152) / 153} messages",
            "SMS" => "sms  ·  Shift+Enter for a new line",
            "RCS" => "rcs  ·  Shift+Enter for a new line",
            _ => "Shift+Enter for a new line"
        };
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async Task SendCurrentAsync()
    {
        if (_selected is null) return;
        var text = Composer.Text.Trim();
        var attachments = _attachments.ToList();
        if (text.Length == 0 && attachments.Count == 0) return;
        var reply = _replyTo is { } r ? (r.SourceId.Length > 0 ? r.SourceId : r.Id) : null;
        Composer.Text = "";
        _drafts.Remove(_selected.Id);
        _attachments.Clear();
        UpdateAttachmentTray();
        CancelReply();
        await SendMessagesAsync(text, attachments, reply);
    }

    private async Task SendMessagesAsync(string text, IReadOnlyList<AttachmentItem> attachments, string? replyTo)
    {
        if (_selected is not { } conversation) return;
        var sends = attachments.Select((a, i) => new PendingSend
        {
            Key = Guid.NewGuid().ToString("N"),
            ConversationId = conversation.Id,
            Attachment = a,
            ReplyToId = i == 0 ? replyTo : null,
            CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + i
        }).ToList();
        if (text.Length > 0)
            sends.Add(new PendingSend
            {
                Key = Guid.NewGuid().ToString("N"),
                ConversationId = conversation.Id,
                Body = text,
                ReplyToId = sends.Count == 0 ? replyTo : null,
                CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + sends.Count
            });
        _pendingSends.AddRange(sends);
        RebuildThread(scrollToBottom: true);
        foreach (var send in sends) await DispatchAsync(send);
        await RefreshThreadAsync();
    }

    private async Task DispatchAsync(PendingSend send)
    {
        try
        {
            if (send.Attachment is { } file)
                await _client.SendMediaAsync(send.ConversationId, file.Data, file.Name, file.Mime, "", send.ReplyToId, send.Key);
            else
                await _client.SendTextAsync(send.ConversationId, send.Body, send.ReplyToId, send.Key);
            send.Accepted = true;
        }
        catch (Exception ex)
        {
            send.Failed = true;
            send.Error = ex.Message.Length > 90 ? ex.Message[..90] + "…" : ex.Message;
        }
        if (_selected?.Id == send.ConversationId) RebuildThread(scrollToBottom: false);
    }

    private async Task RetryAsync(PendingSend failed)
    {
        _pendingSends.Remove(failed);
        var retry = new PendingSend
        {
            Key = Guid.NewGuid().ToString("N"),
            ConversationId = failed.ConversationId,
            Body = failed.Body,
            Attachment = failed.Attachment,
            ReplyToId = failed.ReplyToId
        };
        _pendingSends.Add(retry);
        RebuildThread(scrollToBottom: true);
        await DispatchAsync(retry);
        await RefreshThreadAsync();
    }

    // ----- Attachments -----

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files) await AddFileAsync(file);
    }

    private async Task AddFileAsync(IStorageItem item)
    {
        if (item is not StorageFile file) return;
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > 100UL * 1024 * 1024) { ShowStatus($"{file.Name} is too large to send."); return; }
            var buffer = await FileIO.ReadBufferAsync(file);
            var data = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(data);
            var mime = file.ContentType is { Length: > 0 } type && type != "application/octet-stream" ? type : MediaService.MimeFor(file.Name);
            await AddAttachmentAsync(new AttachmentItem(file.Name, mime, data));
        }
        catch (Exception ex) { ShowStatus($"Couldn't attach {file.Name}: {ex.Message}"); }
    }

    private async Task AddAttachmentAsync(AttachmentItem attachment)
    {
        if (_selected is null) return;
        _attachments.Add(attachment);
        UpdateAttachmentTray();
        await attachment.LoadThumbnailAsync();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AttachmentItem attachment) _attachments.Remove(attachment);
        UpdateAttachmentTray();
    }

    private void UpdateAttachmentTray()
    {
        AttachmentTray.Visibility = Ui.Show(_attachments.Count > 0);
        UpdateComposerState();
    }

    private async void Composer_Paste(object sender, TextControlPasteEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            foreach (var item in await content.GetStorageItemsAsync()) await AddFileAsync(item);
        }
        else if (content.Contains(StandardDataFormats.Bitmap) && !content.Contains(StandardDataFormats.Text))
        {
            e.Handled = true;
            try
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var png = await EncodePngAsync(stream);
                await AddAttachmentAsync(new AttachmentItem($"image-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image/png", png));
            }
            catch (Exception ex) { ShowStatus($"Couldn't paste the image: {ex.Message}"); }
        }
    }

    private static async Task<byte[]> EncodePngAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void Thread_DragOver(object sender, DragEventArgs e)
    {
        if (_selected is null) return;
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"send to {_selected.DisplayName}";
        }
    }

    private async void Thread_Drop(object sender, DragEventArgs e)
    {
        if (_selected is null) return;
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var item in await e.DataView.GetStorageItemsAsync()) await AddFileAsync(item);
            else if (e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                using var stream = await (await e.DataView.GetBitmapAsync()).OpenReadAsync();
                await AddAttachmentAsync(new AttachmentItem($"image-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image/png", await EncodePngAsync(stream)));
            }
        }
        finally { deferral.Complete(); }
    }

    // =====================================================================
    // Media playback, viewer, saving
    // =====================================================================

    private async Task<(byte[] Data, string Mime)?> GetItemMediaAsync(MessageItem item)
    {
        if (item.Pending?.Attachment is { } local) return (local.Data, local.Mime);
        if (item.Model.MediaId.Length == 0) return null;
        try
        {
            var media = await _media.GetMediaAsync(item.Model.Id);
            if (media is { } m) return (m.Data, m.Mime.Length > 0 && m.Mime != "application/octet-stream" ? m.Mime : item.Model.MimeType);
        }
        catch (Exception ex) { ShowStatus($"Couldn't download the attachment: {ex.Message}"); }
        return null;
    }

    private async void Media_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageItem item) return;
        e.Handled = true;
        if (item.Kind == MediaKind.Image && item.MediaImage is null)
        {
            if (!item.MediaFailed) return;
            if (AuthBanner.Visibility == Visibility.Visible) { ShowStatus("Attachments download through Google. Pair again to load them."); return; }
            item.RetryMedia(_media, OnMediaLoaded);
            return;
        }
        var media = await GetItemMediaAsync(item);
        if (media is not { } m) return;
        StopAudio();
        var name = $"{(item.Model.IsFromMe ? "sent" : "received")}-{Ui.Local(item.Model.TimestampMs):yyyyMMdd-HHmmss}{MediaService.ExtensionFor(m.Mime)}";
        _viewerMedia = (m.Data, m.Mime, name);
        ViewerTitle.Text = $"{(item.Model.IsFromMe ? "you" : SenderLabel(_selected!, item.Model).ToLowerInvariant())}  ·  {Ui.ListTime(item.Model.TimestampMs)}";
        if (item.Kind == MediaKind.Video)
        {
            ViewerImage.Visibility = Visibility.Collapsed;
            ViewerPlayer.Visibility = Visibility.Visible;
            ViewerPlayer.Source = MediaSource.CreateFromStream(await MediaService.CreateStreamAsync(m.Data), m.Mime);
            ViewerPlayer.AutoPlay = true;
        }
        else
        {
            ViewerPlayer.Visibility = Visibility.Collapsed;
            ViewerImage.Visibility = Visibility.Visible;
            ViewerImage.Source = await MediaService.CreateBitmapAsync(m.Data);
        }
        ViewerOverlay.Visibility = Visibility.Visible;
    }

    private void CloseViewer()
    {
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerPlayer.MediaPlayer?.Pause();
        ViewerPlayer.Source = null;
        ViewerImage.Source = null;
        _viewerMedia = null;
    }

    private void ViewerClose_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private void ViewerBackground_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ViewerOverlay)) CloseViewer();
    }

    private async void ViewerSave_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerMedia is { } media) await SaveBytesAsync(media.Data, media.Name, media.Mime);
    }

    private async void Audio_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageItem item) return;
        if (_audioItem == item && _audioPlayer is not null)
        {
            if (item.AudioPlaying) { _audioPlayer.Pause(); item.AudioPlaying = false; }
            else { _audioPlayer.Play(); item.AudioPlaying = true; }
            return;
        }
        StopAudio();
        var media = await GetItemMediaAsync(item);
        if (media is not { } m) return;
        if (_audioPlayer is null)
        {
            _audioPlayer = new MediaPlayer();
            _audioPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_audioItem is not null) _audioItem.AudioPlaying = false; });
            _audioPlayer.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_audioItem is not null) _audioItem.AudioPlaying = false;
                ShowStatus("Windows can't play this voice message. Right-click it to save it instead.");
            });
        }
        _audioItem = item;
        _audioPlayer.Source = MediaSource.CreateFromStream(await MediaService.CreateStreamAsync(m.Data), m.Mime);
        _audioPlayer.Play();
        item.AudioPlaying = true;
    }

    private void StopAudio()
    {
        _audioPlayer?.Pause();
        if (_audioItem is not null) _audioItem.AudioPlaying = false;
        _audioItem = null;
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageItem item) await SaveMediaAsync(item);
    }

    private async Task SaveMediaAsync(MessageItem item)
    {
        var media = await GetItemMediaAsync(item);
        if (media is not { } m) return;
        var name = item.Pending?.Attachment?.Name
                   ?? $"{Ui.MediaLabel(m.Mime).ToLowerInvariant().Replace(' ', '-')}-{Ui.Local(item.Model.TimestampMs):yyyyMMdd-HHmmss}{MediaService.ExtensionFor(m.Mime)}";
        await SaveBytesAsync(m.Data, name, m.Mime);
    }

    private async Task SaveBytesAsync(byte[] data, string name, string mime)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length == 0) extension = MediaService.ExtensionFor(mime);
        if (extension.Length == 0) extension = ".bin";
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name), SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeChoices.Add(Ui.MediaLabel(mime), new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await FileIO.WriteBytesAsync(file, data);
        ShowStatus($"Saved {file.Name}", "open", async () => await Launcher.LaunchFileAsync(file));
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageItem { Url: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    // =====================================================================
    // Layout, keyboard, status
    // =====================================================================

    private void ApplyLayout(double width)
    {
        if (width <= 0) return;
        _narrow = width < 760;
        if (!_narrow)
        {
            ListColumn.Width = new GridLength(width < 1000 ? 320 : 380);
            ThreadColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPane.Visibility = Visibility.Visible;
            ThreadPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
        }
        else if (_selected is not null)
        {
            ListColumn.Width = new GridLength(0);
            ThreadColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPane.Visibility = Visibility.Collapsed;
            ThreadPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            ThreadColumn.Width = new GridLength(0);
            ListPane.Visibility = Visibility.Visible;
            ThreadPane.Visibility = Visibility.Collapsed;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (ViewerOverlay.Visibility == Visibility.Visible) { CloseViewer(); e.Handled = true; }
        else if (_replyTo is not null) { CancelReply(); e.Handled = true; }
        else if (_narrow && _selected is not null) { CloseThread(); e.Handled = true; }
    }

    private void ShowStatus(string text, string? actionText = null, Func<Task>? action = null)
    {
        StatusText.Text = text;
        _statusAction = action;
        StatusAction.Content = actionText;
        StatusAction.Visibility = Ui.Show(actionText is not null && action is not null);
        StatusBanner.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(action is null ? 6 : 10);
        _statusTimer.Start();
    }

    private async void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        StatusBanner.Visibility = Visibility.Collapsed;
        if (_statusAction is { } action)
        {
            _statusAction = null;
            try { await action(); }
            catch (Exception ex) { ShowStatus(ex.Message); }
        }
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }

    /// <summary>Theme brush for the window's current theme. Application-level lookups from code keep the launch theme.</summary>
    private Brush ThemeBrush(string key)
    {
        var theme = Root.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary resources
                && resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>The extended title bar's caption buttons don't follow the app theme on their own.</summary>
    private void ApplyCaptionColors()
    {
        var light = Root.ActualTheme == ElementTheme.Light;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonInactiveForegroundColor = light ? Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80) : Windows.UI.Color.FromArgb(255, 0x99, 0x99, 0x99);
        bar.ButtonHoverBackgroundColor = light ? Windows.UI.Color.FromArgb(0x18, 0, 0, 0) : Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = light ? Windows.UI.Color.FromArgb(0x30, 0, 0, 0) : Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void FlashTaskbar()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this),
            dwFlags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = 3
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
