using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using LabelGou.App.Export;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.Core.Interop.Cdr;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.App.ViewModels;

/// <summary>
/// 导入窗口里一行的包装：勾选与否、绑哪个字段，都由用户在窗口里点头（定案 D8）。
/// <para><see cref="Candidate"/> 是 Core 侧的对象，勾选与改绑<strong>直接写回它</strong>，
/// 这样 <see cref="TemplateImportPlan.Build"/> 拿到的就是用户最终确认的那份，不存在两份状态。</para>
/// </summary>
public sealed class ImportTextRow : ObservableObject
{
    private readonly Action _changed;

    public ImportTextRow(TextCandidate candidate, IReadOnlyList<ChoiceOption<MarkFieldKey?>> fieldOptions, Action changed)
    {
        Candidate = candidate;
        FieldOptions = fieldOptions;
        _changed = changed;
    }

    public TextCandidate Candidate { get; }

    /// <summary>字段候选表：所有行共用一份，不要 60 行各拷一份。</summary>
    public IReadOnlyList<ChoiceOption<MarkFieldKey?>> FieldOptions { get; }

    public string Content => Candidate.DisplayText;

    public string PositionText => $"{Candidate.XMm:0.#},{Candidate.YMm:0.#} {Candidate.WidthMm:0.#}×{Candidate.HeightMm:0.#}mm {Candidate.SizePt:0.#}pt";

    public string Reason => Candidate.Reason;

    public bool Promote
    {
        get => Candidate.Promote;
        set
        {
            if (Candidate.Promote == value) return;
            Candidate.Promote = value;
            Raise(nameof(Promote));
            _changed();
        }
    }

    /// <summary>绑哪个唛头字段；null = 当固定文字原样印。</summary>
    public ChoiceOption<MarkFieldKey?>? SelectedField
    {
        get => FieldOptions.FirstOrDefault(o => o.Value == Candidate.Field);
        set
        {
            if (value is null || value.Value == Candidate.Field) return;
            Candidate.Field = value.Value;
            Raise(nameof(SelectedField));
            _changed();
        }
    }
}

/// <summary>
/// 「从底稿导入」窗口的大脑：把 <see cref="TemplateImportPlan"/> 摊给人看，收人改完的结果落库。
/// <para>
/// 三件事必须在这里做，而不是散在 XAML 后面：
/// ① <c>.cdr</c> 的预览图<strong>由 App 层编码成 PNG 存进 <c>assets\</c></strong>（Core 不碰 WPF 编码器），
/// 再把相对路径回填给 <see cref="TemplateImportPlan.PreviewReferencePath"/>；
/// ② 只有过了 <see cref="TemplateValidator"/> 且没有 Error 的模板才允许落库；
/// ③ 不弹 MessageBox——窗口自己显示错误文本，主窗口才不会被 modal 框卡住（§七-13）。
/// </para>
/// </summary>
public sealed class TemplateImportViewModel : ObservableObject
{
    private readonly TemplateImportPlan _plan;
    private readonly TemplateStore _store;

    public TemplateImportViewModel(TemplateImportPlan plan, TemplateStore store)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        TemplateName = SuggestName(plan.SourceName);
        LabelWidthText = _plan.LabelWidthMm.ToString("0.###", CultureInfo.InvariantCulture);
        LabelHeightText = _plan.LabelHeightMm.ToString("0.###", CultureInfo.InvariantCulture);

        FieldOptions = BuildFieldOptions();
        Rows = new ObservableCollection<ImportTextRow>(
            _plan.Texts.Select(t => new ImportTextRow(t, FieldOptions, OnRowsChanged)));

        ImportCommand = new RelayCommand(Import, () => CanImport);
        PromoteAllCommand = new RelayCommand(() => SetAll(true));
        PromoteNoneCommand = new RelayCommand(() => SetAll(false));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>勾选与改绑只影响状态行与按钮可用性，直接重发通知就够（导入窗里的行数量级是几十）。</summary>
    private void OnRowsChanged()
    {
        Raise(nameof(StatusLine));
        ImportCommand.RaiseCanExecute();
    }

    public event Action<LabelTemplate>? Saved;

    public event Action? CloseRequested;

    // ---------- 绑定项 ----------

    public RelayCommand ImportCommand { get; }

    public RelayCommand PromoteAllCommand { get; }

    public RelayCommand PromoteNoneCommand { get; }

    public RelayCommand CloseCommand { get; }

    public ObservableCollection<ImportTextRow> Rows { get; }

