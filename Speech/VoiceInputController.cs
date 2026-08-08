using StardewModdingAPI;
using StardewValley;
using StardewWikiAgent.Threading;

namespace StardewWikiAgent.Speech;

/// <summary>
/// Push-to-talk voice input: the hotkey toggles between recording and transcribing.
/// The whole state machine lives on the game's main thread — <see cref="Toggle"/> is called
/// from the SMAPI input event, and transcription results are marshaled back through the
/// <see cref="MainThreadDispatcher"/> before advancing state — so no locking is needed here.
/// </summary>
internal sealed class VoiceInputController : IDisposable
{
    private enum VoiceState { Idle, Recording, Transcribing }

    // Ignore accidental sub-second taps that capture no real speech.
    private const int MinSamples = SpeechTranscriber.SampleRate / 2;

    private readonly VoiceRecorder recorder;
    private readonly SpeechTranscriber transcriber;
    private readonly MainThreadDispatcher mainThread;
    private readonly IMonitor monitor;
    private readonly Action<string> onTranscribed;
    private readonly string hotkeyLabel;

    private VoiceState state = VoiceState.Idle;

    public VoiceInputController(
        VoiceRecorder recorder,
        SpeechTranscriber transcriber,
        MainThreadDispatcher mainThread,
        IMonitor monitor,
        string hotkeyLabel,
        Action<string> onTranscribed)
    {
        this.recorder = recorder;
        this.transcriber = transcriber;
        this.mainThread = mainThread;
        this.monitor = monitor;
        this.hotkeyLabel = hotkeyLabel;
        this.onTranscribed = onTranscribed;
    }

    public bool IsAvailable => this.recorder.IsAvailable;

    public bool IsRecording => this.state == VoiceState.Recording;

    /// <summary>Advance the push-to-talk state machine. Must be called on the main thread.</summary>
    public void Toggle()
    {
        switch (this.state)
        {
            case VoiceState.Idle:
                if (!this.recorder.Start())
                {
                    Notify("无法启动麦克风，请检查系统录音权限。");
                    return;
                }
                this.state = VoiceState.Recording;
                Notify($"[录音中] 再次按 {this.hotkeyLabel} 结束");
                break;

            case VoiceState.Recording:
                float[] samples = this.recorder.Stop();
                if (samples.Length < MinSamples)
                {
                    this.state = VoiceState.Idle;
                    Notify("录音太短，没有识别到语音。");
                    return;
                }
                this.state = VoiceState.Transcribing;
                Notify("正在识别语音…");
                this.TranscribeAsync(samples);
                break;

            case VoiceState.Transcribing:
                Notify("正在识别中，请稍候…");
                break;
        }
    }

    private void TranscribeAsync(float[] samples)
    {
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                text = this.transcriber.Transcribe(samples).Trim();
            }
            catch (Exception ex)
            {
                this.monitor.Log("Speech transcription failed: " + ex, LogLevel.Error);
                text = string.Empty;
            }

            this.mainThread.Enqueue(() =>
            {
                this.state = VoiceState.Idle;
                if (text.Length == 0)
                {
                    Notify("没有识别到有效语音，请重试。");
                    return;
                }
                this.onTranscribed(text);
            });
        });
    }

    private static void Notify(string message)
    {
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox(message));
    }

    public void Dispose()
    {
        this.recorder.Dispose();
        this.transcriber.Dispose();
    }
}
