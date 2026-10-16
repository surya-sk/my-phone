using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Messages.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace Messages.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>An empty name tells x:Bind to refresh every binding on the item.</summary>
    protected void Raise(string? name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static partial class Ui
{
    // The Windows 8 start screen tile palette.
    private static readonly Color[] TilePalette =
    {
        Color.FromArgb(255, 0x00, 0xAB, 0xA9), // teal
        Color.FromArgb(255, 0x1B, 0xA1, 0xE2), // cyan
        Color.FromArgb(255, 0x00, 0x50, 0xEF), // cobalt
        Color.FromArgb(255, 0x6A, 0x00, 0xFF), // indigo
        Color.FromArgb(255, 0xAA, 0x00, 0xFF), // violet
        Color.FromArgb(255, 0xD8, 0x00, 0x73), // crimson
        Color.FromArgb(255, 0xE5, 0x14, 0x00), // red
        Color.FromArgb(255, 0xF0, 0x96, 0x09), // orange
        Color.FromArgb(255, 0x60, 0xA9, 0x17), // green
        Color.FromArgb(255, 0x00, 0x8A, 0x00), // emerald
        Color.FromArgb(255, 0x64, 0x76, 0x87), // steel
        Color.FromArgb(255, 0xA2, 0x00, 0x25), // magenta
    };

    private static readonly Dictionary<int, SolidColorBrush> TileBrushes = new();

    public static SolidColorBrush TileBrush(string seed)
    {
        var hash = 17;
        foreach (var ch in seed) hash = unchecked(hash * 31 + ch);
        var index = (int)((uint)hash % TilePalette.Length);
        if (!TileBrushes.TryGetValue(index, out var brush))
            TileBrushes[index] = brush = new SolidColorBrush(TilePalette[index]);
        return brush;
    }

    public static string Initials(string name)
    {
        var letters = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.FirstOrDefault(char.IsLetter))
            .Where(ch => ch != default)
            .Take(2)
            .ToArray();
        if (letters.Length > 0) return new string(letters).ToUpper(CultureInfo.CurrentCulture);
        return name.Any(char.IsDigit) ? "#" : "?";
    }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    public static string ClockTime(long ms) => ms <= 0 ? "" : Local(ms).ToString("t", CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);

    /// <summary>Inbox timestamps: time today, then "yesterday", weekday, and date.</summary>
    public static string ListTime(long ms)
    {
        if (ms <= 0) return "";
        var time = Local(ms);
        var today = DateTime.Today;
        if (time.Date == today) return ClockTime(ms);
        if (time.Date == today.AddDays(-1)) return "yesterday";
        if (time.Date > today.AddDays(-7)) return time.ToString("ddd", CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);
        return time.ToString(time.Year == today.Year ? "M/d" : "M/d/yy", CultureInfo.CurrentCulture);
    }

    public static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "today";
        if (day == today.AddDays(-1)) return "yesterday";
        var format = day.Year == today.Year ? "dddd, MMMM d" : "dddd, MMMM d, yyyy";
        return day.ToString(format, CultureInfo.CurrentCulture).ToLower(CultureInfo.CurrentCulture);
    }

    public static bool IsEmojiOnly(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 16) return false;
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (string.IsNullOrWhiteSpace(element)) continue;
            var rune = System.Text.Rune.GetRuneAt(element, 0);
            var emoji = rune.Value >= 0x1F000 || (rune.Value >= 0x2600 && rune.Value <= 0x27BF) || rune.Value == 0x2764;
            if (!emoji) return false;
            count++;
        }
        return count is > 0 and <= 3;
    }

    [GeneratedRegex(@"(https?://[^\s<>""]+|www\.[^\s<>""]+)", RegexOptions.IgnoreCase)]
    public static partial Regex UrlRegex();

    public static string? FirstUrl(string text)
    {
        var match = UrlRegex().Match(text);
        if (!match.Success) return null;
        var url = match.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':');
        return url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
    }

    public static string Snippet(ChatMessage message)
    {
        if (message.Body.Trim().Length > 0) return message.Body.ReplaceLineEndings(" ").Trim();
        return MediaLabel(message.MimeType);
    }

    public static string MediaLabel(string mime)
    {
        mime = mime.ToLowerInvariant();
        if (mime.StartsWith("image/gif")) return "GIF";
        if (mime.StartsWith("image/")) return "Photo";
        if (mime.StartsWith("video/")) return "Video";
        if (mime.StartsWith("audio/")) return "Voice message";
        if (mime.Contains("vcard")) return "Contact card";
        return mime.Length > 0 ? "Attachment" : "Message";
    }
}

