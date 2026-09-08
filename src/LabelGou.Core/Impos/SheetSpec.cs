using System.Text.Json.Serialization;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Impos;

/// <summary>裁切线（角线）画法。</summary>
public enum CropMarkMode
{
    /// <summary>不画。</summary>
    None = 0,

    /// <summary>
    /// 只在整版标签区的外接矩形四角画（默认）。密排不干胶上这是唯一不留脏线的画法，
    /// 因为每枚都画角线时，相邻标签的角线会互相压在对方的印面上。
    /// </summary>
    SheetCorners = 1,

    /// <summary>每枚标签四角都画（手工裁切、单枚出货时用；要留够标签间距）。</summary>
    EveryLabel = 2,
}

/// <summary>
/// 不干胶底纸 / 刀模规格：整版拼版的<strong>唯一几何真源</strong>（毫米）。
/// <para>
/// 各打印店的纸规都不一样，所以这里一律可配置、绝不写死；内置的几张只是常见种子，
/// 用户可以复制后改自己的行列与间距（<see cref="SheetSpecStore"/>）。
/// </para>
/// <para>
/// 与模板的关系：模板管「一枚标签里印什么」，纸规管「一张纸上摆几枚、摆哪里、怎么裁」。
/// 两者标签尺寸不一致时只告警不阻止（有意拿小标签纸排大版式的场景真实存在）。
/// </para>
/// </summary>
public sealed class SheetSpec
{
    /// <summary>纸规 schema 版本，改结构必须递增。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>纸张最长边上限（毫米）：超过基本是误填，也挡住 AI/脚本产出的离谱值。</summary>
    public const double MaxPaperSideMm = 2000;

    public const double MinPaperSideMm = 30;

    /// <summary>稳定标识，如 <c>sheet.a4</c>；用户纸规用 <c>user.xxx</c>。</summary>
    public string Id { get; set; } = "user." + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "未命名纸规";

