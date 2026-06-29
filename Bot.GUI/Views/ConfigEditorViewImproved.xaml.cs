using System.Windows;
using Bot.GUI.ViewModels;
using WPFUserControl = System.Windows.Controls.UserControl;

namespace Bot.GUI.Views
{
    public partial class ConfigEditorViewImproved : WPFUserControl
    {
        public ConfigEditorViewImproved()
        {
            InitializeComponent();
            this.Loaded += ConfigEditorViewImproved_Loaded;
        }

        private void ConfigEditorViewImproved_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ConfigViewModel viewModel)
            {
                // Trigger a refresh of the task list using the public method
                viewModel.RefreshTaskList();
                
                // Subscribe to configuration changes to refresh the entire view
                viewModel.ConfigurationChanged += OnConfigurationChanged;
            }
        }
        
        private void OnConfigurationChanged()
        {
            if (DataContext is ConfigViewModel viewModel)
            {
                // Force the view to refresh all bindings by re-setting the DataContext
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    var currentDataContext = DataContext;
                    DataContext = null;
                    DataContext = currentDataContext;
                    
                    // RefreshAccountDisplay() call removed to prevent circular loops
                    // The DataContext reset above should be sufficient for UI refresh
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }
}