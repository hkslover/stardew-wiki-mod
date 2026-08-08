using System.Runtime.InteropServices;
using PortAudioSharp;
using StardewModdingAPI;

namespace StardewWikiAgent.Speech;

/// <summary>
/// Captures mono 16 kHz float PCM from the default input device via PortAudio.
/// Start/Stop are meant to be driven from the game's main thread; the audio callback
/// runs on PortAudio's own high-priority thread and only appends to a locked buffer.
/// </summary>
internal sealed class VoiceRecorder : IDisposable
{
    private readonly IMonitor monitor;
    private readonly int sampleRate;
    private readonly int maxSamples;
    private readonly object sync = new();
    private readonly List<float> buffer = new();

    // Kept as a field so the delegate is not garbage-collected while the native stream holds it.
    private readonly PortAudioSharp.Stream.Callback callback;

    private PortAudioSharp.Stream? stream;
    private bool portAudioReady;

    public bool IsAvailable { get; private set; }
    public bool IsRecording { get; private set; }

    public VoiceRecorder(int sampleRate, int maxSeconds, IMonitor monitor)
    {
        this.monitor = monitor;
        this.sampleRate = sampleRate;
        this.maxSamples = sampleRate * Math.Max(1, maxSeconds);
        this.callback = this.OnAudio;

        try
        {
            PortAudio.Initialize();
            this.portAudioReady = true;

            if (PortAudio.DefaultInputDevice == PortAudio.NoDevice)
            {
                this.monitor.Log("No microphone (input device) found; voice input is disabled.", LogLevel.Warn);
                return;
            }

            this.IsAvailable = true;
        }
        catch (Exception ex)
        {
            this.monitor.Log("Failed to initialize PortAudio; voice input is disabled: " + ex, LogLevel.Warn);
        }
    }

    private StreamCallbackResult OnAudio(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input != IntPtr.Zero && frameCount > 0)
        {
            var samples = new float[frameCount];
            Marshal.Copy(input, samples, 0, (int)frameCount);
            lock (this.sync)
            {
                if (this.buffer.Count < this.maxSamples)
                    this.buffer.AddRange(samples);
            }
        }

        return StreamCallbackResult.Continue;
    }

    /// <summary>Begin capturing. Returns false if unavailable or already recording.</summary>
    public bool Start()
    {
        if (!this.IsAvailable || this.IsRecording)
            return false;

        try
        {
            lock (this.sync)
                this.buffer.Clear();

            int device = PortAudio.DefaultInputDevice;
            DeviceInfo info = PortAudio.GetDeviceInfo(device);
            var param = new StreamParameters
            {
                device = device,
                channelCount = 1,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = info.defaultLowInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            this.stream = new PortAudioSharp.Stream(
                inParams: param,
                outParams: null,
                sampleRate: this.sampleRate,
                framesPerBuffer: 0,
                streamFlags: StreamFlags.ClipOff,
                callback: this.callback,
                userData: IntPtr.Zero);
            this.stream.Start();
            this.IsRecording = true;
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log("Failed to start recording: " + ex, LogLevel.Error);
            this.SafeCloseStream();
            this.IsRecording = false;
            return false;
        }
    }

    /// <summary>Stop capturing and return the recorded samples (empty if nothing was captured).</summary>
    public float[] Stop()
    {
        if (!this.IsRecording)
            return Array.Empty<float>();

        this.IsRecording = false;
        this.SafeCloseStream();

        lock (this.sync)
            return this.buffer.ToArray();
    }

    private void SafeCloseStream()
    {
        try
        {
            this.stream?.Stop();
            this.stream?.Dispose();
        }
        catch (Exception ex)
        {
            this.monitor.Log("Failed to close the audio stream cleanly: " + ex, LogLevel.Trace);
        }
        finally
        {
            this.stream = null;
        }
    }

    public void Dispose()
    {
        this.SafeCloseStream();
        this.IsRecording = false;
        this.IsAvailable = false;

        if (this.portAudioReady)
        {
            try { PortAudio.Terminate(); }
            catch (Exception ex) { this.monitor.Log("Failed to terminate PortAudio: " + ex, LogLevel.Trace); }
            this.portAudioReady = false;
        }
    }
}
