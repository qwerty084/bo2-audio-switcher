using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Native;

// bo2audio: forward a running game's audio (Steam or Plutonium BO2 by default) to another output device.
// The game keeps rendering to the device it opened at startup; we capture its audio with
// WASAPI process loopback, play it on the chosen device and mute the original device.
// Muting only the game's session does not work, because Windows applies the session volume
// before process loopback captures the audio.
// Nothing here reads or writes the game's memory.

class Device
{
    public IMMDevice Dev;
    public string Id;
    public string Name;

    public T Activate<T>()
    {
        var iid = typeof(T).GUID;
        Dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o);
        return (T)o;
    }
}

static class Audio
{
    static PROPERTYKEY FriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    // 48 kHz stereo float. We open both ends with AUTOCONVERTPCM, so Windows does any conversion.
    public const int SampleRate = 48000;
    public const int BlockAlign = 8;

    public static IntPtr AllocFormat()
    {
        var p = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(p, 0, 3);                          // WAVE_FORMAT_IEEE_FLOAT
        Marshal.WriteInt16(p, 2, 2);                          // channels
        Marshal.WriteInt32(p, 4, SampleRate);
        Marshal.WriteInt32(p, 8, SampleRate * BlockAlign);
        Marshal.WriteInt16(p, 12, BlockAlign);
        Marshal.WriteInt16(p, 14, 32);
        Marshal.WriteInt16(p, 16, 0);
        return p;
    }

    public static List<Device> RenderDevices()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col);
        col.GetCount(out var n);
        var list = new List<Device>();
        for (uint i = 0; i < n; i++)
        {
            col.Item(i, out var dev);
            list.Add(From(dev));
        }
        return list;
    }

    static Device From(IMMDevice dev)
    {
        dev.GetId(out var id);
        dev.OpenPropertyStore(0, out var ps);
        ps.GetValue(ref FriendlyName, out var pv);
        var name = Marshal.PtrToStringUni(pv.pointer) ?? id;
        PropVariantClear(ref pv);
        return new Device { Dev = dev, Id = id, Name = name };
    }

    public static Device DeviceById(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        en.GetDevice(id, out var dev);
        return From(dev);
    }

    // Audio sessions on `device` owned by `pid`. The game keeps idle sessions on devices it
    // is not playing on, so only an active session (`activeOnly`) identifies its real output device.
    public static List<ISimpleAudioVolume> Sessions(Device device, uint pid, bool activeOnly = false)
    {
        var result = new List<ISimpleAudioVolume>();
        device.Activate<IAudioSessionManager2>().GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var n);
        for (int i = 0; i < n; i++)
        {
            sessions.GetSession(i, out var s);
            var control = (IAudioSessionControl2)s;
            if (control.GetProcessId(out var sessionPid) < 0 || sessionPid != pid) continue;
            if (activeOnly) { control.GetState(out var state); if (state != 1) continue; }
            result.Add((ISimpleAudioVolume)s);
        }
        return result;
    }

    // The device the game is really playing on, or null.
    public static Device SourceDevice(List<Device> devices, uint pid) =>
        devices.FirstOrDefault(d => Sessions(d, pid, activeOnly: true).Count > 0);

    // Short quiet confirmation tone on `device`. Runs on its own thread and ignores failures.
    public static void Beep(Device device)
    {
        new Thread(() =>
        {
            var format = AllocFormat();
            try
            {
                int frames = SampleRate * 120 / 1000;
                var samples = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    float fade = Math.Min(1, Math.Min(i, frames - 1 - i) / 240f);   // 5 ms in and out
                    samples[i * 2] = samples[i * 2 + 1] = 0.06f * fade * MathF.Sin(2 * MathF.PI * 880 * i / SampleRate);
                }
                var client = device.Activate<IAudioClient>();
                client.Initialize(0, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000, 0, format, IntPtr.Zero);
                var iid = typeof(IAudioRenderClient).GUID;
                client.GetService(ref iid, out var o);
                var rc = (IAudioRenderClient)o;
                rc.GetBuffer((uint)frames, out var p);
                Marshal.Copy(samples, 0, p, samples.Length);
                rc.ReleaseBuffer((uint)frames, 0);
                client.Start();
                Thread.Sleep(300);
                client.Stop();
                Marshal.ReleaseComObject(rc);
                Marshal.ReleaseComObject(client);
            }
            catch { }
            finally { Marshal.FreeHGlobal(format); }
        }) { IsBackground = true, Name = "beep" }.Start();
    }

    class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public int Result;
        public object Interface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            operation.GetActivateResult(out Result, out Interface);
            Done.Set();
        }
    }

    public static IAudioClient ActivateProcessLoopback(uint pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } } in a VT_BLOB PROPVARIANT
        var blob = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(blob, 0, 1);
        Marshal.WriteInt32(blob, 4, (int)pid);
        Marshal.WriteInt32(blob, 8, 0);
        var pv = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
        Marshal.WriteInt16(pv, 0, 65);
        Marshal.WriteInt32(pv, 8, 12);
        Marshal.WriteIntPtr(pv, 16, blob);
        try
        {
            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out _);
            handler.Done.WaitOne();
            Marshal.ThrowExceptionForHR(handler.Result);
            return (IAudioClient)handler.Interface;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(blob);
        }
    }
}

