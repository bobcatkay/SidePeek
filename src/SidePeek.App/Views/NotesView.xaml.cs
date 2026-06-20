using System.Windows.Controls;
using SidePeek.App.ViewModels;

namespace SidePeek.App.Views;

public partial class NotesView : UserControl
{
    public NotesView()
    {
        InitializeComponent();
        DataContext = new NotesViewModel();
    }
}
