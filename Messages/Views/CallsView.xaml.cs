using System.Globalization;
using Messages.Models;
using Messages.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.Calls;
using Windows.System;

namespace Messages.Views;

/// <summary>One row in the recent calls or contacts list.</summary>
public sealed class CallRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Time { get; init; } = "";
    public string KindGlyph { get; init; } = "";
    public Visibility KindVisible => Ui.Show(KindGlyph.Length > 0);
    public Brush? TitleBrush { get; init; }
    public string Initials { get; init; } = "";
    public Visibility PersonIcon => Ui.Show(Initials.Length == 0 && Photo is null);
    public SolidColorBrush Tile { get; init; } = new(Microsoft.UI.Colors.Gray);
    public ImageSource? Photo { get; init; }
    public string Number { get; init; } = "";
    public PhoneContact? Contact { get; init; }
}

/// <summary>
/// The calls section: recent calls and contacts from the phone (Bluetooth PBAP), a dial pad, and the
/// screen for a call in progress (Bluetooth Hands-Free). Windows also shows its own call window, so a
/// call can be answered from either place.
/// </summary>
public sealed partial class CallsView : UserControl
{
    private static readonly (string Digit, string Letters)[] Keys =
    {
        ("1", ""), ("2", "abc"), ("3", "def"), ("4", "ghi"), ("5", "jkl"), ("6", "mno"),
        ("7", "pqrs"), ("8", "tuv"), ("9", "wxyz"), ("*", ""), ("0", "+"), ("#", "")
    };

    private enum Detail { Dial, Contact }

    public PhoneCalls Calls { get; } = new();
    private readonly Phonebook _book = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, PhoneContact> _byNumber = new();
    private readonly Dictionary<PhoneContact, ImageSource?> _photos = new();
    private string _filter = "recent";
    private Detail _detail = Detail.Dial;
    private PhoneContact? _shownContact;
    private string _shownNumber = "";
    private bool _narrow;
    private bool _detailOpen;
    private bool _syncing;
    private bool _setupRequested;
    private int _lastCallCount;

    /// <summary>The user picked the messages header.</summary>
    public event Action? MessagesRequested;
    /// <summary>Open a text conversation with (number, name).</summary>
    public event Action<string, string>? TextRequested;
    /// <summary>Calls started, ended or changed state; the window updates its call bar.</summary>
    public event Action? CallsChanged;

    public CallsView()
    {
        InitializeComponent();
        BuildKeys(DialKeys, DialKey_Click, large: true);
        BuildKeys(InCallKeys, InCallKey_Click, large: false);
        _tick.Tick += (_, _) => UpdateCallClock();
        foreach (var header in new UIElement[] { CallsHeader, CallsFilters, ListEmpty })
            Controls.SwipeGesture.Attach(header, ListSwipe, touchpad: false);
        Controls.SwipeGesture.Attach(ListPane, ListSwipe, touch: false);
        Controls.SwipeGesture.Attach(DetailPane, direction =>
        {
            if (direction > 0 && _narrow && _detailOpen && Calls.Calls.Count == 0) Back_Click(this, new RoutedEventArgs());
        });
        Calls.Changed += () => DispatcherQueue.TryEnqueue(OnCallsChanged);
        Calls.Missed += (number, name) => DispatcherQueue.TryEnqueue(() =>
            App.Current.ShowMissedCallToast(DisplayName(number, name), number));
        UpdateFilterHeaders();
    }

    public async Task InitializeAsync()
    {
        _book.LoadCache();
        IndexContacts();
        RefreshList();
        Update();
        await Calls.ConnectSavedAsync();
        Update();
        if (Calls.SavedPhoneId is not null) _ = SyncAsync(includeContacts: _book.Updated is not { } u || DateTimeOffset.Now - u > TimeSpan.FromHours(12), quiet: true);
    }

    /// <summary>Best-known name for a number: the phone's contacts first, then what the caller ID said.</summary>
    public string DisplayName(string number, string fallback = "")
    {
        if (Lookup(number) is { } contact) return contact.Name;
        return string.IsNullOrWhiteSpace(fallback) ? number : fallback;
    }

