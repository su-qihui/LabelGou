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

        var detection = HeaderRowDetector.Detect(grid);
        if (detection.DataRows.Count == 0)
            throw new InvalidDataException(
                $"识别到表头（第 {detection.HeaderRowIndex + 1} 行），但表头下面没有数据行。");

        var actualSheet = lower is ".xlsx" or ".xlsm"
            ? (sheetName ?? ListSheets(filePath).FirstOrDefault() ?? "Sheet1")
            : "CSV";

        return new TabularData(
            Path.GetFullPath(filePath),
            actualSheet,
            detection.Headers,
            detection.DataRows,
            detection.HeaderRowIndex,
            encoding);
    }
}
