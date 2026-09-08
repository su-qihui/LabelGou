using System.Windows;
using LabelGou.App.Services.Recognition;

namespace LabelGou.App.Services;

/// <summary>
/// 「和模型对话」窗口 —— 只是 <see cref="AiChatPanel"/> 的一层壳（M7 第 11 棒起）。
/// <para>第 10 棒把它做成独立窗口，用户当天回的话是「这个 AI 界面不应该藏起来，应该显示出来」：
/// 对主流程来说，<strong>飘在外面的窗口仍然算藏</strong>。所以正解是把它搬进主窗口右侧的常驻页签，
/// 而这个窗口保留——一边看纸质样张一边问的时候，把 AI 拖到副屏上比挤在一个窗口里好用。</para>
/// <para>刻意只留一个 <see cref="Panel"/> 属性不做别的：聊天与排版的实现只有一份，
/// 两处行为不一致是最难查的那类 bug（§五-22 同因）。</para>
/// </summary>
public sealed class AiChatWindow : Window
{
    public AiChatPanel Panel { get; }

    public AiChatWindow()
    {
        Title = "和模型对话（问它怎么调）";
        Width = 780;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding = new Thickness(12);

        Panel = new AiChatPanel();
        Content = Panel;
        Loaded += (_, _) => Panel.RefreshChannel();
    }
}
