using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FocusYourself.Core;
using FocusYourself.Services;
using FocusYourself.ViewModels;

namespace FocusYourself.Views;

/// <summary>扩展安装向导。不是模态窗口：装扩展时用户要在浏览器和资源管理器之间来回操作。</summary>
public partial class ExtensionSetupWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ExtensionSetupWindow(ExtensionSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Vm = vm;
        _timer.Tick += (_, _) => vm.Tick();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    public ExtensionSetupViewModel Vm { get; }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExtensionSetupViewModel.CloseRequested) when Vm.CloseRequested:
                Close();
                break;
            // 扩展连上了：把向导拉回前台，让用户看到“已连接”
            case nameof(ExtensionSetupViewModel.CurrentConnected) when Vm.CurrentConnected:
                Activate();
                break;
        }
    }

    void OnAddBrowser(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("PickBrowserTitle"), Filter = Loc.T("ExeFilter") };
        if (dlg.ShowDialog(this) == true) Vm.AddBrowser(dlg.FileName);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
