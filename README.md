<div align="center">
  <img src="assets/labelgou-mark-1024.png" width="96" alt="LabelGou">
  <h1>LabelGou · 唛头标签助手</h1>
  <p>把工厂发来的 Excel/CSV 装箱单，套成能直接上纸的<strong>外贸唛头（运输标签）</strong>：拼版、编号、条码、打印与导出一条走完。</p>
  <p>v0.7.0 · .NET 8 WPF · MIT · 不依赖第三方库</p>
</div>

---

## 为什么做这个

打印店里这活原本是：打开 CorelDRAW，照着表格一格一格敲货号、件数、数量，一敲几十页。
LabelGou 把它变成五步 —— **导表 → 连字段 → 选模板 → 拼版编号 → 核对出片**，
数据错不错、字会不会探出纸边，软件当场点名，而不是等印出来才发现。

## 你会得到什么

- **一份数据，所有出口同一张脸**：单标签预览、整版预览、直连打印、PDF、PNG/TIFF、给 CorelDRAW 的 SVG —— 都走同一个渲染器与同一份拼版几何，不存在"预览一个样、纸上一个样"。
- **可视化模板编辑器**：文本 / 贝塞尔曲线 / 矩形 / 椭圆 / 多边形 / 条码，图层面板、句柄缩放、旋转、吸附、网格，撤销按手势数计，存盘前有校验闸门。
- **纸规与模板分开管**：纸规 = 整张纸（280×200 一开四、一开八、大开二、小开二、A4/A3、刀模纸、自定义），模板 = 纸上一枚唛头。选模板时纸规跟着走，你手工换过的那张会记住。
- **件号 No.x / y 编号**：沿用表里的 / 强制重排 / 按表里某一列数张数（"打印 5 张就出 5 张整张纸"），分组重新起号、补零、前后缀。
- **条码按 CorelDRAW 向导那四格算**：X 尺寸（缩放比例 + 打印分辨率 + 宽度减少）决定条的粗细，框再扁也拉不肥；支持 Code 128 / Code 39 / EAN-13 / EAN-8 / ITF-14 / CodaBar 等，可读数字跟着 X 一起缩放。
- **样张反推版式**：一张唛头截图或厂商 CDR 页面 → 系统 OCR + 云端大模型双通道识别，字段**强制人工核对**后才落地；AI 也能读整张表提出版式方案。
- **CMYK 与分色**：元素可填 CMYK 墨量，导出四张灰版分色、真四通道 CMYK TIFF、PDF 的 DeviceCMYK。
- **三道闸**：出纸前拿真数据量一遍（会不会印出白纸、字会不会探出纸边）；不猜（认不出的列/制式/尺寸一律报，不替你编一个）；可退（每一步都能撤销，改坏能退回）。

## 快速开始

```bat
git clone https://github.com/su-qihui/LabelGou.git
cd LabelGou

compile-labelgou.bat            REM 编译 + 跑单测
compile-labelgou.bat run        REM 编译并启动
compile-labelgou.bat publish    REM 免安装单目录包（自包含 win-x64）→ artifacts\publish\labelgou-win-x64
```

装了 .NET 8 SDK 也可以直接：

```bash
dotnet build LabelGou.sln
dotnet run --project src/LabelGou.App
```

试手感：`samples/` 里有两份样例装箱单（含"一开四"那家），① 步直接拖进去。

## 运行环境

| | |
|---|---|
| 能跑 | Windows 10 x64、Windows 11（工程目标平台是 `windows10.0.19041`，即 Win10 2004；更低的 Win10 版本没实测过） |
| 需要 | .NET 8 **Desktop** Runtime x64；用 `publish` 出来的自包含包则不用装 |
| 不支持 | Windows 7 / 8.1 —— .NET 8 已不支持这两个系统，且系统 OCR 通道在它们上根本不存在 |
| 离线可用 | 是。云端 AI / 大模型是可关的可选通道，离线模式是一条独立可交付的路 |

## 仓库结构

```
src/LabelGou.Core    net8.0：模板模型、版面与折行、拼版几何、编号引擎、
                     条码编码、SVG/PDF/CMYK TIFF 写出器、识别与映射规则、校验器
                     （唯一的 NuGet 依赖是微软自家的 System.Text.Encoding.CodePages —— 读老表格的 GBK 编码要用）
src/LabelGou.App     net8.0-windows，WPF：五步向导、模板编辑器、渲染与打印导出、AI 面板
tests/               两个 xUnit 工程（Core 逻辑与 WPF 界面层分开跑）
assets/              图标与品牌图
samples/             两份真实形态的样例装箱单
tools/               CorelDRAW / AI 侧的辅助脚本
```

依赖方向是单向的：`App → Core`，Core 不引用 WPF，也不写日志、不弹窗（把消息交回调用方）。

## 你的数据放在哪

全在本机 `%APPDATA%\LabelGou\`：`templates\`（模板 JSON）、`sheets\`（你自己另存的纸规）、
`mapping-profiles\`（字段映射方案）、`uistate.json`（上次用的模板/纸规等）、`logs\yyyyMMdd.log`。
AI 密钥走 Windows 凭据（DPAPI），**不落明文文件**。表格数据与模板不出这台机器，除非你主动用云端识别/AI 那条通道。

## 许可与态度

MIT。这是自家店里天天用的工具，顺手开源 —— 它优先服务"今天这批货要出门"，不承诺通用软件的支持义务。
觉得哪儿别扭、想要哪一档纸规/模板，开 issue 说具体场景，比说"不好用"有用得多。
