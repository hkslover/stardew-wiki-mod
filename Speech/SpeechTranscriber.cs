using SherpaOnnx;
using StardewModdingAPI;

namespace StardewWikiAgent.Speech;

/// <summary>
/// Wraps a sherpa-onnx streaming Zipformer2-CTC recognizer for offline (push-to-talk)
/// Chinese speech-to-text. The model is streaming, but we feed the whole utterance at once
/// and finalize it, which is the right fit for a "record then transcribe" flow.
/// All methods are safe to call from a background thread; none touch game state.
/// </summary>
internal sealed class SpeechTranscriber : IDisposable
{
    /// <summary>The sample rate the model expects (16 kHz mono).</summary>
    public const int SampleRate = 16000;

    private readonly OnlineRecognizer recognizer;
    private readonly IMonitor monitor;

    public SpeechTranscriber(string modelPath, string tokensPath, int numThreads, IMonitor monitor)
    {
        this.monitor = monitor;

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Zipformer2Ctc.Model = modelPath;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = Math.Max(1, numThreads);
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        // We hand over a complete utterance, so endpoint detection is unnecessary.
        config.EnableEndpoint = 0;

        this.recognizer = new OnlineRecognizer(config);
    }

    /// <summary>Transcribe a complete mono 16 kHz float PCM buffer to text. Runs on a background thread.</summary>
    public string Transcribe(float[] samples)
    {
        using OnlineStream stream = this.recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        // Tail padding of silence flushes the last frames out of the streaming decoder.
        stream.AcceptWaveform(SampleRate, new float[(int)(SampleRate * 0.6)]);
        stream.InputFinished();

        while (this.recognizer.IsReady(stream))
            this.recognizer.Decode(stream);

        return this.recognizer.GetResult(stream).Text ?? string.Empty;
    }

    public void Dispose()
    {
        try { this.recognizer.Dispose(); }
        catch (Exception ex) { this.monitor.Log("Failed to dispose the speech recognizer: " + ex, LogLevel.Trace); }
    }
}
