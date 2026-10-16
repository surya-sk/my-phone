using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace Messages.Services;

public sealed record PairedPhone(string Id, string Name);

public enum CallDirection { Incoming, Outgoing }

/// <summary>A call on the phone as this PC sees it. <see cref="Call"/> carries the controls.</summary>
public sealed record ActiveCall(PhoneCall Call, string Number, string Name, CallDirection Direction,
    PhoneCallStatus Status, bool IsMuted, bool AudioOnPc, DateTimeOffset? TalkingSince);

/// <summary>
/// Phone calls over Bluetooth Hands-Free. Taking the hands-free role for the paired phone makes Windows
/// treat this PC like a car kit: calls ring here, audio plays through the PC's speakers and microphone,
/// and Windows' own call window appears. This class owns that role plus the phone line used to dial
/// and the list of calls in progress. No companion app is involved.
/// </summary>
public sealed class PhoneCalls : IDisposable
{
    private static string SelectionPath => Path.Combine(BackendHost.DataDirectory, "calls-device.txt");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PhoneLineTransportDevice? _transport;
    private PhoneLine? _line;
    private PhoneLineWatcher? _watcher;
    private readonly Dictionary<string, (PhoneCall Call, DateTimeOffset? TalkingSince, PhoneCallStatus Last, string Number, string Name, CallDirection Direction)> _calls = new();
    private bool _callStateHooked;

    /// <summary>Raised on any thread when the connection, line or calls change.</summary>
    public event Action? Changed;
    /// <summary>An incoming call ended without being answered: number, name.</summary>
    public event Action<string, string>? Missed;

    public PairedPhone? Phone { get; private set; }
    public bool IsConnected { get; private set; }
    public string? Problem { get; private set; }
    public bool CanDial => _line?.CanDial == true;
    public string? NetworkName => _line?.NetworkName is { Length: > 0 } n ? n : null;
    public string? SavedPhoneId => File.Exists(SelectionPath) ? File.ReadAllText(SelectionPath).Trim() is { Length: > 0 } id ? id : null : null;

    public IReadOnlyList<ActiveCall> Calls
    {
        get
        {
            lock (_calls)
                return _calls.Values
                    .Where(c => c.Last is not (PhoneCallStatus.Ended or PhoneCallStatus.Lost))
                    .Select(c => new ActiveCall(c.Call, c.Number, c.Name, c.Direction, c.Last, SafeMuted(c.Call),
                        SafeAudio(c.Call) == PhoneCallAudioDevice.LocalDevice, c.TalkingSince))
                    .ToList();
        }
    }

    /// <summary>Paired Bluetooth devices that can act as a phone (they offer the Hands-Free audio gateway).</summary>
    public static async Task<IReadOnlyList<PairedPhone>> FindPhonesAsync()
    {
        var selector = PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.Select(d => new PairedPhone(d.Id, d.Name)).ToList();
    }

    /// <summary>Connects to the saved phone, if there is one. Quietly does nothing otherwise.</summary>
    public async Task ConnectSavedAsync()
    {
        if (SavedPhoneId is not { } id) return;
        try { await ConnectAsync(id); }
        catch (Exception ex) { Log.Write($"Calls: reconnect failed: {ex.Message}"); }
    }