    public string Note { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// 纸面跟着标签走（「一页一枚」）：宽高一律在 <c>ImpositionEngine.Build</c> 里按实际标签尺寸展开。
    /// <para>五家真样张全部是一页一枚（CDR 页面尺寸 = 唛头尺寸），表里的「一开四 / 一开二」只是纸张裁切
    /// 与贴法指令，不是拼版。清单里这份的宽高只是占位（校验要有一个合法的数），真正生效的是引擎里那次展开。</para>
    /// </summary>
    public bool FollowsLabel { get; set; }

    /// <summary>true 表示内置种子，不可被保存覆盖、不可删除。</summary>
    public bool BuiltIn { get; set; }

    // ---------- 底纸 ----------

    /// <summary>纸张宽（毫米，纵向：宽 &lt; 高）。</summary>
    public double PaperWidthMm { get; set; } = 210;

    /// <summary>纸张高（毫米）。</summary>
    public double PaperHeightMm { get; set; } = 297;

    /// <summary>打印时把纸张按横向使用（等价于把标签整体转 90°摆放，由拼版引擎消费）。</summary>
    public bool Landscape { get; set; }

    public double MarginLeftMm { get; set; } = 8;

    public double MarginTopMm { get; set; } = 8;

    public double MarginRightMm { get; set; } = 8;

    public double MarginBottomMm { get; set; } = 8;

    // ---------- 单枚标签（刀模） ----------

    /// <summary>标签宽（毫米）；填 0 表示跟随所选模板尺寸。</summary>
    public double LabelWidthMm { get; set; }

    /// <summary>标签高（毫米）；填 0 表示跟随所选模板尺寸。</summary>
    public double LabelHeightMm { get; set; }

    /// <summary>标签间距（毫米，列与列之间）。</summary>
    public double GutterXMm { get; set; } = 2;

    /// <summary>标签间距（毫米，行与行之间）。</summary>
    public double GutterYMm { get; set; } = 2;

    // ---------- 行列 ----------

    /// <summary>每行几枚；0 = 由可用宽度自动算（密排）。</summary>
    public int Columns { get; set; }

    /// <summary>每页几行；0 = 由可用高度自动算。</summary>
    public int Rows { get; set; }

    /// <summary>是否允许旋转 90° 以省料（自动择优）。</summary>
    public bool AllowRotate { get; set; } = true;

    /// <summary>
    /// 一页只排同一枚唛头（2026-09-08 用户定调「一般是一个小标签排一张纸，一整张排同一个」，**默认开**）。
    /// <para>开着一页不混两个源数据行：同组超过每页枚数则跨页接着排，不足一页的空位<strong>留着不填</strong>；
    /// 关掉就是按标签顺序铺满整页（跨枚混排）。<see cref="Impos.ImpositionEngine.Build"/> 只有在调用方
    /// 递了分组键（每张标签的源行号）时才真按组落位，没递就照混排走。</para>
    /// <para>代价不粉饰：分组会多耗纸，所以 <see cref="Impos.SheetPlan.MixedPageCount"/> 把混排下的页数
    /// 一起报出来给人对比，而不是让人自己发现页数涨了。</para>
    /// </summary>
    public bool RepeatSameLabelPerPage { get; set; } = true;

    // ---------- 裁切与套准 ----------

    public CropMarkMode CropMarks { get; set; } = CropMarkMode.SheetCorners;

    /// <summary>角线长度（毫米）。</summary>
    public double CropMarkLengthMm { get; set; } = 4;

    /// <summary>角线与标签边缘的间隙（毫米），留白避免线压到印面。</summary>
    public double CropMarkGapMm { get; set; } = 1;

    /// <summary>角线线宽（毫米）。裁切参考线要细，常用 0.1~0.2。</summary>
    public double CropMarkThicknessMm { get; set; } = 0.15;

    /// <summary>是否在纸张四角画套准十字（帮操作员确认纸张放正、方向没错）。</summary>
    public bool RegistrationMarks { get; set; } = true;

    /// <summary>套准十字边长（毫米）。</summary>
    public double RegistrationSizeMm { get; set; } = 4;

    /// <summary>套准十字离纸张边缘的距离（毫米）。</summary>
    public double RegistrationInsetMm { get; set; } = 4;

    /// <summary>每枚标签画一圈细刀模示意线（毫米，0 = 不画）。预览对位用，正式印刷一般关掉。</summary>
    public double LabelOutlineMm { get; set; }

    // ---------- 派生 ----------

    /// <summary>标签尺寸是否跟随模板（0 或负数即为跟随）。</summary>
    [JsonIgnore]
    public bool FollowTemplateSize => LabelWidthMm <= 0 || LabelHeightMm <= 0;

    /// <summary>可用印刷区宽度（毫米，已扣页边、已按横竖向换算）。</summary>
    [JsonIgnore]
    public double UsableWidthMm => Math.Max(0, EffectivePaperWidthMm - MarginLeftMm - MarginRightMm);

    /// <summary>可用印刷区高度（毫米）。</summary>
    [JsonIgnore]
    public double UsableHeightMm => Math.Max(0, EffectivePaperHeightMm - MarginTopMm - MarginBottomMm);

    [JsonIgnore]
    public double EffectivePaperWidthMm => Landscape ? PaperHeightMm : PaperWidthMm;

    [JsonIgnore]
    public double EffectivePaperHeightMm => Landscape ? PaperWidthMm : PaperHeightMm;

    [JsonIgnore]
    public double PaperAreaMm2 => EffectivePaperWidthMm * EffectivePaperHeightMm;

    /// <summary>界面用的一句话描述。</summary>
    public string Description
    {
        get
        {
            var paper = $"{EffectivePaperWidthMm:0.#} × {EffectivePaperHeightMm:0.#} mm" + (Landscape ? "（横向）" : string.Empty);
            var label = FollowTemplateSize ? "标签跟随模板" : $"{LabelWidthMm:0.#} × {LabelHeightMm:0.#} mm";
            var grid = Columns > 0 && Rows > 0 ? $"{Columns} 列 × {Rows} 行" : "自动密排";
            return $"{Name}（{paper} · {label} · {grid} · 间距 {GutterXMm:0.#}/{GutterYMm:0.#}）";
        }
    }

    /// <summary>复制成用户纸规（内置种子不可改，改前先另存）。</summary>
    public SheetSpec CloneAsUserCopy(string newName)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Mapping.ProfileStore.JsonOptions);
        var copy = System.Text.Json.JsonSerializer.Deserialize<SheetSpec>(json, Mapping.ProfileStore.JsonOptions)!;
        copy.Id = "user." + Guid.NewGuid().ToString("N")[..8];
        copy.Name = newName;
        copy.BuiltIn = false;
        return copy;
    }
}

