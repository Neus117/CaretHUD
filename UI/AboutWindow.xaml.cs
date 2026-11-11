using System.Windows;
using System.Windows.Input;

namespace CaretHUD.UI;

/// <summary>说明窗口：程序用途与支持范围。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
