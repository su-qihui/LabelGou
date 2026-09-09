namespace LabelGou.Core.Data;

/// <summary>
/// 数据导入统一入口：按扩展名分派到 CSV / XLSX 读取器，再识别表头生成 <see cref="TabularData"/>。
/// </summary>
public static class TableImporter
{
    /// <summary>支持的数据源扩展名（M1 只吃表格；Word/照片属 M6 AI Agent）。</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        new[] { ".xlsx", ".xlsm", ".csv", ".tsv", ".txt" };

    /// <summary>界面文件过滤器。</summary>
    public const string OpenFileFilter =
        "支持的表格文件 (*.xlsx;*.xlsm;*.csv;*.tsv;*.txt)|*.xlsx;*.xlsm;*.csv;*.tsv;*.txt|Excel 工作簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV 文件 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件 (*.*)|*.*";

    public static bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>列出可选工作表（CSV 返回单个 "CSV"）。</summary>
    public static IReadOnlyList<string> ListSheets(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm")
        {
            try
            {
                return XlsxTableReader.ListSheetNames(filePath);
            }
            catch (InvalidDataException)
            {
                return new List<string> { "Sheet1" };
            }
        }
        return new List<string> { "CSV" };
    }

    /// <summary>
    /// 导入文件。<paramref name="sheetName"/> 仅对 XLSX 有效。
    /// </summary>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="NotSupportedException">扩展名不支持。</exception>
    /// <exception cref="InvalidDataException">文件损坏/没有可用数据。</exception>
    public static TabularData Import(string filePath, string? sheetName = null)
        => Import(filePath, sheetName, null);

    /// <summary>
    /// 按一份切表指令导入（第 21 棒：AI 说「表头在第 3 行」或「第 412 行是合计行」，软件照着重切）。
    /// <para>指令只做三件事，不造任何判断：<strong>表头那一行改到哪个位置、有没有表头、那几行不当数据</strong>。
    /// 几何、字段、数量合法性一律由下游校验（映射与拼版）说，这里只防两种会把自己弄空的指令：</p>
    /// <list type="bullet">
    ///   <item>剔完一行的都不剩 → 直接拒（宁可不切，也不能默默交出一张 0 行的表）；</item>
    ///   <item>表头指在最后一行或更下 → 同样拒（那等于没有数据）。</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidDataException">指令把这张表切到没数据可用。</exception>
    public static TabularData Import(string filePath, string? sheetName, SheetLayoutChoice? choice)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidDataException("请先选择数据文件。");
        if (!File.Exists(filePath)) throw new FileNotFoundException($"找不到文件：{filePath}", filePath);
        if (!IsSupported(filePath))
        {
            var ext = Path.GetExtension(filePath);
            throw new NotSupportedException(
                $"暂不支持 {ext} 格式。目前支持：{string.Join("、", SupportedExtensions)}。" +
                "Word / 照片自动识别将在后续版本（AI 识别）中加入。");
        }

        var lower = Path.GetExtension(filePath).ToLowerInvariant();
        List<string[]> grid;
        System.Text.Encoding? encoding = null;

        try
        {
            if (lower is ".xlsx" or ".xlsm")
            {
                grid = XlsxTableReader.ReadRawGrid(filePath, sheetName);
            }
            else
            {
                grid = CsvTableReader.ReadRawGrid(filePath);
                encoding = CsvTableReader.CurrentEncoding;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"文件无法读取（可能正被 Excel 占用或已损坏）：{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"解析失败：{ex.Message}", ex);
        }

        if (grid.Count == 0)
            throw new InvalidDataException("这个表里没有读到任何内容，请确认文件里有数据。");

        var detection = HeaderRowDetector.Detect(grid, choice);
        if (detection.DataRows.Count == 0)
            throw new InvalidDataException(choice is { IsDefault: false }
                ? $"按这份指令切完（{choice.Describe()}）这张表已经没有数据行了，所以不改。"
                : $"识别到表头（第 {detection.HeaderRowIndex + 1} 行），但表头下面没有数据行。");

        var actualSheet = lower is ".xlsx" or ".xlsm"
            ? (sheetName ?? ListSheets(filePath).FirstOrDefault() ?? "Sheet1")
            : "CSV";

        return new TabularData(
            Path.GetFullPath(filePath),
            actualSheet,
            detection.Headers,
            detection.DataRows,
            detection.HeaderRowIndex,
            encoding,
            detection.PreambleRows,
            ReadImagesSafely(filePath, lower, actualSheet),
            detection.DataRowRawIndexes,
            choice ?? SheetLayoutChoice.Auto,
            detection.RawRowCount);
    }

    /// <summary>
    /// 读贴图。单独包一层 <c>try</c>：图是「锦上添花」（让 AI 看得见参照），
    /// 而 drawing 部分长得不规范（WPS、老版 Excel、改过后缀的 .xlsm）是常态，
    /// 绝不允许因为它读不到就把一份能用的表拒在门外。
    /// </summary>
    private static IReadOnlyList<SheetImage> ReadImagesSafely(string filePath, string extension, string sheetName)
    {
        if (extension is not (".xlsx" or ".xlsm")) return Array.Empty<SheetImage>();
        try
        {
            return XlsxTableReader.ReadSheetImages(filePath, sheetName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TableImporter] 工作表贴图读不到，已按「没图」继续：{ex.Message}");
            return Array.Empty<SheetImage>();
        }
    }
}