    /// <summary>字段下拉的共用选项（给 XAML 的 ItemsSource 备用，行内已经各带一份引用）。</summary>
    public IReadOnlyList<ChoiceOption<MarkFieldKey?>> FieldOptions { get; }

    public bool IsCdrRoute => _plan.Source == TemplateImportSource.CdrPreview;

    public string SourceName => _plan.SourceName;

    public string TemplateName
    {
        get => _templateName;
        set => Set(ref _templateName, value);
    }
    private string _templateName = "底稿模板";

    public string LabelWidthText
    {
        get => _labelWidthText;
        set
        {
            if (!Set(ref _labelWidthText, value)) return;
            if (TrySide(value, out var mm)) _plan.LabelWidthMm = mm;
            ImportCommand.RaiseCanExecute();
        }
    }
    private string _labelWidthText = "100";

    public string LabelHeightText
    {
        get => _labelHeightText;
        set
        {
            if (!Set(ref _labelHeightText, value)) return;
            if (TrySide(value, out var mm)) _plan.LabelHeightMm = mm;
            ImportCommand.RaiseCanExecute();
        }
    }
    private string _labelHeightText = "80";

    public IReadOnlyList<string> SummaryLines => _plan.SummaryLines();

    public IReadOnlyList<string> IssueLines
        => _plan.Issues.Where(i => i.Severity != IssueLevel.Info).Select(i => i.Message).ToList();

    /// <summary>一行文字都没提出来时的解释（两条来路原因不同，不要拿一句话给两边用）。</summary>
    public string NoTextHint => IsCdrRoute
        ? "这份 .cdr 只能取到内嵌预览图（约 50DPI，只作对齐参考，不会印到纸上），文字带不出来。\n要拿到保真底图：在 CorelDRAW 里「文件 → 导出 → SVG」再导进来，tools\\cdr 里有批量导出宏。"
        : "这份底稿里没有可提取的文字——多半在 CorelDRAW 里被转曲了。\n底图照样能用，可变字段请在模板编辑器里自己加。";

    /// <summary>导入按钮旁的一行状态：够不够格、差在哪。</summary>
    public string StatusLine
    {
        get
        {
            var problem = BlockingProblem;
            return problem ?? (IsCdrRoute
                ? $"已取到参考底图 {(_plan.Preview is null ? "?" : _plan.Preview.Width + "×" + _plan.Preview.Height)} 像素，只能看不能印；可编辑文字 {PromotedCount} 段。"
                : $"底图 1 个元素位 + 可编辑文字 {PromotedCount} 段 = 模板共 {1 + PromotedCount} 个元素（上限 {TemplateValidator.MaxElements}）。");
        }
    }

