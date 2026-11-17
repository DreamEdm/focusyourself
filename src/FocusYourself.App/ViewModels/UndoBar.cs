using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FocusYourself.ViewModels;

/// <summary>
/// 删除后列表底部出现的“已删除 xxx · 撤销”提示条，几秒后自动消失。
/// 同一时间只保留最近一次删除的撤销。
/// </summary>
public partial class UndoBar : ObservableObject
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    DispatcherTimer? _timer;
    Action? _undo;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isVisible;

    public void Show(string text, Action undo)
    {
        _undo = undo;
        Text = text;
        IsVisible = true;
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = Lifetime };
            _timer.Tick += (_, _) => Dismiss();
        }
        _timer.Stop();
        _timer.Start();
    }

    [RelayCommand]
    void Undo()
    {
        var undo = _undo;
        Dismiss();
        undo?.Invoke();
    }

    [RelayCommand]
    public void Dismiss()
    {
        _timer?.Stop();
        _undo = null;
        IsVisible = false;
    }
}
