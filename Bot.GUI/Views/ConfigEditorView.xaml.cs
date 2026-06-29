using System.Windows;
using Bot.GUI.ViewModels;
using WPFUserControl = System.Windows.Controls.UserControl;

namespace Bot.GUI.Views
{
    public partial class ConfigEditorView : WPFUserControl
    {
        public ConfigEditorView()
        {
            InitializeComponent();
            this.Loaded += ConfigEditorView_Loaded;
        }

        private void ConfigEditorView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ConfigViewModel viewModel)
            {
                // Trigger a refresh of the task list using the public method
                viewModel.RefreshTaskList();
            }
        }
    }
} 