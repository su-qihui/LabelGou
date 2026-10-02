using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;

namespace LabelGou.App;

/// <summary>
/// 简洁版的设置页（第 102 棒）：把原来写死在代码里的几颗小调优放到明面上，右边摆一份<strong>示意模板</strong>当场反映。
/// <para>用户两句原话合起来读就是一件事：「对新界面部分未加入的小调优方到设置并添加预览效果」+
/// 「设置页面加入一个模板 而不是实际预览」——预览用的是首页海报墙同一入口排出来的那张示意纸，
/// <strong>不用先导表才能调</strong>，也不新建第二条渲染路（画法唯一）。</para>
/// <para>模态开的（<c>ShowDialog</c>）：关掉那一刻壳窗一次性把新值落回资源与自适应，省掉一套跨窗实时绑定。
/// 数值与拖拽并存（他定过的规矩），两边都过 <see cref="SimpleShellFlow"/> 那把夹取尺子——
/// 框里输进坏数（空、字母、∞）不采纳、也不许落盘（§五-183）。</para>
/// </summary>
public partial class SimpleSettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _syncing;
    // XAML 解析阶段就会抛 Slider.ValueChanged（默认 Value=0 撞上 Minimum=24 那一下），
    // 那时 PreviewStage 这些命名控件还没挂上、存过的数也还没读进来——挡在这道旗后面，
    // 构造完才认事件（不然当场 NullReferenceException，这条判据就是这么抓到的）。
    private bool _ready;

    /// <summary>关掉时壳窗读这两格：落盘 + 重新自适应（点「取消」就不读，见 <see cref="Accepted"/>）。</summary>
    public double PaperMargin { get; private set; }

    public double PosterCardWidth { get; private set; }

    /// <summary>点的是「好」还是「取消」。</summary>
    public bool Accepted { get; private set; }

    public SimpleSettingsWindow(MainViewModel vm, bool dark)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        SimpleTheme.ApplyInto(this, dark);

        PaperMargin = _vm.LoadPaperMargin();
        PosterCardWidth = _vm.LoadPosterCardWidth();
        DarkCheck.IsChecked = dark;        // 先落状态（Changed 里那句 ApplyInto 是幂等的）
        PushToControls();
        RefreshPromptState();

        // 示意纸：选中那份模板排出来的真版面（海报墙用的就是它），没选中就退到第一张
        var card = _vm.PosterCards.FirstOrDefault(c => c.IsSelected) ?? _vm.PosterCards.FirstOrDefault();
        if (card is not null) PreviewView.Layout = card.Layout;
        _ready = true;
    }

    private void PushToControls()
    {
        _syncing = true;
        PaperMarginSlider.Value = PaperMargin;
        PaperMarginBox.Text = Text(PaperMargin);
        PosterWidthSlider.Value = PosterCardWidth;
        PosterWidthBox.Text = Text(PosterCardWidth);
        _syncing = false;
        ApplyPreviewStagePadding();
    }

    private static string Text(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>留白就是那块台面的内边距——纸在里面上色缩放，看得见的正是那圈边。</summary>
    private void ApplyPreviewStagePadding() => PreviewStage.Padding = new Thickness(PaperMargin);

    private void OnPaperMarginSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        PaperMargin = SimpleShellFlow.PaperMargin(e.NewValue);
        _syncing = true;
        PaperMarginBox.Text = Text(PaperMargin);
        _syncing = false;
        ApplyPreviewStagePadding();
    }

    private void OnPosterWidthSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        PosterCardWidth = SimpleShellFlow.PosterCardWidth(e.NewValue);
        _syncing = true;
        PosterWidthBox.Text = Text(PosterCardWidth);
        _syncing = false;
    }

    private void OnPaperMarginBoxLostFocus(object sender, RoutedEventArgs e)
        => ReadBox(PaperMarginBox, SimpleShellFlow.PaperMargin, () => PaperMargin, v => PaperMargin = v);

    private void OnPosterWidthBoxLostFocus(object sender, RoutedEventArgs e)
        => ReadBox(PosterWidthBox, SimpleShellFlow.PosterCardWidth, () => PosterCardWidth, v => PosterCardWidth = v);

    /// <summary>认下来的数夹进取、认不下来的（空/字母/∞）留旧值，两种都把框拨回真正生效的那个数——不许留一个屏幕上没有的数。</summary>
    private void ReadBox(TextBox box, Func<double, double> clamp, Func<double> current, Action<double> assign)
    {
        if (_syncing) return;
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            assign(clamp(parsed));
        _syncing = true;
        box.Text = Text(current());
        _syncing = false;
        ApplyPreviewStagePadding();
    }

    /// <summary>这一页只换自己这身皮给预览看；整壳窗那身仍由顶栏那颗钮管（一处开关，不两处）。</summary>
    private void OnDarkChanged(object sender, RoutedEventArgs e)
        => SimpleTheme.ApplyInto(this, DarkCheck.IsChecked == true);

    /// <summary>
    /// 「AI 提示语…」（第 103 棒）：开那扇能看能改的窗。它自己存自己落日志（不等这一页的「好」），
    /// 因为提示词是"下一条请求就用上"的东西，混在这页的取消语义里反而容易误解。
    /// </summary>
    private void OnOpenPromptsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AiPromptWindow(_vm, DarkCheck.IsChecked == true) { Owner = this };
        dlg.ShowDialog();
        RefreshPromptState();
    }

    private void RefreshPromptState()
    {
        var n = _vm.PromptOverrides.OverriddenKeys().Count;
        PromptStateText.Text = n == 0 ? "发给 AI 的八段话：现在全是出厂默认" : $"发给 AI 的八段话：已自定义 {n} 段";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        PaperMargin = SimpleShellFlow.PaperMarginDefault;
        PosterCardWidth = SimpleShellFlow.PosterCardWidthDefault;
        PushToControls();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
