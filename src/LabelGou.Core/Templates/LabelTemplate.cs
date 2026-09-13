using System.Text.Json.Serialization;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Templates;

public enum ElementKind
{
    /// <summary>文本（可含变量占位符）。</summary>
    Text = 0,
    /// <summary>直线（分隔线/边框段）。</summary>
    Line = 1,
    /// <summary>矩形边框。</summary>
    Rect = 2,
    /// <summary>图片（如客户 Logo、条码图）。</summary>
    Image = 3,
    /// <summary>
    /// 矢量底图（C 类模板从 CorelDRAW/Illustrator 导出的 SVG 底稿转来）。
    /// <para>
    /// <strong>一份几百个对象的底稿在这里只占一个元素位</strong>，所以它不会撞 <see cref="TemplateValidator.MaxElements"/> 上限；
    /// 底稿里被提升成可编辑文字的那几行才是普通 Text 元素。路径仍用 <see cref="TemplateElement.ImagePath"/>（指向 <c>assets\*.svg</c>）。
    /// </para>
    /// </summary>
    Vector = 4,

    /// <summary>
    /// 条码（一维码，本软件自己编，不引第三方库）。
    /// <para>用户 2026-09-09：「<strong>加入条码栏目——条码一般是不同的表格里会有数字要对应填入调成正确的</strong>」。
    /// 所以数据不写死：与 Text 同一套占位符（<c>{{ItemNo}}</c> / <c>{{col:条码列}}</c>），
    /// 每一张表把那一列指给它对就行。</para>
    /// <para>数据表达式仍放在 <see cref="TemplateElement.Text"/>；制式与显不显数字用下面两个字段。</para>
    /// </summary>
    Barcode = 5,

    /// <summary>
    /// 椭圆/圆（第 51 棒，CorelDRAW 原文「绘制椭圆形和圆形」）。住在外接盒 <c>X/Y/Width/Height</c> 里，
    /// 填充、描边、笔色与 <see cref="Rect"/> 同一套字段、同一套画法分派。
    /// </summary>
    Ellipse = 6,

    /// <summary>
    /// 正多边形（第 51 棒，「绘制多边形」）。边数在 <see cref="TemplateElement.PolygonSides"/>，
    /// 顶点由 <c>ShapeGeometry</c> 从外接盒算出（内切于该盒的椭圆映射，首点朝上），
    /// 与椭圆、矩形共用那套外观字段。
    /// </summary>
    Polygon = 7,
}

public enum HorizontalAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// 圆角作用在哪几个角上。CorelDRAW 的圆角属性栏给的是「左边角圆滑度 / 右边角圆滑度 / 按下后所有角同时变圆」
/// 三档；这里按用户说的"可以选择任意角或全部角"做成<strong>四角各一位 + 一个 All</strong>，更直白也够表达前者。
/// </summary>
[Flags]
public enum Corner
{
    /// <summary>一个角都不圆（选了半径但全不勾时就是它，与"半径 0"同效）。</summary>
    None = 0,

    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,

    All = TopLeft | TopRight | BottomRight | BottomLeft,
}

/// <summary>
/// 模板元素。<strong>坐标一律毫米、字号一律磅</strong>，绝不用像素——
/// 这样同一份模板在预览、打印、PDF、图片导出里尺寸一致。
/// </summary>
public sealed class TemplateElement
{
    public ElementKind Kind { get; set; } = ElementKind.Text;

    /// <summary>左上角 X（毫米，相对标签左上角）。</summary>
    public double X { get; set; }

    /// <summary>左上角 Y（毫米）。</summary>
    public double Y { get; set; }

    /// <summary>宽（毫米）。对 Line 表示起点到终点的水平跨度由 X2/Y2 决定，此字段忽略。</summary>
    public double Width { get; set; } = 20;

    /// <summary>高（毫米）。</summary>
    public double Height { get; set; } = 6;

    /// <summary>Line 的终点 X（毫米）。</summary>
    public double X2 { get; set; }

    /// <summary>Line 的终点 Y（毫米）。</summary>
    public double Y2 { get; set; }

    /// <summary>
    /// 文本内容。支持变量占位符 <c>{{字段键}}</c>、<c>{{col:列标题}}</c>
    /// 与内置计算量 <c>{{NoXofY}}</c>、<c>{{CartonNo}}</c>、<c>{{RowIndex}}</c> 等。
    /// </summary>
    public string? Text { get; set; }

    /// <summary>图片相对路径（相对模板文件所在目录），Kind=Image / Kind=Vector 时使用。</summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// 只作对齐参考，<strong>不参与打印与导出</strong>。
    /// <para>用在从 <c>.cdr</c> 里抠出来的内嵌缩略图上：这张图只有约 50DPI，看着行、印着糊，
    /// 所以屏幕上画给人对齐用，进纸之前必须过滤掉（过滤口在 <c>LayoutContext.IncludeReference</c>）。</para>
    /// </summary>
    public bool ReferenceOnly { get; set; }

    public string FontFamily { get; set; } = DefaultFont;

    /// <summary>字号（磅）。</summary>
    public double FontSizePt { get; set; } = 8;

