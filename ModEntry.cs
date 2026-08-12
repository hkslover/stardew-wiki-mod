using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
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
using StardewWikiAgent.UI;
using StardewWikiAgent.Wiki;

namespace StardewWikiAgent;

/// <summary>The SMAPI entry point for the Stardew Wiki AI Agent.</summary>
internal sealed class ModEntry : Mod
{
    private const string OnboardingDataKey = "first-use-guide-v1";

    private MainThreadDispatcher mainThread = null!;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object requestStateLock = new();
    private CancellationTokenSource? activeRequestCancellation;
    private DateTimeOffset activeRequestStartedAt;
    private int requestGeneration;
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
        var settings = AgentSettings.From(this.config, this.Monitor);
        helper.WriteConfig(this.config);
        var wiki = new MediaWikiClient(settings.WikiApiUrl, settings.RequestTimeout);
        this.tools = new AgentToolRegistry(this.Monitor, this.mainThread);
        this.tools.Register(new WikiSearchTool(wiki));
        this.tools.Register(new WikiReadTool(wiki));
        this.tools.Register(new HeldItemTool());
        this.tools.Register(new InventoryTool(this.config.AllowFullInventoryRead));
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
        helper.ConsoleCommands.Add(
            "swai_config",
            "Open the vanilla-style Stardew Wiki AI Agent settings menu.",
            (_, _) => this.QueueOpenConfigMenu()
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
            this.ShowAskHelp(chat);
            return;
        }

        if (question.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            this.ShowAskHelp(chat);
            return;
        }

        if (question.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            this.ShowAskStatus(chat);
            return;
        }

        if (question.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            chat.addInfoMessage("正在打开 AI 助手设置…");
            this.QueueOpenConfigMenu(chat);
            return;
        }

        if (question.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || question.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            bool cancelled = this.CancelActiveRequest();
            chat.addInfoMessage(
                cancelled
                    ? "已取消当前 AI 查询；它不会再显示答案或启动导航。"
                    : "当前没有正在进行的 AI 查询。"
            );
            return;
        }

        if (question.Equals("nav stop", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref this.navigationGeneration);
            bool stopped = this.navigation.Stop();
            chat.addInfoMessage(stopped ? "已停止当前导航。" : "当前没有正在进行的导航。");
            return;
        }

        if (question.Equals("nav", StringComparison.OrdinalIgnoreCase))
        {
            chat.addInfoMessage("导航命令：/ask nav stop（停止当前导航）。");
            return;
        }