    public async Task DialAsync(string number, string name = "")
    {
        if (string.IsNullOrWhiteSpace(number)) return;
        try { await Calls.DialAsync(number, DisplayName(number, name)); }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    public void ShowDialPad(string number = "")
    {
        _detail = Detail.Dial;
        NumberBox.Text = number;
        OpenDetail();
        Update();
        NumberBox.Focus(FocusState.Programmatic);
        NumberBox.SelectionStart = NumberBox.Text.Length;
    }

    // =====================================================================
    // Layout
    // =====================================================================

    public void ApplyLayout(double width)
    {
        if (width <= 0) return;
        _narrow = width < 760;
        var showDetail = !_narrow || _detailOpen || Calls.Calls.Count > 0;
        var showList = !_narrow || !showDetail;
        ListColumn.Width = showList ? (_narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 1000 ? 320 : 380)) : new GridLength(0);
        DetailColumn.Width = showDetail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ListPane.Visibility = Ui.Show(showList);
        DetailPane.Visibility = Ui.Show(showDetail);
        BackButton.Visibility = Ui.Show(_narrow && showDetail && Calls.Calls.Count == 0);
    }

    private void OpenDetail()
    {
        _detailOpen = true;
        ApplyLayout(ActualWidth);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _detailOpen = false;
        ApplyLayout(ActualWidth);
    }

    // =====================================================================
    // State
    // =====================================================================

    private readonly HashSet<string> _ringing = new();

    /// <summary>Keeps one ringing notification per incoming call, removed once it stops ringing.</summary>
    private void UpdateCallNotifications()
    {
        var ringing = Calls.Calls.Where(c => c.Status == PhoneCallStatus.Incoming).ToDictionary(c => c.Call.CallId);
        foreach (var (id, call) in ringing)
        {
            if (!_ringing.Add(id)) continue;
            var contact = Lookup(call.Number);
            App.Current.ShowIncomingCallToast(id, DisplayName(call.Number, call.Name), call.Number, PhotoFile(contact));
        }
        foreach (var id in _ringing.Where(id => !ringing.ContainsKey(id)).ToList())
        {
            _ringing.Remove(id);
            App.Current.RemoveCallToast(id);
        }
    }

    /// <summary>Notifications take images by file, so a contact photo is written to the data folder once.</summary>
    private static Uri? PhotoFile(PhoneContact? contact)
    {
        if (contact?.Photo is not { Length: > 0 } bytes) return null;
        try
        {
            var folder = Path.Combine(BackendHost.DataDirectory, "photos");
            Directory.CreateDirectory(folder);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + ".jpg";
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return new Uri(path);
        }
        catch { return null; }
    }

    private PhoneCall? FindCall(string callId) =>
        Calls.Calls.FirstOrDefault(c => c.Call.CallId == callId)?.Call
        ?? Calls.Calls.FirstOrDefault(c => c.Status == PhoneCallStatus.Incoming)?.Call;

    public async Task AnswerAsync(string callId)
    {
        if (FindCall(callId) is not { } call) return;
        try { await PhoneCalls.RunAsync(() => call.AcceptIncomingAsync(), "answer"); }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    public async Task DeclineAsync(string callId)
    {
        if (FindCall(callId) is not { } call) return;
        try { await PhoneCalls.RunAsync(() => call.RejectIncomingAsync(), "decline"); }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    private void OnCallsChanged()
    {
        UpdateCallNotifications();
        var count = Calls.Calls.Count;
        if (count == 0 && _lastCallCount > 0)
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _ = SyncAsync(includeContacts: false, quiet: true)));
        _lastCallCount = count;
        Update();
        CallsChanged?.Invoke();
    }