    public bool Bold { get; set; }

    public HorizontalAlign Align { get; set; } = HorizontalAlign.Left;

    /// <summary>线宽/边框粗细（毫米）。印刷常用 0.25~0.5。</summary>
    public double ThicknessMm { get; set; } = 0.35;

    /// <summary>
    /// 这一支墨的颜色：文本的字色、线与框的笔色、条码的条色<strong>共用这一支</strong>（第 47 棒新增）。
    /// <para><strong>null = 黑</strong>。这么定是为了"缺字段 = 今天的逐字旧行为"：
    /// 现有那 11 份 AI 版式与金沐/邱总模板一个字节都不用改，没填过颜色的元素存盘时也不会多出这个字段
    /// （<c>TemplateStore</c> 用的是 <c>WhenWritingNull</c>）。</para>
    /// <para>「需人工核对」与「缩到下限仍装不下、被省略号截断」那两套警示红<strong>压在它上面</strong>——
    /// 元素自己填了什么色都不许把警示盖掉，那是"这一格没核"必须看得见的那道闸。</para>
    /// <para>屏幕上看到的是 <see cref="Colors.CmykMath"/> 的 naive 近似（<strong>不是色彩管理</strong>）；
    /// 这个对象里那四个百分数才是印刷口径的原值，出片端要用的是它们（第 48 棒）。</para>
    /// </summary>
    public Colors.LabelColor? InkColor { get; set; }

    /// <summary>
    /// 曲线的<strong>中间节点</strong>（第 49 棒：线条改成贝塞尔工具）。
    /// <para><strong>null 或空 = 没有中间节点</strong>；再配上下面两根柄都为空，就还是从前那条直线——
    /// 老模板文件一个字节都不用改（<c>TemplateStore</c> 的 <c>WhenWritingNull</c> 会把整条省掉）。</para>
    /// <para>起点与终点仍然住在 <see cref="X"/>/<see cref="Y"/> 与 <see cref="X2"/>/<see cref="Y2"/> 里，
    /// 这里只存中间那些点。这么定是为了不让"同一个点存两处、拖一下就不一样"有发生的机会：
    /// 端点只有一个出处，曲线算法在 <see cref="CurveGeometry"/> 里现拼段序列。</para>
    /// </summary>
    public List<CurveNode>? Nodes { get; set; }

    /// <summary>起点的出柄（相对起点，毫米）。null 或零＝起点是尖角。</summary>
    public CurveHandle? StartOut { get; set; }

    /// <summary>终点的进柄（相对终点，毫米）。null 或零＝终点是尖角。</summary>
    public CurveHandle? EndIn { get; set; }

    /// <summary>
    /// 曲线是否<strong>闭合</strong>（第 53 棒：终点回到起点再补一段收口，多边形/矩形"转换为曲线"就落在这）。
    /// <para>只对 <see cref="ElementKind.Line"/> 有意义；false = 逐字旧行为（老文件缺字段读出来就是它，
    /// 新文件不闭合也不写这一行——<c>WhenWritingDefault</c> 对 bool 恰好就是"false 不上盘"）。
    /// 闭合时起点与终点是同一个可见点：<c>CurveGeometry.MoveNode</c> 拖一个另一个跟着走。</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; set; }

    /// <summary>
    /// 矩形的内填充色（第 50 棒）。<strong>null = 不填充</strong>——缺字段就是从前那只只有边框的框，
    /// 老模板文件不用更新（<c>WhenWritingNull</c> 会整条省掉）。
    /// </summary>
    public Colors.LabelColor? FillColor { get; set; }

    /// <summary>
    /// 描边开不开。<strong>null = 开</strong>（＝从前行为）；只有关掉时才写 <c>"stroked": false</c>。
    /// <para>为什么用可空而不是 <c>bool = true</c>：默认值为 true 的布尔会被序列化器逐条写出去，
    /// 每只框都多一行、老文件当场全变——"模板文件不用更新"这条就这么被破了。</para>
    /// </summary>
    public bool? Stroked { get; set; }

    /// <summary>圆角半径（毫米）。0＝直角；0 不写进文件（<c>WhenWritingDefault</c>）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CornerRadiusMm { get; set; }

    /// <summary>哪几个角要圆。<strong>null = 四个角全圆</strong>；单独勾某几个角时才写。</summary>
    public Corner? RoundedCorners { get; set; }

    /// <summary>
    /// 正多边形的边数（第 51 棒，CorelDRAW 的「多边形的边数」）。<strong>null = 默认 5（五边形）</strong>，
    /// 缺字段与老文件同形——这里不用 <c>WhenWritingDefault</c>：它对 int 的"默认"是<em>类型的</em> 0，
    /// 不是属性的 5，写出去每个多边形都白占一行（§五 同族：参数的语义要按实现核对，别按名字想当然）。
    /// 渲染端一律按 <c>ShapeGeometry.SidesOf</c> 取值并夹进 3~100，坏文件也不崩。
    /// </summary>
    public int? PolygonSides { get; set; }

