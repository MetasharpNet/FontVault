using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace FontVault.UI;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private object? _dragItem;

    public MainWindow()
    {
        InitializeComponent();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        Title = $"FontVault ({version}) © Metasharp";
        var vm = new MainViewModel();
        DataContext = vm;
        Closing += (_, _) => vm.SaveSettings(); // persist source/vault fields on close
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    // ---- Identify: image input (drag-drop, paste, file) ----

    private void IdentifyDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void IdentifyDrop_Drop(object sender, DragEventArgs e)
    {
        var img = ExtractImage(e.Data);
        if (img != null && DataContext is MainViewModel vm) vm.SetQueryImage(img);
    }

    private void IdentifyLoad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an image of text",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
        };
        if (dlg.ShowDialog() == true) LoadImageFile(dlg.FileName);
    }

    private void IdentifyPaste_Click(object sender, RoutedEventArgs e) => PasteImage();

    // ---- Identify: drag the overlay match to align it on the image ----

    private bool _overlayDrag;
    private Point _overlayDragStart;
    private double _overlayStartX, _overlayStartY;

    private void OverlayText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _overlayDrag = true;
        _overlayDragStart = e.GetPosition(this);
        _overlayStartX = vm.OverlayX;
        _overlayStartY = vm.OverlayY;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OverlayText_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_overlayDrag || DataContext is not MainViewModel vm) return;
        var p = e.GetPosition(this);
        vm.OverlayX = _overlayStartX + (p.X - _overlayDragStart.X);
        vm.OverlayY = _overlayStartY + (p.Y - _overlayDragStart.Y);
    }

    private void OverlayText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_overlayDrag) return;
        _overlayDrag = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBox)
        {
            try
            {
                if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()) { PasteImage(); e.Handled = true; }
            }
            catch { /* clipboard busy */ }
        }
    }

    private void PasteImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null && DataContext is MainViewModel vm) { if (img.CanFreeze) img.Freeze(); vm.SetQueryImage(img); }
                return;
            }
            if (Clipboard.ContainsFileDropList())
                foreach (string? f in Clipboard.GetFileDropList())
                    if (f != null && IsImageFile(f)) { LoadImageFile(f); return; }
        }
        catch { /* clipboard busy or unsupported format */ }
    }

    private void LoadImageFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            if (DataContext is MainViewModel vm) vm.SetQueryImage(bmp);
        }
        catch { /* unreadable image */ }
    }

    private static BitmapSource? ExtractImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (string f in files)
                    if (IsImageFile(f))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(f);
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
            if (data.GetData(DataFormats.Bitmap) is BitmapSource bs)
            {
                if (bs.CanFreeze) bs.Freeze();
                return bs;
            }
        }
        catch { /* unsupported drop payload */ }
        return null;
    }

    private static bool IsImageFile(string p)
    {
        string e = System.IO.Path.GetExtension(p).ToLowerInvariant();
        return e is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    // Outbound drag & drop: one vault file for a variant, all variant files for a family.

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnderMouse(e.OriginalSource);
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        if (DataContext is not MainViewModel vm) return;
        string[] paths = vm.GetDragPaths(item);
        if (paths.Length == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && (sender as FrameworkElement)?.DataContext is FamilyGroup family)
            vm.ToggleFavorite(family);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "FontVault", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {version?.ToString() ?? "?"}",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 16),
        });
        panel.Children.Add(LabeledLink("Official site", "https://github.com/MetasharpNet/FontVault"));
        panel.Children.Add(new TextBlock { Text = "Author: Metasharp", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(LabeledLink("Donation", "https://ko-fi.com/metasharp"));

        new Window
        {
            Title = "About FontVault",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
        }.ShowDialog();
    }

    private static TextBlock LabeledLink(string label, string url)
    {
        var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* no browser available */ }
            e.Handled = true;
        };
        var block = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
        block.Inlines.Add(new Run(label + ": "));
        block.Inlines.Add(link);
        return block;
    }

    private static object? ItemUnderMouse(object source)
    {
        var current = source as DependencyObject;
        while (current != null && current is not ListBoxItem)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return (current as ListBoxItem)?.DataContext;
    }
}