public enum MediaKind { None, Image, Video, Audio, File }

public sealed class ConversationItem : Observable
{
    private ImageSource? _avatar;
    private bool _avatarRequested;

    public ConversationItem(Conversation model) => Model = model;

    public Conversation Model { get; private set; }

    public void Update(Conversation model)
    {
        if (Model.Equals(model)) return;
        var participantsChanged = !Model.Participants.SequenceEqual(model.Participants);
        Model = model;
        if (participantsChanged) _avatarRequested = false;
        Raise();
    }

    public string Id => Model.Id;
    public string Title => Model.DisplayName;
    public string Preview => Model.Preview.Length > 0 ? Model.Preview.ReplaceLineEndings(" ") : Model.IsGroup ? "group conversation" : "";
    public string TimeText => Ui.ListTime(Model.TimestampMs);
    public bool HasUnread => Model.UnreadCount > 0;
    public string UnreadText => Model.UnreadCount > 99 ? "99+" : Model.UnreadCount.ToString(CultureInfo.CurrentCulture);
    public Visibility UnreadVisibility => Ui.Show(HasUnread);
    public FontWeight TitleWeight => HasUnread ? FontWeights.SemiBold : FontWeights.Normal;
    public double PreviewOpacity => HasUnread ? 0.92 : 0.58;
    public Visibility PinVisibility => Ui.Show(Model.IsFavorite);
    public Visibility MuteVisibility => Ui.Show(Model.IsMuted);
    // Groups and unnamed numbers get a glyph instead of initials, like Metro People tiles.
    public string TileGlyph => Model.IsGroup ? "\uE716" : Ui.Initials(Title) == "#" ? "\uE77B" : "";
    public string Initials => TileGlyph.Length > 0 ? "" : Ui.Initials(Title);
    public Visibility GroupGlyphVisibility => Ui.Show(TileGlyph.Length > 0 && _avatar is null);
    public Brush TileBrush => Ui.TileBrush(Model.Id);
    public string AutomationName => $"{Title}, {(HasUnread ? $"{Model.UnreadCount} unread, " : "")}{Preview}, {TimeText}";

    public ImageSource? Avatar
    {
        get => _avatar;
        private set
        {
            if (!Set(ref _avatar, value)) return;
            Raise(nameof(AvatarVisibility));
            Raise(nameof(GroupGlyphVisibility));
        }
    }
    public Visibility AvatarVisibility => Ui.Show(_avatar is not null);

    public Participant? AvatarParticipant => Model.IsGroup ? null : Model.Others.FirstOrDefault() ?? Model.Participants.FirstOrDefault();

    public async void EnsureAvatar(MediaService media)
    {
        if (_avatarRequested) return;
        _avatarRequested = true;
        Avatar = await media.GetAvatarImageAsync(AvatarParticipant);
    }
}

public sealed class SectionHeaderItem
{
    public SectionHeaderItem(string text) => Text = text;
    public string Text { get; }
}

public sealed class SearchHitItem
{
    public SearchHitItem(ChatMessage message, string title)
    {
        Message = message;
        Title = title;
    }
    public ChatMessage Message { get; }
    public string Title { get; }
    public string Snippet => Ui.Snippet(Message);
    public string TimeText => Ui.ListTime(Message.TimestampMs);
}

public abstract class ThreadItem : Observable
{
    public abstract string Key { get; }
}

