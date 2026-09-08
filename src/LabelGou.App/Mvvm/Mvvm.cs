using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace LabelGou.App.Mvvm;

/// <summary>
/// 反向 Bool→Visibility：WPF 自带的 <see cref="BooleanToVisibilityConverter"/> 不支持取反（参数被忽略），
/// 而编辑器里“没选中时给提示、选中时给面板”这类成对显示用得很多。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 字符串非空才显示（空串与 null 都Collapsed）。给「只在真的对不上时才插一句」这类提示用：
/// 把一个派生文本直接绑到 TextBlock 上，空的时候会在面板里留一行空白，看上去像个坏了的控件。
/// </summary>
public sealed class StringNotEmptyToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 步骤号 → 只显示当前那一步的面板。向导一次只露一块，所以每个面板都要问一句“现在轮到我了吗”；
/// <paramref name="parameter"/> 传本面板负责的步骤号（0 基）。用转换器而不是给每步派生一个带 DataTrigger 的样式，
/// 是为了让 XAML 里每个面板只多一个属性，不至于为五步写五套样式。
/// </summary>
public sealed class StepIndexToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int step && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted)
            && step == wanted
                ? Visibility.Visible
                : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>最小 MVVM 基类（不引第三方 MVVM 库，保持依赖最少）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecute() => CommandManager.InvalidateRequerySuggested();
}
