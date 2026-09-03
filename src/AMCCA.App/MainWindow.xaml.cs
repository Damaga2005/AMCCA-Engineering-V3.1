using System.Windows;
using AMCCA.App.ViewModels;

namespace AMCCA.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
