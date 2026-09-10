namespace LabelGou.Core.Data;

/// <summary>
/// 一个格子上<strong>量到的</strong>可见格式（第 39 棒）。行号列号都是 0 基，与读取器内部口径一致。
/// <para><strong>只有写了字的格子才会有一条</strong>：读取器碰到值为空的格子直接跳过，
/// 所以「某一列的这些条按行号排好」就等于「那一列里写了字的那些格」——
/// <c>RowFormatEvidence</c> 就是靠这一点找出「抄标签」那一块的。</para>
/// <para><see cref="SizePt"/> 为 0 = 这一格的字号量不到（工作簿根本没写字体表），不是「字号是 0」。
/// <see cref="Align"/> 为 null = 没单独设对齐（Excel 里文字的实际效果就是靠左）。</para>
/// </summary>
/// <param name="Row">0 基行号。</param>
/// <param name="Col">0 基列号。</param>
/// <param name="SizePt">字号（磅），0 = 量不到。</param>
/// <param name="Bold">是否粗体。</param>
/// <param name="Align">水平对齐原样字符串（<c>center</c>/<c>right</c>/<c>left</c>/<c>justify</c>；没设是 null）。</param>
public readonly record struct CellFormat(int Row, int Col, double SizePt, bool Bold, string? Align)
{
    /// <summary>字号量到了没有。</summary>
    public bool HasSize => SizePt > 0;
}
