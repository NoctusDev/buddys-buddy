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
    public float Peak => peak;
    public float MusicGain { set => musicGain = value; }
    public float MicGain { set => micGain = value; }
    public event Action<string>? Failed;

    public AudioBroadcaster(AudioDevice source, string? microphone, Action<byte[]> send)
    {
        this.send = send;
        encoder.Bitrate = 128000;
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
        mixer.AddMixerInput(new GainProvider(samples, () => microphone ? micGain : musicGain));
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
    private bool disposed;
    public float Volume { set { lock (gate) if (!disposed) output.Volume = Math.Clamp(value, 0, 1); } }
    public AudioListener(float volume)
    {
        try { output.Init(buffer); output.Volume = volume; output.Play(); }
        catch { output.Dispose(); throw; }
    }
    public void Receive(byte[] packet)
    {
        lock (gate)
        {
            if (disposed) return;
            int samples = decoder.Decode(packet, 0, packet.Length, pcm, 0, 5760, false);
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