public sealed class DayItem : ThreadItem
{
    public DayItem(DateTime day)
    {
        Day = day;
        Text = Ui.DayLabel(day);
    }
    public DateTime Day { get; }
    public string Text { get; }
    public override string Key => "day:" + Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>A thread event (protocol switch, encryption, someone joined) shown as a quiet centered line.</summary>
public sealed class SystemItem : ThreadItem
{
    public SystemItem(ChatMessage model)
    {
        Model = model;
        Text = Protocol.Describe(model);
    }
    public ChatMessage Model { get; }
    public string Text { get; }
    public override string Key => Model.Id;
}

public sealed class ReactionChip
{
    public ReactionChip(Reaction reaction, bool isMine, string tooltip, ChatMessage message)
    {
        Reaction = reaction;
        IsMine = isMine;
        Tooltip = tooltip;
        Message = message;
    }
    public Reaction Reaction { get; }
    public bool IsMine { get; }
    public ChatMessage Message { get; }
    public string Text => Reaction.Count > 1 ? $"{Reaction.Emoji} {Reaction.Count}" : Reaction.Emoji;
    public Thickness MineBorder => new(IsMine ? 1.5 : 0);
    public string Tooltip { get; }
}

/// <summary>A send the user just made, shown in the transcript until the phone echoes it back.</summary>
public sealed class PendingSend
{
    public required string Key { get; init; }
    public required string ConversationId { get; init; }
    public string Body { get; init; } = "";
    public AttachmentItem? Attachment { get; init; }
    public string? ReplyToId { get; init; }
    public long CreatedMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Failed { get; set; }
    public bool Accepted { get; set; }
    public string Error { get; set; } = "";

    public ChatMessage ToMessage() => new("pending:" + Key, ConversationId, "", "", Body, CreatedMs, true,
        Failed ? "OUTGOING_FAILED" : Accepted ? "OUTGOING_COMPLETE" : "OUTGOING_SENDING", "", Array.Empty<Reaction>(),
        "", Attachment?.Mime ?? "", ReplyToId ?? "");
}

public sealed class MessageItem : ThreadItem
{
    private ImageSource? _mediaImage;
    private bool _mediaRequested;
    private bool _mediaFailed;
    private bool _previewRequested;
    private LinkPreview? _preview;
    private ImageSource? _previewImage;
    private bool _highlighted;
    private bool _audioPlaying;
    private IReadOnlyList<ReactionChip> _reactions = Array.Empty<ReactionChip>();

    public MessageItem(ChatMessage model, PendingSend? pending = null)
    {
        Model = model;
        Pending = pending;
    }

    public ChatMessage Model { get; private set; }
    public PendingSend? Pending { get; private set; }
    public override string Key => Model.Id;

    public bool IsGroupChat { get; private set; }
    public bool IsFirst { get; private set; } = true;
    public bool IsLast { get; private set; } = true;
    public string ReplySender { get; private set; } = "";
    public string ReplySnippet { get; private set; } = "";

    public void Update(ChatMessage model, PendingSend? pending, bool isGroup, bool first, bool last,
        string replySender, string replySnippet, IReadOnlyList<ReactionChip> reactions)
    {
        var changed = !Model.Equals(model) || Pending != pending || IsGroupChat != isGroup || IsFirst != first ||
                      IsLast != last || ReplySender != replySender || ReplySnippet != replySnippet ||
                      !_reactions.Select(r => (r.Text, r.Tooltip, r.IsMine)).SequenceEqual(reactions.Select(r => (r.Text, r.Tooltip, r.IsMine)));
        if (!changed) return;
        // A pending send keeps its local preview when the phone's copy replaces it.
        if (Model.MediaId != model.MediaId && Model.MediaId.Length > 0) { _mediaRequested = false; _mediaImage = null; }
        Model = model;
        Pending = pending;
        IsGroupChat = isGroup;
        IsFirst = first;
        IsLast = last;
        ReplySender = replySender;
        ReplySnippet = replySnippet;
        _reactions = reactions;
        Raise();
    }

    // ----- Layout -----
    public Thickness RowMargin => new(0, IsFirst ? 10 : 2, 0, 0);
    public Visibility TailVisibility => Ui.Show(Model.IsFromMe ? IsLast : IsFirst);
    public double BubbleOpacity => Pending is { Failed: false } ? 0.72 : 1;
    public Thickness HighlightThickness => new(_highlighted ? 2 : 0);

