using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Messages.Services;

public enum CallKind { Incoming, Outgoing, Missed }

public sealed record CallLogEntry(string Name, string Number, CallKind Kind, DateTimeOffset When);

public sealed record PhoneContact(string Name, IReadOnlyList<(string Label, string Number)> Numbers, byte[]? Photo);

/// <summary>
/// Reads the phone's contacts and call history over Bluetooth PBAP (Phone Book Access Profile), the same
/// profile car kits use. Android asks once on the phone ("allow access to contacts and call history")
/// and remembers the answer per PC. Results are cached in the data folder so the calls view opens
/// instantly and works while the phone is out of range.
/// </summary>
public sealed class Phonebook
{
    private static string CachePath => Path.Combine(BackendHost.DataDirectory, "phonebook.json");
    private static readonly byte[] PbapTarget = Convert.FromHexString("796135F0F0C511D809660800200C9A66");

    public IReadOnlyList<CallLogEntry> Calls { get; private set; } = Array.Empty<CallLogEntry>();
    public IReadOnlyList<PhoneContact> Contacts { get; private set; } = Array.Empty<PhoneContact>();
    public DateTimeOffset? Updated { get; private set; }

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath));
            if (cache is null) return;
            Calls = cache.Calls.Select(c => new CallLogEntry(c.Name, c.Number, c.Kind, c.When)).ToList();
            Contacts = cache.Contacts.Select(c => new PhoneContact(c.Name, c.Numbers.Select(n => (n.Label, n.Number)).ToList(),
                c.Photo is { Length: > 0 } p ? Convert.FromBase64String(p) : null)).ToList();
            Updated = cache.Updated;
        }
        catch (Exception ex) { Log.Write($"Phonebook: cache unreadable: {ex.Message}"); }
    }

    /// <summary>Downloads call history and contacts from the phone behind <paramref name="hfpDeviceId"/>.</summary>
    public async Task SyncAsync(string hfpDeviceId, bool includeContacts, CancellationToken cancellationToken = default)
    {
        var service = await FindServiceAsync(hfpDeviceId)
            ?? throw new InvalidOperationException("Your phone isn't sharing contacts and call history over Bluetooth.");
        using var socket = new StreamSocket();
        await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(cancellationToken);
        var session = new ObexSession(socket);
        await session.ConnectAsync(PbapTarget, cancellationToken);
        try
        {
            var history = await session.GetAsync("telecom/cch.vcf", "x-bt/phonebook", PullParameters(1000, HistoryFields), cancellationToken);
            Calls = ParseCallHistory(history);
            if (includeContacts || Contacts.Count == 0)
            {
                var book = await session.GetAsync("telecom/pb.vcf", "x-bt/phonebook", PullParameters(5000, ContactFields), cancellationToken);
                Contacts = ParseContacts(book);
            }
            Updated = DateTimeOffset.Now;
            SaveCache();
        }
        finally { await session.DisconnectAsync(); }
    }

    private static async Task<RfcommDeviceService?> FindServiceAsync(string hfpDeviceId)
    {
        // The hands-free transport id belongs to the phone's Bluetooth device; PBAP lives on the same device.
        var info = await DeviceInformation.CreateFromIdAsync(hfpDeviceId, new[] { "System.DeviceInterface.Bluetooth.DeviceAddress", "System.Devices.Aep.DeviceAddress", "System.Devices.ContainerId" });
        var address = info.Properties.Values.OfType<string>().Select(ParseAddress).FirstOrDefault(a => a != 0);
        if (address == 0) address = await AddressFromContainerAsync(info);
        if (address == 0) return null;
        using var device = await BluetoothDevice.FromBluetoothAddressAsync(address);
        if (device is null) return null;
        var services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.PhoneBookAccessPse, BluetoothCacheMode.Uncached);
        return services.Services.FirstOrDefault();
    }

    private static async Task<ulong> AddressFromContainerAsync(DeviceInformation info)
    {
        if (!info.Properties.TryGetValue("System.Devices.ContainerId", out var container) || container is not Guid id) return 0;
        var selector = $"System.Devices.Aep.ContainerId:=\"{{{id}}}\" AND System.Devices.Aep.ProtocolId:=\"{{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}}\"";
        var matches = await DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.DeviceAddress" }, DeviceInformationKind.AssociationEndpoint);
        return matches.Select(m => m.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) ? ParseAddress(a as string) : 0).FirstOrDefault(a => a != 0);
    }

    private static ulong ParseAddress(string? text)
    {
        var hex = (text ?? "").Replace(":", "").Replace("-", "");
        return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, null, out var value) ? value : 0;
    }

    // PBAP property mask bits: VERSION 0, FN 1, N 2, PHOTO 3, TEL 7, X-IRMC-CALL-DATETIME 28.
    // Android leaves photos out unless the mask asks for them.
    private const ulong ContactFields = 0b1000_1111;
    private const ulong HistoryFields = 0b1000_0111 | (1UL << 28);

    /// <summary>PBAP application parameters: MaxListCount, Format = vCard 2.1 (every phone supports it), PropertySelector.</summary>
    private static byte[] PullParameters(ushort max, ulong fields)
    {
        var p = new List<byte> { 0x04, 2, (byte)(max >> 8), (byte)max, 0x07, 1, 0x00, 0x06, 8 };
        for (var shift = 56; shift >= 0; shift -= 8) p.Add((byte)(fields >> shift));
        return p.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // vCard parsing

    internal static IReadOnlyList<CallLogEntry> ParseCallHistory(string vcards)
    {
        var list = new List<CallLogEntry>();
        foreach (var card in VCard.Parse(vcards))
        {
            var stamp = card.FirstOrDefault(p => p.Name == "X-IRMC-CALL-DATETIME");
            if (stamp is null) continue;
            var kind = stamp.Params.Any(p => p.Equals("MISSED", StringComparison.OrdinalIgnoreCase)) ? CallKind.Missed
                : stamp.Params.Any(p => p.Equals("DIALED", StringComparison.OrdinalIgnoreCase)) ? CallKind.Outgoing
                : CallKind.Incoming;
            if (!TryParseStamp(stamp.Value, out var when)) continue;
            var number = card.FirstOrDefault(p => p.Name == "TEL")?.Value.Trim() ?? "";
            list.Add(new CallLogEntry(NameOf(card), number, kind, when));
        }
        return list.OrderByDescending(c => c.When).ToList();
    }

    internal static IReadOnlyList<PhoneContact> ParseContacts(string vcards)
    {
        var list = new List<PhoneContact>();
        foreach (var card in VCard.Parse(vcards))
        {
            var numbers = card.Where(p => p.Name == "TEL" && p.Value.Trim().Length > 0)
                .Select(p => (Label(p.Params), p.Value.Trim())).Distinct().ToList();
            var name = NameOf(card);
            if (numbers.Count == 0 || name.Length == 0) continue;  // The owner card and empty entries.
            byte[]? photo = null;
            if (card.FirstOrDefault(p => p.Name == "PHOTO") is { } ph)
                try { photo = Convert.FromBase64String(ph.Value.Replace(" ", "")); } catch (FormatException) { }
            list.Add(new PhoneContact(name, numbers, photo));
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string NameOf(IReadOnlyList<VCard.Property> card)
    {
        var fn = card.FirstOrDefault(p => p.Name == "FN")?.Value.Trim();
        if (!string.IsNullOrEmpty(fn)) return fn;
        var n = card.FirstOrDefault(p => p.Name == "N")?.Value.Split(';');
        return n is null ? "" : string.Join(' ', new[] { n.ElementAtOrDefault(1), n.ElementAtOrDefault(0) }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    private static string Label(IEnumerable<string> parameters)
    {
        foreach (var p in parameters.Select(p => p.ToUpperInvariant().Replace("TYPE=", "")))
            switch (p)
            {
                case "CELL": return "mobile";
                case "HOME": return "home";
                case "WORK": return "work";
                case "MAIN": return "main";
            }
        return "phone";
    }

    private static bool TryParseStamp(string value, out DateTimeOffset when)
    {
        var text = value.Trim();
        var utc = text.EndsWith('Z');
        if (DateTime.TryParseExact(text.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            when = utc ? new DateTimeOffset(local, TimeSpan.Zero) : new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return true;
        }
        when = default;
        return false;
    }

    private void SaveCache()
    {
        try
        {
            var cache = new Cache(Updated,
                Calls.Select(c => new CachedCall(c.Name, c.Number, c.Kind, c.When)).ToList(),
                Contacts.Select(c => new CachedContact(c.Name, c.Numbers.Select(n => new CachedNumber(n.Label, n.Number)).ToList(),
                    c.Photo is null ? null : Convert.ToBase64String(c.Photo))).ToList());
            Directory.CreateDirectory(BackendHost.DataDirectory);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) { Log.Write($"Phonebook: cache not saved: {ex.Message}"); }
    }

    private sealed record Cache(DateTimeOffset? Updated, List<CachedCall> Calls, List<CachedContact> Contacts);
    private sealed record CachedCall(string Name, string Number, CallKind Kind, DateTimeOffset When);
    private sealed record CachedContact(string Name, List<CachedNumber> Numbers, string? Photo);
    private sealed record CachedNumber(string Label, string Number);
}

/// <summary>Just enough vCard 2.1/3.0 for PBAP: folded lines, parameters, quoted-printable and base64 values.</summary>
internal static class VCard
{
    public sealed record Property(string Name, IReadOnlyList<string> Params, string Value);

    public static IEnumerable<IReadOnlyList<Property>> Parse(string text)
    {
        List<Property>? card = null;
        foreach (var line in Unfold(text))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var head = line[..colon].Split(';');
            var name = head[0].Trim().ToUpperInvariant();
            var group = name.LastIndexOf('.');
            if (group >= 0) name = name[(group + 1)..];
            var value = line[(colon + 1)..];
            if (name == "BEGIN") { card = new List<Property>(); continue; }
            if (name == "END") { if (card is not null) yield return card; card = null; continue; }
            if (card is null) continue;
            var parameters = head.Skip(1).Select(p => p.Trim()).ToList();
            if (parameters.Any(p => p.Equals("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase) || p.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)))
                value = DecodeQuotedPrintable(value);
            card.Add(new Property(name, parameters, value.Replace("\\,", ",").Replace("\\;", ";")));
        }
    }

    /// <summary>Joins continuation lines: a leading space (base64, 3.0 folding) or a trailing '=' (quoted-printable).</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && current.Length > 0)
            {
                current.Append(raw.TrimStart());
                continue;
            }
            if (current.Length > 0 && current[^1] == '=' && current.ToString().Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            {
                current.Length--;
                current.Append(raw);
                continue;
            }
            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(raw);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string DecodeQuotedPrintable(string value)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>Minimal OBEX client (CONNECT, GET, DISCONNECT) over an RFCOMM socket.</summary>
internal sealed class ObexSession
{
    private const byte HeaderName = 0x01, HeaderType = 0x42, HeaderBody = 0x48, HeaderEndOfBody = 0x49,
        HeaderTarget = 0x46, HeaderAppParameters = 0x4C, HeaderConnectionId = 0xCB;
    private const byte Success = 0xA0, Continue = 0x90;

    private readonly DataReader _reader;
    private readonly DataWriter _writer;
    private uint? _connectionId;
    private ushort _maxPacket = 0x2000;

    public ObexSession(StreamSocket socket)
    {
        _reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        _writer = new DataWriter(socket.OutputStream);
    }

    public async Task ConnectAsync(byte[] target, CancellationToken cancellationToken)
    {
        var packet = new List<byte> { 0x10, 0x00, (byte)(_maxPacket >> 8), (byte)_maxPacket };
        AddBytesHeader(packet, HeaderTarget, target);
        var (code, body, headers) = await SendAsync(0x80, packet, connectResponse: true, cancellationToken);
        if (code != Success) throw new InvalidOperationException(code == 0xC3 || code == 0xC1
            ? "Allow contacts and call history access on your phone (check its notifications), then try again."
            : $"The phone refused the phonebook connection (0x{code:X2}).");
        if (body.Length >= 4) _maxPacket = Math.Min(_maxPacket, BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2)));
        if (headers.TryGetValue(HeaderConnectionId, out var id)) _connectionId = BinaryPrimitives.ReadUInt32BigEndian(id);
    }

    public async Task<string> GetAsync(string name, string type, byte[] appParameters, CancellationToken cancellationToken)
    {
        var request = new List<byte>();
        AddConnectionId(request);
        AddUnicodeHeader(request, HeaderName, name);
        AddBytesHeader(request, HeaderType, Encoding.ASCII.GetBytes(type + "\0"));
        AddBytesHeader(request, HeaderAppParameters, appParameters);
        var result = new MemoryStream();
        while (true)
        {
            var (code, _, headers) = await SendAsync(0x83, request, connectResponse: false, cancellationToken);
            if (headers.TryGetValue(HeaderBody, out var chunk)) result.Write(chunk);
            if (headers.TryGetValue(HeaderEndOfBody, out var end)) result.Write(end);
            if (code == Success) break;
            if (code != Continue) throw new InvalidOperationException(code is 0xC3 or 0xC1
                ? "Allow contacts and call history access on your phone (check its notifications), then try again."
                : $"The phone couldn't share {name} (0x{code:X2}).");
            request.Clear();
            AddConnectionId(request);
        }
        return Encoding.UTF8.GetString(result.ToArray());
    }

    public async Task DisconnectAsync()
    {
        try
        {
            var request = new List<byte>();
            AddConnectionId(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendAsync(0x81, request, connectResponse: false, timeout.Token);
        }
        catch { }
    }

    private void AddConnectionId(List<byte> packet)
    {
        if (_connectionId is not { } id) return;
        packet.Add(HeaderConnectionId);
        packet.AddRange(new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });
    }

    private static void AddBytesHeader(List<byte> packet, byte id, byte[] value)
    {
        var length = value.Length + 3;
        packet.Add(id);
        packet.Add((byte)(length >> 8));
        packet.Add((byte)length);
        packet.AddRange(value);
    }

    private static void AddUnicodeHeader(List<byte> packet, byte id, string value) =>
        AddBytesHeader(packet, id, Encoding.BigEndianUnicode.GetBytes(value + "\0"));

    private async Task<(byte Code, byte[] Prefix, Dictionary<byte, byte[]> Headers)> SendAsync(
        byte opcode, List<byte> payload, bool connectResponse, CancellationToken cancellationToken)
    {
        var length = payload.Count + 3;
        _writer.WriteByte(opcode);
        _writer.WriteUInt16((ushort)length);
        _writer.WriteBytes(payload.ToArray());
        await _writer.StoreAsync().AsTask(cancellationToken);

        var head = await ReadExactAsync(3, cancellationToken);
        var code = head[0];
        var total = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
        var rest = total > 3 ? await ReadExactAsync(total - 3, cancellationToken) : Array.Empty<byte>();
        var offset = connectResponse ? Math.Min(4, rest.Length) : 0;
        var prefix = rest[..offset];
        var headers = new Dictionary<byte, byte[]>();
        while (offset < rest.Length)
        {
            var id = rest[offset];
            switch (id >> 6)
            {
                case 0: case 1:  // Unicode text or byte sequence, two-byte length including the 3-byte prefix.
                    var len = BinaryPrimitives.ReadUInt16BigEndian(rest.AsSpan(offset + 1));
                    headers[id] = rest[(offset + 3)..(offset + len)];
                    offset += len;
                    break;
                case 2:
                    headers[id] = rest[(offset + 1)..(offset + 2)];
                    offset += 2;
                    break;
                default:
                    headers[id] = rest[(offset + 1)..(offset + 5)];
                    offset += 5;
                    break;
            }
        }
        return (code, prefix, headers);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            if (_reader.UnconsumedBufferLength == 0)
            {
                var loaded = await _reader.LoadAsync((uint)(count - read)).AsTask(cancellationToken);
                if (loaded == 0) throw new IOException("The phone closed the Bluetooth connection.");
            }
            var take = (int)Math.Min(_reader.UnconsumedBufferLength, (uint)(count - read));
            var chunk = new byte[take];
            _reader.ReadBytes(chunk);
            chunk.CopyTo(buffer, read);
            read += take;
        }
        return buffer;
    }
}
