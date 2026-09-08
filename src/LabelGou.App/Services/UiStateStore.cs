using System;
using System.IO;
using System.Text.Json;

namespace LabelGou.App.Services;

/// <summary>
/// 界面状态：上次用的模板、纸规、文字大小写与① 步表格高度。刻意只记这几样，不做"什么都能记住"。
/// </summary>
public sealed class UiState
{
    /// <summary>上次选中的内置/用户模板 id（<c>builtin.xxx</c> 或 <c>user.xxx</c>）。</summary>
    public string? TemplateId { get; set; }

    /// <summary>上次选中的纸规 id。</summary>
    public string? SheetSpecId { get; set; }

    /// <summary>
    /// 唛头文字大小写口径（2026-09-08 用户要的三档开关）。<strong>默认按表格里的</strong>：
    /// 旧状态文件没这个字段时反序列化就是 0 = AsSource，等于谁都没被改变。
    /// </summary>
    public LabelGou.Core.Layout.MarkTextCase TextCase { get; set; }

    /// <summary>
    /// ① 步「打开工厂发来的数据」里那张预览表的高度（用户 2026-09-08：「这个表格显示区太小了可以选择扩大」）。
    /// <para><strong>0 = 没记过 = 用默认那一档</strong>（170），所以旧状态文件缺这个字段时行为零变化。</para>
    /// </summary>
    public double PreviewTableHeight { get; set; }

    /// <summary>
    /// 拆出来的 AI 浮动窗口上次摆在哪、多大（用户 2026-09-09 原话「你这个窗口很膈应」，
    /// 上一棒问「要不要把位置与大小也记住」，他今天回了要）。
    /// <para><strong>Width/Height 为 0 = 没记过</strong>，那时贴着主窗右侧开默认尺寸；
    /// 旧状态文件缺这四个字段时行为零变化。</para>
    /// </summary>
    public double AiFloatLeft { get; set; }

    public double AiFloatTop { get; set; }

    public double AiFloatWidth { get; set; }

    public double AiFloatHeight { get; set; }
}

/// <summary>
/// 界面状态存储（M7）。
/// <para>
/// 为什么要它：内置模板与纸规每加一批，启动时都退回"标准 100×80 + A4"，用户每次开工都要
/// 去下拉里重选一遍——于是新做的版式在界面上等于不存在（2026-09-07 用户反馈"打开看没变化"）。
/// </para>
/// <para>
/// 目录可注入是为了单测能指到临时目录，不往用户机器上写（§五-48 那个坑：测试写进真实运行目录）。
/// 读不到或 JSON 坏了就退回默认，<strong>绝不因为状态文件出问题而拦人启动</strong>。
/// </para>
/// </summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _filePath;

    public UiStateStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou");
        _filePath = Path.Combine(dir, "uistate.json");
    }

    /// <summary>状态文件路径（排障时用，界面上不显示）。</summary>
    public string FilePath => _filePath;

    public UiState Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new UiState();
            return JsonSerializer.Deserialize<UiState>(File.ReadAllText(_filePath)) ?? new UiState();
        }
        catch (Exception ex)
        {
            AppLog.Warning("界面状态读取失败，本次退回默认模板与纸规：" + ex.Message);
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(state, Indented));
        }
        catch (Exception ex)
        {
            // 记不住上次选择是可接受的降级，不是故障，所以只留一行日志不弹框
            AppLog.Warning("界面状态保存失败（下次启动回到默认）：" + ex.Message);
        }
    }
}
