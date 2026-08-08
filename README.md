# Stardew Wiki AI Agent

一个适用于 Stardew Valley 1.6.15 与 SMAPI 4.5.2 的中文 Wiki 问答 Mod。载入存档后，在聊天框输入 `/ask <问题>`，即可获得结合中文 Stardew Valley Wiki 和当前游戏状态的回答与来源。

## 功能

- 检索并阅读中文 Stardew Valley Wiki；
- 只读获取季节、天气、背包、玩家状态、关系信息和当前任务日志；
- 当玩家提到当前任务或询问下一步时，AI 可按需读取任务目标，结合 Wiki 给出建议；
- 询问地点位置时，可根据游戏当前加载的世界地图数据在玩家脚边显示实时方向箭头；
- 通过直接的 OpenAI-compatible Chat Completions HTTP 请求接入兼容接口（`HttpClient` + `System.Text.Json`，不依赖官方 OpenAI SDK）；
- 提供 API，允许其他 SMAPI Mod 注册扩展工具。

导航接近目标后会自动结束并显示 HUD 提示。输入 `/ask stop` 可以随时停止当前导航；如果地点名称有歧义或游戏世界地图中没有对应数据，则不会启动箭头。

## 配置

首次运行会生成 `config.json`。填写 `BaseUrl`、`Model` 和需要时的 `ApiKey`，也可以使用环境变量 `OPENAI_BASE_URL`、`OPENAI_MODEL`、`OPENAI_API_KEY`。

`MaxResponseTokens` 默认是 `8192`，控制模型单次返回的 token 上限；旧版本自动生成的默认值 `2048` 会在下次启动时迁移到 `8192`。`MaxAnswerCharacters` 默认仍为 `1800`，它只控制最终显示在聊天框里的正文字符数，两者不是同一个限制。

使用 DeepSeek V4 Flash/Pro 时，将 `BaseUrl` 设置为 `https://api.deepseek.com`、`Model` 设置为对应的 `deepseek-v4-*` 名称，并填写 DeepSeek API Key。Mod 的协议客户端会直接发送 DeepSeek 专用的 `max_tokens`、思考模式（`thinking`）与思考等级（`reasoning_effort`）字段；带 Wiki 工具调用时也会在后续轮次中原样回放 DeepSeek 返回的 `reasoning_content`。

`ReasoningEffort` 控制思考等级，默认 `medium`，可选 `low`/`medium`/`high`。它只作用于 DeepSeek V4 这类推理模型：等级越低，单次查询消耗的按量计费思考 token 越少；需要更强推理时再调到 `high`。

`EnableQuestLogTool` 默认为 `true`。关闭后，任务日志读取工具不会注册，任务内容也不会发送给所配置的 AI 服务。任务日志只在问题确实涉及当前任务时按需读取，不会读取 SMAPI 调试日志或磁盘文件。

例如可询问：`/ask 日志里的“认识法师”下一步要去哪？`。AI 会先核对当前任务目标，再查询 Wiki；能够唯一定位目的地时会同时启动方向箭头。

## 构建

```bash
dotnet build StardewWikiAgent.csproj -c Release
```

项目采用 [MIT License](LICENSE)。
