using System.Globalization;
using System.Windows.Threading;
using LabelGou.App.Mvvm;

namespace LabelGou.App.ViewModels;

/// <summary>
/// 「行检查」这一屏的两件顺手工具（第 89 棒②，用户 2026-09-20：「向右箭头--下一张，向左箭头--上一张，
/// 并添加幻灯片式检查」）。
/// <para>幻灯片干的事就一件：<strong>一行一张自动往下过</strong>。核对一板货几十行，手动点几十次「下一件」
/// 手腕酸，还容易漏一行；放着过一遍，看不过来的那行按 ← 退回去就是。</para>
/// <para>三条口径是有意定的，别顺手改：① 放到<strong>最后一行自己停</strong>，不绕回第一行——循环播放会把
/// "已经看完了"和"还在看"混成一件事；② 他手动翻一张（按钮、箭头、点缩略格）<strong>立刻停下</strong>，
/// 自动的手不跟人的手抢方向盘；③ 这一整套只动 <see cref="CurrentIndex"/>，
/// 出片那份标签集一张不多一张不少（判据在 <c>RowCheckPagingAndSlideshowTests</c>）。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>默认每张停 2 秒：够扫一眼货号与件数，又不至于慢到像卡住。</summary>
    public const double DefaultSlideshowSeconds = 2;

    public const double MinSlideshowSeconds = 0.5;
    public const double MaxSlideshowSeconds = 10;

    private DispatcherTimer? _slideshowTimer;
    private bool _slideshowPlaying;
    private bool _slideshowAdvancing;
    private double _slideshowSeconds = DefaultSlideshowSeconds;

    /// <summary>▶ / ⏸ 那一颗。</summary>
    public RelayCommand ToggleSlideshowCommand { get; private set; } = null!;

    /// <summary>每张停久一点（＋）。</summary>
    public RelayCommand SlideshowLongerCommand { get; private set; } = null!;

    /// <summary>每张停短一点（－）。</summary>
    public RelayCommand SlideshowShorterCommand { get; private set; } = null!;

    /// <summary>由构造函数调用（命令要在界面绑定之前建好）。</summary>
    private void InitRowCheckTools()
    {
        ToggleSlideshowCommand = new RelayCommand(ToggleSlideshow);
        SlideshowLongerCommand = new RelayCommand(() => SlideshowSeconds += 0.5);
        SlideshowShorterCommand = new RelayCommand(() => SlideshowSeconds -= 0.5);
    }

    /// <summary>正在放吗（界面那颗的图标与文字都问这一句）。</summary>
    public bool SlideshowPlaying
    {
        get => _slideshowPlaying;
        private set
        {
            if (!Set(ref _slideshowPlaying, value)) return;
            Raise(nameof(SlideshowButtonLabel));
        }
    }

    /// <summary>那颗上写的话：放着的时候必须写"停止"，不然他不知道再点一下会发生什么。</summary>
    public string SlideshowButtonLabel => SlideshowPlaying ? "⏸ 停止幻灯片" : "▶ 幻灯片检查";

    /// <summary>每张停几秒（0.5 ~ 10，越界夹住）。</summary>
    public double SlideshowSeconds
    {
        get => _slideshowSeconds;
        set
        {
            var v = Math.Clamp(value, MinSlideshowSeconds, MaxSlideshowSeconds);
            if (!Set(ref _slideshowSeconds, v)) return;
            if (_slideshowTimer is not null) _slideshowTimer.Interval = TimeSpan.FromSeconds(v);
            Raise(nameof(SlideshowIntervalText));
        }
    }

    /// <summary>秒数的"直接填数字"那条路（与 －/＋ 并存）。填不进就不改，并把框里弹回原值。</summary>
    public string SlideshowIntervalText
    {
        get => _slideshowSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                SlideshowSeconds = seconds;
            else Raise(nameof(SlideshowIntervalText));
        }
    }

    /// <summary>
    /// 点那颗 ▶ / ⏸。行检查没开的话它替用户开（幻灯片就是一行一张），摊成「全部行」时先收回来
    /// ——那一屏满屏都是，没有"下一张"可放。
    /// </summary>
    public void ToggleSlideshow()
    {
        if (SlideshowPlaying)
        {
            StopSlideshow("手动停下");
            return;
        }

        if (!RowCheck) RowCheck = true;
        if (RowCheckAll) RowCheckAll = false;
        if (RecordTotal == 0)
        {
            StatusMessage = "没有可放的行：先在 ① 步导入表格；只想看版式就用「示意预览」。";
            return;
        }

        _slideshowTimer ??= NewSlideshowTimer();
        _slideshowTimer.Interval = TimeSpan.FromSeconds(_slideshowSeconds);
        CurrentIndex = 1;
        SlideshowPlaying = true;
        _slideshowTimer.Start();
        StatusMessage = $"幻灯片检查：从第 1 行起，每 {_slideshowSeconds:0.##} 秒下一行，共 {RecordTotal} 行。" +
                        "再点一颗停；中途用 ← / → 自己翻会自动停下。";
    }

    /// <summary>
    /// 走一拍：下一行；已经在最后一行就停下。
    /// <para>单独开成方法就是为了让判据不靠睡觉——测试直接点这一拍，不许 <c>Thread.Sleep</c> 等定时器。</para>
    /// </summary>
    public void SlideshowTick()
    {
        if (!SlideshowPlaying) return;
        if (_currentIndex >= RecordTotal)
        {
            StopSlideshow("放到最后一行了");
            return;
        }

        _slideshowAdvancing = true;
        try
        {
            CurrentIndex = _currentIndex + 1;
        }
        finally
        {
            _slideshowAdvancing = false;
        }

        if (_currentIndex >= RecordTotal) StopSlideshow("放到最后一行了");
    }

    /// <summary>停下自动播放。<strong>只停这一件事</strong>：不动行检查、不动当前这一张。</summary>
    public void StopSlideshow(string why)
    {
        _slideshowTimer?.Stop();
        if (!SlideshowPlaying) return;
        SlideshowPlaying = false;
        StatusMessage = $"幻灯片已停下（{why}）。要接着放再点一颗 ▶。";
    }

    private DispatcherTimer NewSlideshowTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_slideshowSeconds) };
        timer.Tick += (_, _) => SlideshowTick();
        return timer;
    }
}
