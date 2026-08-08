using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewWikiAgent.Agent;
using StardewWikiAgent.Api;
using StardewWikiAgent.Chat;
using StardewWikiAgent.Config;
using StardewWikiAgent.Game;
using StardewWikiAgent.Speech;
using StardewWikiAgent.Threading;
using StardewWikiAgent.Wiki;

namespace StardewWikiAgent;

/// <summary>The SMAPI entry point for the Stardew Wiki AI Agent.</summary>
internal sealed class ModEntry : Mod
{
    private MainThreadDispatcher mainThread = null!;
    private SemaphoreSlim requestGate = new(1, 1);
    private CancellationTokenSource requestCancellation = new();
    private ModConfig config = new();
    private AgentRunner? agent;
    private AgentToolRegistry? tools;
    private IStardewWikiAgentApi? api;
    private EasterEggGreeter? greeter;
    private NavigationService navigation = null!;
    private int navigationGeneration;
    private VoiceInputController? voice;
    private SButton voiceHotkey = SButton.V;

    public override void Entry(IModHelper helper)
    {
        this.mainThread = new MainThreadDispatcher(this.Monitor);
        this.config = helper.ReadConfig<ModConfig>();
        helper.WriteConfig(this.config);

        var settings = AgentSettings.From(this.config, this.Monitor);
        var wiki = new MediaWikiClient(settings.WikiApiUrl, settings.RequestTimeout);
        this.tools = new AgentToolRegistry(this.Monitor, this.mainThread);
        this.tools.Register(new WikiSearchTool(wiki));
        this.tools.Register(new WikiReadTool(wiki));
        this.tools.Register(new InventoryTool());
        this.tools.Register(new PlayerStatusTool());
        this.tools.Register(new RelationshipsTool());
        if (this.config.EnableQuestLogTool)
            this.tools.Register(new QuestLogTool());
        this.tools.Register(new WorldMapLocationTool());

        this.agent = new AgentRunner(settings, this.tools, this.Monitor);
        this.api = new StardewWikiAgentApi(this.agent, this.tools, this.mainThread);
        this.navigation = new NavigationService(this.Monitor);

        this.RegisterChatCommand();
        helper.ConsoleCommands.Add(
            "swai_status",
            "Show the Stardew Wiki AI Agent configuration status (without secrets).",
            (_, _) => this.LogStatus(settings)
        );
        helper.ConsoleCommands.Add(
            "swai_ask",
            "Ask the AI agent from the SMAPI console (useful for diagnostics).",
            (_, args) => this.HandleConsoleAsk(args)
        );

        this.greeter = new EasterEggGreeter(this.Monitor);

        if (this.config.EnableVoiceInput)
            this.InitializeVoiceAsync();

        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.greeter.OnDayStarted;
        helper.Events.GameLoop.TimeChanged += this.greeter.OnTimeChanged;
        helper.Events.Display.RenderedWorld += this.navigation.Render;

        this.Monitor.Log(
            $"Loaded AI agent core (model: {settings.Model}, Wiki: {settings.WikiApiUrl}). " +
            "Use /ask <问题> in the in-game chat.",
            LogLevel.Info
        );
        if (!settings.IsConfigured)
        {
            this.Monitor.Log(
                "LLM is not configured yet. Set OPENAI_BASE_URL/OPENAI_API_KEY/OPENAI_MODEL " +
                "or edit the generated config.json in the mod folder.",
                LogLevel.Warn
            );
        }
    }

    /// <summary>Expose the extension API to other SMAPI mods.</summary>
    public override object? GetApi()
    {
        return this.api;
    }

    private void RegisterChatCommand()
    {
        string canonical = $"{this.ModManifest.UniqueID}_ask";
        if (ChatCommands.Exists(canonical))
        {
            this.Monitor.Log($"Chat command /{canonical} is already registered.", LogLevel.Error);
            return;
        }

        // Stardew 1.6.15 supports aliases. The canonical command remains available
        // even if another mod already owns the short /ask name.
        string[] aliases = ChatCommands.Exists("ask") ? Array.Empty<string>() : new[] { "ask" };
        ChatCommands.Register(
            canonical,
            this.HandleAsk,
            name => $"{name} <问题>: 查询中文 Stardew Valley Wiki（也可使用 /ask）",
            mainOnly: false,
            multiplayerOnly: false,
            cheatsOnly: false,
            aliases: aliases
        );

        this.Monitor.Log(
            aliases.Length > 0
                ? "Registered chat commands /ask and /" + canonical + "."
                : "Registered chat command /" + canonical + "; /ask is already in use.",
            LogLevel.Info
        );
    }

