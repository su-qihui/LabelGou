# LabelGou AI 通道调用手册（本机 Ollama / 阿里云百炼 / DeepSeek）

给后续接手的人（和 AI agent）：**要接大模型时从这里开始，不要重新摸一遍。**
代码真源：`src/LabelGou.App/Services/Recognition/`。

## 一、通道长什么样

```
RecognitionService（双通道交叉校验，定案 D11）
├─ 第一通道：本地 OCR（系统内置，零依赖，几十毫秒）—— 永远开
└─ 第二通道：大模型 —— OllamaVisionClient 一个门面，按 settings.Provider 分流
     ├─ "ollama"  → 原生 /api/tags、/api/generate（本机，默认）
     └─ "openai"  → OpenAI 兼容 /v1/chat/completions（百炼、DeepSeek、vLLM、Ollama 的 /v1）
```

调用方**不需要知道对端是谁**：`AskFieldsAsync(settings, imagePath, cancel, handler)` 一个入口。

## 二、三条红线（改代码前先读，全部有测试钉住）

1. **AI 不碰毫米。** 提示词只许要相对等级（`xl/l/m/s`），几何一律由 `RowLayoutSpec` + 纸规算成毫米。
   探针里那条"是否混进绝对尺寸"就是专门查这个的。
2. **数据出网必须可见。** `settings.StaysOnThisMachine` 为 false 时，状态栏与核对窗口要显示
   「外部服务，数据会离开这台电脑」。工厂订单外传是用户明确关切的红线（对接文档 §五-11）。
3. **不支持图片的模型绝不发图。** `ModelAcceptsImages=false`（DeepSeek 的 `deepseek-chat`）时走
   `AskFieldsFromOcrLinesAsync`：把本地 OCR 认出的文字行交给它整理。硬发图只会换来一句看不懂的 400。

## 三、怎么配

设置文件：`%APPDATA%\LabelGou\recognition.json`（UTF-8，可注释、允许尾逗号）。

```json
{
  "useLocalOcr": true,
  "useVisionModel": true,
  "provider": "openai",
  "endpoint": "https://dashscope.aliyuncs.com/compatible-mode/v1",
  "model": "qwen-vl-max",
  "modelAcceptsImages": true,
  "apiKeyEnvVar": "LABELGOU_LLM_KEY",
  "timeoutSeconds": 120
}
```

**密钥优先走环境变量**（`ResolveApiKey()` 先查 `apiKeyEnvVar` 指定的名字，再退回文件里的 `apiKey`）：

```powershell
setx LABELGOU_LLM_KEY "sk-你自己的key"     # 新开终端生效
```

两家现成参数（代码里 `RecognitionSettings.CloudPresets`，`ApplyPreset()` 一键切）：

| 家 | endpoint | model | 吃图 | 用途 |
|---|---|---|---|---|
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-vl-max` | 是 | 认样张版式、认单据字段（替代本机慢模型） |
| DeepSeek | `https://api.deepseek.com` | `deepseek-chat` | **否** | 只整理 OCR 文字行、做纯文本推理 |

`endpoint` 带不带 `/v1` 都行，`OpenAiUrl()` 只补一次（这条有 Theory 钉住，四种写法全覆盖）。

## 四、怎么验（不花钱的验法）

单测全部走 `StubHandler`，**不真连公网**，所以随便跑：

```powershell
D:\dev\dotnet-sdk\dotnet.exe test tests\LabelGou.App.Tests\LabelGou.App.Tests.csproj --filter CloudChannelTests
```

要真连云端时才需要 key，最小验证：

```powershell
$env:LABELGOU_LLM_KEY="sk-..."
# 用 LabelGou 的「智能识别单据…」按钮跑一张真样张，看核对窗口里"云端 qwen-vl-max"那条通道耗时与字段数
```

## 五、本机实测数据（别再当 bug 重查）

| 事实 | 数值 |
|---|---|
| 本机 `qwen3.5:2b`（vision）认一张样张 | **243 秒/张** |
| 本机 `qwen3-vl:4b` 认一张单据 | 约 34 秒/张，冷启动加载模型另算 |
| 所以 | 云端不是可选项，是必需品；且只能"认一次 → 人核 → 存成模板复用" |

模型把 JSON 放在 `thinking` 还是 `response` 因模型而异 —— `LlmFieldJsonParser.PickBestJson` 两个都捞，
只读 `response` 会误判成"模型没返回"。

## 六、下一步（AI 认版式，还没接进界面）

探针在 `labelgou-other\_probe\ai-layout\probe.py`（不入库，可复跑）：把样张交给模型，
要求只输出版式 JSON（行序 / 固定文字 vs 数据 / `xl|l|m|s` / 对齐），实测四行与顺序全对、
`BOLAROM` 认成最大号标题行、`olu830-35` 自己就没带 `*144`。

要接的三段（顺序别换）：
1. `RowLayoutSpec` 的 JSON 导入通道 —— 严格校验，**不合法整份拒绝**，绝不落半套版式；
2. 第 3 步加「从样张让 AI 认版式」入口，复用本手册这套客户端；
3. 认完进核对窗口由人确认再存成用户模板（D13：AI 输出强制人工核对）。

评测基准 = `BuiltInTemplates` 里那三套**故意不进内置清单**的厂牌定义（邱总 / OLU / TOP）：
模型看对应样张，输出版式该跟它们对得上。用户给的那批样张是训练与评测素材，
**不是让我抄成内置选项塞给用户选**（这条已被 `ItemNoTailTests.厂牌样张不充当内置模板只能当评测基准` 钉住）。
