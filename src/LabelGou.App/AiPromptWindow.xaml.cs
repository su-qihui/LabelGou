using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Recognition;

namespace LabelGou.App;

/// <summary>
/// 「AI 提示语」那扇窗（第 103 棒）：能看全文、能改、能一键回出厂。
/// <para><strong>开的是哪几段</strong>：<see cref="PromptCatalog.All"/> 里那八段"交代分工"的固定话。
/// 一条请求里其余大半是软件现算的<strong>事实</strong>（表像、行数、字段清单、他拍过的板），
/// 那些不在这里——让人手改事实比不改还危险。</para>
/// <para><strong>护栏（这一扇存在的前提）</strong>：① 空文与少了地基词的稿子不收
/// （<see cref="PromptCatalog.Validate"/>，地基词就是软件接得住的那七个 action 名之类）；
/// ② 每一段都明标"当前：出厂默认 / 已自定义"，并把文件位置写在顶上；
/// ③ 每次存与每次恢复都落一行日志——文案被手改之后，"AI 为什么这次不这么做"没法再从代码回查，
/// 日志是剩下的唯一线索。</para>
/// </summary>
public partial class AiPromptWindow : Window
{
    private sealed record Row(PromptSection Section, string Title, string Mark);

    private readonly PromptOverrideStore _store;
    private readonly List<Row> _rows = new();
    private bool _loading;

    /// <summary>这一扇里存过或恢复过东西（设置页据此刷新"已自定义 N 段"那行）。</summary>
    public bool Changed { get; private set; }

    /// <summary>清单上有几段（判据钉的是"一段都没漏"）。</summary>
    public int SectionCount => _rows.Count;

    /// <summary>这一段现在实际会发出去的那版文字（有覆盖就是覆盖）。</summary>
    public string TextOf(string key) => _store.Load(key) ?? PromptCatalog.DefaultOf(key);

    public AiPromptWindow(MainViewModel vm, bool dark)
    {
        if (vm is null) throw new ArgumentNullException(nameof(vm));
        _store = vm.PromptOverrides;
        InitializeComponent();
        SimpleTheme.ApplyInto(this, dark);
        WhereText.Text = $"这些字发出去之前会先问一遍磁盘：改过的存在 {_store.Directory}（一段一个 txt，可以直接用记事本看）";
        RebuildRows(PromptCatalog.All[0].Key);
    }

    private void RebuildRows(string selectKey)
    {
        _loading = true;
        _rows.Clear();
        foreach (var section in PromptCatalog.All)
            _rows.Add(new Row(section, section.Title, _store.IsOverridden(section.Key) ? "●" : ""));
        SectionList.ItemsSource = null;
        SectionList.ItemsSource = _rows;
        var pick = _rows.FirstOrDefault(r => r.Section.Key == selectKey) ?? _rows[0];
        SectionList.SelectedItem = pick;
        _loading = false;
        Show(pick.Section);
    }

    private void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || SectionList.SelectedItem is not Row row) return;
        Show(row.Section);
    }

    private void Show(PromptSection section)
    {
        var over = _store.Load(section.Key);
        TitleText.Text = $"{section.Step} · {section.Title}";
        NoteText.Text = section.Note;
        Editor.Text = over ?? section.DefaultText;
        StateText.Text = over is null
            ? "当前：出厂默认"
            : $"当前：已自定义（{over.Length} 字，出厂 {section.DefaultText.Length} 字）";
        StateText.Foreground = (System.Windows.Media.Brush)(over is null
            ? FindResource("SubInkBrush") : FindResource("BrandBrush"));
        HintText.Text = "改完点「保存这段」立刻生效；改坏了随时「恢复默认」。";
        HintText.Foreground = (System.Windows.Media.Brush)FindResource("SubInkBrush");
    }

    private PromptSection? Current() => (SectionList.SelectedItem as Row)?.Section;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } section) return;
        if (PromptCatalog.Validate(section, Editor.Text) is { } problem)
        {
            HintText.Text = "没保存：" + problem;
            HintText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            AppLog.Warning($"AI 提示词「{section.Key}」这次没存进去：{problem}");
            return;
        }
        _store.Save(section.Key, Editor.Text);
        Changed = true;
        AppLog.Info($"AI 提示词已改：{section.Key}（{Editor.Text.Length} 字，出厂 {section.DefaultText.Length} 字）");
        RebuildRows(section.Key);
    }

    private void OnResetOneClick(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } section) return;
        _store.Clear(section.Key);
        Changed = true;
        AppLog.Info($"AI 提示词恢复出厂：{section.Key}");
        RebuildRows(section.Key);
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        var keys = _store.OverriddenKeys();
        if (keys.Count == 0)
        {
            HintText.Text = "现在八段全是出厂默认，没什么可恢复的。";
            return;
        }
        var answer = MessageBox.Show(this,
            $"现在有 {keys.Count} 段是你自己改过的。全部恢复出厂？改的那些字会删掉，删完就找不回来。",
            "AI 提示语", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var key in keys) _store.Clear(key);
        Changed = true;
        AppLog.Info($"AI 提示词全部恢复出厂（删掉 {keys.Count} 段覆盖：{string.Join("、", keys)}）");
        RebuildRows(PromptCatalog.All[0].Key);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
