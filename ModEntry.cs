using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewWikiAgent.Agent;
using StardewWikiAgent.Api;
using StardewWikiAgent.Chat;
using StardewWikiAgent.Config;
using StardewWikiAgent.Game;
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
                    ChatAnswerPresenter.Show(chat, answer.Text);
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
