using System.Text;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 他自己改过的那几段提示词落在哪儿（第 103 棒）：一段一个 UTF-8 <c>.txt</c>，放在
/// <c>%APPDATA%\LabelGou\prompts\</c> 下，文件名就是 <see cref="PromptSection.Key"/>。
/// <para><strong>为什么是一段一个文件、不塞进 uistate.json</strong>：他要能直接用记事本打开看
/// "我当初到底改了什么"，也能整份拷走备份；JSON 里一长串转义过的中文做不到这件事。</para>
/// <para><strong>读的时候一律宽容</strong>：文件不存在 / 空 / 读失败都当"没改过"，退回出厂那段——
/// 提示词读不出来不是故障，不该拦人干活（同 <c>UiStateStore</c> 那条口径）。</para>
/// </summary>
public sealed class PromptOverrideStore
{
    private readonly string _directory;

    public PromptOverrideStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou", "prompts");
    }

    /// <summary>文件摆在哪（界面上要告诉他，别让他猜）。</summary>
    public string Directory => _directory;

    private string PathOf(string key) => System.IO.Path.Combine(_directory, key + ".txt");

    /// <summary>读过这一段就返回它，没改过 / 读不到 / 空白一律 null（= 用出厂）。</summary>
    public string? Load(string key)
    {
        try
        {
            var file = PathOf(key);
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsOverridden(string key) => Load(key) is not null;

    public void Save(string key, string text)
    {
        System.IO.Directory.CreateDirectory(_directory);
        File.WriteAllText(PathOf(key), text, Encoding.UTF8);
    }

    /// <summary>删掉覆盖 = 回到出厂。文件本来不在就不动，也不报错。</summary>
    public void Clear(string key)
    {
        try
        {
            if (File.Exists(PathOf(key))) File.Delete(PathOf(key));
        }
        catch (Exception)
        {
            // 删不掉就是还是那份旧的：界面上"当前：自定义"照样标着，不骗人
        }
    }

    /// <summary>现在被改过的是哪几段（设置页上那行"已自定义 N 段"读它）。</summary>
    public IReadOnlyList<string> OverriddenKeys()
        => PromptCatalog.All.Where(s => IsOverridden(s.Key)).Select(s => s.Key).ToList();

    /// <summary>交给 <see cref="PromptTexts"/> 的那个取法。</summary>
    public Func<string, string?> Snapshot() => Load;
}