    public async Task ConnectAsync(string deviceId)
    {
        await _gate.WaitAsync();
        try
        {
            Problem = null;
            var phones = await FindPhonesAsync();
            var phone = phones.FirstOrDefault(p => p.Id == deviceId)
                ?? throw new InvalidOperationException("That phone isn't paired with this PC any more. Pair it again in Bluetooth settings.");
            // The phone is chosen even if Windows refuses the calls role: contacts and call history
            // come over a different Bluetooth profile that needs no permission.
            Phone = phone;
            Directory.CreateDirectory(BackendHost.DataDirectory);
            await File.WriteAllTextAsync(SelectionPath, deviceId);

            var transport = PhoneLineTransportDevice.FromId(deviceId)
                ?? throw new InvalidOperationException("Windows couldn't open that phone for calls.");

            LimitedAccess.TryUnlock();
            var access = await transport.RequestAccessAsync();
            Log.Write($"Calls: transport access {access}; device consent {DeviceAccessInformation.CreateFromId(deviceId).CurrentStatus}");
            if (access != DeviceAccessStatus.Allowed)
                throw new UnauthorizedAccessException(access == DeviceAccessStatus.DeniedByUser
                    ? "Windows blocked calls for My Phone. Turn on Settings → Privacy & security → Phone calls, then try again."
                    // Windows hands the calls role to one app per phone.
                    : "Another app, usually Phone Link, handles calls for this phone. Remove the phone in Bluetooth settings, pair it again, then choose it here. Recent calls and contacts still work.");

            // Taking the role is what makes calls ring and play on this PC. Only one app holds it per phone.
            if (!transport.IsRegistered()) transport.RegisterApp();
            var connected = await transport.ConnectAsync();
            Log.Write($"Calls: registered={transport.IsRegistered()} connected={connected}");

            _transport = transport;
            IsConnected = transport.IsRegistered();
            HookCallState();
            StartLineWatcher(deviceId);
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            IsConnected = false;
            throw;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Gives the hands-free role back so the phone stops routing calls to this PC.</summary>
    public void Disconnect()
    {
        try { if (_transport?.IsRegistered() == true) _transport.UnregisterApp(); }
        catch (Exception ex) { Log.Write($"Calls: unregister failed: {ex.Message}"); }
        StopLineWatcher();
        _transport = null;
        _line = null;
        Phone = null;
        IsConnected = false;
        try { File.Delete(SelectionPath); } catch (IOException) { }
        lock (_calls) _calls.Clear();
        Changed?.Invoke();
    }

    public async Task DialAsync(string number, string displayName)
    {
        if (_line is not { } line)
            throw new InvalidOperationException(IsConnected ? "The phone line isn't ready yet. Try again in a moment." : "Connect your phone for calls first.");
        var result = await line.DialWithResultAsync(number, displayName);
        if (result.DialCallStatus != PhoneCallOperationStatus.Succeeded)
            throw new InvalidOperationException($"The call couldn't be placed ({result.DialCallStatus}).");
        if (result.DialedCall is { } call) Track(call, number, displayName, CallDirection.Outgoing);
        Changed?.Invoke();
    }

    public static async Task RunAsync(Func<IAsyncOperation<PhoneCallOperationStatus>> operation, string what)
    {
        var status = await operation();
        if (status != PhoneCallOperationStatus.Succeeded)
            throw new InvalidOperationException($"Couldn't {what} ({status}).");
    }

    // ---------------------------------------------------------------------------------------------

    private void StartLineWatcher(string deviceId)
    {
        StopLineWatcher();
        _ = Task.Run(async () =>
        {
            try
            {
                var store = await PhoneCallManager.RequestStoreAsync();
                var watcher = store.RequestLineWatcher();
                watcher.LineAdded += async (_, args) => await ConsiderLineAsync(args.LineId, deviceId);
                watcher.LineUpdated += async (_, args) => await ConsiderLineAsync(args.LineId, deviceId);
                watcher.LineRemoved += (_, args) =>
                {
                    if (_line?.Id != args.LineId) return;
                    _line = null;
                    Changed?.Invoke();
                };
                _watcher = watcher;
                watcher.Start();
            }
            catch (Exception ex)
            {
                Log.Write($"Calls: line watcher failed: {ex.Message}");
                Problem = "Windows didn't expose a phone line for dialing.";
                Changed?.Invoke();
            }
        });
    }

    private async Task ConsiderLineAsync(Guid lineId, string deviceId)
    {
        try
        {
            var line = await PhoneLine.FromIdAsync(lineId);
            if (line.Transport != PhoneLineTransport.Bluetooth || !SameDevice(line.TransportDeviceId, deviceId)) return;
            if (_line?.Id != line.Id)
            {
                _line = line;
                line.LineChanged += (_, _) => Changed?.Invoke();
                Log.Write("Calls: phone line ready");
            }
            await RefreshCallsAsync();
        }
        catch (Exception ex) { Log.Write($"Calls: line lookup failed: {ex.Message}"); }
        Changed?.Invoke();
    }

    private static bool SameDevice(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void StopLineWatcher()
    {
        try { if (_watcher is { Status: PhoneLineWatcherStatus.Started or PhoneLineWatcherStatus.EnumerationCompleted }) _watcher.Stop(); }
        catch { }
        _watcher = null;
    }

    private void HookCallState()
    {
        if (_callStateHooked) return;
        _callStateHooked = true;
        PhoneCallManager.CallStateChanged += async (_, _) =>
        {
            await RefreshCallsAsync();
            Changed?.Invoke();
        };
    }

    /// <summary>Picks up calls that began on the phone, or rang in, since the last look.</summary>
    private async Task RefreshCallsAsync()
    {
        if (_line is not { } line) return;
        try
        {
            var result = await line.GetAllActivePhoneCallsAsync();
            if (result.OperationStatus != PhoneLineOperationStatus.Succeeded) return;
            foreach (var call in result.AllActivePhoneCalls)
            {
                bool known;
                lock (_calls) known = _calls.ContainsKey(call.CallId);
                if (known) continue;
                var info = await call.GetPhoneCallInfoAsync();
                Track(call, info.PhoneNumber ?? "", info.DisplayName ?? "",
                    info.CallDirection == PhoneCallDirection.Incoming ? CallDirection.Incoming : CallDirection.Outgoing);
            }
        }
        catch (Exception ex) { Log.Write($"Calls: listing active calls failed: {ex.Message}"); }
    }

    private void Track(PhoneCall call, string number, string name, CallDirection direction)
    {
        lock (_calls)
        {
            if (_calls.ContainsKey(call.CallId)) return;
            var status = SafeStatus(call);
            _calls[call.CallId] = (call, status == PhoneCallStatus.Talking ? DateTimeOffset.Now : null, status, number, name, direction);
        }
        call.StatusChanged += (c, _) => OnStatusChanged(c);
        call.IsMutedChanged += (_, _) => Changed?.Invoke();
        call.AudioDeviceChanged += (_, _) => Changed?.Invoke();
    }

    private void OnStatusChanged(PhoneCall call)
    {
        var status = SafeStatus(call);
        (string Number, string Name)? missed = null;
        lock (_calls)
        {
            if (!_calls.TryGetValue(call.CallId, out var entry)) return;
            if (status is PhoneCallStatus.Ended or PhoneCallStatus.Lost)
            {
                if (entry.Direction == CallDirection.Incoming && entry.TalkingSince is null && entry.Last == PhoneCallStatus.Incoming)
                    missed = (entry.Number, entry.Name);
                _calls.Remove(call.CallId);
            }
            else
            {
                var since = entry.TalkingSince ?? (status == PhoneCallStatus.Talking ? DateTimeOffset.Now : null);
                _calls[call.CallId] = entry with { Last = status, TalkingSince = since };
            }
        }
        if (missed is { } m) Missed?.Invoke(m.Number, m.Name);
        Changed?.Invoke();
    }

    private static PhoneCallStatus SafeStatus(PhoneCall call) { try { return call.Status; } catch { return PhoneCallStatus.Lost; } }
    private static bool SafeMuted(PhoneCall call) { try { return call.IsMuted; } catch { return false; } }
    private static PhoneCallAudioDevice SafeAudio(PhoneCall call) { try { return call.AudioDevice; } catch { return PhoneCallAudioDevice.Unknown; } }

    public void Dispose() => StopLineWatcher();
}

/// <summary>
/// Windows 11 gates the Bluetooth calls role behind a Limited Access Feature: apps need a token
/// Microsoft issues for their package family name (request form linked from the LimitedAccessFeatures
/// docs). Without one, RequestAccessAsync answers DeniedBySystem. Put the issued token here.
/// </summary>
internal static class LimitedAccess
{
    public const string FeatureId = "com.microsoft.windows.applicationmodel.phonelinetransportdevice_v1";
    // Unofficial feature key supplied by the user. The token Windows checks is derived from it and this
    // app's package family name, so it is computed at run time.
    private const string FeatureKey = "cb9WIvVfhp+8lFhaSrB6V6zUBGqctteKi/f/9AIeoZ4";

    public static void TryUnlock()
    {
        try
        {
            var family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{FeatureId}!{FeatureKey}!{family}"));
            var token = Convert.ToBase64String(hash, 0, 16);
            var publisherId = family[(family.LastIndexOf('_') + 1)..];
            var attestation = $"{publisherId} has registered their use of {FeatureId} with Microsoft and agrees to the terms of use.";
            var result = Windows.ApplicationModel.LimitedAccessFeatures.TryUnlockFeature(FeatureId, token, attestation);
            Log.Write($"Calls: limited access feature status {result.Status}");
        }
        catch (Exception ex) { Log.Write($"Calls: limited access check failed: {ex.Message}"); }
    }
}
