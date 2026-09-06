using System.Globalization;
using System.Text;

namespace LabelGou.Core.Export;

/// <summary>
/// 页范围（打印/导出都靠它挑页）：用户输入 <c>1-3,5,7-9</c> 这种写法，空着表示"全部"。
/// 规则从严但宽容：段内前大后小自动纠正、重复只保留一次、超出总页数的部分裁掉、
/// 完全拿不出一页时给出人话错误而不是空列表。编号一律 1 起始（用户在纸堆里数页就是这么数的）。
/// </summary>
public sealed class PageRange
{
    public const int MaxPagesSelectable = 20000;      // 防呆上限，与 ImpositionEngine.MaxPages 同量级
    private readonly SortedSet<int> _pageNumbers;     // 1 起始

    private PageRange(SortedSet<int> pageNumbers, bool isAll, string sourceText)
    {
        _pageNumbers = pageNumbers;
        IsAll = isAll;
        SourceText = sourceText;
    }

    /// <summary>原始输入文本，回填 UI 用。</summary>
    public string SourceText { get; }

    /// <summary>是否为"全部"（用户没填具体页码）。</summary>
    public bool IsAll { get; }

    public int Count => _pageNumbers.Count;

    public static PageRange All(int pageCount)
    {
        var pages = new SortedSet<int>();
        for (var i = 1; i <= Math.Max(0, pageCount); i++) pages.Add(i);
        return new PageRange(pages, isAll: true, string.Empty);
    }

    /// <summary>
    /// 解析用户文本。<paramref name="pageCount"/> &lt;= 0 时认为"还没有整版可挑"，直接给出提示。
    /// </summary>
    public static bool TryParse(string? text, int pageCount, out PageRange? range, out string? error)
    {
        range = null;
        error = null;
        var trimmed = (text ?? string.Empty).Trim();

        if (pageCount <= 0)
        {
            error = "当前没有整版可挑页，请先完成数据与拼版设置。";
            return false;
        }

        if (trimmed.Length == 0)
        {
            range = All(pageCount);
            return true;
        }

        // 分隔符宽容：逗号/分号/空格/顿号都当分隔，连字符支持 - – — 与 "～/~"。
        var pages = new SortedSet<int>();
        var segments = trimmed.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n', '/', '|', '、' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var dashIndex = IndexOfDash(segment, out var dashLength);
            if (dashIndex < 0)
            {
                if (!TryPage(segment.Trim(), pageCount, out var single))
                {
                    error = $"页码 \"{segment}\" 看不懂或超出总页数（共 {pageCount} 页）。";
                    return false;
                }
                pages.Add(single);
                continue;
            }

            var head = segment[..dashIndex].Trim();
            var tail = segment[(dashIndex + dashLength)..].Trim();
            if (!TryPage(head, pageCount, out var from) || !TryPage(tail, pageCount, out var to))
            {
                error = $"页范围 \"{segment}\" 写法不对，应当像 1-3 这样（共 {pageCount} 页）。";
                return false;
            }

            if (from > to) (from, to) = (to, from);      // 用户手滑写 3-1：照他要的意思办
            for (var p = from; p <= to; p++) pages.Add(p);
            if (pages.Count > MaxPagesSelectable)
            {
                error = $"一次最多选 {MaxPagesSelectable} 页，当前已选 {pages.Count} 页，请缩小范围分批打。";
                return false;
            }
        }

        if (pages.Count == 0)
        {
            error = $"页范围 \"{trimmed}\" 没有命中任何一页（共 {pageCount} 页）。";
            return false;
        }

        range = new PageRange(pages, isAll: false, trimmed);
        return true;
    }

    /// <summary>这页在不在范围内。传入 1 起始页号。</summary>
    public bool Contains(int pageNumber) => _pageNumbers.Contains(pageNumber);

    /// <summary>挑出来的页号（升序）。</summary>
    public IReadOnlyList<int> SelectedPages() => new List<int>(_pageNumbers);

    /// <summary>把选中的页号投影成 0 起始索引，供 <see cref="Impos.SheetPlan"/> 使用。</summary>
    public IReadOnlyList<int> SelectedIndexes()
    {
        var indexes = new List<int>(_pageNumbers.Count);
        foreach (var page in _pageNumbers) indexes.Add(page - 1);
        return indexes;
    }

    /// <summary>回显文案：全部时写"全部 N 页"，否则压缩成 1-3,5 这样。</summary>
    public string Describe()
    {
        if (_pageNumbers.Count == 0) return "未选择任何页";
        var sb = new StringBuilder();
        var runStart = -1;
        var previous = -1;
        foreach (var page in _pageNumbers)
        {
            if (runStart < 0)
            {
                runStart = previous = page;
                continue;
            }
            if (page == previous + 1)
            {
                previous = page;
                continue;
            }
            AppendRun(sb, runStart, previous);
            runStart = previous = page;
        }
        AppendRun(sb, runStart, previous);
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, int from, int to)
    {
        if (sb.Length > 0) sb.Append(',');
        sb.Append(from == to ? from.ToString(CultureInfo.InvariantCulture)
                             : $"{from}-{to}");
    }

    private static int IndexOfDash(string segment, out int length)
    {
        length = 1;
        for (var i = 1; i < segment.Length; i++)     // 从 1 开始：首位的 '-' 属负数写法，交给 TryPage 报错
        {
            var c = segment[i];
            if (c is '-' or '\u2013' or '\u2014' or '~' or '\uFF5E') return i;
        }
        return -1;
    }

    private static bool TryPage(string text, int pageCount, out int page)
    {
        page = 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed < 1 || parsed > pageCount) return false;         // 越界直接判错，静默裁掉更容易打错货
        page = parsed;
        return true;
    }
}
