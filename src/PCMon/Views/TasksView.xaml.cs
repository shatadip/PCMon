using System.Windows.Controls;

namespace PCMon.Views;

public partial class TasksView : UserControl
{
    public TasksView() => InitializeComponent();

    // Jumping here from a dashboard "Top 5" row selects a process that may be off-screen.
    void OnProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is { } item) ProcessGrid.ScrollIntoView(item);
    }
}
