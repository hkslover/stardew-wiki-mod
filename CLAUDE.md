# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A SMAPI 4.5.2 mod for Stardew Valley 1.6.15 (`net6.0`). It adds an in-game `/ask <问题>` chat command that answers questions about the game by driving an OpenAI-compatible Chat Completions endpoint through a tool-calling loop. The agent's tools query the **Chinese** Stardew Valley Wiki (`zh.stardewvalleywiki.com`) and expose read-only game state. Answers and their source URLs are printed back into the chat box. The user-facing language is Chinese (tool descriptions, system prompt, and chat replies are all Chinese).

The containing directory is `stardew_wiki_mod`, but the `.csproj`, assembly, namespace, and mod folder are all `StardewWikiAgent` (see `manifest.json` / `StardewWikiAgent.csproj`).

## Build & deploy

`dotnet` is not on the default PATH; use the explicit SDK path and repo-local package/home dirs (these are gitignored):

```bash
DOTNET_ROOT=/usr/local/share/dotnet \
DOTNET_CLI_HOME="$PWD/.dotnet-cli-home" \
NUGET_PACKAGES="$PWD/.nuget-packages" \
/usr/local/share/dotnet/dotnet build StardewWikiAgent.csproj
```

`Pathoschild.Stardew.ModBuildConfig` (the only NuGet dependency) resolves the SMAPI/Stardew reference assemblies and, on build, auto-deploys the DLL + `manifest.json` into the game's `Mods/StardewWikiAgent` folder and produces a release zip. There is no separate test project — verification is manual, in-game (see below).

## Running & diagnostics

- In-game chat: `/ask <问题>` (a collision-proof alias `/snow.StardewWikiAgent_ask` is always registered; the short `/ask` is only claimed if no other mod owns it). `/ask stop` immediately stops the current location navigation without calling the LLM.
- SMAPI console: `swai_status` prints config without secrets; `swai_ask <问题>` runs the agent and logs the answer + sources (diagnostics only).
- A save must be loaded (`Context.IsWorldReady`) before `/ask` works.

## Configuration

`config.json` is generated in the mod folder on first run (`ModConfig`). **Environment variables override file values** (`AgentSettings.From`): `OPENAI_BASE_URL`, `OPENAI_MODEL`, `OPENAI_API_KEY`, `STARDEW_WIKI_API_URL`. The Wiki URL is validated to stay on the `stardewvalleywiki.com` domain; anything else falls back to the official Chinese API. The LLM is considered configured only when `BaseUrl` is a valid http(s) URI and `Model` is non-empty.

## Architecture

The request flow, thread-by-thread, is the key thing to understand — SMAPI game APIs are **not thread-safe** and must only be touched on the main update loop, while all HTTP happens on background threads.

1. `ModEntry.HandleAsk` (main thread) captures a `GameContextSnapshot` synchronously, gates concurrent requests with a `SemaphoreSlim` (one at a time), then offloads to `Task.Run`.
2. `AgentRunner.AskAsync` (background) runs the tool-calling loop: builds messages (system prompt + user question + optional game context), then loops up to `MaxAgentSteps`. First step forces `tool_choice=required` (with a fallback to `auto` if the endpoint 400/404s), later steps use `auto`. Each assistant `tool_calls` entry is executed and the result appended as a `tool` message. The loop ends when the model returns content with no tool calls.
3. `OpenAiCompatibleClient` does the raw `POST /chat/completions`. It uses `HttpClient` with an infinite client-level timeout; the real deadline is a linked `CancellationTokenSource` in `AgentRunner` (`RequestTimeout`). Non-2xx throws `LlmHttpException` (carries the status code, used for the tool_choice fallback).
4. Results marshal back to the main thread via `MainThreadDispatcher`, which is drained in `OnUpdateTicked` (8 callbacks/tick). `ChatAnswerPresenter` runs the answer through `MarkdownChatFormatter` (strips Markdown, colors lines: green body / gold emphasis / cyan sources, inline `**bold**` → 【…】 since the chat box is one-color-per-line) and prints to the `ChatBox`, then pops a `HUDMessage.ForCornerTextbox` so a slow/faded answer isn't missed.

