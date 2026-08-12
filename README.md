<div align="center">
  <h1>Stardew Wiki AI Agent</h1>
  <p>把中文星露谷 Wiki 放进游戏聊天框。</p>
  <p>
    <code>Stardew Valley 1.6.15+</code>
    · <code>SMAPI 4.5.2+</code>
    · 中文 Wiki
    · 本地语音输入
  </p>
</div>

---

不用再切出游戏查资料。载入存档后，在聊天框输入 `/ask <问题>`，Mod 会查阅中文 Stardew Valley Wiki，再结合当前存档给出回答和来源。问到地点时，还能直接在游戏里显示方向箭头。

## 功能

| 功能 | 说明 |
| --- | --- |
| **Wiki 问答** | 检索并阅读中文 Stardew Valley Wiki，回答后附上来源 |
| **存档信息** | 按需读取时间、天气、背包、状态、技能、关系和任务日志 |
| **任务建议** | 根据当前任务目标查 Wiki，告诉你下一步该做什么 |
| **地点导航** | 用游戏当前的世界地图数据定位地点，实时显示方向箭头 |
| **语音提问** | 按住快捷键录音，松开后在本地完成中文语音识别 |
| **Mod 扩展** | 其他 SMAPI Mod 可以通过公开 API 注册新的工具 |

内置游戏工具只读取信息，不会替玩家改钱、加物品或完成任务。导航只负责带路，到达目标附近后会自动结束。

## 快速开始

1. 安装 Stardew Valley `1.6.15+` 与 SMAPI `4.5.2+`。
2. 下载发布包，解压到游戏的 `Mods` 目录。
3. 通过 SMAPI 启动游戏并载入存档。
4. 输入 `/ask config`，填写模型服务地址、模型名称和 API Key。
5. 保存设置，重启 SMAPI 后即可提问。

以 DeepSeek 为例：

| 设置 | 示例 |
| --- | --- |
| LLM 服务地址 | `https://api.deepseek.com` |
| 模型 | `deepseek-v4-flash` |
| API Key | 你自己的 DeepSeek API Key |

```text
/ask 春天第一年种什么比较合适？
/ask 海莉喜欢哪些礼物？
/ask 日志里的“认识法师”下一步要去哪？
/ask 矿井怎么走？
```

## 常用命令

| 命令 | 用途 |
| --- | --- |
| `/ask <问题>` | 提问 |
| `/ask stop` | 取消正在进行的查询 |
| `/ask nav stop` | 停止当前导航 |
| `/ask status` | 查看查询和导航状态 |
| `/ask config` | 打开游戏内设置菜单 |
| `/ask help` | 查看简短说明 |

## 配置

第一次运行会生成 `config.json`。大部分设置都能在 `/ask config` 中修改，也可以直接编辑文件，或用 `OPENAI_BASE_URL`、`OPENAI_MODEL`、`OPENAI_API_KEY` 环境变量覆盖模型配置。

- `ReasoningEffort`：默认为 `medium`，可选 `low`、`medium`、`high`。
- `MaxResponseTokens`：模型单次返回上限，默认为 `8192`。
- `MaxAnswerCharacters`：聊天框正文长度，默认为 `1800`。
- `EnableQuestLogTool`：是否允许按需读取当前任务日志，默认为 `true`。
- `EnableVoiceInput`：是否启用本地语音识别，默认为 `true`。
- `VoiceHotkey`：按住录音、松开识别，默认为 `V`。

API Key 会以明文保存在本机的 `config.json` 中，请不要把这个文件提交到公开仓库。

## 语音输入

语音识别由随 Mod 提供的 sherpa-onnx 模型在本地完成，录音不会上传；识别出的文字仍会作为问题发送给你配置的模型服务。如果语音模型、原生库或麦克风不可用，文字提问不受影响。

## 从源码构建

```bash
# macOS：Debug 构建并部署
./build.sh

# macOS：Release 构建、部署并生成 zip
./build.sh release
```

```bat
:: Windows：Debug / Release
build.bat
build.bat release
```

如果游戏不在默认位置，请先设置 `GamePath`。只检查编译、不部署到游戏目录：

```bash
dotnet build StardewWikiAgent.csproj -c Release -p:EnableModDeploy=false
```

其他 Mod 的扩展接口见 [`Api/AgentContracts.cs`](Api/AgentContracts.cs)。

## License

[MIT](LICENSE)
