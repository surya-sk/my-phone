using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Messages.Services;

/// <summary>Loopback HTTP client for the bundled openmessage backend.</summary>
public sealed class OpenMessageClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{BackendHost.Port}/"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Event streams stay open indefinitely, so they use their own client without a timeout.
    private readonly HttpClient _events = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{BackendHost.Port}/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public void LoadLocalToken()
    {
        if (!File.Exists(BackendHost.TokenPath)) return;
        var header = new AuthenticationHeaderValue("Bearer", File.ReadAllText(BackendHost.TokenPath).Trim());
        _http.DefaultRequestHeaders.Authorization = header;
        _events.DefaultRequestHeaders.Authorization = header;
    }

    public async Task<PhoneStatus> GetStatusAsync()
    {
        using var response = await _http.GetAsync("api/status");
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var google = Field(json.RootElement, "google");
        if (google.ValueKind != JsonValueKind.Object) return new PhoneStatus(false, false, false, "", false);
        var connected = Bool(google, "connected");
        var signInExpired = !connected && (Bool(google, "needs_pairing") || Bool(google, "needs_repair"));
        return new PhoneStatus(connected, signInExpired, Bool(google, "phone_responding"), String(google, "last_error"),
            Bool(google, "session_inactive"));
    }

    /// <summary>Makes this PC the active Google Messages session again ("Use here"). Google only delivers
    /// live messages to one linked session at a time.</summary>
    public async Task ActivateAsync()
    {
        using var response = await _http.PostAsync("api/google/activate", null);
        await EnsureSuccessAsync(response);
    }

    public async Task<IReadOnlyList<Conversation>> GetConversationsAsync()
    {
        using var response = await _http.GetAsync("api/conversations?limit=500");
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.EnumerateArray()
            .Where(e => IsGoogleMessages(String(e, "source_platform")))
            .Select(ParseConversation)
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .ToArray();
    }

    /// <summary>Latest messages, or the page before <paramref name="before"/> when paging back through history.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, int limit = 100, ChatMessage? before = null)
    {
        var path = $"api/conversations/{Uri.EscapeDataString(conversationId)}/messages?limit={limit}";
        if (before is not null && before.TimestampMs > 0)
            path += $"&before={before.TimestampMs}&before_id={Uri.EscapeDataString(before.Id)}";
        using var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return Deduplicate(json.RootElement.EnumerateArray().Select(ParseMessage));
    }

    public async Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(string query, int limit = 40)
    {
        using var response = await _http.GetAsync($"api/search/messages?q={Uri.EscapeDataString(query)}&limit={limit}");
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.EnumerateArray()
            .Where(e => IsGoogleMessages(String(e, "source_platform")))
            .Select(ParseMessage)
            .OrderByDescending(m => m.TimestampMs)
            .ToArray();
    }

    public async Task<IReadOnlyList<ContactEntry>> GetContactsAsync(string query)
    {
        using var response = await _http.GetAsync($"api/contacts?q={Uri.EscapeDataString(query)}&limit=12");
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.EnumerateArray()
            .Select(e => new ContactEntry(String(e, "Name"), String(e, "Number")))
            .Where(c => c.Number.Length > 0)
            .ToArray();
    }

    public async Task SendTextAsync(string conversationId, string body, string? replyToId, string idempotencyKey)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/outbox/messages", new
        {
            conversation_id = conversationId,
            body,
            reply_to_id = string.IsNullOrEmpty(replyToId) ? null : replyToId,
            idempotency_key = idempotencyKey
        });
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            using var legacy = await _http.PostAsJsonAsync("api/send", new
            {
                conversation_id = conversationId,
                message = body,
                reply_to_id = string.IsNullOrEmpty(replyToId) ? null : replyToId,
                idempotency_key = idempotencyKey
            });
            await EnsureSuccessAsync(legacy);
            return;
        }
        await EnsureSuccessAsync(response);
    }

    public async Task SendMediaAsync(string conversationId, byte[] data, string fileName, string mimeType,
        string caption, string? replyToId, string idempotencyKey)
    {
        HttpContent Build()
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(conversationId), "conversation_id");
            form.Add(new StringContent(idempotencyKey), "idempotency_key");
            if (caption.Length > 0) form.Add(new StringContent(caption), "caption");
            if (!string.IsNullOrEmpty(replyToId)) form.Add(new StringContent(replyToId), "reply_to_id");
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            form.Add(file, "file", fileName);
            return form;
        }
        using var content = Build();
        using var response = await _http.PostAsync("api/v1/outbox/media", content);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            using var legacyContent = Build();
            using var legacy = await _http.PostAsync("api/send-media", legacyContent);
            await EnsureSuccessAsync(legacy);
            return;
        }
        await EnsureSuccessAsync(response);
    }

    public async Task ReactAsync(string conversationId, string messageId, string emoji, string action = "add")
    {
        using var response = await _http.PostAsJsonAsync("api/react", new
        {
            conversation_id = conversationId,
            message_id = messageId,
            emoji,
            action
        });
        await EnsureSuccessAsync(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (Field(json.RootElement, "success").ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("The phone did not accept the reaction.");
    }

    /// <summary>Asks the phone for everything since <paramref name="since"/>. Returns false when a sync is already running.</summary>
    public async Task<bool> SyncSinceAsync(DateTimeOffset since)
    {
        using var response = await _http.PostAsJsonAsync("api/backfill", new { since = since.ToUnixTimeMilliseconds() });
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        await EnsureSuccessAsync(response);
        return true;
    }

    public async Task MarkReadAsync(string conversationId)
    {
        using var response = await _http.PostAsJsonAsync("api/mark-read", new { conversation_id = conversationId });
        await EnsureSuccessAsync(response);
    }

    /// <summary>Moves a thread between the inbox ("") and the archive ("archive").</summary>
    public async Task SetTabAsync(string conversationId, string tab)
    {
        using var response = await _http.PostAsJsonAsync(ConversationPath(conversationId, "tab"), new { tab });
        await EnsureSuccessAsync(response);
    }

    public async Task SetFavoriteAsync(string conversationId, bool favorite)
    {
        using var response = await _http.PostAsJsonAsync(ConversationPath(conversationId, "favorite"), new { favorite });
        await EnsureSuccessAsync(response);
    }

    public async Task SetNotificationModeAsync(string conversationId, string mode)
    {
        using var response = await _http.PostAsJsonAsync(ConversationPath(conversationId, "notification-mode"),
            new { notification_mode = mode });
        await EnsureSuccessAsync(response);
    }

    public async Task<string> NewConversationAsync(string phoneNumber)
    {
        using var response = await _http.PostAsJsonAsync("api/new-conversation", new
        {
            phone_number = phoneNumber,
            platform = "sms"
        });
        await EnsureSuccessAsync(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var id = String(json.RootElement, "conversation_id");
        if (id.Length == 0) throw new InvalidOperationException("The phone did not return a conversation ID.");
        return id;
    }

    public async Task<LinkPreview?> GetLinkPreviewAsync(string url)
    {
        using var response = await _http.GetAsync($"api/link-preview?url={Uri.EscapeDataString(url)}");
        if (!response.IsSuccessStatusCode) return null;
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        var preview = new LinkPreview(url, String(root, "title"), String(root, "description"),
            String(root, "image_url"), String(root, "site_name") is { Length: > 0 } site ? site : String(root, "domain"));
        return preview.Title.Length == 0 && preview.Description.Length == 0 ? null : preview;
    }

    /// <summary>Fetches a binary resource (media, avatar, preview image). Returns null when the backend has none.</summary>
    public async Task<(byte[] Data, string ContentType)?> GetBytesAsync(string relativeUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(relativeUrl.TrimStart('/'), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return data.Length == 0 ? null : (data, response.Content.Headers.ContentType?.MediaType ?? "");
    }

    public async Task WatchEventsAsync(Action<BackendEvent> onEvent, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await _events.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                string eventType = "";
                string data = "";
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal)) eventType = line[7..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                    else if (line.Length == 0 && data.Length > 0)
                    {
                        using var json = JsonDocument.Parse(data);
                        var root = json.RootElement;
                        var type = eventType.Length > 0 ? eventType : String(root, "type");
                        var connected = Field(root, "connected");
                        onEvent(new BackendEvent(type, String(root, "conversation_id"),
                            String(root, "sender_name"), Bool(root, "typing"),
                            connected.ValueKind is JsonValueKind.True or JsonValueKind.False ? connected.GetBoolean() : null));
                        eventType = "";
                        data = "";
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { }
            try { await Task.Delay(2000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string ConversationPath(string conversationId, string action) =>
        $"api/conversations/{Uri.EscapeDataString(conversationId)}/{action}";

    private static readonly HashSet<string> OtherPlatforms = new(StringComparer.OrdinalIgnoreCase)
        { "whatsapp", "signal", "telegram", "gchat", "imessage" };

    private static bool IsGoogleMessages(string platform) => !OtherPlatforms.Contains(platform);

    private static Conversation ParseConversation(JsonElement e)
    {
        var participants = new List<Participant>();
        var raw = String(e, "Participants");
        if (raw.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var p in doc.RootElement.EnumerateArray())
                        participants.Add(new Participant(String(p, "id"), String(p, "name"), String(p, "number"),
                            Bool(p, "is_me"), String(p, "contact_id")));
            }
            catch (JsonException) { }
        }
        return new Conversation(
            String(e, "ConversationID"),
            String(e, "Name"),
            String(e, "last_message_preview"),
            Number(e, "LastMessageTS"),
            String(e, "display_protocol"),
            (int)Number(e, "UnreadCount"),
            Bool(e, "IsGroup"),
            participants,
            Bool(e, "is_favorite"),
            String(e, "notification_mode"),
            String(e, "tab"));
    }

    private static ChatMessage ParseMessage(JsonElement e) => new(
        String(e, "MessageID"),
        String(e, "ConversationID"),
        String(e, "SenderName"),
        String(e, "SenderNumber"),
        String(e, "Body"),
        Number(e, "TimestampMS"),
        Bool(e, "IsFromMe"),
        String(e, "Status"),
        String(e, "source_id"),
        ParseReactions(e),
        String(e, "MediaID"),
        String(e, "MimeType"),
        String(e, "ReplyToID"));

    /// <summary>The outbox and the phone echo can both report one send; keep the one with the furthest status.</summary>
    private static IReadOnlyList<ChatMessage> Deduplicate(IEnumerable<ChatMessage> source)
    {
        var items = new List<ChatMessage>();
        foreach (var message in source.OrderBy(m => m.TimestampMs))
        {
            var duplicate = items.FindIndex(existing =>
                existing.Id == message.Id ||
                (!string.IsNullOrEmpty(existing.SourceId) && existing.SourceId == message.SourceId) ||
                (existing.IsFromMe && message.IsFromMe && existing.Body == message.Body &&
                 existing.MediaId.Length == 0 && message.MediaId.Length == 0 &&
                 Math.Abs(existing.TimestampMs - message.TimestampMs) <= 2000 &&
                 (string.IsNullOrEmpty(existing.SourceId) || string.IsNullOrEmpty(message.SourceId))));
            if (duplicate < 0) items.Add(message);
            else if (message.StatusRank >= items[duplicate].StatusRank) items[duplicate] = message;
        }
        return items.OrderBy(m => m.TimestampMs).ToArray();
    }

    private static IReadOnlyList<Reaction> ParseReactions(JsonElement message)
    {
        var value = Field(message, "Reactions");
        if (value.ValueKind == JsonValueKind.Undefined) value = Field(message, "reactions");
        JsonDocument? owned = null;
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) return Array.Empty<Reaction>();
                owned = JsonDocument.Parse(text);
                value = owned.RootElement;
            }
            if (value.ValueKind != JsonValueKind.Array) return Array.Empty<Reaction>();
            return value.EnumerateArray().Select(r =>
            {
                var actors = Field(r, "actors");
                var list = actors.ValueKind == JsonValueKind.Array
                    ? actors.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray()
                    : Array.Empty<string>();
                var count = (int)Number(r, "count");
                return new Reaction(String(r, "emoji"), Math.Max(count, Math.Max(1, list.Length)), list);
            }).Where(r => r.Emoji.Length > 0).ToArray();
        }
        catch (JsonException) { return Array.Empty<Reaction>(); }
        finally { owned?.Dispose(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = (await response.Content.ReadAsStringAsync()).Trim();
        try
        {
            using var json = JsonDocument.Parse(detail);
            if (String(json.RootElement, "error") is { Length: > 0 } error) detail = error;
        }
        catch (JsonException) { }
        throw new HttpRequestException($"{detail} ({(int)response.StatusCode})");
    }

    private static JsonElement Field(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement root, string name)
    {
        var value = Field(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
    private static long Number(JsonElement root, string name)
    {
        var value = Field(root, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
    private static bool Bool(JsonElement root, string name) => Field(root, name).ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        _http.Dispose();
        _events.Dispose();
    }
}

/// <param name="SessionInactive">Google is delivering to another linked session; nothing new arrives here until activated.</param>
public sealed record PhoneStatus(bool Connected, bool SignInExpired, bool PhoneResponding, string LastError, bool SessionInactive);

public sealed record BackendEvent(string Type, string ConversationId, string SenderName, bool Typing, bool? Connected);

public sealed record ContactEntry(string Name, string Number)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Number : Name;
    public override string ToString() => DisplayName;
}

public sealed record LinkPreview(string Url, string Title, string Description, string ImageUrl, string Site);

public sealed record Participant(string Id, string Name, string Number, bool IsMe, string ContactId);

public sealed record Reaction(string Emoji, int Count, IReadOnlyList<string> Actors);

public sealed record Conversation(string Id, string Name, string Preview, long TimestampMs, string Protocol,
    int UnreadCount, bool IsGroup, IReadOnlyList<Participant> Participants, bool IsFavorite, string NotificationMode, string Tab)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Others.FirstOrDefault()?.Number is { Length: > 0 } n ? n : Id) : Name;
    public bool IsArchived => Tab.Equals("archive", StringComparison.OrdinalIgnoreCase);
    public bool IsMuted => NotificationMode.Equals("muted", StringComparison.OrdinalIgnoreCase);
    public IEnumerable<Participant> Others => Participants.Where(p => !p.IsMe);

    /// <summary>Reaction actors are Google participant IDs; "me" marks a reaction this app just added.</summary>
    public bool IsMine(Reaction reaction) => reaction.Actors.Any(IsMyActor);

    public bool IsMyActor(string actor) => actor.Equals("me", StringComparison.OrdinalIgnoreCase) ||
        Participants.Any(p => p.IsMe && p.Id.Length > 0 && p.Id == actor);
    public string ProtocolLabel => Protocol.ToUpperInvariant() switch
    {
        "RCS" => "RCS",
        "SMS" or "TEXT" or "MMS" => "SMS",
        _ => ""
    };

    // Value equality on the participant list, so refreshes only replace rows that really changed.
    public bool Equals(Conversation? other) => other is not null && Id == other.Id && Name == other.Name &&
        Preview == other.Preview && TimestampMs == other.TimestampMs && Protocol == other.Protocol &&
        UnreadCount == other.UnreadCount && IsGroup == other.IsGroup && IsFavorite == other.IsFavorite &&
        NotificationMode == other.NotificationMode && Tab == other.Tab && Participants.SequenceEqual(other.Participants);
    public override int GetHashCode() => HashCode.Combine(Id, TimestampMs, UnreadCount);
}

public sealed record ChatMessage(string Id, string ConversationId, string SenderName, string SenderNumber, string Body,
    long TimestampMs, bool IsFromMe, string Status, string SourceId, IReadOnlyList<Reaction> Reactions,
    string MediaId, string MimeType, string ReplyToId)
{
    public bool HasMedia => MediaId.Length > 0 || MimeType.Length > 0;

    /// <summary>Google "tombstones": thread events such as "switched to RCS", not messages anyone sent.</summary>
    public bool IsSystem => Status.StartsWith("TOMBSTONE", StringComparison.OrdinalIgnoreCase);

    public int StatusRank => Status.ToUpperInvariant() switch
    {
        var x when x.Contains("READ") || x.Contains("DISPLAYED") || x.Contains("PLAYED") => 4,
        var x when x.Contains("DELIVERED") => 3,
        var x when x.Contains("SENT") || x.Contains("COMPLETE") => 2,
        _ => 1
    };

    public bool IsFailed
    {
        get
        {
            var status = Status.ToUpperInvariant();
            return IsFromMe && (status.Contains("FAILED") || status.Contains("CANCELED") || status.Contains("ERROR"));
        }
    }

    /// <summary>Only what Google Messages reported; SMS never claims a read receipt it did not get.</summary>
    public string StatusDisplay
    {
        get
        {
            if (!IsFromMe) return "";
            var status = Status.ToUpperInvariant();
            if (IsFailed) return "not sent";
            if (status.Contains("READ") || status.Contains("DISPLAYED") || status.Contains("PLAYED")) return "read";
            if (status.Contains("DELIVERED")) return "delivered";
            if (status.Contains("SENDING") || status.Contains("YET_TO_SEND") || status.Contains("PENDING")) return "sending…";
            if (status.Contains("SENT") || status.Contains("COMPLETE") || status.Contains("OUTGOING")) return "sent";
            return "";
        }
    }

    public bool Equals(ChatMessage? other) => other is not null && Id == other.Id &&
        ConversationId == other.ConversationId && Body == other.Body && TimestampMs == other.TimestampMs &&
        IsFromMe == other.IsFromMe && Status == other.Status && SourceId == other.SourceId &&
        MediaId == other.MediaId && MimeType == other.MimeType && ReplyToId == other.ReplyToId &&
        SenderName == other.SenderName && SenderNumber == other.SenderNumber &&
        Reactions.Count == other.Reactions.Count &&
        Reactions.Zip(other.Reactions).All(p => p.First.Emoji == p.Second.Emoji && p.First.Count == p.Second.Count &&
            p.First.Actors.SequenceEqual(p.Second.Actors));
    public override int GetHashCode() => HashCode.Combine(Id, TimestampMs, Status);
}
