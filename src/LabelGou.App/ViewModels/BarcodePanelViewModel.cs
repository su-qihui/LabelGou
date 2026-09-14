using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Mvvm;
using LabelGou.App.Rendering;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.ViewModels;

/// <summary>条码摆在标签上的哪个位置（三种够用了：通栏底部、通栏顶部、右上角）。</summary>
public enum BarcodePlacement
{
    /// <summary>底部通栏——最常见的箱唛摆法。</summary>
    Bottom = 0,

    /// <summary>顶部通栏。</summary>

    Top = 1,

    /// <summary>右上角——TOP 那张样张的 EAN-13 就在那儿。</summary>
    TopRight = 2,
}

/// <summary>
/// ③ 步里的「条码」栏目：选制式、选它读表里哪一列、摆哪儿，然后加到当前模板上。
/// <para><strong>为什么要单独一个栏目而不是让人去模板编辑器里拖</strong>（用户 2026-09-09：
/// 「<strong>加入条码栏目——条码一般是不同的表格里会有数字要对应填入调成正确的</strong>」）：
/// 每家表的条码列名都不一样（条码 / BARCODE / 国际条码 / GTIN / ITEM NO 后面那串数字），
/// 所以这里直接把<strong>这张表真有的列</strong>列出来给他指，而不是让他猜占位符怎么写。</para>
/// <para><strong>「调成正确的」这一半由 Core 的编码器负责</strong>：EAN-13 给 12 位就补第 13 位校验码、
/// ITF-14 给 13 位就补第 14 位，且补了什么一定写在结果里；校验位本来就错、或列里有装不下的字符，
/// 一律挡下来报错而不是硬画。这一栏下面那句实时预览就是让他点之前先看见会编成什么。</para>
/// <para>加条码这件事本身走 <see cref="_apply"/> 回主 VM：内置模板先另存副本再改，
/// 与编辑器改模板是同一条落盘路（不开第二条写模板的路）。</para>
/// </summary>
public sealed class BarcodePanelViewModel : ObservableObject
{
    /// <summary>下拉里代表「不读表、整批一个码」的那一项。</summary>
    public const string FixedSourceOption = "（手填固定数字）";

    private readonly ILabelSource _source;
    private readonly Func<TemplateElement, string, (bool Ok, string Message)> _apply;

    private ChoiceOption<BarcodeSymbology>? _selectedSymbology;
    private string? _selectedSource;
    private string _fixedText = string.Empty;
    private ChoiceOption<BarcodePlacement>? _selectedPlacement;
    private bool _showText = true;
    private string _preview = "先选它读哪一列。";

    // 第 58 棒：条码的尺寸照 CorelDRAW 条码向导那四格（从前这里是一格写死的「高(mm)」，
    // 用户 2026-09-14：「条码还是扁的……把人家框架抄过来」）。
    private int _dpi = BarcodeSizing.DefaultDpi;
    private double _scalePercent = BarcodeSizing.DefaultScalePercent;
    private double _heightFactor = BarcodeSizing.DefaultHeightFactor;
    private double _widthReductionPx = BarcodeSizing.DefaultWidthReductionPx;

    /// <summary>试编那一次真正编出来的编码——摆框的尺寸由它和上面四格算出来（编不出来就是 null）。</summary>
    private BarcodeEncoding? _probeEncoding;

    /// <summary>当前四格拼出的尺寸参数（写进模板元素的就是它，schema v12）。</summary>
    public BarcodeSizing Sizing => new(_dpi, _scalePercent, _heightFactor, _widthReductionPx);