/// <summary>
/// 纸规校验器：与 <see cref="TemplateValidator"/> 同一套语义（有 Error 就不许入库、不许拼版）。
/// </summary>
public static class SheetSpecValidator
{
    /// <summary>
    /// 校验纸规本身，并顺带检查它与标签尺寸是否搭配。
    /// </summary>
    /// <param name="spec">纸规。</param>
    /// <param name="labelWidthMm">实际要排的标签宽（模板尺寸），用于尺寸不匹配告警。</param>
    /// <param name="labelHeightMm">实际标签高。</param>
    public static IReadOnlyList<TemplateIssue> Validate(SheetSpec spec, double labelWidthMm = 0, double labelHeightMm = 0)
    {
        var issues = new List<TemplateIssue>();
        if (spec is null)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, "纸规为空。"));
            return issues;
        }

        if (spec.SchemaVersion > SheetSpec.CurrentSchemaVersion)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"纸规版本 v{spec.SchemaVersion} 高于当前程序支持的 v{SheetSpec.CurrentSchemaVersion}，请升级 LabelGou。"));
        }

        // 「一页一枚」的纸面是引擎按实际标签展开的，二十几毫米的小唛头加页边本来就不到 30mm，
        // 拿标准纸的下限去卡它只会把一个能用的档报成死胡同（上一版就是这样）。
        var paperFloorMm = spec.FollowsLabel ? 1 : SheetSpec.MinPaperSideMm;
        CheckSide(issues, "纸张宽度", spec.PaperWidthMm, paperFloorMm);
        CheckSide(issues, "纸张高度", spec.PaperHeightMm, paperFloorMm);

        foreach (var (label, value) in new[]
                 {
                     ("左边距", spec.MarginLeftMm), ("上边距", spec.MarginTopMm),
                     ("右边距", spec.MarginRightMm), ("下边距", spec.MarginBottomMm),
                     ("列间距", spec.GutterXMm), ("行间距", spec.GutterYMm),
                 })
        {
            if (value < 0)
                issues.Add(new TemplateIssue(IssueLevel.Error, $"{label}不能为负（当前 {value:0.##} mm）。"));
            else if (value > 100)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{label} {value:0.##} mm 偏大，整版会浪费很多纸。"));
        }

        if (spec.MarginLeftMm + spec.MarginRightMm >= spec.EffectivePaperWidthMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, "左右页边距之和不小于纸宽，可用区为负。"));
        if (spec.MarginTopMm + spec.MarginBottomMm >= spec.EffectivePaperHeightMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, "上下页边距之和不小于纸高，可用区为负。"));

        if (!spec.FollowTemplateSize)
        {
            CheckSide(issues, "标签宽度", spec.LabelWidthMm, MinLabelMm, TemplateValidator.MaxLabelSideMm);
            CheckSide(issues, "标签高度", spec.LabelHeightMm, MinLabelMm, TemplateValidator.MaxLabelSideMm);
        }
        else if (spec.LabelWidthMm > 0 != spec.LabelHeightMm > 0)
        {
            issues.Add(new TemplateIssue(IssueLevel.Warning, "标签宽高只填了一个，另一个为 0：整张纸规都按模板尺寸排版。"));
        }

        if (spec.Columns < 0 || spec.Rows < 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "行列数不能为负（0 表示自动）。"));

        if (spec.CropMarks != CropMarkMode.None)
        {
            if (spec.CropMarkLengthMm <= 0)
                issues.Add(new TemplateIssue(IssueLevel.Error, "开了裁切线但长度是 0，等于画不出来。"));
            if (spec.CropMarkThicknessMm <= 0)
                issues.Add(new TemplateIssue(IssueLevel.Warning, "裁切线线宽为 0，打印时不会显示。"));
            if (spec.CropMarks == CropMarkMode.EveryLabel)
            {
                // 相邻两枚各自向外画角线，彼此占用的间距 = 2 ×（间隙 + 线长）
                var needed = 2 * (spec.CropMarkGapMm + spec.CropMarkLengthMm);
                if (spec.GutterXMm < needed || spec.GutterYMm < needed)
                {
                    issues.Add(new TemplateIssue(IssueLevel.Warning,
                        $"每枚都画角线时至少需要 {needed:0.#} mm 的标签间距，当前 {spec.GutterXMm:0.#}/{spec.GutterYMm:0.#} mm —— " +
                        "相邻角线会重叠，建议改用「整版四角」或加大间距。"));
                }
            }
            else
            {
                // 整版四角的角线向外走，吃的是页边：页边不够就会被纸张边界夹掉。
                // 上一版靠 Clamp 默默夹成 1mm 的小尾巴（看着像坏了的线），现在引擎主动收短、这里把原因说清。
                var narrowest = Math.Min(Math.Min(spec.MarginLeftMm, spec.MarginRightMm),
                    Math.Min(spec.MarginTopMm, spec.MarginBottomMm));
                var needed = spec.CropMarkGapMm + spec.CropMarkLengthMm;
                if (narrowest < needed)
                {
                    issues.Add(new TemplateIssue(IssueLevel.Warning,
                        narrowest - spec.CropMarkGapMm < 1.5
                            ? $"最窄的一边页边只有 {narrowest:0.#} mm，装不下 {needed:0.#} mm（间隙 + 线长）的四角角线：这几个角会整角不画。" +
                              "纸面贴满标签的档（一页一枚、一开四铺满）就是这样，建议把裁切线关掉或加宽页边。"
                            : $"最窄的一边页边只有 {narrowest:0.#} mm，小于角线要的 {needed:0.#} mm（间隙 + 线长）：四角角线会被裁切。"));
                }
            }
        }

        if (spec.LabelOutlineMm < 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "刀模示意线宽不能为负。"));

        // 与标签尺寸的搭配（只提醒，不阻止）
        if (labelWidthMm > 0 && labelHeightMm > 0 && !spec.FollowTemplateSize)
        {
            var same = Nearly(spec.LabelWidthMm, labelWidthMm) && Nearly(spec.LabelHeightMm, labelHeightMm);
            var swapped = Nearly(spec.LabelWidthMm, labelHeightMm) && Nearly(spec.LabelHeightMm, labelWidthMm);
            if (!same && !swapped)
            {
                issues.Add(new TemplateIssue(IssueLevel.Warning,
                    $"纸规标签 {spec.LabelWidthMm:0.#}×{spec.LabelHeightMm:0.#} mm 与模板 {labelWidthMm:0.#}×{labelHeightMm:0.#} mm 不一致，" +
                    "拼版将按模板尺寸落位。"));
            }
        }

        return issues;
    }

    /// <summary>最小标签边长（毫米），比这还小的是热敏小票级别，本项目不管。</summary>
    public const double MinLabelMm = 8;

    private static void CheckSide(List<TemplateIssue> issues, string what, double value,
        double min = SheetSpec.MinPaperSideMm, double max = SheetSpec.MaxPaperSideMm)
    {
        if (value <= 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"{what}必须大于 0。"));
        else if (value < min)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"{what} {value:0.##} mm 小于下限 {min:0.#} mm。"));
        else if (value > max)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"{what} {value:0.##} mm 超过上限 {max:0.#} mm。"));
    }

    private static bool Nearly(double a, double b) => Math.Abs(a - b) <= TemplateValidator.ToleranceMm;
}