unsafe class Forwarder
{
    const int CushionFrames = Audio.SampleRate * 15 / 1000;   // silence written when the output runs dry
    const int MaxQueuedFrames = Audio.SampleRate * 60 / 1000; // drop input above this to bound latency

    readonly object gate = new();
    readonly IntPtr format = Audio.AllocFormat();
    readonly AutoResetEvent captureEvent = new(false);
    IAudioClient capture;
    IAudioCaptureClient captureClient;
    IAudioClient render;
    IAudioRenderClient renderClient;
    uint renderBufferFrames;
    Thread thread;
    volatile bool running;

    public float CapturePeak;   // peak of captured samples since the last read
    public volatile bool Audible;   // non-silent audio has been captured

    public void Start(uint pid)
    {
        capture = Audio.ActivateProcessLoopback(pid);
        capture.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
            200000, 0, format, IntPtr.Zero);
        capture.SetEventHandle(captureEvent.SafeWaitHandle.DangerousGetHandle());
        var iid = typeof(IAudioCaptureClient).GUID;
        capture.GetService(ref iid, out var o);
        captureClient = (IAudioCaptureClient)o;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "forward" };
        thread.Start();
        capture.Start();
    }

    // Pass null to stop forwarding.
    public void SetTarget(Device device)
    {
        lock (gate)
        {
            if (render != null)
            {
                try { render.Stop(); } catch { }
                Marshal.ReleaseComObject(renderClient);
                Marshal.ReleaseComObject(render);
                render = null; renderClient = null;
            }
            if (device == null) return;
            var client = device.Activate<IAudioClient>();
            client.Initialize(0, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                1000000, 0, format, IntPtr.Zero);
            client.GetBufferSize(out renderBufferFrames);
            var iid = typeof(IAudioRenderClient).GUID;
            client.GetService(ref iid, out var o);
            renderClient = (IAudioRenderClient)o;
            render = client;
            render.Start();
        }
    }

    public void Stop()
    {
        running = false;
        captureEvent.Set();
        thread?.Join(1000);
        try { capture?.Stop(); } catch { }
        SetTarget(null);
    }

    void Loop()
    {
        while (running)
        {
            captureEvent.WaitOne(200);
            while (running && captureClient.GetNextPacketSize(out var next) >= 0 && next > 0)
            {
                if (captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) break;
                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent) TrackPeak((float*)data, (int)frames * 2);
                lock (gate)
                {
                    if (render != null)
                    {
                        try { Write(data, frames, silent); }
                        catch (COMException) { /* target device vanished; keep capturing */ }
                    }
                }
                captureClient.ReleaseBuffer(frames);
            }
        }
    }

    void Write(IntPtr data, uint frames, bool silent)
    {
        render.GetCurrentPadding(out var queued);
        if (queued == 0)
        {
            renderClient.GetBuffer(CushionFrames, out _);
            renderClient.ReleaseBuffer(CushionFrames, AUDCLNT_BUFFERFLAGS_SILENT);
            queued = CushionFrames;
        }
        if (queued > MaxQueuedFrames) return;
        uint n = Math.Min(frames, renderBufferFrames - queued);
        if (n == 0) return;
        renderClient.GetBuffer(n, out var dst);
        if (!silent) Buffer.MemoryCopy((void*)data, (void*)dst, n * Audio.BlockAlign, n * Audio.BlockAlign);
        renderClient.ReleaseBuffer(n, silent ? AUDCLNT_BUFFERFLAGS_SILENT : 0);
    }

    void TrackPeak(float* samples, int count)
    {
        float peak = CapturePeak;
        for (int i = 0; i < count; i++)
        {
            float v = Math.Abs(samples[i]);
            if (v > peak) peak = v;
        }
        CapturePeak = peak;
        if (peak > 0) Audible = true;
    }
}

// A game attached for forwarding. Captures the game's audio, plays it on the chosen device and
// mutes the game's own device in the meantime. Records the muted device in MuteState, so the next
// start can undo a killed run, and undoes a record left by an earlier run when it starts.
class Session : IDisposable
{
    public readonly uint Pid;
    public readonly Device Source;
    public readonly Forwarder Forwarder = new();
    public Device Target { get; private set; }   // null while the game plays on its own device