    private void Update()
    {
        var calls = Calls.Calls;
        var needsSetup = !Calls.IsConnected && (Calls.SavedPhoneId is null || _setupRequested);
        SetupPanel.Visibility = Ui.Show(needsSetup && calls.Count == 0);
        CallPanel.Visibility = Ui.Show(calls.Count > 0);
        DialPanel.Visibility = Ui.Show(!needsSetup && calls.Count == 0 && _detail == Detail.Dial);
        ContactPanel.Visibility = Ui.Show(!needsSetup && calls.Count == 0 && _detail == Detail.Contact);
        DisconnectItem.IsEnabled = Calls.IsConnected;
        DialPadButton.IsEnabled = Calls.IsConnected;
        CallButton.IsEnabled = Calls.IsConnected && NumberBox.Text.Trim().Length > 0;

        var healthy = Calls.IsConnected && Calls.CanDial;
        LineStatus.Visibility = Ui.Show(!healthy);
        LineStatus.Text = Calls.IsConnected
            ? $"{Calls.Phone?.Name}{(Calls.NetworkName is { } n ? "  ·  " + n : "")}{(Calls.CanDial ? "" : "  ·  connecting the line…")}"
            : Calls.Problem ?? (Calls.SavedPhoneId is null ? "set up calls to use your phone here" : "phone not connected for calls");

        if (needsSetup && SetupPanel.Visibility == Visibility.Visible && PhoneChoices.Items.Count == 0) _ = LoadPhonesAsync();
        if (calls.Count > 0) ShowCall(calls);
        else
        {
            _tick.Stop();
            InCallKeys.Visibility = Visibility.Collapsed;
            KeypadButton.IsChecked = false;
        }
        ApplyLayout(ActualWidth);
    }

    private void ShowProblem(string text)
    {
        LineStatus.Text = text;
        LineStatus.Visibility = Visibility.Visible;
        if (SetupPanel.Visibility == Visibility.Visible) SetupStatus.Text = text;
    }

    // =====================================================================
    // Setup
    // =====================================================================