    /// <summary>多边形默认边数：五边形（CorelDRAW 多边形工具起手就是这个）。</summary>
    public const int DefaultPolygonSides = 5;

    /// <summary>
    /// 逐角圆角半径（毫米），顺序 <c>[左上, 右上, 右下, 左下]</c>（第 50 棒补正四，照 CorelDRAW
    /// 圆角泊坞窗"四格各自填数 + 一颗全部圆角锁"的样子）。
    /// <para><strong>只有四角半径真的不相等时才写这一格</strong>；四角相同（含全直角）折回
    /// <see cref="CornerRadiusMm"/> + <see cref="RoundedCorners"/> 那份老形态——
    /// 所以 v8 文件不碰就存字节不变，"均匀圆角"在新老文件里长一个样子。缺字段 = 按老字段算 = 逐字旧行为。</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? CornerRadiiMm { get; set; }

    /// <summary>这框到底该不该描边（null 视作开，与老文件同形）。</summary>
    public bool ShowsStroke => Stroked ?? true;

    /// <summary>这只框圆了哪几个角（null 视作四角全圆）。</summary>
    public Corner CornersToRound => RoundedCorners ?? Corner.All;

    /// <summary>
    /// 四个角各圆到多少毫米——<strong>全项目读圆角只走这一个口</strong>。
    /// 逐角数组优先；没有数组就从老字段（半径 + 角名单）折出来。
    /// </summary>
    public (double TopLeft, double TopRight, double BottomRight, double BottomLeft) CornerRadii()
    {
        if (CornerRadiiMm is { Length: 4 } a) return (a[0], a[1], a[2], a[3]);
        var r = CornerRadiusMm;
        var c = CornersToRound;
        return (
            (c & Corner.TopLeft) != 0 ? r : 0,
            (c & Corner.TopRight) != 0 ? r : 0,
            (c & Corner.BottomRight) != 0 ? r : 0,
            (c & Corner.BottomLeft) != 0 ? r : 0);
    }

    /// <summary>
    /// 一次写全四角半径——<strong>全项目写圆角只走这一个口</strong>（读写同口径，才不会出现
    /// "面板显示一套、落盘另一套"）。落盘形态取最省的一种：
    /// 四角相等 → 只写 <see cref="CornerRadiusMm"/>（全直角时它按 WhenWritingDefault 不上盘）；
    /// 非零值相等、其余角为 0 → 老半径 + 角名单；真的逐角不同 → 才写 <see cref="CornerRadiiMm"/> 数组。
    /// </summary>
    public void SetCornerRadii(double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        topLeft = Math.Max(0, topLeft);
        topRight = Math.Max(0, topRight);
        bottomRight = Math.Max(0, bottomRight);
        bottomLeft = Math.Max(0, bottomLeft);

        CornerRadiiMm = null;
        CornerRadiusMm = 0;
        RoundedCorners = null;
        if (topLeft == topRight && topRight == bottomRight && bottomRight == bottomLeft)
        {
            CornerRadiusMm = topLeft;                       // 0 = 直角，不写进文件；全圆 = 老形态的缺 RoundedCorners
            return;
        }
        var nonZero = new[] { topLeft, topRight, bottomRight, bottomLeft }.Where(v => v > 0).Distinct().ToArray();
        if (nonZero.Length == 1)
        {
            CornerRadiusMm = nonZero[0];
            var flags = Corner.None;
            if (topLeft > 0) flags |= Corner.TopLeft;
            if (topRight > 0) flags |= Corner.TopRight;
            if (bottomRight > 0) flags |= Corner.BottomRight;
            if (bottomLeft > 0) flags |= Corner.BottomLeft;
            RoundedCorners = flags;                         // 这里到不了 All（全等那支已经先走），不必再省成 null
            return;
        }
        CornerRadiiMm = new[] { topLeft, topRight, bottomRight, bottomLeft };
    }

    /// <summary>文本框内容放不下时是否允许自动缩字号（渲染端执行，引擎只带标志）。</summary>
    public bool ShrinkToFit { get; set; } = true;

    /// <summary>最多几行，超出后截断加省略号。0 表示不限。</summary>
    public int MaxLines { get; set; } = 3;

    /// <summary>
    /// 条码制式（只有 <see cref="ElementKind.Barcode"/> 用）。默认 Code 128：
    /// 它是唯一字母与数字都能编、又不要求定长的常用档，店里拿不准时选它不会错。
    /// </summary>
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;

    /// <summary>
    /// 条码下方要不要印那串可读数字（<see cref="ElementKind.Barcode"/> 专用）。
    /// <para>默认要：扫不出时人还能报号。占掉元素框底部约 22% 的高（最少 3 mm），条占剩下的——
    /// 这一刀在 <c>LayoutEngine</c> 里算，只算一处，五出口才能一致。</para>
    /// </summary>
    public bool ShowBarcodeText { get; set; } = true;

