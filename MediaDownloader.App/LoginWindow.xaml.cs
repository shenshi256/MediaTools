using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MediaDownloader.Core;
using QRCoder;

namespace MediaDownloader.App;

public partial class LoginWindow : Window
{
    private readonly QrSession _session;
    private readonly LoginService _service;
    private readonly CancellationTokenSource _cts = new();

    public Dictionary<string, string>? Cookies { get; private set; }

    public LoginWindow(QrSession session, LoginService service)
    {
        InitializeComponent();
        Icon = UiIcons.Blank; // 标题栏不显示图标
        _session = session;
        _service = service;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(_session.Url, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(png);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            QrImage.Source = bmp;

            StatusText.Text = "等待扫码…";

            var progress = new Progress<string>(s => StatusText.Text = s);
            var result = await _service.PollUntilDoneAsync(_session, TimeSpan.FromMinutes(3), progress, _cts.Token);

            if (result.Success)
            {
                Cookies = result.Cookies;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录出错：" + ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
        Close();
    }
}