    private async Task LoadPhonesAsync()
    {
        SetupProgress.IsActive = true;
        try
        {
            var phones = await PhoneCalls.FindPhonesAsync();
            PhoneChoices.Items.Clear();
            foreach (var phone in phones)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(16, 12, 16, 12),
                    Tag = phone,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 14,
                        Children =
                        {
                            new FontIcon { Glyph = "", FontSize = 22 },
                            new TextBlock { Text = phone.Name, FontSize = 17, VerticalAlignment = VerticalAlignment.Center }
                        }
                    }
                };
                button.Click += PhoneChoice_Click;
                PhoneChoices.Items.Add(button);
            }
            NoPhonesText.Visibility = Ui.Show(phones.Count == 0);
        }
        catch (Exception ex) { SetupStatus.Text = ex.Message; }
        finally { SetupProgress.IsActive = false; }
    }

    private async void PhoneChoice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PairedPhone phone) return;
        SetupProgress.IsActive = true;
        SetupStatus.Text = $"Connecting to {phone.Name}… If your phone asks to allow calls or contacts for this PC, allow it.";
        try
        {
            try { await Calls.ConnectAsync(phone.Id); }
            catch (Exception ex) { Log.Write($"Calls: connect failed: {ex.Message}"); }
            if (Calls.SavedPhoneId is null) { SetupStatus.Text = Calls.Problem ?? "Couldn't use that phone."; return; }
            _setupRequested = false;
            SetupStatus.Text = "";
            _detail = Detail.Dial;
            Update();
            await SyncAsync(includeContacts: true, quiet: false);
        }
        finally { SetupProgress.IsActive = false; }
    }

    private void LookAgain_Click(object sender, RoutedEventArgs e) => _ = LoadPhonesAsync();

    private async void BluetoothSettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "stop taking calls here?",
            Content = "Calls will ring only on your phone. Recent calls and contacts already on this PC stay until you set up calls again.",
            PrimaryButtonText = "Stop",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Calls.Disconnect();
        _setupRequested = true;
        PhoneChoices.Items.Clear();
        Update();
    }

    // =====================================================================
    // Phonebook
    // =====================================================================

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncAsync(includeContacts: true, quiet: false);

    private async Task SyncAsync(bool includeContacts, bool quiet)
    {
        if (_syncing || Calls.SavedPhoneId is not { } id) return;
        _syncing = true;
        SyncProgress.Visibility = Visibility.Visible;
        // Android asks on the phone before sharing, and gives up after about 30 seconds.
        var firstTime = _book.Updated is null;
        if (firstTime || !quiet)
            ShowProblem("Check your phone: tap Allow on the request to share contacts and call history with this PC.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _book.SyncAsync(id, includeContacts, timeout.Token);
            IndexContacts();
            RefreshList();
            Update();
        }
        catch (Exception ex)
        {
            Log.Write($"Phonebook sync failed: {ex.Message}");
            if (!quiet || firstTime)
                ShowProblem(ex is IOException
                    ? "Your phone didn't share contacts and call history. Choose ⋯ → Refresh calls and contacts, then tap Allow on your phone within 30 seconds."
                    : ex.Message);
        }
        finally
        {
            _syncing = false;
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void IndexContacts()
    {
        _byNumber.Clear();
        _photos.Clear();
        foreach (var contact in _book.Contacts)
            foreach (var (_, number) in contact.Numbers)
                _byNumber.TryAdd(Key(number), contact);
    }

    /// <summary>Compares numbers by their last ten digits, so +1 (555) 010-0199 matches 5550100199.</summary>
    private static string Key(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private PhoneContact? Lookup(string number) =>
        Key(number) is { Length: >= 3 } key && _byNumber.TryGetValue(key, out var contact) ? contact : null;

    private ImageSource? PhotoOf(PhoneContact? contact)
    {
        if (contact?.Photo is not { Length: > 0 } bytes) return null;
        if (_photos.TryGetValue(contact, out var cached)) return cached;
        BitmapImage? image = null;
        try
        {
            image = new BitmapImage { DecodePixelWidth = 96 };
            image.SetSource(new MemoryStream(bytes).AsRandomAccessStream());
        }
        catch { image = null; }
        _photos[contact] = image;
        return image;
    }

    // =====================================================================
    // List
    // =====================================================================

    private static readonly string[] Filters = { "recent", "missed", "contacts" };

    private void Filter_Click(object sender, RoutedEventArgs e) => SetFilter((sender as FrameworkElement)?.Tag as string ?? "recent");

    private void SetFilter(string filter)
    {
        _filter = filter;
        UpdateFilterHeaders();
        RefreshList();
    }

    /// <summary>Swiping moves through recent → missed → contacts; past the first one goes back to messages.</summary>
    private void ListSwipe(int direction)
    {
        var next = Array.IndexOf(Filters, _filter) - direction;
        if (next < 0) MessagesRequested?.Invoke();
        else if (next < Filters.Length) SetFilter(Filters[next]);
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Visibility == Visibility.Visible && SearchBox.Text.Length == 0) SearchBox.Visibility = Visibility.Collapsed;
        else ShowSearch();
    }

    public void ShowSearch()
    {
        SearchBox.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) SearchBox.Visibility = Visibility.Collapsed;
    }

    private async void SwipeCall_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.DataContext is CallRow { Number.Length: > 0 } row) await DialAsync(row.Number, row.Contact?.Name ?? "");
    }

    private void SwipeText_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl.DataContext is CallRow { Number.Length: > 0 } row) TextRequested?.Invoke(row.Number, row.Contact?.Name ?? "");
    }

    private void UpdateFilterHeaders()
    {
        FilterRecent.Opacity = _filter == "recent" ? 1 : 0.4;
        FilterMissed.Opacity = _filter == "missed" ? 1 : 0.4;
        FilterContacts.Opacity = _filter == "contacts" ? 1 : 0.4;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = "";
            SearchBox.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var text = SearchBox.Text.Trim();
        if (LooksLikeNumber(text)) ShowDialPad(text);
        else if (CallList.Items.FirstOrDefault() is CallRow row) Open(row);
    }

    private static bool LooksLikeNumber(string text) =>
        text.Length > 0 && text.All(c => char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '*' or '#');

    private void RefreshList()
    {
        var query = SearchBox.Text.Trim();
        var rows = new List<CallRow>();
        if (_filter == "contacts" || query.Length > 0)
        {
            var key = Key(query);
            foreach (var contact in _book.Contacts)
            {
                if (query.Length > 0 && !contact.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    && !(key.Length >= 3 && contact.Numbers.Any(n => Key(n.Number).Contains(key)))) continue;
                rows.Add(ContactRow(contact));
            }
        }
        else
        {
            var entries = _book.Calls.Where(c => _filter != "missed" || c.Kind == CallKind.Missed);
            // Consecutive calls with the same number collapse into one row, like on the phone.
            CallLogEntry? head = null;
            var count = 0;
            foreach (var entry in entries.Append(null))
            {
                if (head is not null && entry is not null && Key(entry.Number) == Key(head.Number) && entry.Kind == head.Kind && Key(head.Number).Length > 0)
                {
                    count++;
                    continue;
                }
                if (head is not null) rows.Add(HistoryRow(head, count));
                head = entry;
                count = 1;
            }
        }
        CallList.ItemsSource = rows;
        var empty = rows.Count == 0;
        ListEmpty.Visibility = Ui.Show(empty);
        if (empty)
        {
            (ListEmptyTitle.Text, ListEmptyText.Text) = query.Length > 0
                ? ("no matches", LooksLikeNumber(query) ? "Press Enter to dial this number." : "Try another name or number.")
                : _filter switch
                {
                    "contacts" => ("no contacts yet", Calls.SavedPhoneId is null ? "Set up calls to bring your phone's contacts here." : "Refresh to load them from your phone."),
                    "missed" => ("no missed calls", ""),
                    _ => ("no recent calls", Calls.SavedPhoneId is null ? "Set up calls to see your phone's call history." : "Calls you make and get on your phone show up here.")
                };
        }
    }

    private CallRow ContactRow(PhoneContact contact)
    {
        var photo = PhotoOf(contact);
        var first = contact.Numbers[0];
        return new CallRow
        {
            Title = contact.Name,
            TitleBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            Detail = contact.Numbers.Count == 1 ? $"{first.Label}  {first.Number}" : $"{first.Label}  {first.Number}  ·  +{contact.Numbers.Count - 1} more",
            Initials = photo is null ? Ui.Initials(contact.Name) : "",
            Tile = Ui.TileBrush(contact.Name),
            Photo = photo,
            Number = first.Number,
            Contact = contact
        };
    }

    private CallRow HistoryRow(CallLogEntry entry, int count)
    {
        var contact = Lookup(entry.Number);
        var name = contact?.Name ?? (entry.Name.Length > 0 ? entry.Name : entry.Number.Length > 0 ? entry.Number : "Unknown");
        var photo = PhotoOf(contact);
        var kind = entry.Kind switch { CallKind.Missed => "missed", CallKind.Outgoing => "outgoing", _ => "incoming" };
        var label = contact?.Numbers.FirstOrDefault(n => Key(n.Number) == Key(entry.Number)).Label;
        return new CallRow
        {
            Title = count > 1 ? $"{name} ({count})" : name,
            Detail = label is { Length: > 0 } && name != entry.Number ? $"{kind}  ·  {label}" : kind,
            KindGlyph = entry.Kind == CallKind.Outgoing ? "↗" : "↙",
            TitleBrush = (Brush)Application.Current.Resources[entry.Kind == CallKind.Missed ? "CallEndBrush" : "TextFillColorPrimaryBrush"],
            Time = WhenText(entry.When),
            Initials = photo is null && contact is not null ? Ui.Initials(name) : "",
            Tile = Ui.TileBrush(name),
            Photo = photo,
            Number = entry.Number,
            Contact = contact
        };
    }

    private static string WhenText(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("t", CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd", CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);
        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    private void CallList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CallRow row) Open(row);
    }

    private void Open(CallRow row)
    {
        _shownContact = row.Contact;
        _shownNumber = row.Number;
        _detail = Detail.Contact;
        ShowContact();
        OpenDetail();
        Update();
    }

    private void ShowContact()
    {
        var contact = _shownContact;
        var name = contact?.Name ?? (_shownNumber.Length > 0 ? _shownNumber : "Unknown caller");
        var photo = PhotoOf(contact);
        ContactName.Text = name;
        ContactTile.Background = Ui.TileBrush(name);
        ContactPhoto.Source = photo;
        ContactInitials.Text = photo is null && contact is not null ? Ui.Initials(name) : "";
        ContactPersonIcon.Visibility = Ui.Show(photo is null && contact is null);

        ContactNumbers.Items.Clear();
        var numbers = contact?.Numbers ?? (_shownNumber.Length > 0 ? new List<(string, string)> { ("phone", _shownNumber) } : new());
        foreach (var (label, number) in numbers)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = number, FontSize = 18, IsTextSelectionEnabled = true },
                    new TextBlock { Text = label, FontSize = 12, Opacity = 0.6 }
                }
            });
            var call = new Button { Style = (Style)Application.Current.Resources["CircleButtonStyle"], Content = "", Tag = number, IsEnabled = Calls.IsConnected };
            ToolTipService.SetToolTip(call, "Call");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(call, $"Call {label}");
            call.Click += async (_, _) => await DialAsync(number, name);
            var text = new Button { Style = (Style)Application.Current.Resources["CircleButtonStyle"], Content = "", Tag = number };
            ToolTipService.SetToolTip(text, "Message");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, $"Message {label}");
            text.Click += (_, _) => TextRequested?.Invoke(number, contact?.Name ?? "");
            Grid.SetColumn(call, 1);
            Grid.SetColumn(text, 2);
            row.Children.Add(call);
            row.Children.Add(text);
            ContactNumbers.Items.Add(row);
        }

        ContactHistory.Items.Clear();
        var keys = numbers.Select(n => Key(n.Item2)).Where(k => k.Length > 0).ToHashSet();
        foreach (var entry in _book.Calls.Where(c => keys.Contains(Key(c.Number))).Take(30))
        {
            var kind = entry.Kind switch { CallKind.Missed => "missed", CallKind.Outgoing => "outgoing", _ => "incoming" };
            ContactHistory.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = entry.Kind == CallKind.Outgoing ? "↗" : "↙", Opacity = 0.65 },
                    new TextBlock { Text = $"{kind}  ·  {entry.When.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}", Opacity = 0.8 }
                }
            });
        }
        ContactHistoryHeader.Visibility = Ui.Show(ContactHistory.Items.Count > 0);
    }

    // =====================================================================
    // Dial pad
    // =====================================================================

    private static void BuildKeys(Grid grid, RoutedEventHandler click, bool large)
    {
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Keys.Length; i++)
        {
            var (digit, letters) = Keys[i];
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = digit,
                FontFamily = (FontFamily)Application.Current.Resources["MetroDisplayFont"],
                FontWeight = Microsoft.UI.Text.FontWeights.Light,
                FontSize = large ? 30 : 26,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock { Text = letters, FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 14 });
            var button = new Button { Style = (Style)Application.Current.Resources["DialKeyButtonStyle"], Content = content, Tag = digit };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, digit);
            button.Click += click;
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            grid.Children.Add(button);
        }
    }

    private void ShowDialPad_Click(object sender, RoutedEventArgs e) => ShowDialPad(LooksLikeNumber(SearchBox.Text.Trim()) ? SearchBox.Text.Trim() : NumberBox.Text);

    private void DialKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string digit) return;
        var at = NumberBox.SelectionStart;
        NumberBox.Text = NumberBox.Text.Remove(at, NumberBox.SelectionLength).Insert(at, digit);
        NumberBox.SelectionStart = at + 1;
    }

    private void Backspace_Click(object sender, RoutedEventArgs e)
    {
        if (NumberBox.Text.Length == 0) return;
        NumberBox.Text = NumberBox.Text[..^1];
        NumberBox.SelectionStart = NumberBox.Text.Length;
    }

    private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var number = NumberBox.Text.Trim();
        NumberMatch.Text = Lookup(number)?.Name ?? "";
        CallButton.IsEnabled = Calls.IsConnected && number.Length > 0;
    }

    private async void NumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await DialAsync(NumberBox.Text.Trim());
    }

    private async void Call_Click(object sender, RoutedEventArgs e) => await DialAsync(NumberBox.Text.Trim());

    // =====================================================================
    // Call in progress
    // =====================================================================

    /// <summary>The call the screen is about: a ringing call first, then the one being talked on.</summary>
    private static ActiveCall Primary(IReadOnlyList<ActiveCall> calls) =>
        calls.FirstOrDefault(c => c.Status == PhoneCallStatus.Incoming)
        ?? calls.FirstOrDefault(c => c.Status == PhoneCallStatus.Talking)
        ?? calls.FirstOrDefault(c => c.Status == PhoneCallStatus.Dialing)
        ?? calls[0];

    private void ShowCall(IReadOnlyList<ActiveCall> calls)
    {
        var call = Primary(calls);
        var contact = Lookup(call.Number);
        var name = DisplayName(call.Number, call.Name);
        var photo = PhotoOf(contact);
        CallName.Text = name;
        CallNumber.Text = name == call.Number ? "" : call.Number;
        CallTile.Background = Ui.TileBrush(name);
        CallPhoto.Source = photo;
        CallInitials.Text = photo is null && contact is not null ? Ui.Initials(name) : "";
        CallPersonIcon.Visibility = Ui.Show(photo is null && contact is null);
        var others = calls.Where(c => c != call).ToList();
        CallOthers.Text = others.Count == 0 ? "" : string.Join("  ·  ", others.Select(o => $"{DisplayName(o.Number, o.Name)} {(o.Status == PhoneCallStatus.Held ? "on hold" : o.Status.ToString().ToLowerInvariant())}"));
        CallOthers.Visibility = Ui.Show(others.Count > 0);

        var incoming = call.Status == PhoneCallStatus.Incoming;
        IncomingButtons.Visibility = Ui.Show(incoming);
        ActiveButtons.Visibility = Ui.Show(!incoming);
        DeclineTextPanel.Visibility = Ui.Show(call.Number.Length > 0);
        MuteButton.IsChecked = call.IsMuted;
        HoldButton.IsChecked = call.Status == PhoneCallStatus.Held;
        AudioLabel.Text = call.AudioOnPc ? "use phone" : "use PC";
        AudioGlyph.Glyph = call.AudioOnPc ? "" : "";
        UpdateCallClock();
        if (!_tick.IsEnabled) _tick.Start();
    }

    private void UpdateCallClock()
    {
        var calls = Calls.Calls;
        if (calls.Count == 0) return;
        var call = Primary(calls);
        CallState.Text = call.Status switch
        {
            PhoneCallStatus.Incoming => "incoming call",
            PhoneCallStatus.Dialing => "calling…",
            PhoneCallStatus.Held => "on hold",
            PhoneCallStatus.Talking when call.TalkingSince is { } since => Duration(DateTimeOffset.Now - since),
            _ => "connected"
        };
    }

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private PhoneCall? CurrentCall => Calls.Calls is { Count: > 0 } calls ? Primary(calls).Call : null;

    private async Task RunOnCallAsync(Func<PhoneCall, Windows.Foundation.IAsyncOperation<PhoneCallOperationStatus>> action, string what)
    {
        if (CurrentCall is not { } call) return;
        try { await PhoneCalls.RunAsync(() => action(call), what); }
        catch (Exception ex) { ShowProblem(ex.Message); }
        Update();
    }

    private async void Answer_Click(object sender, RoutedEventArgs e) => await RunOnCallAsync(c => c.AcceptIncomingAsync(), "answer");
    private async void Decline_Click(object sender, RoutedEventArgs e) => await RunOnCallAsync(c => c.RejectIncomingAsync(), "decline");
    private async void End_Click(object sender, RoutedEventArgs e) => await RunOnCallAsync(c => c.EndAsync(), "end the call");

    private async void DeclineWithText_Click(object sender, RoutedEventArgs e)
    {
        if (Calls.Calls is not { Count: > 0 } calls) return;
        var call = Primary(calls);
        await RunOnCallAsync(c => c.RejectIncomingAsync(), "decline");
        TextRequested?.Invoke(call.Number, Lookup(call.Number)?.Name ?? call.Name);
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) =>
        await RunOnCallAsync(c => c.IsMuted ? c.UnmuteAsync() : c.MuteAsync(), "change mute");

    private async void Hold_Click(object sender, RoutedEventArgs e) =>
        await RunOnCallAsync(c => c.Status == PhoneCallStatus.Held ? c.ResumeFromHoldAsync() : c.HoldAsync(), "change hold");

    private async void Audio_Click(object sender, RoutedEventArgs e) =>
        await RunOnCallAsync(c => c.ChangeAudioDeviceAsync(c.AudioDevice == PhoneCallAudioDevice.LocalDevice
            ? PhoneCallAudioDevice.RemoteDevice : PhoneCallAudioDevice.LocalDevice), "switch audio");

    private void Keypad_Click(object sender, RoutedEventArgs e)
    {
        var show = KeypadButton.IsChecked == true;
        InCallKeys.Visibility = Ui.Show(show);
    }

    private async void InCallKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string digit) return;
        var key = digit switch
        {
            "*" => DtmfKey.Star,
            "#" => DtmfKey.Pound,
            _ => (DtmfKey)Enum.Parse(typeof(DtmfKey), "D" + digit)
        };
        await RunOnCallAsync(c => c.SendDtmfKeyAsync(key, DtmfToneAudioPlayback.Play), "send the tone");
    }

    private void MessagesSection_Click(object sender, RoutedEventArgs e) => MessagesRequested?.Invoke();
}
