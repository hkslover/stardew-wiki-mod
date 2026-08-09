using StardewModdingAPI;
using StardewValley;
using StardewWikiAgent.Threading;

namespace StardewWikiAgent.Speech;

/// <summary>
/// Push-to-talk voice input. Pressing the hotkey begins recording and releasing it ends
/// recording and starts transcription. Public state-machine methods must be called on the
/// game thread; background transcription communicates through a small locked result slot.
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
    private readonly object sync = new();

    private VoiceState state = VoiceState.Idle;
    private bool dropTranscriptionResult;
    private bool transcriptionInProgress;
    private bool transcriptionCompleted;
    private bool isDisposed;
    private string completedText = string.Empty;

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

    public bool IsTranscribing => this.state == VoiceState.Transcribing;

    /// <summary>Begin push-to-talk recording. Must be called on the game thread.</summary>
    /// <returns>Whether a new recording was started.</returns>
    public bool BeginRecording()
    {
        return this.BeginRecording(toggleMode: false);
    }

    /// <summary>
    /// End push-to-talk recording and start transcription. Must be called on the game thread.
    /// </summary>
    /// <returns>Whether an active recording was ended.</returns>
    public bool EndRecording()
    {
        if (this.isDisposed || this.state != VoiceState.Recording)
            return false;

        this.FinishRecording(reachedTimeLimit: false);
        return true;
    }

    /// <summary>
    /// Maintain automatic time-limit and world-state handling. Call once per update tick on
    /// the game thread, after draining the main-thread dispatcher.
    /// </summary>
    public void Update()
    {
        if (this.isDisposed)
            return;

        this.ProcessCompletedTranscription();

        if (!Context.IsWorldReady)
        {
            this.Cancel(notify: false);
            return;
        }

        if (this.state == VoiceState.Recording
            && (!Context.IsPlayerFree
                || Game1.activeClickableMenu is not null
                || Game1.chatBox?.isActive() == true))
        {
            this.Cancel();
            return;
        }

        if (this.state == VoiceState.Recording && this.recorder.HasReachedMaxDuration)
            this.FinishRecording(reachedTimeLimit: true);
    }

    /// <summary>
    /// Compatibility helper for an optional press-once-to-start, press-again-to-stop mode.
    /// The default push-to-talk flow should use <see cref="BeginRecording"/> and
    /// <see cref="EndRecording"/> directly.
    /// </summary>
    public void Toggle()
    {
        switch (this.state)
        {
            case VoiceState.Idle:
                this.BeginRecording(toggleMode: true);
                break;

            case VoiceState.Recording:
                this.EndRecording();
                break;

            case VoiceState.Transcribing:
                Notify("正在识别中，请稍候…");
                break;
        }
    }

    /// <summary>
    /// Stop an active recording without transcribing, or discard the result of an in-progress
    /// transcription. This is safe to call when leaving a save or returning to the title screen.
    /// </summary>
    public void Cancel(bool notify = true)
    {
        if (this.isDisposed)
            return;

        switch (this.state)
        {
            case VoiceState.Recording:
                this.recorder.Stop();
                this.state = VoiceState.Idle;
                if (notify && Context.IsWorldReady)
                    Notify("已取消语音录制。");
                break;

            case VoiceState.Transcribing:
                lock (this.sync)
                    this.dropTranscriptionResult = true;
                if (notify && Context.IsWorldReady)
                    Notify("已取消语音识别。");
                break;
        }
    }

    private bool BeginRecording(bool toggleMode)
    {
        if (this.isDisposed || !Context.IsWorldReady || !Context.IsPlayerFree)
            return false;

        switch (this.state)
        {
            case VoiceState.Recording:
                return false;

            case VoiceState.Transcribing:
                Notify("正在识别中，请稍候…");
                return false;
        }

        if (!this.recorder.Start())
        {
            Notify("无法启动麦克风，请检查系统录音权限。");
            return false;
        }

        this.state = VoiceState.Recording;
        Notify(toggleMode
            ? $"[录音中] 再次按 {this.hotkeyLabel} 结束"
            : $"[录音中] 松开 {this.hotkeyLabel} 后开始识别");
        return true;
    }

    private void FinishRecording(bool reachedTimeLimit)
    {
        float[] samples = this.recorder.Stop();
        if (samples.Length < MinSamples)
        {
            this.state = VoiceState.Idle;
            Notify("录音太短，没有识别到语音。");
            return;
        }

        this.state = VoiceState.Transcribing;
        lock (this.sync)
        {
            this.dropTranscriptionResult = false;
            this.transcriptionInProgress = true;
            this.transcriptionCompleted = false;
            this.completedText = string.Empty;
        }

        Notify(reachedTimeLimit
            ? "已达到最长录音时间，正在识别语音…"
            : "正在识别语音…");
        this.TranscribeAsync(samples);
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

            bool shouldDisposeTranscriber;
            lock (this.sync)
            {
                this.completedText = text;
                this.transcriptionCompleted = true;
                this.transcriptionInProgress = false;
                shouldDisposeTranscriber = this.isDisposed;
            }

            if (shouldDisposeTranscriber)
                this.transcriber.Dispose();
            else
                this.mainThread.Enqueue(this.ProcessCompletedTranscription);
        });
    }

    /// <summary>Consume a completed background result. Must be called on the game thread.</summary>
    private void ProcessCompletedTranscription()
    {
        string text;
        bool shouldDrop;
        lock (this.sync)
        {
            if (!this.transcriptionCompleted)
                return;

            text = this.completedText;
            shouldDrop = this.dropTranscriptionResult
                || this.isDisposed
                || !Context.IsWorldReady
                || !Context.IsPlayerFree
                || Game1.activeClickableMenu is not null
                || Game1.chatBox?.isActive() == true;
            this.completedText = string.Empty;
            this.transcriptionCompleted = false;
            this.dropTranscriptionResult = false;
        }

        this.state = VoiceState.Idle;
        if (shouldDrop)
            return;

        if (text.Length == 0)
        {
            Notify("没有识别到有效语音，请重试。");
            return;
        }

        this.onTranscribed(text);
    }

    private static void Notify(string message)
    {
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox(message));
    }

    public void Dispose()
    {
        if (this.isDisposed)
            return;

        this.Cancel(notify: false);
        bool disposeTranscriberNow;
        lock (this.sync)
        {
            this.isDisposed = true;
            this.dropTranscriptionResult = true;
            this.completedText = string.Empty;
            this.transcriptionCompleted = false;
            disposeTranscriberNow = !this.transcriptionInProgress;
        }

        this.state = VoiceState.Idle;
        this.recorder.Dispose();
        if (disposeTranscriberNow)
            this.transcriber.Dispose();
    }
}