    public BarcodePanelViewModel(ILabelSource source, Func<TemplateElement, string, (bool Ok, string Message)> apply)
    {
        _source = source;
        _apply = apply;

        // 顺序按「店里用得上的排前面」，不按枚举值——枚举值是持久化用的（见 BarcodeSymbology 的注释），
        // 新增制式时只能往后追加，所以下拉得自己排一份。
        SymbologyOptions = new List<ChoiceOption<BarcodeSymbology>>
        {
            new(BarcodeSymbology.Code128, BarcodeSymbology.Code128.DisplayName()),
            new(BarcodeSymbology.Ean13, BarcodeSymbology.Ean13.DisplayName()),
            new(BarcodeSymbology.Ean8, BarcodeSymbology.Ean8.DisplayName()),
            new(BarcodeSymbology.UpcA, BarcodeSymbology.UpcA.DisplayName()),
            new(BarcodeSymbology.UpcE, BarcodeSymbology.UpcE.DisplayName()),
            new(BarcodeSymbology.Code39, BarcodeSymbology.Code39.DisplayName()),
            new(BarcodeSymbology.Itf14, BarcodeSymbology.Itf14.DisplayName()),
            new(BarcodeSymbology.Itf, BarcodeSymbology.Itf.DisplayName()),
            // 25 码紧跟 ITF：两者都叫「二五码」却是两种码，摆一起才看得区别（见 DisplayName 与 DataHint）
            new(BarcodeSymbology.Code25, BarcodeSymbology.Code25.DisplayName()),
            new(BarcodeSymbology.Codabar, BarcodeSymbology.Codabar.DisplayName()),
            new(BarcodeSymbology.Msi, BarcodeSymbology.Msi.DisplayName()),
            new(BarcodeSymbology.Jan8, BarcodeSymbology.Jan8.DisplayName()),
            new(BarcodeSymbology.Jan13, BarcodeSymbology.Jan13.DisplayName()),
            new(BarcodeSymbology.Isbn, BarcodeSymbology.Isbn.DisplayName()),
            new(BarcodeSymbology.Issn, BarcodeSymbology.Issn.DisplayName()),
        };
        SelectedSymbology = SymbologyOptions[0];

        PlacementOptions = new List<ChoiceOption<BarcodePlacement>>
        {
            new(BarcodePlacement.Bottom, "底部通栏"),
            new(BarcodePlacement.Top, "顶部通栏"),
            new(BarcodePlacement.TopRight, "右上角"),
        };
        SelectedPlacement = PlacementOptions[0];

        AddBarcodeCommand = new RelayCommand(_ => AddBarcode(), _ => SelectedSource is not null || _fixedText.Trim().Length > 0);
        // 构造时不试编：那时表还没进来，编一次只会拿到空版面。导表后由 RefreshSources 走第一次。
    }

    /// <summary>四种制式。只做了店里用得上的这四个，其余的（UPC-A、Codabar…）等真有人要再加。</summary>
    public IReadOnlyList<ChoiceOption<BarcodeSymbology>> SymbologyOptions { get; }

    public IReadOnlyList<ChoiceOption<BarcodePlacement>> PlacementOptions { get; }

    /// <summary>表里真有的列 + 手填固定数字。整个下拉随导入的表变。</summary>
    public IReadOnlyList<string> SourceOptions { get; private set; } = Array.Empty<string>();

    public RelayCommand AddBarcodeCommand { get; }

    public ChoiceOption<BarcodeSymbology>? SelectedSymbology
    {
        get => _selectedSymbology;
        set
        {
            if (Set(ref _selectedSymbology, value)) Refresh();
        }
    }