    /// <summary>
    /// 旋转角度（<strong>度，绕元素中心，正值顺时针</strong>）。默认 0 = 不转，第 43 棒新增。
    /// <para>CDR 式任意角度。可转的只有 Text / Rect / Image / Vector；
    /// 条码直接 Error（歪码扫描枪认不出，宁可别转），线段转不转由两端点说话、不设此字段。</para>
    /// <para>校验器量的是<strong>转完之后的外接矩形</strong>——斜放的宽行带四角可能探出标签，
    /// 那是会被刀模裁掉的（"会印错且看不见"那一类），必须拦。</para>
    /// </summary>
    public double RotationDeg { get; set; }

    /// <summary>
    /// 字面横向放大倍率（1 = 原样），<strong>把字身抻宽/压扁的那种"拉伸"</strong>（第 43 棒，CDR 变换docker 手感）。
    /// <para>与两个近亲分清：字号缩放（拖角，第 42 棒）是等比把字变大；<c>RowSpec.Stretch</c>（行式骨架）
    /// 是"字号撑满行带"；这里是<strong>非等比抻字身</strong>，只管文本元素。</para>
    /// <para><strong>模型分工</strong>：Width/Height 从此是<em>画出来的视觉尺寸</em>（选择框、句柄、
    /// 越界校验都按它）；<c>TextFit</c> 折行与缩字按 <c>宽÷倍率</c> 的未拉伸盒做，字面再整体乘回倍率——
    /// 变换在排版决定之后、五出口共用同一份，不会出现"预览拉伸了导出没拉伸"。
    /// 所以拉一行让它变宽，<em>不会</em>让本来折行的长值摊平成一行——要改折行去改"宽"（面板）或先复位倍率再拉框。</para>
    /// </summary>
    public double TextScaleX { get; set; } = 1;

    /// <summary>字面纵向放大倍率（1 = 原样）。见 <see cref="TextScaleX"/>。</summary>
    public double TextScaleY { get; set; } = 1;

    /// <summary>
    /// 折行宽度（毫米）。<strong>0 = 永不折行</strong>（默认）：内容多长就排多长，超出纸就超出——
    /// 超没超由"看得见的墨迹"说话，不再由一条比字宽得多的隐形行带替他折回去。
    /// <para>第 46 棒：这条字段是把「折行边界」从 <see cref="Width"/> 里拆出来的产物。此前 <c>Width</c>
    /// 一人兼三职——对齐基准、垂直居中参照、折行与缩字边界；而 AI 行式骨架给的是整幅纸的行带
    /// （实测 11 份模板全是 130mm 带 / 140mm 纸），于是"摆位被顶住"和"长值默默折回"两根症状同出一处。
    /// 现在 <c>Width</c> 只管<strong>对齐基准与垂直居中</strong>（视觉位置一寸不动），折行只在显式给了宽度时发生。</para>
    /// <para>想要旧行为（按行带折行）：把这一格填成与「宽(mm)」相同的数。</para>
    /// </summary>
    public double WrapWidthMm { get; set; }

    /// <summary>这一行是不是"永不折行"。五处消费方共用这一个判据，别各写一遍 <c>&lt;= 0</c>。</summary>
    [JsonIgnore]
    public bool NoWrap => Kind != ElementKind.Text || WrapWidthMm <= 0;

    public bool Visible { get; set; } = true;

    /// <summary>默认字体：微软雅黑，Win10/11 自带，中英混排都不会掉字。</summary>
    public const string DefaultFont = "Microsoft YaHei";

