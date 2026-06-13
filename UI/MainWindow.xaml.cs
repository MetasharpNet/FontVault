using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
