using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VenueAudio;

public sealed record AudioDevice(string Id, string Name, bool IsOutput);

public static class AudioDevices
{
    public static List<AudioDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) devices.Add(new AudioDevice(device.ID, device.FriendlyName, flow == DataFlow.Render));
            }
        return devices;
    }
}

/// <summary>Native WASAPI capture. No monitoring: capturing and playing the same endpoint would feed back.</summary>
public sealed class AudioBroadcaster : IDisposable
{
    private readonly List<WasapiCapture> captures = [];
    private readonly List<MMDevice> devices = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly MixingSampleProvider mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
    private readonly OpusEncoder encoder = OpusEncoder.Create(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
    private readonly Action<byte[]> send;
    private Task? pumping;
    private volatile float peak;
    private volatile float musicGain = 1, micGain = 1;
    private volatile bool musicMuted, micMuted;
    public float Peak => peak;
    public float MusicGain { set => musicGain = value; }
    public float MicGain { set => micGain = value; }
    public bool MusicMuted { set => musicMuted = value; }
    public bool MicMuted { set => micMuted = value; }
    public event Action<string>? Failed;

    public AudioBroadcaster(AudioDevice source, string? microphone, Action<byte[]> send, int bitrate = 128000)
    {
        this.send = send;
        encoder.Bitrate = bitrate;
        encoder.Complexity = 5;
        try
        {
            AddSource(source.Id, source.IsOutput, false);
            if (!string.IsNullOrEmpty(microphone)) AddSource(microphone, false, true);
        }
        catch { Dispose(); throw; }
    }
    private void AddSource(string id, bool output, bool microphone)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(id); devices.Add(device);
        WasapiCapture capture = output ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        captures.Add(capture);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500), ReadFully = true, DiscardOnBufferOverflow = true,
        };
        capture.DataAvailable += (_, args) =>
        {
            if (lifetime.IsCancellationRequested) return;
            // Bound catch-up latency if capture briefly outruns the encoder.
            if (buffer.BufferedDuration.TotalMilliseconds > 250) buffer.ClearBuffer();
            buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
        };
        capture.RecordingStopped += (_, args) =>
        {
            if (!lifetime.IsCancellationRequested)
                Failed?.Invoke(args.Exception?.Message ?? "Audio source stopped. Choose an active device and broadcast again.");
        };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2)
        {
            // Explicit downmix of multichannel device output using the Windows resampler.
            var resampler = new MediaFoundationResampler(samples.ToWaveProvider(), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            extraResources.Add(resampler);
            samples = resampler.ToSampleProvider();
        }
        if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
        mixer.AddMixerInput(new GainProvider(samples, () => microphone ? (micMuted ? 0 : micGain) : (musicMuted ? 0 : musicGain)));
    }
    private readonly List<IDisposable> extraResources = [];
    public void Start()
    {
        foreach (var capture in captures) capture.StartRecording();
        pumping = Task.Run(PumpAsync);
    }
    private async Task PumpAsync()
    {
        var floats = new float[1920];
        var pcm = new short[1920];
        var encoded = new byte[1500];
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                mixer.Read(floats, 0, floats.Length);
                float level = 0;
                for (int i = 0; i < floats.Length; i++)
                {
                    float sample = Math.Clamp(floats[i], -1, 1);
                    level = Math.Max(level, Math.Abs(sample));
                    pcm[i] = (short)(sample * 32767);
                }
                peak = level;
                int size = encoder.Encode(pcm, 0, 960, encoded, 0, encoded.Length);
                send(encoded.AsSpan(0, size).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Failed?.Invoke("Audio encoding stopped: " + e.Message); }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var capture in captures) { try { capture.StopRecording(); } catch { } }
        try { pumping?.GetAwaiter().GetResult(); } catch { }
        foreach (var capture in captures) capture.Dispose();
        foreach (var extra in extraResources) extra.Dispose();
        foreach (var device in devices) device.Dispose();
        lifetime.Dispose();
    }
    private sealed class GainProvider(ISampleProvider source, Func<float> gain) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            float volume = gain();
            for (int i = offset; i < offset + read; i++) buffer[i] *= volume;
            return read;
        }
    }
}

public sealed class AudioListener : IDisposable
{
    private readonly object gate = new();
    private readonly OpusDecoder decoder = OpusDecoder.Create(48000, 2);
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48000, 16, 2))
    {
        BufferDuration = TimeSpan.FromMilliseconds(500), ReadFully = true, DiscardOnBufferOverflow = true,
    };
    private readonly WaveOutEvent output = new() { DesiredLatency = 120 };
    private readonly short[] pcm = new short[11520];
    private readonly byte[] bytes = new byte[23040];
    private bool disposed, muted;
    private float volume;
    private volatile float peak;
    // What arrives, before volume or mute, so a muted test listener still shows the stream is flowing.
    public float Peak => peak;
    public float Volume { set { lock (gate) { volume = Math.Clamp(value, 0, 1); Apply(); } } }
    public bool Muted { set { lock (gate) { muted = value; Apply(); } } }
    public AudioListener(float volume, bool muted = false)
    {
        this.volume = Math.Clamp(volume, 0, 1); this.muted = muted;
        try { output.Init(buffer); Apply(); output.Play(); }
        catch { output.Dispose(); throw; }
    }
    private void Apply() { if (!disposed) output.Volume = muted ? 0 : volume; }
    public void Receive(byte[] packet)
    {
        lock (gate)
        {
            if (disposed) return;
            int samples = decoder.Decode(packet, 0, packet.Length, pcm, 0, 5760, false);
            int loudest = 0;
            for (int i = 0; i < samples * 2; i++) loudest = Math.Max(loudest, Math.Abs((int)pcm[i]));
            peak = loudest / 32768f;
            int length = samples * 2 * sizeof(short);
            Buffer.BlockCopy(pcm, 0, bytes, 0, length);
            if (buffer.BufferedDuration.TotalMilliseconds > 250) buffer.ClearBuffer();
            buffer.AddSamples(bytes, 0, length);
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; output.Stop(); output.Dispose(); buffer.ClearBuffer(); }
    }
}
