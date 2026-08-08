# Stardew Wiki AI Agent

一个适用于 Stardew Valley 1.6.15 与 SMAPI 4.5.2 的中文 Wiki 问答 Mod。载入存档后，在聊天框输入 `/ask <问题>`，即可获得结合中文 Stardew Valley Wiki 和当前游戏状态的回答与来源。

## 功能

- 检索并阅读中文 Stardew Valley Wiki；
- 只读获取季节、天气、背包、玩家状态和关系信息；
- 支持 OpenAI-compatible Chat Completions 接口；
- 提供 API，允许其他 SMAPI Mod 注册扩展工具。

## 配置

首次运行会生成 `config.json`。填写 `BaseUrl`、`Model` 和需要时的 `ApiKey`，也可以使用环境变量 `OPENAI_BASE_URL`、`OPENAI_MODEL`、`OPENAI_API_KEY`。

## 构建

```bash
dotnet build ConsoleHelloMod.csproj -c Release
```

项目采用 [MIT License](LICENSE)。
