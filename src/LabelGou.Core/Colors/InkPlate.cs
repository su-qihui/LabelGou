namespace LabelGou.Core.Colors;

/// <summary>
/// 分色时「这一遍只算哪一版的墨」。<see cref="None"/> = 不分色，走从前那套屏幕色渲染。
/// <para>第 48 棒用它把一页拆成 C/M/Y/K 四张灰版，每张再按 <see cref="InkPlates.ForPlate"/>
/// 的说法折成"这一版这里上多少墨"。为什么非要分四遍画、而不是把渲染好的 RGB 逐像素反算成 CMYK：
/// <see cref="CmykMath"/> 那对公式折回来<strong>不是同一套配墨</strong>（CMYK 37/63/11/5 折回是 29/58/0/15），
/// 反算等于把用户亲手填的那四个数换掉——那正是 47 棒立"两端并存"要防的事。</para>
/// </summary>
public enum InkPlate
{
    /// <summary>不分色：元素用什么色就画什么色。</summary>
    None = 0,

    /// <summary>青版。</summary>
    Cyan = 1,

    /// <summary>品红版。</summary>
    Magenta = 2,

    /// <summary>黄版。</summary>
    Yellow = 3,

    /// <summary>黑版（唛头的实地黑与角线都只该落在这一版）。</summary>
    Black = 4,
}

/// <summary>一支墨在某一张版上占多少墨，以及把它画成什么灰。</summary>
public static class InkPlates
{
    /// <summary>
    /// 这一版的墨量百分数（0~100）。<paramref name="ink"/> 为 null＝没填颜色＝黑，
    /// 所以只有 <see cref="InkPlate.Black"/> 版上是 100，其余三版 0——与 47 棒"缺字段=黑"同一条口径。
    /// </summary>
    public static int Percent(LabelColor? ink, InkPlate plate) => plate switch
    {
        InkPlate.Cyan => (ink ?? LabelColor.Black).C,
        InkPlate.Magenta => (ink ?? LabelColor.Black).M,
        InkPlate.Yellow => (ink ?? LabelColor.Black).Y,
        InkPlate.Black => (ink ?? LabelColor.Black).K,
        _ => 0,
    };

    /// <summary>
    /// 把一支墨折成这一版上的那块灰：<strong>0% 墨＝白，100% 墨＝黑</strong>。
    /// <para>交给现有渲染管线原样画就行（渲染端只认 RGB，不判颜色归属，这条分工是 47 棒定的）。
    /// 0% 墨画成白不是"没画"而是<strong>挖空</strong>（knockout）：上面这块把下面那块的墨擦掉，
    /// 这正是印刷里非专色叠印的默认行为，也是四版分开画还成立的前提。</para>
    /// </summary>
    public static LabelColor ForPlate(LabelColor? ink, InkPlate plate)
    {
        var grey = 255 - (int)Math.Round(Percent(ink, plate) * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        return LabelColor.FromSrgb(grey, grey, grey);
    }
}