    private void HandleAsk(string[] command, ChatBox chat)
    {
        string question = string.Join(" ", command.Skip(1)).Trim();
        if (question.Length == 0)
        {
            chat.addInfoMessage("用法：/ask 你的问题，例如：/ask 这个季节适合种什么？");
            return;
        }

        if (question.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref this.navigationGeneration);
            bool stopped = this.navigation.Stop();
            chat.addInfoMessage(stopped ? "已停止当前导航。" : "当前没有正在进行的导航。");
            return;
        }

        this.StartAskRequest(question, chat);
    }

    /// <summary>Run the agent for a question and present the answer. Shared by the chat command and voice input.</summary>
    private void StartAskRequest(string question, ChatBox chat)
    {
        if (this.agent is null)
        {
            chat.addErrorMessage("AI 助手尚未初始化，请查看 SMAPI 日志。");
            return;
        }

        if (!Context.IsWorldReady)
        {
            chat.addErrorMessage("请先载入一个存档，再使用 /ask。");
            return;
        }

        if (!this.requestGate.Wait(0))
        {
            chat.addInfoMessage("上一条问题还在查询中，请稍等片刻。");
            return;
        }

        GameContextSnapshot context = GameContextSnapshot.Capture();
        int navigationGeneration = Volatile.Read(ref this.navigationGeneration);
        chat.addInfoMessage("正在分析并检索 Wiki…");
        CancellationToken requestToken = this.requestCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                AgentAnswer answer = await this.agent.AskAsync(question, context, requestToken);
                this.mainThread.Enqueue(() =>
                {
                    if (answer.NavigationTarget is not null
                        && navigationGeneration == Volatile.Read(ref this.navigationGeneration))
                        this.navigation.Start(answer.NavigationTarget);
                    ChatAnswerPresenter.Show(chat, answer.Text, answer.Sources);
                });
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                this.Monitor.Log("AI request cancelled.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                this.Monitor.Log("AI request failed: " + ex, LogLevel.Error);
                this.mainThread.Enqueue(() => ChatAnswerPresenter.ShowError(chat, ToFriendlyError(ex)));
            }
            finally
            {
                this.requestGate.Release();
            }
        });
    }

    private void HandleConsoleAsk(string[] command)
    {
        string question = string.Join(" ", command).Trim();
        if (question.Length == 0)
        {
            this.Monitor.Log("Usage: swai_ask <question>", LogLevel.Info);
            return;
        }
        if (this.agent is null)
        {
            this.Monitor.Log("AI agent is not initialized.", LogLevel.Error);
            return;
        }
        GameContextSnapshot context = GameContextSnapshot.Capture();
        CancellationToken requestToken = this.requestCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                AgentAnswer answer = await this.agent.AskAsync(question, context, requestToken);
                this.mainThread.Enqueue(() =>
                {
                    this.Monitor.Log("AI answer: " + answer.Text, LogLevel.Info);
                    if (answer.Sources.Count > 0)
                        this.Monitor.Log("Sources: " + string.Join(" | ", answer.Sources), LogLevel.Info);
                });
            }
            catch (Exception ex)
            {
                this.Monitor.Log("Console AI request failed: " + ex, LogLevel.Error);
            }
        });
    }

    /// <summary>Load the local speech model and open the microphone on a background thread.</summary>
    private void InitializeVoiceAsync()
    {
        if (!Enum.TryParse(this.config.VoiceHotkey, ignoreCase: true, out SButton hotkey) || hotkey == SButton.None)
        {
            this.Monitor.Log($"Invalid VoiceHotkey '{this.config.VoiceHotkey}'; falling back to V.", LogLevel.Warn);
            hotkey = SButton.V;
        }
        this.voiceHotkey = hotkey;

        string assetDir = Path.Combine(this.Helper.DirectoryPath, "assets", "asr");
        string modelPath = Path.Combine(assetDir, "model.int8.onnx");
        string tokensPath = Path.Combine(assetDir, "tokens.txt");
        if (!File.Exists(modelPath) || !File.Exists(tokensPath))
        {
            this.Monitor.Log(
                "Voice input is enabled but the ASR model was not found in assets/asr; voice input is disabled.",
                LogLevel.Warn
            );
            return;
        }

        // SMAPI's loader doesn't probe the mod folder for native libraries, so register a resolver
        // that loads them from here. Pre-flight the load: if the native libs are missing we must not
        // construct the recognizer at all — a failed constructor leaves a finalizable object whose
        // finalizer re-enters native code and would crash the whole game.
        NativeLibraryResolver.Register(this.Helper.DirectoryPath);
        if (!NativeLibraryResolver.CanLoad("sherpa-onnx-c-api") || !NativeLibraryResolver.CanLoad("portaudio"))
        {
            this.Monitor.Log(
                "Voice input native libraries could not be loaded from the mod folder; voice input is disabled.",
                LogLevel.Warn
            );
            return;
        }

        int threads = this.config.VoiceNumThreads;
        _ = Task.Run(() =>
        {
            SpeechTranscriber? transcriber = null;
            VoiceRecorder? recorder = null;
            try
            {
                transcriber = new SpeechTranscriber(modelPath, tokensPath, threads, this.Monitor);
                recorder = new VoiceRecorder(SpeechTranscriber.SampleRate, this.config.VoiceMaxSeconds, this.Monitor);
            }
            catch (Exception ex)
            {
                this.Monitor.Log("Failed to initialize voice input: " + ex, LogLevel.Error);
                transcriber?.Dispose();
                recorder?.Dispose();
                return;
            }

            SpeechTranscriber readyTranscriber = transcriber;
            VoiceRecorder readyRecorder = recorder;
            this.mainThread.Enqueue(() =>
            {
                if (!readyRecorder.IsAvailable)
                {
                    readyTranscriber.Dispose();
                    readyRecorder.Dispose();
                    return;
                }

                this.voice = new VoiceInputController(
                    readyRecorder,
                    readyTranscriber,
                    this.mainThread,
                    this.Monitor,
                    this.voiceHotkey.ToString(),
                    this.OnVoiceTranscribed
                );
                this.Monitor.Log(
                    $"Voice input ready. Press {this.voiceHotkey} to start/stop recording (Chinese speech-to-text).",
                    LogLevel.Info
                );
            });
        });
    }

    /// <summary>Called on the main thread with recognized text; routes it through the normal ask flow.</summary>
    private void OnVoiceTranscribed(string question)
    {
        ChatBox? chat = Game1.chatBox;
        if (chat is null)
            return;

        chat.addInfoMessage("[语音] " + question);
        this.StartAskRequest(question, chat);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (this.voice is null || !this.voice.IsAvailable || e.Button != this.voiceHotkey)
            return;

        // Never hijack the key while the player is typing into the chat box.
        if (Game1.chatBox is { } chatBox && chatBox.isActive())
            return;

        // Starting requires a free player; stopping an in-progress recording is always allowed.
        if (!this.voice.IsRecording && !Context.IsPlayerFree)
            return;

        this.Helper.Input.Suppress(e.Button);
        this.voice.Toggle();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.mainThread.Drain(8);
        this.navigation.Update(e);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.Monitor.Log("Game context is ready for AI queries.", LogLevel.Debug);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Interlocked.Increment(ref this.navigationGeneration);
        this.navigation.Stop();
        this.navigation.Dispose();
        this.requestCancellation.Cancel();
        this.requestCancellation.Dispose();
        this.requestCancellation = new CancellationTokenSource();
        this.mainThread.Clear();
        this.Monitor.Log("Returned to title; pending UI callbacks were cleared.", LogLevel.Debug);
    }

    private void LogStatus(AgentSettings settings)
    {
        this.Monitor.Log(
            $"Configured={settings.IsConfigured}; Model={settings.Model}; " +
            $"BaseUrl={settings.BaseUrl}; WikiApi={settings.WikiApiUrl}; " +
            $"MaxSteps={settings.MaxAgentSteps}; MaxResponseTokens={settings.MaxResponseTokens}; " +
            $"QuestLogTool={this.config.EnableQuestLogTool}",
            LogLevel.Info
        );
    }

    private static string ToFriendlyError(Exception ex)
    {
        if (ex is InvalidOperationException)
            return ex.Message;
        if (ex is HttpRequestException)
            return "无法连接 LLM 或中文 Wiki，请检查网络和 OPENAI 配置。";
        if (ex is TaskCanceledException)
            return "查询超时，请稍后重试。";
        return "查询失败，请查看 SMAPI 日志中的详细错误。";
    }
}
