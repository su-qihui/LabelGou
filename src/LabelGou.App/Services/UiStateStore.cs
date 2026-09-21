using System;
using System.IO;
using System.Text.Json;

namespace LabelGou.App.Services;

/// <summary>
/// 界面状态：上次用的模板、纸规、模板→纸规的绑定、文字大小写与① 步表格高度。刻意只记这几样，不做"什么都能记住"。
/// </summary>
public sealed class UiState
{
    /// <summary>上次选中的内置/用户模板 id（<c>builtin.xxx</c> 或 <c>user.xxx</c>）。</summary>
    public string? TemplateId { get; set; }

    /// <summary>上次选中的纸规 id。</summary>
    public string? SheetSpecId { get; set; }

    /// <summary>
    /// 模板 → 纸规 的绑定（第 68 棒，用户 2026-09-14：「模版和纸归是绑定的，若后续再次使用那个模板
    /// 纸归也会变成此模板对应纸归」）。记的是<strong>他上一次为这份模板挑的那张纸</strong>，
    /// 所以下次再用这份模板时纸规跟着回来；他没挑过（这一格没有）就退回按单枚尺寸找预设档。
    /// <para><strong>null = 没记过</strong>：旧状态文件缺这个字段时行为零变化（只按尺寸预设档走）。</para>
    /// </summary>
    public Dictionary<string, string>? TemplateSheetIds { get; set; }

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

    /// <summary>
    /// AI 那块上次停在哪个泊位（用户 2026-09-09：「拉到右侧可以吸附」）。
    /// <para>存的是 <c>"Bottom"</c> / <c>"Right"</c> 这样的字符串而不是枚举值：认不出的旧字符串能当场退回默认，
    /// 而越界的枚举数字反序列化得回来，却要等到用的时候才发现没有那个泊位。</para>
    /// <para><strong>空 = 没记过 = 右栏</strong>（第 19 棒，用户 2026-09-09：「默认打开软件是左栏导数选模版 中栏是预览 右栏是AI」）。
    /// 旧版的默认是底部那一行，而那一行正是他说「不要显示在中栏下面」的那一个——所以这一格不是「零变化」，
    /// 是一次有意改默认；真存过 <c>"Bottom"</c> 的仍按 <c>Bottom</c>（只有跟「右栏窄条」撞在一起时才搬家，见 <c>DockSnap.ReconcileRightPane</c>）。
    /// 浮动不存：关软件时 AI 正飘着，下次启动不该莫名多开一个窗口，所以存的是「上次停靠的那一个」。</para>
    /// </summary>
    public string AiDockSite { get; set; } = "";

    /// <summary>上次吸到右栏时那一栏多宽（0 = 没记过 = 用 <c>DockSnap.DefaultRightColumnDip</c>）。</summary>
    public double AiRightColumnWidth { get; set; }

    /// <summary>
    /// 左栏（五步向导）与右栏（吸过来的 AI）各自停在哪一态：<c>"Open"</c> / <c>"Narrow"</c> / <c>"Closed"</c>。
    /// <para>用户 2026-09-09：「两边稍微缩一点，然后再可以关闭左边或右边」——缩与关都得留到下次启动，
    /// 不然每天开软件都要重收一遍。</para>
    /// <para>存名字而不是枚举值，而且<strong>空 = 没记过 = 展开</strong>：旧状态文件缺这两个字段时行为零变化。</para>
    /// </summary>
    public string LeftPaneMode { get; set; } = "";

    public string RightPaneMode { get; set; } = "";

    /// <summary>
    /// 运行模式：<c>"ai"</c> / <c>"offline"</c>；**空 = 没记过 = AI 模式**（第 30 棒）。
    /// <para>与上面左右栏那两个不一样：那两个「空 = 展开」是为了让旧状态文件的行为零变化，
    /// 这一个「空 = AI」是**用户 2026-09-10 明确要的默认**——导入表格后先不绑定，交给 AI 读懂再由它绑。</para>
    /// <para>同样是存名字不存枚举值：序号以后重排就全错，名字坏了也只是退回默认。</para>
    /// </summary>
    public string RunMode { get; set; } = "";

    /// <summary>
    /// 简洁版壳窗左右两根栏的宽度（第 94 棒：三栏骨架，中间预览为主导）。
    /// <para><strong>0 = 没记过 = 默认</strong>（左 560、右 440），旧状态文件缺这两格行为零变化。</para>
    /// </summary>
    public double LeftPaneWidth { get; set; }

    public double RightPaneWidth { get; set; }

    /// <summary>
    /// 两根栏上次开没开。<strong>null = 没记过 = 左关右开</strong>（用户 2026-09-21 指着 Qoder 界面说的口径：
    /// 以中间预览为主，左右随时开关——默认收起左栏，中间才留得住 ~840 像素看纸）。
    /// </summary>
    public bool? LeftPaneOpen { get; set; }

    public bool? RightPaneOpen { get; set; }
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