### Tools and the read-only guarantee

`AgentToolRegistry` holds the tools and produces their OpenAI function definitions. Every tool declares a `ToolExecutionAffinity`:
- `BackgroundReadOnly` — runs directly on the background thread (the Wiki tools, `get_game_context`).
- `MainThreadReadOnly` — marshaled onto the main thread via `MainThreadDispatcher.InvokeAsync` before executing (for tools that must read live game state safely).
- `MainThreadMutating` — **rejected at registration.** The registry refuses to register mutating tools; world-changing behavior is reserved for the (currently unimplemented) `IAgentAction` path, which is designed to require player confirmation and run on the game thread.

Built-in tools: `wiki_search` + `wiki_read` (`Wiki/`, `BackgroundReadOnly`, via `MediaWikiClient` against the MediaWiki `api.php`) and the live-state tools `get_inventory` / `get_player_status` / `get_relationships` / `get_quest_log` / `find_game_location` (`Game/`, `MainThreadReadOnly`, read `Game1` directly at call time). `get_quest_log` reads the local player's in-game quest journal only when the question needs it, supports an optional title/keyword filter, bounds returned text, and can be disabled with `EnableQuestLogTool`; it never reads SMAPI log files. `find_game_location` resolves a Wiki-confirmed localized place name against the active `Data/WorldMap` tooltips and returns a structured world-map target. Only a *compact* context line (date/season/time/weather/location/language, `GameContextSnapshot.ToCompactPromptText`) is injected eagerly; everything else is fetched on demand via those tools to keep the prompt small. The English system prompt in `AgentRunner` treats Wiki and quest-log content as **untrusted data** (prompt-injection defense), requires the model to cite source pages, prefer Wiki lookups over memory, and emit near-plain text (only `**bold**` is allowed) with the reply itself in Simplified Chinese.

`NavigationService` consumes a resolved target only after the agent finishes, projects the local player's live tile through `WorldMapManager`, and draws a pulsing arrow near the player's feet through `Display.RenderedWorld`. It checks proximity every six update ticks, clears the target on arrival or return to title, and shows an arrival HUD message. Navigation state is local UI state; the location tool itself remains read-only.

### Public extension API

`ModEntry.GetApi()` returns `IStardewWikiAgentApi` (`Api/AgentContracts.cs`) so other SMAPI mods can `RegisterTool`/`RegisterAction`, list names, and call `AskAsync`. The `Api/`, `Game/GameContextSnapshot`, and `AgentAnswer` types are `public` because they cross the mod boundary; internal wiring (`AgentRunner`, registry, clients, dispatcher) is `internal`. Keep the public surface minimal and stable — changing it breaks dependent mods.

### Source layout

`Api/` public contracts · `Agent/` LLM loop, tool registry, HTTP client · `Wiki/` MediaWiki client + wiki tools · `Game/` context snapshot + live-state tools + easter-egg greeter · `Chat/` chat output formatting + Markdown-to-color · `Threading/` main-thread dispatcher · `Config/` config + resolved settings · `ModEntry.cs` entry point & command wiring.

There is also a private easter egg (`Game/EasterEggGreeter.cs` + `SteamIdentity.cs`): on `DayStarted` / evening `TimeChanged` (18:00) it shows a warm HUD greeting, but only for one **hardcoded** SteamID64 (`TargetSteamId` in `EasterEggGreeter`) — no config, no-op for everyone else. The Steam ID is read best-effort by reflecting into the game's `Steamworks.NET` assembly, so it silently no-ops on non-Steam builds.

## Conventions

- Nullable reference types and implicit usings are enabled; `LangVersion` is `latest`. Code uses `this.` qualification on members and `sealed` classes throughout — match this style.
- Tool `ParametersSchemaJson` is a raw JSON string validated at registration; tool `Name` must be `[A-Za-z0-9_]` only.
- All user-visible strings are Chinese. New tool descriptions/errors/chat text should follow suit.
