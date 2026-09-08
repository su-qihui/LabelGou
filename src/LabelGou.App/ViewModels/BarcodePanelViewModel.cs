using System.Windows.Media;
using LabelGou.App.Mvvm;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

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
    private double _heightMm = 14;
    private bool _showText = true;
    private string _preview = "先选它读哪一列。";

    public BarcodePanelViewModel(ILabelSource source, Func<TemplateElement, string, (bool Ok, string Message)> apply)
    {
        _source = source;
        _apply = apply;

        SymbologyOptions = new List<ChoiceOption<BarcodeSymbology>>(
            from BarcodeSymbology s in Enum.GetValues(typeof(BarcodeSymbology))
            select new ChoiceOption<BarcodeSymbology>(s, s.DisplayName()));
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

    /// <summary>条码带的高（毫米，含下面那行可读数字）。</summary>
    public double HeightMm
    {
        get => _heightMm;
        set
        {
            if (Set(ref _heightMm, Math.Max(6, value))) Refresh();
        }
    }

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

    /// <summary>把元素摆进具体那张标签的毫米框里。通栏留 4 mm 页边，右上角占右半幅。</summary>
    private void PlaceInto(TemplateElement element, LabelTemplate template)
    {
        var margin = Math.Min(4, template.WidthMm * 0.04);
        var height = Math.Min(_heightMm, template.HeightMm - margin * 2);
        switch (SelectedPlacement?.Value ?? BarcodePlacement.Bottom)
        {
            case BarcodePlacement.Top:
                element.X = margin;
                element.Y = margin;
                element.Width = template.WidthMm - margin * 2;
                element.Height = height;
                break;

            case BarcodePlacement.TopRight:
                element.X = template.WidthMm * 0.5 + margin / 2;
                element.Y = margin;
                element.Width = template.WidthMm * 0.5 - margin * 1.5;
                element.Height = height;
                break;

            default:
                element.X = margin;
                element.Y = template.HeightMm - height - margin;
                element.Width = template.WidthMm - margin * 2;
                element.Height = height;
                break;
        }
    }

    /// <summary>拿这张表第一行真值试编一次（走的就是生产那条 <see cref="LayoutEngine"/> 路，不另写一套判据）。</summary>
    private void Refresh()
    {
        var expression = DataExpression();
        var symbology = SelectedSymbology?.Value ?? BarcodeSymbology.Code128;
        if (expression.Length == 0)
        {
            Preview = SelectedSource == FixedSourceOption
                ? "还没填那串数字。"
                : "还没选列。表里的列名都在下面这个下拉里——条码在哪一列就选哪一列。";
            return;
        }

        var probe = new LabelTemplate { WidthMm = 400, HeightMm = 300, BorderMm = 0 };
        probe.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = expression,
            Symbology = symbology,
            ShowBarcodeText = ShowText,
            FontSizePt = 8,
            X = 4,
            Y = 4,
            Width = 392,
            Height = Math.Max(10, _heightMm),
        });

        var record = _source.RawRecords.FirstOrDefault() ?? SampleRecords.StandardSample();
        var layout = LayoutEngine.Build(probe, record, new LayoutContext(1, Math.Max(1, _source.RawRecords.Count), "试编"));
        var item = layout.Items.OfType<BarcodeItem>().FirstOrDefault();
        if (item is null)
        {
            Preview = "这一格在这张表的第一行没值（所以它不会画）。换一列，或先核对那一列是不是真的空着。";
            return;
        }
        if (item.Error is { Length: > 0 } error)
        {
            Preview = "编不出来：" + error;
            return;
        }

        var note = item.Data.Equals(expression, StringComparison.Ordinal) ? string.Empty : "（已按规范补齐）";
        Preview = $"第一行会编成 {item.Data}{note}：{symbology.ShortName()}，{item.Bars.Count} 根条，" +
                  $"模块 {item.ModuleMm:0.00} mm。{(item.Warning is { Length: > 0 } w ? "注意：" + w : string.Empty)}";
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
