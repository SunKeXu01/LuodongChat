using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatGPTConnector.App;

public partial class AvatarCropDialog : Window
{
    private Point? _dragOrigin;
    private double _translationOriginX;
    private double _translationOriginY;

    public byte[]? CroppedPngBytes { get; private set; }

    public AvatarCropDialog(string sourcePath)
    {
        InitializeComponent();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(sourcePath), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        SourceImage.Source = image;
    }

    private void ZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is null) return;
        ImageScale.ScaleX = e.NewValue;
        ImageScale.ScaleY = e.NewValue;
    }

    private void RotateButton_OnClick(object sender, RoutedEventArgs e) =>
        ImageRotation.Angle = (ImageRotation.Angle + 90) % 360;

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 1;
        ImageRotation.Angle = 0;
        ImageTranslation.X = 0;
        ImageTranslation.Y = 0;
    }

    private void CropSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(this);
        _translationOriginX = ImageTranslation.X;
        _translationOriginY = ImageTranslation.Y;
        CropRenderSurface.CaptureMouse();
        e.Handled = true;
    }

    private void CropSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        ImageTranslation.X = _translationOriginX + current.X - origin.X;
        ImageTranslation.Y = _translationOriginY + current.Y - origin.Y;
    }

    private void CropSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = null;
        CropRenderSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        CropRenderSurface.UpdateLayout();
        const int outputSize = 512;
        var dpi = 96d * outputSize / CropRenderSurface.ActualWidth;
        var target = new RenderTargetBitmap(outputSize, outputSize, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(CropRenderSurface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new MemoryStream();
        encoder.Save(output);
        CroppedPngBytes = output.ToArray();
        DialogResult = true;
    }
}
