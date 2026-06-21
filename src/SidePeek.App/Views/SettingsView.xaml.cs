using System.Windows.Controls;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
