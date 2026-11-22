using System.Collections.ObjectModel;
using System.Windows;
using MediaDownloader.App.ViewModels;

namespace MediaDownloader.App;

public partial class DuplicateWindow : Window
{
    /// <summary>用户选择；关闭时未选择则为 Cancel。</summary>
    public DuplicateChoice Choice { get; private set; } = DuplicateChoice.Skip;

    public ObservableCollection<string> Files { get; } = new();

    public DuplicateWindow(IEnumerable<string> files)
    {
        InitializeComponent();
        Icon = UiIcons.Blank; // 标题栏不显示图标

        foreach (var f in files) Files.Add(f);
        DataContext = this;
        HeadText.Text = $"下载目录中已有 {Files.Count} 个同名文件：";
    }

    private void OnDownloadAll(object sender, RoutedEventArgs e)
    {
        Choice = DuplicateChoice.DownloadAll;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Choice = DuplicateChoice.Skip;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = DuplicateChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