    [JsonIgnore]
    public double AreaMm2 => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// 一份唛头标签模板（A 类内置 / B 类用户拖拽 / C 类 CDR 底稿，共用同一结构）。
/// <para>
/// 这个类同时是 <strong>M7「AI 生成模板」的输出契约</strong>：大模型只允许产出符合本结构
/// 且能通过 <see cref="TemplateValidator.Validate"/> 的 JSON，任何越界、未知字段、超大字号
/// 都在校验阶段拒绝，AI 不允许直接把毫米坐标写进印面。
/// </para>
/// </summary>
public sealed class LabelTemplate
{
    /// <summary>当前 schema 版本。改结构必须递增，并让 <see cref="TemplateValidator"/> 兼容旧版。</summary>
    /// <remarks>v2 = M5 新增 <see cref="ElementKind.Vector"/> 与 <see cref="TemplateElement.ReferenceOnly"/>；v1 文件仍能读。
    /// v3 = 第 17 棒新增 <see cref="ElementKind.Barcode"/> 与 <see cref="TemplateElement.Symbology"/> /
    /// <see cref="TemplateElement.ShowBarcodeText"/>；同前例，v2 文件照旧能读，只是里面不会出现条码。
    /// v4 = 第 43 棒新增 <see cref="TemplateElement.RotationDeg"/> / <see cref="TemplateElement.TextScaleX"/> /
    /// <see cref="TemplateElement.TextScaleY"/>；同前例，v3 文件照旧能读（缺字段 = 0 度不转、1 倍不拉伸）。
    /// v5 = 第 46 棒新增 <see cref="TemplateElement.WrapWidthMm"/>。<strong>缺字段 = 0 = 永不折行，这是刻意的
    /// 行为变更</strong>（用户 2026-09-12 拍板"排版层盒子拆掉，按墨迹层算"）：旧模板里长值不再被那条隐形行带
    /// 折回纸内，可能横着伸出纸边——接手这份保护的是"墨迹越界"那道检查（编辑器按样例拦、④⑤ 步按真数据进
    /// 复核闸门）与「缩回纸内」一键，不再是排版盒。想恢复旧行为，把「折行宽度(mm)」填成与「宽(mm)」同值。
    /// v6 = 第 47 棒新增 <see cref="TemplateElement.InkColor"/>。<strong>缺字段 = null = 黑 = 逐字旧行为</strong>，
    /// 与 v5 的区别只在"能不能填"，不在"不填会怎样"——所以现有模板文件一个都不用更新（用户 2026-09-13 明确：
    /// 模板文件暂时不用更新）。JSON 里它是一个字符串：<c>"#c62828"</c> 或 <c>"cmyk(0 91 90 0)"</c>（整数百分数）。
    /// v7 = 第 49 棒把「线条」升级成贝塞尔工具，新增 <see cref="TemplateElement.Nodes"/> /
    /// <see cref="TemplateElement.StartOut"/> / <see cref="TemplateElement.EndIn"/>。<strong>三个字段全缺 =
    /// 没有中间节点也没有柄 = 从前那条直线 = 逐字旧行为</strong>，所以现有模板文件还是一个都不用更新；
    /// 用曲线工具拖出来的<strong>直线</strong>也不会写这三个字段（见 <c>CurveGeometry.ApplyNodes</c>），
    /// 老文件与新文件里"一条直线"长同一个样子。</remarks>
    /// <remarks>v8 = 第 50 棒给矩形加了 <see cref="TemplateElement.FillColor"/> / <see cref="TemplateElement.Stroked"/> /
    /// <see cref="TemplateElement.CornerRadiusMm"/> / <see cref="TemplateElement.RoundedCorners"/>。<strong>四个字段全缺 =
    /// 不填充、有描边、四角直角 = 逐字旧行为</strong>，现有模板文件还是一个都不用更新；填充色与笔色一样是
    /// <c>LabelColor</c> 那个字符串写法（<c>"#c62828"</c> / <c>"cmyk(0 91 90 0)"</c>）。</remarks>
    /// <remarks>v9 = 第 50 棒补正四：圆角照 CorelDRAW 的圆角泊坞窗改成<strong>逐角各一个半径</strong>，新增
    /// <see cref="TemplateElement.CornerRadiiMm"/>。<strong>缺这个字段 = 按 v8 那对老字段（半径 + 角名单）折出来 =
    /// 逐字旧行为</strong>，现有模板文件还是一个都不用更新；反过来，四角相等的圆角仍然只写老字段
    /// （见 <see cref="TemplateElement.SetCornerRadii"/>），新文件里的"均匀圆角"与 v8 长一个样子。</remarks>
    /// <remarks>v10 = 第 51 棒加两种形状元素 <c>Ellipse</c> / <c>Polygon</c> 与 <see cref="TemplateElement.PolygonSides"/>
    /// （默认 5，非默认才写盘）。<strong>老文件里既没有这两种 kind 也没有这个字段，读进来行为逐字不变</strong>；
    /// 它们的填充 / 描边 / 笔色用的就是 v8 那几件字段，不新造第二套外观。</remarks>
    /// <remarks>v11 = 第 53 棒给曲线加 <see cref="TemplateElement.Closed"/>（闭合标志，多边形/矩形"转换为曲线"的落点）。
    /// <strong>缺字段 = false = 开口曲线 = 逐字旧行为</strong>，现有模板文件还是一个都不用更新。</remarks>
    public const int CurrentSchemaVersion = 11;

    /// <summary>稳定标识，如 <c>builtin.standard-100x80</c>。用户模板用 <c>user.xxx</c>。</summary>
    public string Id { get; set; } = "user." + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "未命名模板";

    public string Note { get; set; } = string.Empty;

    /// <summary>标签宽（毫米）。</summary>
    public double WidthMm { get; set; } = 100;

    /// <summary>标签高（毫米）。</summary>
    public double HeightMm { get; set; } = 80;

    /// <summary>内边距（毫米）。B 类编辑器拖拽时的对齐基准。</summary>
    public double PaddingMm { get; set; } = 4;

    /// <summary>0 表示不画外框；否则为外框线宽（毫米）。</summary>
    public double BorderMm { get; set; } = 0.5;

    /// <summary>
    /// 保留字段（旧 schema 兼容用）：<strong>整版裁切线由纸张/刀模决定，不由内容模板决定</strong>，
    /// 拼版时实际生效的是 <c>SheetSpec.CropMarks</c>，本字段不参与落位。
    /// </summary>
    public double CropMarkMm { get; set; }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>true 表示内置模板，不允许被保存覆盖。</summary>
    public bool BuiltIn { get; set; }

    public List<TemplateElement> Elements { get; set; } = new();

    /// <summary>版面方向（仅用于界面提示与实际打印时的纸张方向，M2/M3 消费）。</summary>
    public string Description => $"{Name}（{WidthMm:0.#} × {HeightMm:0.#} mm）";