    public ChoiceOption<BarcodePlacement>? SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (Set(ref _selectedPlacement, value)) Refresh();
        }
    }

    /// <summary>条码读哪一列（"（手填固定数字）" 除外）。</summary>
    public string? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (Set(ref _selectedSource, value))
            {
                AddBarcodeCommand.RaiseCanExecute();
                Raise(nameof(IsFixedTextEntry));
                Refresh();
            }
        }
    }

    /// <summary>只有选了「手填固定数字」那一格，下面那个手填框才可输入——否则会让人以为填了它也生效。</summary>
    public bool IsFixedTextEntry => string.Equals(SelectedSource, FixedSourceOption, StringComparison.Ordinal);

    /// <summary>手填的那串固定数字（整批同一个码时才用）。</summary>
    public string FixedText
    {
        get => _fixedText;
        set
        {
            if (Set(ref _fixedText, value))
            {
                AddBarcodeCommand.RaiseCanExecute();
                Refresh();
            }
        }
    }

    /// <summary>打印机分辨率（dpi）——向导默认 300。X 尺寸要向上取整到这台机器的整像素，选 600 会更细一档。</summary>
    public int Dpi
    {
        get => _dpi;
        set
        {
            if (Set(ref _dpi, Math.Clamp(value, 72, 2400))) Refresh();
        }
    }

    /// <summary>缩放比例（%）：100 % 就是 CDR 的标称 X（0.013 英寸 @300dpi = 4 像素）。要更宽的码就加大它——高会跟着长，那是规矩。</summary>
    public double ScalePercent
    {
        get => _scalePercent;
        set
        {
            if (Set(ref _scalePercent, Math.Clamp(value, 1, 1000))) Refresh();
        }
    }

    /// <summary>条形码高度倍数——向导那格「条形码高度(H)」，只乘在高上。想要"宽而不高"是加大缩放＋调小这个倍数，不是压扁框。</summary>
    public double HeightFactor
    {
        get => _heightFactor;
        set
        {
            if (Set(ref _heightFactor, Math.Clamp(value, 0.1, 5))) Refresh();
        }
    }

    /// <summary>条形码宽度减少值（像素）：出纸时每根条两侧各让半个像素，补偿网点增益。不参与符号宽度。</summary>
    public double WidthReductionPx
    {
        get => _widthReductionPx;
        set
        {
            if (Set(ref _widthReductionPx, Math.Clamp(value, 0, 8))) Refresh();
        }
    }

    /// <summary>只读那一格：向导上的「符号宽度」——窄元素数 × X（不含静区）。</summary>
    public string SymbolWidthText => _probeEncoding is { } enc
        ? $"符号宽度 {Sizing.SymbolWidthMm(enc):0.###} mm（X = {Sizing.ModuleMm:0.###} mm，" +
          $"{BarcodeSizing.NarrowElements(enc)} 个窄元素）"
        : "符号宽度：选好列、编出来才知道（它＝窄元素数 × X）。";

    public bool ShowText
    {
        get => _showText;
        set
        {
            if (Set(ref _showText, value)) Refresh();
        }
    }

    /// <summary>实时预览：拿这张表第一行真值试编一次，把会编成什么/为什么编不出来直接摊开。</summary>
    public string Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    private ImageSource? _previewImage;

    /// <summary>
    /// 条码的<strong>实时预览图</strong>（条码栏目下面那块空白，用户 2026-09-11 圈的）。
    /// <para>改制式、换列、改高度、改「印不印数字」都立即重画——就是 Corel 向导里那个
    /// 「样本预览」的作用：选完能看见长什么样，不用加到模板再回头改。</para>
    /// </summary>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set => Set(ref _previewImage, value);
    }

    public Brush PreviewBrush => _preview.StartsWith("编不出来", StringComparison.Ordinal)
        ? new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E))
        : new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x55));

    /// <summary>导入新表之后重列一遍可选的列。</summary>
    public void RefreshSources()
    {
        var keep = SelectedSource;
        var options = new List<string>();
        foreach (var column in _source.ColumnHeaders)
            options.Add(column);
        options.Add(FixedSourceOption);
        SourceOptions = options;
        Raise(nameof(SourceOptions));
        SelectedSource = options.Contains(keep ?? string.Empty, StringComparer.Ordinal) ? keep : DefaultSource(options);
        AddBarcodeCommand.RaiseCanExecute();
        Refresh();
    }

    /// <summary>默认帮人选一列：表里名字带「条码/barcode/gtin/编码」的那一列，没有就第一列（不猜数字列）。</summary>
    private static string? DefaultSource(IReadOnlyList<string> options)
    {
        foreach (var name in options)
        {
            if (name.Contains("条码", StringComparison.Ordinal) ||
                name.Contains("barcode", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("gtin", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ean", StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;      // 认不出来就不替人选：选错一列编出一个不相干的码，比空着更糟
    }

    /// <summary>当前选择对应的占位符（或手填的那串数字）。</summary>
    private string DataExpression()
    {
        var name = SelectedSource;
        if (name is null) return string.Empty;
        if (name == FixedSourceOption) return _fixedText.Trim();

        // 这一列已经连到某个内置字段了 → 用字段占位符。连上的列不会另存一份 col: 键，
        // 拿 {{col:那一列}} 去取会取空（④ 步「按哪一列数张数」踩过同一个坑）。
        var bound = _source.FieldBoundToColumn(name);
        return bound is { } key ? "{{" + key + "}}" : "{{col:" + name + "}}";
    }

    /// <summary>按当前选择造一个条码元素（还没落位到具体模板尺寸，位置在 <see cref="PlaceInto"/> 里算）。</summary>
    private TemplateElement BuildElement() => new()
    {
        Kind = ElementKind.Barcode,
        Text = DataExpression(),
        Symbology = SelectedSymbology?.Value ?? BarcodeSymbology.Code128,
        ShowBarcodeText = ShowText,
        FontSizePt = 8,
        ThicknessMm = 0.35,
        Visible = true,
    };

    /// <summary>摆放位置空出来的那条带：左边界、可用宽，以及它是贴在标签底边还是顶边。</summary>
    private (double Left, double Width, bool AtBottom) SlotOf(LabelTemplate template)
    {
        var margin = Math.Min(4, template.WidthMm * 0.04);
        return (SelectedPlacement?.Value ?? BarcodePlacement.Bottom) switch
        {
            BarcodePlacement.Top => (margin, template.WidthMm - margin * 2, false),
            BarcodePlacement.TopRight => (template.WidthMm * 0.5 + margin / 2, template.WidthMm * 0.5 - margin * 1.5, false),
            _ => (margin, template.WidthMm - margin * 2, true),
        };
    }

    /// <summary>
    /// 这只码在该模板上实际用的尺寸参数与框大小（第 58 棒）。
    /// <para>尺寸由「X 尺寸 × 窄元素数」算出来（CDR 的账），<strong>不是</strong>由框宽反算；
    /// 只有当算出来的框塞不进这条位置时，才把缩放比例降到位——降的是 X（仍取整像素），不是把条压扁。</para>
    /// <para>还没编出来（第一行没值、校验位不对…）时无从知道窄元素数，退回「铺满这条位置 + 14 mm 高」，
    /// 与从前那一格「高(mm)=14」同形，不至于给不出框。</para>
    /// </summary>
    private (BarcodeSizing Sizing, double Width, double Height) SizingFor(LabelTemplate? template, bool fit)
    {
        var sizing = Sizing;
        var margin = template is null ? 4 : Math.Min(4, template.WidthMm * 0.04);
        var availWidth = template is null ? 80.0 : SlotOf(template).Width;
        var availHeight = template is null ? 20.0 : Math.Max(6, template.HeightMm - margin * 2);
        if (_probeEncoding is not { } encoding) return (sizing, availWidth, Math.Min(14, availHeight));

        var (width, height) = sizing.BoxOf(encoding, ShowText);
        if (fit)
        {
            sizing = sizing.FittedTo(encoding, availWidth, availHeight, ShowText);
            (width, height) = sizing.BoxOf(encoding, ShowText);
        }
        return (sizing, width, height);
    }

    /// <summary>
    /// 把元素摆进具体那张标签的毫米框里：宽按摆放位置给（通栏留 4 mm 页边，右上角占右半幅），
    /// <strong>框的大小由 X 尺寸算</strong>（第 58 棒），算出来比这条位置窄就在里面水平居中——
    /// 绝不再把条码拉去填满框（那就是他报的"扁"）。
    /// </summary>
    private void PlaceInto(TemplateElement element, LabelTemplate template)
    {
        var margin = Math.Min(4, template.WidthMm * 0.04);
        var (left, availWidth, atBottom) = SlotOf(template);
        var (sizing, width, height) = SizingFor(template, fit: true);
        element.BarcodeSize = sizing;
        element.X = left + Math.Max(0, (availWidth - width) / 2);
        element.Y = atBottom ? template.HeightMm - height - margin : margin;
        element.Width = width;
        element.Height = height;
    }

    /// <summary>
    /// 拿这张表第一行真值试编一次（走的就是生产那条 <see cref="LayoutEngine"/> 路，不另写一套判据）。
    /// <para><strong>两趟</strong>：第一趟只为问出「这一格在第一行到底是什么值」——X 尺寸算出来的符号宽度
    /// 要先知道这串数字编出来占几个窄元素，而算框之前它是未知的；第二趟换成算出来的真框，
    /// 于是预览里的比例、模块宽、条数就是加到模板之后会得到的那一份。</para>
    /// </summary>
    private void Refresh()
    {
        _probeEncoding = null;
        var expression = DataExpression();
        var symbology = SelectedSymbology?.Value ?? BarcodeSymbology.Code128;
        if (expression.Length == 0)
        {
            Preview = SelectedSource == FixedSourceOption
                ? "还没填那串数字。"
                : "还没选列。表里的列名都在下面这个下拉里——条码在哪一列就选哪一列。";
            PreviewImage = null;
            Raise(nameof(SymbolWidthText));
            return;
        }

        // 预览按「加到模板后」的实际尺寸画——用户 2026-09-11：「按照实际效果来而不是固定的这个数值压成什么样了」。
        var template = _source.Template;
        var (_, probeW, probeH) = SizingFor(template, fit: false);
        var (layout, item) = Probe(expression, symbology, probeW, probeH);
        if (item is null)
        {
            Preview = "这一格在这张表的第一行没值（所以它不会画）。换一列，或先核对那一列是不是真的空着。";
            PreviewImage = null;
            Raise(nameof(SymbolWidthText));
            return;
        }
        if (item.Error is { Length: > 0 } error)
        {
            Preview = "编不出来：" + error;
            PreviewImage = null;
            Raise(nameof(SymbolWidthText));
            return;
        }

        // 编得出来才知道窄元素数 → X 尺寸与符号宽度才算得出（同一台编码器，不开第二套判据）。
        _probeEncoding = BarcodeEncoder.Encode(item.Data, symbology);
        var (sizing, barW, barH) = SizingFor(template, fit: true);
        var (second, secondItem) = Probe(expression, symbology, barW, barH);
        layout = second;
        item = secondItem ?? item;

        var note = item.Data.Equals(expression, StringComparison.Ordinal) ? string.Empty : "（已按规范补齐）";
        Preview = $"第一行会编成 {item.Data}{note}：{symbology.ShortName()}，{item.Bars.Count} 根条，" +
                  $"X = {sizing.ModuleMm:0.###} mm（缩放 {sizing.ScalePercent:0.#} %、高度 ×{sizing.HeightFactor:0.##}），" +
                  $"条码带 {barW:0.##} × {barH:0.##} mm。{(item.Warning is { Length: > 0 } w ? "注意：" + w : string.Empty)}";
        Raise(nameof(SymbolWidthText));
        RenderPreview(layout);
    }

    /// <summary>把这一格的第一行真值装进一只指定大小的框里排一次版——试编的两趟与预览图都从这儿来。</summary>
    private (LabelLayout Layout, BarcodeItem? Item) Probe(string expression, BarcodeSymbology symbology,
        double widthMm, double heightMm)
    {
        var probe = new LabelTemplate { WidthMm = widthMm, HeightMm = heightMm, BorderMm = 0 };
        probe.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = expression,
            Symbology = symbology,
            ShowBarcodeText = ShowText,
            FontSizePt = 8,
            BarcodeSize = Sizing,
            X = 0,
            Y = 0,
            Width = widthMm,
            Height = heightMm,
        });
        var record = _source.RawRecords.FirstOrDefault() ?? SampleRecords.StandardSample();
        var layout = LayoutEngine.Build(probe, record, new LayoutContext(1, Math.Max(1, _source.RawRecords.Count), "试编"));
        return (layout, layout.Items.OfType<BarcodeItem>().FirstOrDefault());
    }

    /// <summary>把预览版面画成位图（走 <see cref="LabelRenderer"/> 同一条画法——预览和正式输出必须一张脸）。</summary>
    private void RenderPreview(LabelLayout layout)
    {
        try
        {
            const double previewPx = 340;
            var scale = previewPx / Mm.ToDiu(layout.WidthMm);
            var w = (int)Math.Ceiling(Mm.ToDiu(layout.WidthMm) * scale);
            var h = (int)Math.Ceiling(Mm.ToDiu(layout.HeightMm) * scale);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                LabelRenderer.Draw(dc, layout, scale, offsetX: 0, offsetY: 0,
                    showGuides: false, pixelsPerDip: 1.0, drawBackground: true);
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            PreviewImage = bmp;
        }
        catch
        {
            // 预览画不出来不该把整个面板带崩：文字预览还在，用户照样能往下走
            PreviewImage = null;
        }
    }

    /// <summary>
    /// 加到当前模板。摆放尺寸按当前模板的真尺寸算，之后仍可在模板编辑器里拖。
    /// <para>内置模板不直接改：<see cref="_apply"/> 那侧会先另存成用户副本（与「编辑模板」同一条路）。</para>
    /// </summary>
    private void AddBarcode()
    {
        var template = _source.Template;
        if (template is null)
        {
            Preview = "还没选模板：先回上面选一份唛头模板。";
            return;
        }

        var element = BuildElement();
        PlaceInto(element, template);
        var description = $"{SelectedSymbology?.Value.ShortName()} 条码 · " +
                          (SelectedSource == FixedSourceOption ? $"固定 {element.Text}" : $"读「{SelectedSource}」这一列") +
                          $" · {SelectedPlacement?.Label}";
        var (ok, message) = _apply(element, description);
        Preview = (ok ? "已加到模板：" : "没加上去：") + message;
        Refresh();
    }
}
