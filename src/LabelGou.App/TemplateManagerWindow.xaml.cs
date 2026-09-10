using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using LabelGou.App.Services;
using LabelGou.Core.Templates;

namespace LabelGou.App;

/// <summary>
/// 「批量管理模板」对话框（用户 2026-09-10 点名要的：**多选删除**）。
/// <para><strong>为什么要有它</strong>：③ 步那个下拉当时堆了 14 个同名的「AI 建议版式」，
/// 名字一样、尺寸一样，**根本看不出哪份是哪份**，而且原来那颗「删除这份模板…」一次只删一个。
/// 他要的不只是"能多选"，更是"**删之前我得认得出每份是什么**"——所以每一行都把
/// 名字／尺寸／要素数／**印的是什么**／来源／最后改动 全摊出来，
/// 并且把「来源」直接标成「AI 排的」，好让他一眼认出那一堆是怎么来的。</para>
/// <para>删除走 <see cref="TemplateCleanup.DeleteWithBackup"/>：**先备份再删**（他会担心"有用的也删了"），
/// 内置模板勾不了也删不掉。</para>
/// </summary>
public partial class TemplateManagerWindow : Window
{
    private readonly TemplateStore _store;
    private readonly ObservableCollection<Row> _rows = new();

    public TemplateManagerWindow(TemplateStore store)
    {
        InitializeComponent();
        _store = store;
        Rows.ItemsSource = _rows;
        Refresh();
    }

    /// <summary>界面上的一行。属性全是只读的展示值——这一屏只做"选哪些删"，不改任何内容。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _selected;

        public Row(LabelTemplate template, string? filePath)
        {
            Template = template;
            FilePath = filePath;
        }

        public LabelTemplate Template { get; }

        public string? FilePath { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>勾没勾上（复选框双向绑它）。</summary>
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        /// <summary>能不能删：内置的不行（它不落盘，也没有文件可删）。</summary>
        public bool CanDelete => !Template.BuiltIn && FilePath is not null;

        public string Name => Template.Name;

        public string SizeText => $"{Template.WidthMm:0.#} × {Template.HeightMm:0.#} mm";

        public string ElementText => $"{Template.Elements.Count}";

        /// <summary>
        /// 「印的是什么」：**第一行文字**（这就是他认出"哪版有 JP"的办法）。
        /// 模板里的令牌（<c>{{…}}</c>）换成「…」显示——令牌本身对人不构成识别信息。
        /// </summary>
        public string Summary
        {
            get
            {
                var first = Template.Elements
                    .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                    .OrderBy(e => e.Y)
                    .Select(e => e.Text!.Trim())
                    .FirstOrDefault();
                if (first is null) return "（这一份里没有文字）";
                var plain = new System.Text.StringBuilder();
                var inside = false;
                foreach (var c in first)
                {
                    if (c == '{') { inside = true; continue; }
                    if (c == '}') { inside = false; continue; }
                    if (!inside) plain.Append(c);
                }
                var text = plain.ToString().Trim();
                if (text.Length == 0) text = "（第一行全是变量）";
                return text.Length <= 18 ? text : text[..18] + "…";
            }
        }

        /// <summary>来源：内置／AI 排的／自己存的。AI 那份靠 Id 前缀认（行式版式的骨架生成的）。</summary>
        public string SourceText => Template.BuiltIn
            ? "内置"
            : Template.Id.StartsWith("user.rows-", StringComparison.Ordinal) ? "AI 排的" : "自己存的";

        public string TimeText => FilePath is null
            ? "—"
            : File.GetLastWriteTime(FilePath).ToString("MM-dd HH:mm");
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (var template in _store.ListAll()
                     .OrderByDescending(t => t.BuiltIn ? DateTime.MinValue : FileTime(t))
                     .ThenBy(t => t.Name, StringComparer.CurrentCulture))
        {
            var path = template.BuiltIn ? null : _store.FindFileFor(template.Id);
            var row = new Row(template, path);
            // 勾一下就要看到按钮上的份数跟着变——不然人不知道"我一共勾了几份"。
            row.PropertyChanged += (_, _) => UpdateStatus();
            _rows.Add(row);
        }
        UpdateStatus();
    }

    private DateTime FileTime(LabelTemplate template)
    {
        var path = _store.FindFileFor(template.Id);
        try { return path is null ? DateTime.MinValue : File.GetLastWriteTime(path); }
        catch (IOException) { return DateTime.MinValue; }
    }

    private IEnumerable<Row> Picked => _rows.Where(r => r.Selected && r.CanDelete);

    private void UpdateStatus()
    {
        var n = Picked.Count();
        var canDelete = _rows.Count(r => r.CanDelete);
        DeleteButton.IsEnabled = n > 0;
        DeleteButton.Content = n > 0 ? $"删除选中的 {n} 份…" : "删除选中的…";
        StatusText.Text = $"这一共 {_rows.Count} 份（可删 {canDelete} 份）；已勾 {n} 份。";
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => r.CanDelete)) row.Selected = true;
        UpdateStatus();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = false;
        UpdateStatus();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var picked = Picked.ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "还没勾任何可删的模板。", "删除模板",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 确认框里**逐条列出要删的是哪几份**（名字 + 最后改动）：用户点名担心的就是"把有用的也删了"，
        // 所以这一步不许只说一个数字。
        var list = string.Join("\n", picked.Take(15).Select(r => $"· {r.Name}（{r.TimeText}）"));
        if (picked.Count > 15) list += $"\n… 还有 {picked.Count - 15} 份";
        var answer = MessageBox.Show(this,
            $"要删这 {picked.Count} 份模板吗？\n\n{list}\n\n（删前会自动备份一份，删完告诉你备份在哪。）",
            "删除模板", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var (deleted, skipped, backupDir) = TemplateCleanup.DeleteWithBackup(
            _store, picked.Select(r => r.Template.Id).ToList());
        AppLog.Info($"批量删除模板：选了 {picked.Count} 份，删掉 {deleted} 份"
            + (backupDir is null ? "（没建备份）" : $"，备份在 {backupDir}"));

        Refresh();
        MessageBox.Show(this,
            $"删了 {deleted} 份。"
            + (skipped > 0 ? $"\n{skipped} 份没删成（内置的、或文件已经不在）。" : string.Empty)
            + (backupDir is null ? string.Empty : $"\n\n备份在这个目录里，删错了可以拿回来：\n{backupDir}"),
            "删除完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
