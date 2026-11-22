using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaDownloader.App.ViewModels;
using MediaDownloader.Core;

namespace MediaDownloader.App.Views;

public partial class CompressPage : UserControl
{
    public CompressPage()
    {
        InitializeComponent();

        // 拖放提示色
        DropZone.DragEnter += (_, _) => Highlight(true);
        DropZone.DragLeave += (_, _) => Highlight(false);
    }

    private CompressViewModel? Vm => DataContext as CompressViewModel;

    private void Highlight(bool on)
    {
        DropZone.BorderBrush = on
            ? (System.Windows.Media.Brush)FindResource("App.Accent")
            : (System.Windows.Media.Brush)FindResource("App.BorderStrong");
        DropZone.Background = on
            ? (System.Windows.Media.Brush)FindResource("App.AccentSoft")
            : (System.Windows.Media.Brush)FindResource("App.SurfaceAlt");
    }

    // ---------- 拖放 ----------

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => Highlight(false);

    private void Page_Drop(object sender, DragEventArgs e)
    {
        Highlight(false);
        if (Vm is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        // 只收视频扩展名，其他文件默默忽略
        var exts = new[] { ".mp4", ".mkv", ".mov", ".m4v", ".ts", ".flv" };
        Vm.AddPaths(files.Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())));
    }

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Vm?.AddFilesCommand.Execute(null);

    // ---------- 设置控件转发 ----------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string t } && Vm is not null && Vm.ModeIndex != int.Parse(t))
            Vm.ModeIndex = int.Parse(t);
    }

    private void Encoder_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string t } && Vm is not null && Vm.EncoderIndex != int.Parse(t))
            Vm.EncoderIndex = int.Parse(t);
    }

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string t } && Vm is not null && Vm.QualityIndex != int.Parse(t))
            Vm.QualityIndex = int.Parse(t);
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string p } } && Vm is not null)
            Vm.Preset = p;
    }
}