    public bool CanImport => BlockingProblem is null;

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }
    private string _errorText = string.Empty;

    private int PromotedCount => _plan.PromotedCount;

    /// <summary>为什么现在还不许按导入（null = 可以）。</summary>
    private string? BlockingProblem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TemplateName)) return "先给模板起个名字。";
            if (!TrySide(LabelWidthText, out var w) || !TrySide(LabelHeightText, out var h))
                return "标签宽高必须是数字（毫米）。";
            if (w < TemplateValidator.MinLabelSideMm || w > TemplateValidator.MaxLabelSideMm
                || h < TemplateValidator.MinLabelSideMm || h > TemplateValidator.MaxLabelSideMm)
                return $"标签边长必须在 {TemplateValidator.MinLabelSideMm}~{TemplateValidator.MaxLabelSideMm}mm 之间。";
            if (!IsCdrRoute && _plan.Document is null) return "底稿没解析成功，关掉重开一份吧。";
            if (IsCdrRoute && _plan.Preview is null) return "这份 .cdr 里没找到内嵌预览图，请按窗口下方的说明导出 SVG 再导入。";
            if (!IsCdrRoute && !_plan.HasVectorBackground && PromotedCount == 0) return "这份底稿里没有可印的东西（既没几何也没提升文字）。";
            if (PromotedCount > TemplateImportPlan.PromotableLimit)
                return $"可编辑文字最多 {TemplateImportPlan.PromotableLimit} 段，请先取消一部分。";
            return null;
        }
    }

    // ---------- 入口 ----------

    /// <summary>
    /// 按扩展名分三态入口（定案：文件框同时收 <c>.svg</c> 与 <c>.cdr</c>）。
    /// <para>失败时返回一句人话加一条出路，不抛异常也不留半份模板。</para>
    /// </summary>
    public static (TemplateImportViewModel? ViewModel, string? Error) Open(string path, TemplateStore store)
    {
        if (!File.Exists(path)) return (null, "文件打不开：它不在原来的位置了，或被其它程序占用。");

        var isCdr = Path.GetExtension(path).Equals(".cdr", StringComparison.OrdinalIgnoreCase);
        var plan = isCdr ? TemplateImporter.FromCdrFile(path) : TemplateImporter.FromSvgFile(path);
        var errors = plan.Issues.ErrorMessages();
        if (errors.Count > 0) return (null, string.Join("\n", errors));

        return (new TemplateImportViewModel(plan, store), null);
    }

    // ---------- 动作 ----------

    private void SetAll(bool promote)
    {
        foreach (var row in Rows) row.Promote = promote;
    }

    private void Import()
    {
        ErrorText = string.Empty;
        try
        {
            if (IsCdrRoute) MaterializePreviewAsset();

            var (template, issues) = _plan.Build(TemplateName.Trim(), _store);
            var blocking = issues.Where(i => i.Severity == IssueLevel.Error).ToList();
            if (blocking.Count > 0)
            {
                ErrorText = "这份模板还没合格：\n" + string.Join("\n", blocking.Select(i => "· " + i.Message));
                AppLog.Warning($"底稿导入被校验拦下：{SourceName} → {string.Join(" / ", blocking.Select(i => i.Message))}");
                return;
            }

            var (saved, fileName, saveIssues) = _store.Save(template);
            if (!saved)
            {
                ErrorText = "存进模板库失败：\n" + string.Join("\n", saveIssues.Select(i => "· " + i.Message));
                return;
            }

            var warnings = issues.Where(i => i.Severity == IssueLevel.Warning).Select(i => i.Message).ToList();
            AppLog.Info($"底稿导入成功：{SourceName} → {fileName}（{template.Elements.Count} 个元素，{warnings.Count} 条告警）");
            Saved?.Invoke(template);
        }
        catch (Exception ex)
        {
            ErrorText = "导入过程中出错了：" + ex.Message;
            AppLog.Error("底稿导入失败", ex);
        }
    }

    /// <summary>
    /// 把 <c>.cdr</c> 里抠出来的预览图落成 <c>assets\*.png</c>，并把相对路径回填给计划。
    /// <para>Core 只用 BCL 拿到像素，编码成品必须靠 WPF 的位图编码器，所以这一步天然属于 App 层。</para>
    /// </summary>
    private void MaterializePreviewAsset()
    {
        var preview = _plan.Preview;
        if (preview is null) return;

        var assetName = TemplateStore.SafeAssetName(TemplateName) + ".reference";
        byte[] png;
        try
        {
            var source = Decode(preview);
            png = PageRasterizer.EncodePng(source);
        }
        catch (Exception ex)
        {
            // 预览图解不开（比如 CDR 塞的是个 WPF 不认的 WMF 变体）：宁可不带参考图，也不拦着人建模板
            AppLog.Warning($"底稿预览图编码失败，模板将不含参考图：{ex.Message}");
            _plan.PreviewReferencePath = null;
            return;
        }

        _plan.PreviewReferencePath = _store.SaveAsset(assetName + ".png", png);
    }

    private static BitmapSource Decode(CdrPreview preview)
    {
        if (preview.Pixels is { Length: > 0 } pixels)
        {
            return BitmapSource.Create(preview.Width, preview.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, preview.Width * 4);
        }

        var bytes = preview.EncodedBytes ?? Array.Empty<byte>();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TrySide(string text, out double mm)
        => double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out mm)
            || double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mm);

    private static string SuggestName(string sourceFileName)
    {
        var head = Path.GetFileNameWithoutExtension(sourceFileName ?? string.Empty);
        foreach (var bad in Path.GetInvalidFileNameChars()) head = head.Replace(bad, '_');
        head = head.Trim();
        if (head.Length == 0) head = "底稿模板";
        return head.Length > 40 ? head[..40] : head;
    }

    private static IReadOnlyList<ChoiceOption<MarkFieldKey?>> BuildFieldOptions()
    {
        var list = new List<ChoiceOption<MarkFieldKey?>>
        {
            new(null, "（不绑字段，按固定文字印）"),
        };
        list.AddRange(MarkFieldCatalog.Mappable.Select(def => new ChoiceOption<MarkFieldKey?>(def.Key, $"{def.ChineseName} · {def.EnglishLabel}")));
        return list;
    }
}