    public LabelTemplate CloneAsUserCopy(string newName)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Mapping.ProfileStore.JsonOptions);
        var copy = System.Text.Json.JsonSerializer.Deserialize<LabelTemplate>(json, Mapping.ProfileStore.JsonOptions)!;
        copy.Id = "user." + Guid.NewGuid().ToString("N")[..8];
        copy.Name = newName;
        copy.BuiltIn = false;
        return copy;
    }
}

/// <summary>模板校验问题。</summary>
/// <param name="Severity">级别。</param>
/// <param name="Message">人话描述。</param>
/// <param name="ElementIndex">涉及元素下标，-1 表示模板级问题。</param>
public sealed record TemplateIssue(IssueLevel Severity, string Message, int ElementIndex = -1);

public enum IssueLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// 模板校验器：AI/M7 与人工编辑（B 类）的<strong>共同闸门</strong>。
/// <para>
/// 规则设计成"能印就印得对"的下限：元素不越界、字号不夸张、变量名合法、结构不超载。
/// 大模型生成的模板 JSON 只要这里报 Error，一律拒绝入模板库。
/// </para>
/// </summary>
public static class TemplateValidator
{
    /// <summary>单张标签允许的最大边长（毫米）。超过基本是误填。</summary>
    public const double MaxLabelSideMm = 600;

    /// <summary>允许的最小边长。</summary>
    public const double MinLabelSideMm = 8;

    /// <summary>字号下限（磅）。</summary>
    public const double MinFontPt = 3;

    /// <summary>
    /// 字号上限（磅）= 45.9mm 字高。
    /// <para>原来定的是 60pt（21mm），但真厂商样张不是这么回事：<c>labelgou-CL</c> 里
    /// “广州郑小姐”是 160×120 的纸上只放两行超大字（每行吃掉约 50mm 高），
    /// “OLU”顶部一行 SKU 大字也一样——60pt 会把它们默默削小一半，导入结果就“看着不对但说不清”。
    /// 越界与重叠仍有元素级校验拦着，放开的只是“大字”这一项。</para>
    /// </summary>
    public const double MaxFontPt = 130;

    /// <summary>元素数量上限，防呆也防 AI 无限堆。</summary>
    public const int MaxElements = 80;

    /// <summary>横/纵拉伸倍率的合理区间（第 43 棒）。0.2 倍已压成一条线，5 倍基本是误填。</summary>
    public const double MinStretch = 0.2;
    public const double MaxStretch = 5;

    /// <summary>旋转角的合理区间（度）。绕中心转，超过 ±360 无新意义，多半是填错。</summary>
    public const double MaxRotationDeg = 360;

    /// <summary>坐标比对容差（毫米），0.05mm 以内不算越界。</summary>
    public const double ToleranceMm = 0.05;