    public bool Highlighted
    {
        get => _highlighted;
        set { if (Set(ref _highlighted, value)) Raise(nameof(HighlightThickness)); }
    }

    // ----- Sender (group chats) -----
    public string SenderName => Model.SenderName.Length > 0 ? Model.SenderName : Model.SenderNumber;
    public Visibility SenderVisibility => Ui.Show(IsGroupChat && !Model.IsFromMe && IsFirst && SenderName.Length > 0);
    public Brush SenderBrush => Ui.TileBrush(Model.SenderNumber.Length > 0 ? Model.SenderNumber : SenderName);
    public Visibility AvatarSlotVisibility => Ui.Show(IsGroupChat && !Model.IsFromMe);
    public double AvatarOpacity => IsFirst ? 1 : 0;
    public string SenderInitials => Ui.Initials(SenderName);

    private ImageSource? _senderAvatar;
    private bool _senderAvatarRequested;
    public ImageSource? SenderAvatar
    {
        get => _senderAvatar;
        private set { if (Set(ref _senderAvatar, value)) Raise(nameof(SenderAvatarVisibility)); }
    }
    public Visibility SenderAvatarVisibility => Ui.Show(_senderAvatar is not null);

    public async void EnsureSenderAvatar(MediaService media, Participant? participant)
    {
        if (_senderAvatarRequested || participant is null) return;
        _senderAvatarRequested = true;
        SenderAvatar = await media.GetAvatarImageAsync(participant);
    }

    // ----- Text -----
    public string Body => Model.Body;
    public Visibility BodyVisibility => Ui.Show(Model.Body.Trim().Length > 0);
    public double BodyFontSize => Ui.IsEmojiOnly(Model.Body) ? 34 : 15;

    // ----- Meta line -----
    public string MetaText
    {
        get
        {
            var time = Ui.ClockTime(Model.TimestampMs);
            if (Model.IsFailed) return "";
            var status = Model.StatusDisplay;
            return status.Length > 0 && Model.IsFromMe ? $"{time}  ·  {status}" : time;
        }
    }
    public Visibility MetaVisibility => Ui.Show(!Model.IsFailed && (IsLast || Pending is not null));
    public Visibility FailedVisibility => Ui.Show(Model.IsFailed);
    public string FailedText => Pending is { Error.Length: > 0 } p ? $"not sent · {p.Error}" : "not sent · right-click to try again";
    public string AutomationName => $"{(Model.IsFromMe ? "You" : SenderName)}: {Ui.Snippet(Model)}, {MetaText}";

    // ----- Reply quote -----
    public Visibility ReplyVisibility => Ui.Show(Model.ReplyToId.Length > 0);
    public string ReplyHeader => ReplySender.Length > 0 ? ReplySender : "replying to a message";

    // ----- Reactions -----
    public IReadOnlyList<ReactionChip> Reactions => _reactions;
    public Visibility ReactionsVisibility => Ui.Show(_reactions.Count > 0);

    // ----- Media -----
    public MediaKind Kind
    {
        get
        {
            if (!Model.HasMedia) return MediaKind.None;
            var mime = Model.MimeType.ToLowerInvariant();
            if (mime.StartsWith("image/")) return MediaKind.Image;
            if (mime.StartsWith("video/")) return MediaKind.Video;
            if (mime.StartsWith("audio/")) return MediaKind.Audio;
            return MediaKind.File;
        }
    }

    public ImageSource? MediaImage
    {
        get => _mediaImage;
        private set
        {
            if (!Set(ref _mediaImage, value)) return;
            Raise(nameof(ImageVisibility));
            Raise(nameof(ImagePlaceholderVisibility));
        }
    }
    public Visibility ImageVisibility => Ui.Show(Kind == MediaKind.Image && _mediaImage is not null);
    public Visibility ImagePlaceholderVisibility => Ui.Show(Kind == MediaKind.Image && _mediaImage is null);
    public string ImagePlaceholderGlyph => _mediaFailed ? "" : "";
    public bool ImageLoading => Kind == MediaKind.Image && _mediaImage is null && !_mediaFailed;
    public Visibility VideoVisibility => Ui.Show(Kind == MediaKind.Video);
    public Visibility AudioVisibility => Ui.Show(Kind == MediaKind.Audio);
    public Visibility FileVisibility => Ui.Show(Kind == MediaKind.File);
    public string FileTitle => Pending?.Attachment?.Name is { Length: > 0 } name ? name : Ui.MediaLabel(Model.MimeType);
    public string FileSubtitle => Model.MimeType.Length > 0 ? Model.MimeType.ToLowerInvariant() : "file";
    public string FileGlyph => Model.MimeType.Contains("vcard", StringComparison.OrdinalIgnoreCase) ? "" : "";

