using System.Windows;

namespace L2Launcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += async (_, _) => await _vm.InitAsync();
    }
}
