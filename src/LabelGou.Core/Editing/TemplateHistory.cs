using System.Text.Json;
using LabelGou.Core.Mapping;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Editing;

/// <summary>
/// 模板深拷贝工具。<strong>编辑器的撤销/重做与“另存为副本”都依赖它</strong>：
/// 快照必须与在编模板彻底断开，否则撤销会把当前对象一起改掉（这类 bug 单测才抓得住）。
/// </summary>
public static class TemplateCopier
{
    public static LabelTemplate CloneTemplate(this LabelTemplate template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        var json = JsonSerializer.Serialize(template, ProfileStore.JsonOptions);
        return JsonSerializer.Deserialize<LabelTemplate>(json, ProfileStore.JsonOptions)
            ?? throw new InvalidOperationException("模板序列化后无法还原，内部逻辑有误。");
    }

    /// <summary>
    /// 把 <paramref name="source"/> 的内容原地写进 <paramref name="target"/>（保留 target 引用）。
    /// UI 侧绑的是 target 实例，撤销时不能换实例，只能换内容。
    /// </summary>
    public static void CopyInto(this LabelTemplate source, LabelTemplate target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (target is null) throw new ArgumentNullException(nameof(target));

        target.SchemaVersion = source.SchemaVersion;
        target.Id = source.Id;
        target.Name = source.Name;
        target.Note = source.Note;
        target.WidthMm = source.WidthMm;
        target.HeightMm = source.HeightMm;
        target.PaddingMm = source.PaddingMm;
        target.BorderMm = source.BorderMm;
        target.CropMarkMm = source.CropMarkMm;
        target.BuiltIn = source.BuiltIn;

        target.Elements.Clear();
        foreach (var element in source.Elements) target.Elements.Add(element.CloneTemplate());
    }

    /// <summary>单个元素的深拷贝。</summary>
    public static TemplateElement CloneTemplate(this TemplateElement element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        var json = JsonSerializer.Serialize(element, ProfileStore.JsonOptions);
        return JsonSerializer.Deserialize<TemplateElement>(json, ProfileStore.JsonOptions)
            ?? throw new InvalidOperationException("元素序列化后无法还原，内部逻辑有误。");
    }
}

/// <summary>
/// 撤销/重做栈（JSON 快照实现）。
/// <para>
/// 用法是「<strong>动手之前先录</strong>」：每次即将改变模板的操作前调 <see cref="Capture"/>，
/// 撤销时把当前状态压入重做栈、再取出最近快照原地写回。这样快照栈不需要知道操作语义，
/// 一次拖动结束记一步，而不是鼠标每动一像素记一步。
/// </para>
/// </summary>
public sealed class TemplateHistory
{
    /// <summary>最多记几步。再多对模板编辑没意义，只是白占内存。</summary>
    public const int DefaultLimit = 50;

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();

    public TemplateHistory(int limit = DefaultLimit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), "快照栈上限至少 1 步。");
        Limit = limit;
    }

    public int Limit { get; }

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>在改动之前调用，记录当前（尚未被改的）状态。</summary>
    public void Capture(LabelTemplate state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        _undo.Add(JsonSerializer.Serialize(state, ProfileStore.JsonOptions));
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>撤销：把当前状态存入重做栈，再用最近快照原地写回。没得撤返回 false。</summary>
    public bool Undo(LabelTemplate current)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (_undo.Count == 0) return false;

        _redo.Add(JsonSerializer.Serialize(current, ProfileStore.JsonOptions));
        if (_redo.Count > Limit) _redo.RemoveAt(0);

        Restore(_undo[^1], current);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    /// <summary>重做。</summary>
    public bool Redo(LabelTemplate current)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (_redo.Count == 0) return false;

        _undo.Add(JsonSerializer.Serialize(current, ProfileStore.JsonOptions));
        if (_undo.Count > Limit) _undo.RemoveAt(0);

        Restore(_redo[^1], current);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>
    /// 丢掉最近录的那一步快照（<strong>不</strong>改动当前状态）。
    /// <para>一次手势起手就录了快照，最后发现什么都没动时用——不然"只点一下选元素"也会吃掉一步撤销名额
    /// （栈上限 <see cref="Limit"/>），真历史被空步挤掉。用 Undo 退这一步是错的：那会顺手往重做栈塞一个空项。</para>
    /// </summary>
    public void DiscardTop()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>两个元素是否一模一样（判"这一笔手势其实没改任何东西"）。</summary>
    public static bool Same(TemplateElement before, TemplateElement after) =>
        JsonSerializer.Serialize(before, ProfileStore.JsonOptions) == JsonSerializer.Serialize(after, ProfileStore.JsonOptions);

    private static void Restore(string json, LabelTemplate current)
    {
        var snapshot = JsonSerializer.Deserialize<LabelTemplate>(json, ProfileStore.JsonOptions)
            ?? throw new InvalidOperationException("快照无法还原，内部逻辑有误。");
        snapshot.CopyInto(current);
    }
}
