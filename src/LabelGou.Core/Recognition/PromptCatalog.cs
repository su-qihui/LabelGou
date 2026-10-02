namespace LabelGou.Core.Recognition;

/// <summary>提示词那几段的键（也是落盘的文件名，所以定下来就别改——改了旧覆盖文件读不到，等于悄悄回到默认）。</summary>
public static class PromptKeys
{
    public const string System = "system";
    public const string ReadTask = "read.task";
    public const string ReadBind = "read.bind";
    public const string ReadAsk = "read.ask";
    public const string ReadTone = "read.tone";
    public const string LayoutNoAsk = "layout.noask";
    public const string LayoutFont = "layout.font";
    public const string LayoutTone = "layout.tone";
}

/// <summary>
/// 一段可以给他自己改的提示词（第 103 棒，用户：「对AI识别调优 / 可以自由调整AI提示语开放窗口」）。
/// <para><strong>为什么只开这几段、不开整条</strong>：一条请求里大半是<strong>软件现算出来的数据</strong>
/// （表像、行数、字段清单、他拍过的板），那些不是"话"，是事实——让人手改就等于让人手改事实。
/// 能改的只有"交代分工"那几段固定的话。</para>
/// <para><strong>代价记在这里</strong>：文案一被手改，"AI 为什么这次不这么做了"就没法再从代码回查。
/// 所以每一段都带 <see cref="RequiredTokens"/>（删掉就会把软件的行为删掉的词），存之前先过
/// <see cref="PromptCatalog.Validate"/>；改了什么、什么时候改的，全部落日志。</para>
/// </summary>
/// <param name="Default">出厂那段话的取法（不是值——值住在 <c>AiSheetProposalPrompt</c> 里那圈常量，
/// 这里只是把它登记进清单，避免同一句话抄两份、早晚对不上）。</param>
public sealed record PromptSection(string Key, string Title, string Step, Func<string> Default, string[] RequiredTokens)
{
    /// <summary>这一段在界面上的说明：为什么值得改、改坏会怎样。</summary>
    public string Note { get; init; } = "";

    public string DefaultText => Default();
}

/// <summary>可改段落的清单与守门判据。</summary>
public static class PromptCatalog
{
    public static readonly IReadOnlyList<PromptSection> All = new[]
    {
        new PromptSection(PromptKeys.System, "系统那句（两步共用）", "共用",
            () => AiSheetProposalPrompt.DefaultSystem, Array.Empty<string>())
        {
            Note = "定的是「你是谁、只回 JSON、说人话」。这里加一句术语，模型就可能回一段解析不了的东西。",
        },
        new PromptSection(PromptKeys.ReadTask, "第一步的任务：只读表、不许排版", "读表",
            () => AiSheetProposalPrompt.DefaultReadTask, new[] { "不要给排版方案" })
        {
            Note = "这句删软了，模型会在你还没拍板时就把版排出来（第 40 棒拆两步就是为了拦这件事）。",
        },
        new PromptSection(PromptKeys.ReadBind, "第一步：怎么认每一列", "读表",
            () => AiSheetProposalPrompt.DefaultReadBind, Array.Empty<string>())
        {
            Note = "这段管「宁可少绑一列，也不要错绑一列」——错一列就是数错张数、印错货。",
        },
        new PromptSection(PromptKeys.ReadAsk, "第一步：只许问这几类 + 问的规矩", "读表",
            () => AiSheetProposalPrompt.DefaultReadAsk,
            new[] { "itemno-tail", "qty-column", "row-keep", "template-source", "header-row", "fixed-value", "column-meaning" })
        {
            Note = "这七个 action 是软件接得住的全部动作，少写一个名字，模型问出那一类时软件就丢掉了那条问题。",
        },
        new PromptSection(PromptKeys.ReadTone, "第一步：说话要求", "读表",
            () => AiSheetProposalPrompt.DefaultReadTone, Array.Empty<string>())
        {
            Note = "管的是「别把几件事挤进一句话」——他是一行一行扫的。",
        },
        new PromptSection(PromptKeys.LayoutNoAsk, "第二步：这一步不要提问", "排版",
            () => AiSheetProposalPrompt.DefaultLayoutNoAsk, new[] { "不要提问" })
        {
            Note = "排版看得见也退得回，再问一遍「要不要重排」是白问；疑点走 warnings 摆在结果旁边。",
        },
        new PromptSection(PromptKeys.LayoutFont, "第二步：字号不用模型管", "排版",
            () => AiSheetProposalPrompt.DefaultLayoutFont, new[] { "软件自己会算" })
        {
            Note = "第 39 棒的定案：字号/粗细/居中由软件去量模板那一列，不让模型口算回填（每轮都不一样就是这么来的）。",
        },
        new PromptSection(PromptKeys.LayoutTone, "第二步：说话要求", "排版",
            () => AiSheetProposalPrompt.DefaultLayoutTone, Array.Empty<string>()),
    };

    public static PromptSection? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    public static string DefaultOf(string key) => Find(key)?.DefaultText ?? "";

    /// <summary>
    /// 存之前守一道：<strong>空文不收</strong>（整段清空等于把那条指令删了，多半是误操作），
    /// <strong>少了必须留着的词也不收</strong>（那些词背后是软件的行为，不是修辞）。
    /// 返回 null = 可以存；否则是要摆给他的那句话。
    /// </summary>
    public static string? Validate(PromptSection section, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "这段不能清空：整段删掉等于把那条指令撤了。要恢复出厂的话点「恢复默认」。";
        var missing = section.RequiredTokens.Where(t => !text.Contains(t, StringComparison.Ordinal)).ToList();
        return missing.Count == 0
            ? null
            : "这段里有几个词是软件行为的地基，删掉模型就会问出接不住的东西：" + string.Join("、", missing);
    }
}

/// <summary>
/// 一次请求实际用的那套文案：有覆盖用覆盖，没有用出厂。
/// <para>做成"递一个取法进来"而不是让 Core 去读文件——Core 不知道 %APPDATA% 在哪，
/// 单测也不用碰磁盘（§五-48 那一族：测试不许写进真实运行目录）。</para>
/// </summary>
public sealed class PromptTexts
{
    private readonly Func<string, string?>? _lookup;

    public PromptTexts(Func<string, string?>? lookup = null) => _lookup = lookup;

    /// <summary>出厂那套（没有任何覆盖）。</summary>
    public static PromptTexts Default { get; } = new();

    public string Get(string key) => _lookup?.Invoke(key) is { } over && !string.IsNullOrWhiteSpace(over)
        ? over
        : PromptCatalog.DefaultOf(key);

    /// <summary>这一段现在用的是覆盖还是出厂（界面上那句"当前：自定义/默认"读它）。</summary>
    public bool IsOverridden(string key) => _lookup?.Invoke(key) is { } over && !string.IsNullOrWhiteSpace(over);
}