        this.StartAskRequest(question, chat);
    }

    private void ShowAskHelp(ChatBox chat)
    {
        chat.addInfoMessage("提问：/ask 你的问题（例如：/ask 春天种什么？）");
        chat.addInfoMessage("控制：/ask stop 取消查询；/ask nav stop 停止导航；/ask status 查看状态。");
        chat.addInfoMessage("设置：/ask config 打开配置；/ask help 查看本帮助。");
    }

    private void QueueOpenConfigMenu(ChatBox? chat = null)
    {
        this.mainThread.Enqueue(() =>
        {
            IClickableMenu? returnMenu = Game1.activeClickableMenu;
            if (returnMenu is not null && returnMenu is not TitleMenu)
            {
                const string message = "请先关闭当前菜单，再打开 AI 助手设置。";
                if (chat is not null)
                    chat.addInfoMessage(message);
                else
                    this.Monitor.Log(message, LogLevel.Info);
                return;
            }

            void RestorePreviousMenu()
            {
                if (returnMenu is null)
                    return;

                // exitThisMenu clears the active menu after invoking callbacks, so
                // restore the title screen on the next game tick.
                this.mainThread.Enqueue(() =>
                {
                    if (Game1.activeClickableMenu is null)
                        Game1.activeClickableMenu = returnMenu;
                });
            }

            Game1.activeClickableMenu = new ModConfigMenu(
                this.config,
                updated =>
                {
                    try
                    {
                        updated.Validate(this.Monitor);
                        this.Helper.WriteConfig(updated);
                        this.config = updated;
                        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("AI 助手设置已保存；重启 SMAPI 后全部生效。"));
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log("Failed to save the in-game configuration: " + ex, LogLevel.Error);
                        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("设置保存失败，请查看 SMAPI 日志。"));
                    }
                    finally
                    {
                        RestorePreviousMenu();
                    }
                },
                RestorePreviousMenu
            );
        });
    }

    private void ShowAskStatus(ChatBox chat)
    {
        string requestStatus;
        lock (this.requestStateLock)
        {
            if (this.activeRequestCancellation is null)
            {
                requestStatus = "空闲";
            }
            else if (this.activeRequestCancellation.IsCancellationRequested)
            {
                requestStatus = "正在取消";
            }
            else
            {
                int elapsedSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - this.activeRequestStartedAt).TotalSeconds);
                requestStatus = $"查询中（{elapsedSeconds} 秒）";
            }
        }

        string navigationStatus = this.navigation.IsActive
            ? $"正在前往 {this.navigation.TargetName ?? "目标地点"}"
            : "未启用";
        chat.addInfoMessage($"AI 查询：{requestStatus}；导航：{navigationStatus}。");
    }

    /// <summary>Run the agent for a question and present the answer. Shared by the chat command and voice input.</summary>
    private void StartAskRequest(string question, ChatBox chat, GameContextSnapshot? capturedContext = null)
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

        GameContextSnapshot context = capturedContext ?? GameContextSnapshot.Capture();
        RequestLease? request = this.TryStartRequest();
        if (request is null)
        {
            chat.addInfoMessage("上一条问题还在查询中；可用 /ask cancel 取消。");
            return;
        }

        int navigationGeneration = Volatile.Read(ref this.navigationGeneration);
        chat.addInfoMessage("正在分析并检索 Wiki…");

        _ = Task.Run(async () =>
        {
            try
            {
                AgentAnswer answer = await this.agent.AskAsync(question, context, request.Token);
                this.mainThread.Enqueue(() =>
                {
                    if (!this.IsRequestCurrent(request))
                        return;

                    if (answer.NavigationTarget is not null
                        && navigationGeneration == Volatile.Read(ref this.navigationGeneration))
                        this.navigation.Start(answer.NavigationTarget);
                    ChatAnswerPresenter.Show(chat, answer.Text, answer.Sources);
                });
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
                this.Monitor.Log("AI 查询已取消。", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                this.Monitor.Log("AI request failed: " + ex, LogLevel.Error);
                this.mainThread.Enqueue(() =>
                {
                    if (this.IsRequestCurrent(request))
                        ChatAnswerPresenter.ShowError(chat, ToFriendlyError(ex));
                });
            }
            finally
            {
                this.CompleteRequest(request);
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

        RequestLease? request = this.TryStartRequest();
        if (request is null)
        {
            this.Monitor.Log("上一条 AI 查询仍在进行中；请等待完成或在游戏聊天中使用 /ask cancel。", LogLevel.Info);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // SMAPI runs console commands on its own input thread, so capture the
                // live game state on the main update loop before touching Game1.
                GameContextSnapshot context = await this.mainThread.InvokeAsync(GameContextSnapshot.Capture);
                AgentAnswer answer = await this.agent.AskAsync(question, context, request.Token);
                this.mainThread.Enqueue(() =>
                {
                    if (!this.IsRequestCurrent(request))
                        return;

                    this.Monitor.Log("AI answer: " + answer.Text, LogLevel.Info);
                    if (answer.Sources.Count > 0)
                        this.Monitor.Log("Sources: " + string.Join(" | ", answer.Sources), LogLevel.Info);
                });
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
                this.Monitor.Log("控制台 AI 查询已取消。", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                this.Monitor.Log("Console AI request failed: " + ex, LogLevel.Error);
            }
            finally
            {
                this.CompleteRequest(request);
            }
        });
    }

    private RequestLease? TryStartRequest()
    {
        if (!this.requestGate.Wait(0))
            return null;

        var cancellation = new CancellationTokenSource();
        lock (this.requestStateLock)
        {
            int generation = Interlocked.Increment(ref this.requestGeneration);
            this.activeRequestCancellation = cancellation;
            this.activeRequestStartedAt = DateTimeOffset.UtcNow;
            return new RequestLease(generation, cancellation, cancellation.Token);
        }
    }

    private bool CancelActiveRequest()
    {
        lock (this.requestStateLock)
        {
            // Invalidate even an already-completed response whose main-thread callback
            // has not run yet, so /ask cancel can never show a stale answer or restart navigation.
            Interlocked.Increment(ref this.requestGeneration);
            if (this.activeRequestCancellation is null)
                return false;

            this.activeRequestCancellation.Cancel();
            return true;
        }
    }

    private bool IsRequestCurrent(RequestLease request)
    {
        return !request.Token.IsCancellationRequested
            && request.Generation == Volatile.Read(ref this.requestGeneration);
    }

    private void CompleteRequest(RequestLease request)
    {
        lock (this.requestStateLock)
        {
            if (ReferenceEquals(this.activeRequestCancellation, request.Cancellation))
                this.activeRequestCancellation = null;
            request.Cancellation.Dispose();
        }
        this.requestGate.Release();
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
                    GameContextSnapshot.Capture,
                    this.OnVoiceTranscribed
                );
                this.Monitor.Log(
                    $"Voice input ready. Hold {this.voiceHotkey} to record and release it to transcribe (Chinese speech-to-text).",
                    LogLevel.Info
                );
            });
        });
    }

    /// <summary>Called on the main thread with recognized text; routes it through the normal ask flow.</summary>
    private void OnVoiceTranscribed(string question, GameContextSnapshot capturedContext)
    {
        ChatBox? chat = Game1.chatBox;
        if (chat is null)
            return;

        chat.addInfoMessage("[语音] " + question);
        this.StartAskRequest(question, chat, capturedContext);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (this.voice is null || !this.voice.IsAvailable || e.Button != this.voiceHotkey)
            return;

        // Never hijack the key while the player is typing into the chat box.
        if (Game1.chatBox is { } chatBox && chatBox.isActive())
            return;

        // Starting requires a free player. A press while transcription is running is
        // still consumed so the voice hotkey never leaks through to gameplay.
        if (!this.voice.IsRecording && !this.voice.IsTranscribing && !Context.IsPlayerFree)
            return;

        // Suppressing the press keeps the key out of gameplay for the whole hold: SMAPI keeps
        // the button in its released-override set until it is physically let go. We must NOT
        // rely on SMAPI's ButtonReleased for the end of the hold, though — that same override
        // makes SMAPI report the still-held key as "released" on the very next tick. Instead we
        // poll the real hardware state each tick in OnUpdateTicked (see EndVoiceRecordingIfKeyReleased).
        this.Helper.Input.Suppress(e.Button);
        this.voice.BeginRecording();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.mainThread.Drain(8);
        this.voice?.Update();
        this.EndVoiceRecordingIfKeyReleased();
        this.navigation.Update(e);
    }

    /// <summary>
    /// End a hold-to-talk recording once the hotkey is physically released. SMAPI's suppressed
    /// button state can't be trusted here, so this reads the raw MonoGame device state directly.
    /// </summary>
    private void EndVoiceRecordingIfKeyReleased()
    {
        if (this.voice is not { IsAvailable: true, IsRecording: true })
            return;

        if (!IsButtonPhysicallyDown(this.voiceHotkey))
            this.voice.EndRecording();
    }

    /// <summary>Read the true hardware state of a button, bypassing SMAPI's input overrides.</summary>
    private static bool IsButtonPhysicallyDown(SButton button)
    {
        if (button.TryGetKeyboard(out Keys key))
            return Keyboard.GetState().IsKeyDown(key);

        if (button.TryGetController(out Buttons controllerButton))
            return GamePad.GetState(PlayerIndex.One).IsButtonDown(controllerButton);

        MouseState mouse = Mouse.GetState();
        return button switch
        {
            SButton.MouseLeft => mouse.LeftButton == ButtonState.Pressed,
            SButton.MouseRight => mouse.RightButton == ButtonState.Pressed,
            SButton.MouseMiddle => mouse.MiddleButton == ButtonState.Pressed,
            SButton.MouseX1 => mouse.XButton1 == ButtonState.Pressed,
            SButton.MouseX2 => mouse.XButton2 == ButtonState.Pressed,
            _ => false,
        };
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.Monitor.Log("Game context is ready for AI queries.", LogLevel.Debug);

        try
        {
            FirstUseState? state = this.Helper.Data.ReadGlobalData<FirstUseState>(OnboardingDataKey);
            if (state?.Shown == true)
                return;

            Game1.addHUDMessage(HUDMessage.ForCornerTextbox("Stardew Wiki AI 助手已就绪：在聊天框输入 /ask help 查看用法。"));
            string secondTip = this.config.EnableVoiceInput
                ? $"按住 {this.voiceHotkey} 说话，松开后识别；输入 /ask config 可打开设置。"
                : "输入 /ask config 可打开原版风格设置菜单。";
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox(secondTip));
            this.Helper.Data.WriteGlobalData(OnboardingDataKey, new FirstUseState { Shown = true });
        }
        catch (Exception ex)
        {
            this.Monitor.Log("Failed to read or write the first-use guide state: " + ex.Message, LogLevel.Debug);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.voice?.Cancel(notify: false);
        this.CancelActiveRequest();
        Interlocked.Increment(ref this.navigationGeneration);
        this.navigation.Stop();
        this.navigation.Dispose();
        this.mainThread.Clear();
        this.Monitor.Log("Returned to title; pending UI callbacks were cleared.", LogLevel.Debug);
    }

    private void LogStatus(AgentSettings settings)
    {
        this.Monitor.Log(
            $"Configured={settings.IsConfigured}; Model={settings.Model}; " +
            $"BaseUrl={settings.BaseUrl}; WikiApi={settings.WikiApiUrl}; " +
            $"MaxSteps={settings.MaxAgentSteps}; MaxResponseTokens={settings.MaxResponseTokens}; " +
            $"ReasoningEffort={settings.ReasoningEffort}; QuestLogTool={this.config.EnableQuestLogTool}; " +
            $"AllowFullInventoryRead={this.config.AllowFullInventoryRead}",
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

    private sealed record RequestLease(
        int Generation,
        CancellationTokenSource Cancellation,
        CancellationToken Token
    );

    private sealed class FirstUseState
    {
        public bool Shown { get; set; }
    }
}