    readonly IAudioEndpointVolume sourceVolume;
    bool muted, weMuted;
    int disposed;

    public Session(uint pid, Device source)
    {
        Pid = pid;
        Source = source;
        try { MuteState.Recover(); } catch { }
        sourceVolume = source.Activate<IAudioEndpointVolume>();
        Forwarder.Start(pid);
    }

    // null or the game's own device stops forwarding. On failure it stops forwarding and rethrows.
    public void SwitchTo(Device device)
    {
        if (device?.Id == Source.Id) device = null;
        try
        {
            Forwarder.SetTarget(device);
            Mute(device != null);
            Target = device;
        }
        catch
        {
            Target = null;
            try { Forwarder.SetTarget(null); Mute(false); } catch { }
            throw;
        }
    }

    void Mute(bool mute)
    {
        if (mute == muted) return;
        var ctx = Guid.Empty;
        if (mute)
        {
            // If the user muted the device already, leave it muted, also after forwarding stops.
            sourceVolume.GetMute(out var already);
            if (!already)
            {
                MuteState.Set(Source.Id);
                weMuted = true;
                sourceVolume.SetMute(true, ref ctx);
            }
        }
        else if (weMuted)
        {
            sourceVolume.SetMute(false, ref ctx);
            weMuted = false;
            MuteState.Set(null);
        }
        muted = mute;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Forwarder.Stop(); } catch { }
        try { Mute(false); } catch { }
    }
}

// %AppData%\bo2audio\settings.json
class Settings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+F11";
    public List<string> SkipInCycle { get; set; } = [];   // device ids the hotkey skips
    public bool HotkeyEnabled { get; set; } = true;
    public bool Beep { get; set; } = true;                // beep after a hotkey switch

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bo2audio");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            s.Hotkey ??= "Ctrl+Alt+F11";
            s.SkipInCycle ??= [];
            return s;
        }
        catch { return new(); }
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }
}

// Id of the device a Session has muted, kept in %AppData%\bo2audio\muted.txt while it is muted.
static class MuteState
{
    static readonly string FilePath = Path.Combine(Settings.Dir, "muted.txt");

    public static void Set(string deviceId)
    {
        try
        {
            if (deviceId == null) File.Delete(FilePath);
            else { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(FilePath, deviceId); }
        }
        catch { }
    }

    // Unmutes the recorded device and forgets it. Returns its name, or null if nothing was recorded.
    // Throws and keeps the record if Windows cannot find the device, for example while it is unplugged.
    public static string Recover()
    {
        string id;
        try { id = File.ReadAllText(FilePath).Trim(); } catch { return null; }
        if (id.Length == 0) { Set(null); return null; }
        var device = Audio.DeviceById(id);
        var ctx = Guid.Empty;
        device.Activate<IAudioEndpointVolume>().SetMute(false, ref ctx);
        Set(null);
        return device.Name;
    }
}

static class Games
{
    // Steam zombies, Steam multiplayer, and Plutonium (one bootstrapper exe for all its games).
    public static readonly (string Process, string Title)[] Known =
    [
        ("t6zm", "Black Ops 2 Zombies (Steam)"),
        ("t6mp", "Black Ops 2 Multiplayer (Steam)"),
        ("plutonium-bootstrapper-win32", "Plutonium"),
    ];

    // The first running known game. Pid 0 means none is running.
    public static (uint Pid, string Process) Find()
    {
        foreach (var name in Known.Select(g => g.Process))
        {
            var found = Process.GetProcessesByName(name);
            uint pid = found.Length > 0 ? (uint)found[0].Id : 0;
            foreach (var p in found) p.Dispose();
            if (pid != 0) return (pid, name);
        }
        return (0, null);
    }

    public static string Title(string process) =>
        Known.FirstOrDefault(g => g.Process == process).Title ?? process;
}

static class Hotkey
{
    // Turns "Ctrl+Alt+F11" into RegisterHotKey modifiers and a virtual key.
    public static (uint mods, uint vk) Parse(string hotkey)
    {
        uint mods = 0, vk = 0;
        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries))
        {
            var k = part.ToUpperInvariant();
            if (k == "ALT") mods |= 1;
            else if (k is "CTRL" or "CONTROL") mods |= 2;
            else if (k == "SHIFT") mods |= 4;
            else if (k == "WIN") mods |= 8;
            else if (k.Length > 1 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
            else if (k.Length == 1 && char.IsLetterOrDigit(k[0])) vk = k[0];
            else throw new ArgumentException($"bad hotkey part '{part}'");
        }
        if (vk == 0) throw new ArgumentException($"hotkey '{hotkey}' has no key");
        return (mods | 0x4000, vk);   // MOD_NOREPEAT
    }
}