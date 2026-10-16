using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Messages.Services;

/// <summary>
/// Loads avatars, attachments and link previews through the authenticated loopback API and caches them.
/// Image sources are created on the UI thread; call the Get*ImageAsync methods from it.
/// </summary>
public sealed class MediaService
{
    private const long MediaCacheBudget = 192L * 1024 * 1024;

    private readonly OpenMessageClient _client;
    private readonly Dictionary<string, Task<byte[]?>> _avatarBytes = new();
    private readonly Dictionary<string, ImageSource?> _avatarImages = new();
    private readonly Dictionary<string, Task<(byte[] Data, string Mime)?>> _mediaInflight = new();
    private readonly LinkedList<(string Key, byte[] Data, string Mime)> _mediaLru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Data, string Mime)>> _mediaIndex = new();
    private readonly Dictionary<string, Task<LinkPreview?>> _previews = new();
    private long _mediaBytes;

    public MediaService(OpenMessageClient client) => _client = client;

    public static string CacheDirectory => Path.Combine(BackendHost.DataDirectory, "cache");

    public void Clear()
    {
        _avatarBytes.Clear();
        _avatarImages.Clear();
        _mediaInflight.Clear();
        _mediaLru.Clear();
        _mediaIndex.Clear();
        _previews.Clear();
        _mediaBytes = 0;
    }

    // ---------- Avatars ----------

    private static string AvatarUrl(Participant p)
    {
        var query = new StringBuilder("api/avatar?source=sms");
        if (p.Id.Length > 0) query.Append("&participant_id=").Append(Uri.EscapeDataString(p.Id));
        if (p.ContactId.Length > 0) query.Append("&contact_id=").Append(Uri.EscapeDataString(p.ContactId));
        if (p.Number.Length > 0) query.Append("&phone=").Append(Uri.EscapeDataString(p.Number));
        return query.ToString();
    }

    private static string AvatarKey(Participant p) => $"{p.Id}|{p.ContactId}|{p.Number}";

    private Task<byte[]?> GetAvatarBytesAsync(Participant p)
    {
        var key = AvatarKey(p);
        if (key == "||") return Task.FromResult<byte[]?>(null);
        if (_avatarBytes.TryGetValue(key, out var task)) return task;
        task = FetchAvatarAsync(p);
        _avatarBytes[key] = task;
        return task;
    }

    private async Task<byte[]?> FetchAvatarAsync(Participant p)
    {
        try { return (await _client.GetBytesAsync(AvatarUrl(p)))?.Data; }
        catch { _avatarBytes.Remove(AvatarKey(p)); return null; }
    }

    public async Task<ImageSource?> GetAvatarImageAsync(Participant? p)
    {
        if (p is null) return null;
        var key = AvatarKey(p);
        if (_avatarImages.TryGetValue(key, out var cached)) return cached;
        var data = await GetAvatarBytesAsync(p);
        if (_avatarImages.TryGetValue(key, out cached)) return cached;
        ImageSource? image = null;
        if (data is not null)
        {
            try { image = await CreateBitmapAsync(data, 96); }
            catch { image = null; }
        }
        _avatarImages[key] = image;
        return image;
    }

    /// <summary>Writes the avatar to disk so a toast can show it. Returns null when there is no photo.</summary>
    public async Task<Uri?> GetAvatarFileAsync(Participant? p)
    {
        if (p is null) return null;
        var data = await GetAvatarBytesAsync(p);
        return data is null ? null : await WriteCacheFileAsync("avatars", data, ".png");
    }

    // ---------- Attachments ----------

    public async Task<(byte[] Data, string Mime)?> GetMediaAsync(string messageId)
    {
        if (_mediaIndex.TryGetValue(messageId, out var node))
        {
            _mediaLru.Remove(node);
            _mediaLru.AddFirst(node);
            return (node.Value.Data, node.Value.Mime);
        }
        if (!_mediaInflight.TryGetValue(messageId, out var task))
        {
            task = FetchMediaAsync(messageId);
            _mediaInflight[messageId] = task;
        }
        try
        {
            var result = await task;
            if (result is { } value && !_mediaIndex.ContainsKey(messageId)) Remember(messageId, value.Data, value.Mime);
            return result;
        }
        finally { _mediaInflight.Remove(messageId); }
    }

    private async Task<(byte[] Data, string Mime)?> FetchMediaAsync(string messageId)
    {
        var result = await _client.GetBytesAsync($"api/media/{Uri.EscapeDataString(messageId)}");
        return result is { } r ? (r.Data, r.ContentType) : null;
    }

    private void Remember(string key, byte[] data, string mime)
    {
        if (data.Length > MediaCacheBudget / 4) return;
        var node = _mediaLru.AddFirst((key, data, mime));
        _mediaIndex[key] = node;
        _mediaBytes += data.Length;
        while (_mediaBytes > MediaCacheBudget && _mediaLru.Last is { } last)
        {
            _mediaLru.RemoveLast();
            _mediaIndex.Remove(last.Value.Key);
            _mediaBytes -= last.Value.Data.Length;
        }
    }

    public static async Task<BitmapImage> CreateBitmapAsync(byte[] data, int decodeWidth = 0)
    {
        var bitmap = new BitmapImage();
        if (decodeWidth > 0)
        {
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.DecodePixelType = DecodePixelType.Logical;
        }
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    public static async Task<IRandomAccessStream> CreateStreamAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        return stream;
    }

    public static async Task<Uri> WriteCacheFileAsync(string folder, byte[] data, string extension)
    {
        var directory = Path.Combine(CacheDirectory, folder);
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant() + extension;
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, data);
        return new Uri(path);
    }

    // ---------- Link previews ----------

    public Task<LinkPreview?> GetLinkPreviewAsync(string url)
    {
        if (_previews.TryGetValue(url, out var task)) return task;
        task = FetchPreviewAsync(url);
        _previews[url] = task;
        return task;
    }

    private async Task<LinkPreview?> FetchPreviewAsync(string url)
    {
        try { return await _client.GetLinkPreviewAsync(url); }
        catch { return null; }
    }

    public async Task<ImageSource?> GetRemoteImageAsync(string relativeUrl, int decodeWidth)
    {
        try
        {
            var result = await _client.GetBytesAsync(relativeUrl);
            return result is { } r ? await CreateBitmapAsync(r.Data, decodeWidth) : null;
        }
        catch { return null; }
    }

    // ---------- Helpers ----------

    public static string ExtensionFor(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "video/mp4" => ".mp4",
        "video/3gpp" => ".3gp",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/amr" => ".amr",
        "audio/ogg" => ".ogg",
        "audio/aac" => ".aac",
        "audio/wav" or "audio/x-wav" => ".wav",
        "text/vcard" or "text/x-vcard" => ".vcf",
        "application/pdf" => ".pdf",
        _ => ""
    };

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".3gp" => "video/3gpp",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".amr" => "audio/amr",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".vcf" => "text/vcard",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };
}