    public bool AudioPlaying
    {
        get => _audioPlaying;
        set { if (Set(ref _audioPlaying, value)) Raise(nameof(AudioGlyph)); }
    }
    public string AudioGlyph => _audioPlaying ? "" : "";

    public bool MediaFailed => _mediaFailed;

    public void RetryMedia(MediaService media, Action? loaded)
    {
        _mediaRequested = false;
        _mediaFailed = false;
        Raise(nameof(ImageLoading));
        Raise(nameof(ImagePlaceholderGlyph));
        EnsureMedia(media, loaded);
    }

    public async void EnsureMedia(MediaService media, Action? loaded)
    {
        if (_mediaRequested || Kind != MediaKind.Image) return;
        _mediaRequested = true;
        try
        {
            byte[]? data = Pending?.Attachment?.Data;
            if (data is null && Model.MediaId.Length > 0 && !Model.Id.StartsWith("pending:", StringComparison.Ordinal))
                data = (await media.GetMediaAsync(Model.Id))?.Data;
            if (data is null) throw new InvalidOperationException("No media");
            MediaImage = await MediaService.CreateBitmapAsync(data, 560);
            loaded?.Invoke();
        }
        catch
        {
            _mediaFailed = true;
            Raise(nameof(ImagePlaceholderGlyph));
            Raise(nameof(ImageLoading));
        }
    }

    // ----- Link preview -----
    public string? Url => Ui.FirstUrl(Model.Body);
    public Visibility LinkVisibility => Ui.Show(_preview is not null);
    public string LinkTitle => _preview?.Title ?? "";
    public string LinkDescription => _preview?.Description ?? "";
    public Visibility LinkDescriptionVisibility => Ui.Show(LinkDescription.Length > 0);
    public string LinkSite => (_preview?.Site ?? "").ToLowerInvariant();
    public ImageSource? LinkImage => _previewImage;
    public Visibility LinkImageVisibility => Ui.Show(_previewImage is not null);

    public async void EnsurePreview(MediaService media, Action? loaded)
    {
        if (_previewRequested || Url is not { } url || Kind != MediaKind.None) return;
        _previewRequested = true;
        _preview = await media.GetLinkPreviewAsync(url);
        if (_preview is null) return;
        if (_preview.ImageUrl.Length > 0) _previewImage = await media.GetRemoteImageAsync(_preview.ImageUrl, 480);
        Raise();
        loaded?.Invoke();
    }
}

public sealed class AttachmentItem : Observable
{
    private ImageSource? _thumbnail;

    public AttachmentItem(string name, string mime, byte[] data)
    {
        Name = name;
        Mime = mime;
        Data = data;
    }

    public string Name { get; }
    public string Mime { get; }
    public byte[] Data { get; }
    public bool IsImage => Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public string Glyph => Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? ""
        : Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "" : "";
    public string SizeText => Data.Length >= 1024 * 1024 ? $"{Data.Length / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, Data.Length / 1024)} KB";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (!Set(ref _thumbnail, value)) return;
            Raise(nameof(ThumbnailVisibility));
            Raise(nameof(GlyphVisibility));
        }
    }
    public Visibility ThumbnailVisibility => Ui.Show(_thumbnail is not null);
    public Visibility GlyphVisibility => Ui.Show(_thumbnail is null);

    public async Task LoadThumbnailAsync()
    {
        if (!IsImage) return;
        try { Thumbnail = await MediaService.CreateBitmapAsync(Data, 128); }
        catch { }
    }
}