    public static IReadOnlyList<TemplateIssue> Validate(LabelTemplate template)
    {
        var issues = new List<TemplateIssue>();
        if (template is null)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, "模板为空。"));
            return issues;
        }

        if (template.SchemaVersion > LabelTemplate.CurrentSchemaVersion)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"模板版本 v{template.SchemaVersion} 高于当前程序支持的 v{LabelTemplate.CurrentSchemaVersion}，请升级 LabelGou。"));
        }

        if (template.WidthMm is < MinLabelSideMm or > MaxLabelSideMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"标签宽度 {template.WidthMm:0.#} mm 不在 {MinLabelSideMm}~{MaxLabelSideMm} mm 之间。"));
        if (template.HeightMm is < MinLabelSideMm or > MaxLabelSideMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"标签高度 {template.HeightMm:0.#} mm 不在 {MinLabelSideMm}~{MaxLabelSideMm} mm 之间。"));
        if (template.PaddingMm < 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "内边距不能为负。"));

        if (template.Elements.Count == 0)
            issues.Add(new TemplateIssue(IssueLevel.Warning, "模板里还没有任何元素，预览会是空白。"));
        if (template.Elements.Count > MaxElements)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"元素数量 {template.Elements.Count} 超过上限 {MaxElements}。"));
        }

        if (template.Elements.Count > 0 && !template.Elements.Any(e => e.Visible && !e.ReferenceOnly) && template.BorderMm <= 0)
        {
            // 从 .cdr 导出来的参考底图很容易被人当成成品模板直接去打印，结果一张空纸
            issues.Add(new TemplateIssue(IssueLevel.Warning,
                "这份模板里只有参考图（不打印），也没有外框：直接出片会是空白。请在底图上叠上字段，或改用 SVG 导出的保真底图。"));
        }

        for (var i = 0; i < template.Elements.Count; i++)
        {
            var e = template.Elements[i];
            var tag = $"第 {i + 1} 个元素";

            if (e.X < -ToleranceMm || e.Y < -ToleranceMm)
                issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 起点超出标签左上角（X={e.X:0.#}, Y={e.Y:0.#} mm）。", i));

            if (e.Kind != ElementKind.Line)
            {
                if (e.Width <= 0 || e.Height <= 0)
                    issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 宽高必须大于 0。", i));
                // 第 43 棒：越界一律按 OccupiedBoundsOf 判——它已把文字拉伸与旋转都算进去，
                // 拉出纸/转出纸都等于会被刀模裁掉，与"越界"同一性质，必须在这拦（"会印错且看不见"那一类）。
                // 第 46 棒改口：**永不折行的文本不拿排版盒当占物**。那时那条带子只是"字在哪对齐"的虚拟基准，
                // 它自己不出纸（出纸的是墨迹），Core 又量不了字 → 判它探出纸只会把"能拖到右边"变成"存不了盘"。
                // 这一格改由两处能量墨迹的地方接手：编辑器按样例墨迹拦、④⑤ 步按真数据墨迹进复核闸门。
                if (!(e.Kind == ElementKind.Text && e.NoWrap))
                {
                    var occ = Editing.EditGeometry.OccupiedBoundsOf(e);
                    if (occ.X < -ToleranceMm || occ.Y < -ToleranceMm
                        || occ.Right > template.WidthMm + ToleranceMm || occ.Bottom > template.HeightMm + ToleranceMm)
                    {
                        var howFar = Math.Max(
                            Math.Max(occ.Right - template.WidthMm, occ.Bottom - template.HeightMm),
                            Math.Max(-occ.X, -occ.Y));
                        issues.Add(new TemplateIssue(IssueLevel.Error,
                            $"{tag} 占了 X {occ.X:0.#}~{occ.Right:0.#}、Y {occ.Y:0.#}~{occ.Bottom:0.#} mm，" +
                            $"探出标签（{template.WidthMm:0.#} × {template.HeightMm:0.#} mm）约 {howFar:0.##} mm——" +
                            "会被裁掉，请挪回纸内或减小尺寸/角度。", i));
                    }
                }
            }
            else
            {
                if (Math.Abs(e.X - e.X2) < ToleranceMm && Math.Abs(e.Y - e.Y2) < ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 是一条零长度线段，不会显示。", i));
                if (e.X2 > template.WidthMm + ToleranceMm || e.Y2 > template.HeightMm + ToleranceMm
                    || e.X2 < -ToleranceMm || e.Y2 < -ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 线段端点超出标签范围。", i));

                // 第 49 棒：曲线多了三个要管的地方。端点都在纸内、中间鼓出去一大截是常见手滑，
                // 所以越界这条量的是弧本身（CurveGeometry.BoundsMm 解导数根，不是控制点那个虚胖框）。
                if (e.Nodes is { Count: > CurveGeometry.MaxNodes })
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 有 {e.Nodes.Count} 个中间节点，超过上限 {CurveGeometry.MaxNodes}。", i));
                else if (e.Nodes is { } nodes && nodes.Any(n => n is null
                         || !double.IsFinite(n.X) || !double.IsFinite(n.Y)
                         || !double.IsFinite(n.InX) || !double.IsFinite(n.InY)
                         || !double.IsFinite(n.OutX) || !double.IsFinite(n.OutY)))
                    issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 的节点坐标不是有效数字（NaN/无穷），画不出来。", i));
                else if (CurveGeometry.IsCurved(e))
                {
                    var arc = CurveGeometry.BoundsMm(e);
                    if (arc.X < -ToleranceMm || arc.Y < -ToleranceMm
                        || arc.X + arc.Width > template.WidthMm + ToleranceMm
                        || arc.Y + arc.Height > template.HeightMm + ToleranceMm)
                    {
                        issues.Add(new TemplateIssue(IssueLevel.Error,
                            $"{tag} 的弧鼓到 X {arc.X:0.#}~{arc.X + arc.Width:0.#}、Y {arc.Y:0.#}~{arc.Y + arc.Height:0.#} mm，" +
                            $"探出标签（{template.WidthMm:0.#} × {template.HeightMm:0.#} mm）——两端在纸内不算过关，弧身也会被裁掉。", i));
                    }
                }
            }

            if (e.Kind is ElementKind.Text or ElementKind.Barcode)
            {
                if (e.FontSizePt is < MinFontPt or > MaxFontPt)
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 字号 {e.FontSizePt:0.#}pt 不在 {MinFontPt}~{MaxFontPt}pt 之间。", i));

                foreach (var token in TemplateTokenizer.EnumerateTokens(e.Text))
                {
                    if (TemplateTokenizer.IsBuiltInToken(token)) continue;
                    if (token.StartsWith("col:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!MarkFieldCatalog.TryParseKey(token, out _))
                    {
                        issues.Add(new TemplateIssue(IssueLevel.Error,
                            $"{tag} 引用了未知字段「{token}」，可用字段见字段清单。", i));
                    }
                }
            }

            // 条码自己的两条：没绑数据 = 一个永远不画的空位；制式越界 = 旧文件/手改 JSON 拿来的坏值。
            // 字段名与 {{col:…}} 的检查上面那段已经做了（条码与文本共用同一套占位符，不开第二套）。
            if (e.Kind == ElementKind.Barcode)
            {
                if (string.IsNullOrWhiteSpace(e.Text))
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 是条码但没绑数据：在 ③ 步「条码」那一栏里选它读哪一列（或先填一串固定数字）。", i));
                if (!Enum.IsDefined(e.Symbology))
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 的条码制式不认识：{e.Symbology}。请重选一个。", i));
                if (e.ShowBarcodeText && e.FontSizePt < 5)
                    issues.Add(new TemplateIssue(IssueLevel.Warning,
                        $"{tag} 的可读数字只 {e.FontSizePt:0.#}pt：扫不出时人也读不了那串号。建议 6pt 以上。", i));
            }

            if (e.Kind is ElementKind.Image or ElementKind.Vector && string.IsNullOrWhiteSpace(e.ImagePath))
                issues.Add(new TemplateIssue(IssueLevel.Warning,
                    $"{tag} 是{(e.Kind == ElementKind.Vector ? "矢量底图" : "图片")}元素但没有指定文件，它会画不出来。", i));

            // —— 第 43 棒：旋转与文字拉伸 ——
            if (Math.Abs(e.RotationDeg) > MaxRotationDeg)
                issues.Add(new TemplateIssue(IssueLevel.Error,
                    $"{tag} 旋转角 {e.RotationDeg:0.#}° 超出 ±{MaxRotationDeg:0}°（绕中心转，超过一整圈多半是填错）。", i));
            if (e.Kind == ElementKind.Barcode && (Math.Abs(e.RotationDeg) > ToleranceMm
                    || Math.Abs(e.TextScaleX - 1) > ToleranceMm || Math.Abs(e.TextScaleY - 1) > ToleranceMm))
                issues.Add(new TemplateIssue(IssueLevel.Error,
                    $"{tag} 是条码：条码不许旋转或拉伸，歪一点斜一点扫描枪就认不出（宁可别转）。", i));
            if (e.Kind == ElementKind.Text && (e.TextScaleX is < MinStretch or > MaxStretch || e.TextScaleY is < MinStretch or > MaxStretch))
                issues.Add(new TemplateIssue(IssueLevel.Error,
                    $"{tag} 的文字拉伸 {e.TextScaleX:0.##} × {e.TextScaleY:0.##} 不在 {MinStretch:0.#}~{MaxStretch:0.#} 之间。", i));
            // 旋转/拉伸探出纸的越界，上面 OccupiedBoundsOf 那条已经一并拦了（不在这重复判）。

            // —— 第 50 棒：矩形的填充 / 描边 / 圆角；第 51 棒：椭圆与多边形共用这套外观；第 53 棒：闭合曲线也算 ——
            if ((ShapeGeometry.IsBoxShape(e) || (e.Kind == ElementKind.Line && e.Closed)) && !e.ShowsStroke && e.FillColor is null)
                issues.Add(new TemplateIssue(IssueLevel.Warning,
                    $"{tag} 既不描边也不填充，纸上看不到任何东西（要隐形占位的话，留描边把线宽设小更稳）。", i));
            if (e.Kind == ElementKind.Polygon && e.PolygonSides is { } sides && (sides < ShapeGeometry.MinSides || sides > ShapeGeometry.MaxSides))
                issues.Add(new TemplateIssue(IssueLevel.Error,
                    $"{tag} 多边形边数 {sides} 不在 {ShapeGeometry.MinSides}~{ShapeGeometry.MaxSides} 之间（少于三条不叫多边形，多于 {ShapeGeometry.MaxSides} 条画出来就是圆）。", i));
            if (e.Kind == ElementKind.Rect)
            {
                // 逐角判（补正四）：哪个角超了短边一半点哪个角的名，别拿一个数替四个角说话。
                var cap = Math.Min(e.Width, e.Height) / 2;
                var (rTl, rTr, rBr, rBl) = e.CornerRadii();
                var over = new List<string>();
                if (rTl > cap + 0.01) over.Add($"左上 {rTl:0.##}");
                if (rTr > cap + 0.01) over.Add($"右上 {rTr:0.##}");
                if (rBr > cap + 0.01) over.Add($"右下 {rBr:0.##}");
                if (rBl > cap + 0.01) over.Add($"左下 {rBl:0.##}");
                if (over.Count > 0)
                    issues.Add(new TemplateIssue(IssueLevel.Warning,
                        $"{tag} 圆角 {string.Join('、', over)} mm 超过短边一半，实际按 {cap:0.##} mm 画（再大就成了胶囊，不是更圆）。", i));
            }

            if (e.ThicknessMm <= 0)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 线宽为 0，打印时不会显示。", i));
            else if (e.ThicknessMm > 3)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 线宽 {e.ThicknessMm:0.##} mm 偏粗，不干胶上容易糊。", i));
        }

        return issues;
    }

    public static bool HasError(this IReadOnlyList<TemplateIssue> issues)
        => issues.Any(i => i.Severity == IssueLevel.Error);

    /// <summary>只取需要修掉的错误信息文本。</summary>
    public static IReadOnlyList<string> ErrorMessages(this IReadOnlyList<TemplateIssue> issues)
        => issues.Where(i => i.Severity == IssueLevel.Error).Select(i => i.Message).ToList();
}
