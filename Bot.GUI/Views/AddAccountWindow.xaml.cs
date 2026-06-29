using System.Windows;
using System.Windows.Input;
using WPFControls = System.Windows.Controls;
using Bot.Core.Models;
using Bot.GUI.ViewModels;

namespace Bot.GUI.Views
{
    public partial class AddAccountWindow : Window
    {
        private bool _isCheckBoxClick = false;

        public AddAccountWindow()
        {
            InitializeComponent();
            Loaded += AddAccountWindow_Loaded;
        }

        private void AddAccountWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is AddAccountViewModel viewModel)
            {
                viewModel.CloseRequested += (s, result) =>
                {
                    try
                    {
                        DialogResult = result;
                        Close();
                    }
                    catch (InvalidOperationException)
                    {
                        // This can occur if the window is closed via other means.
                        // It's safe to ignore in this context.
                    }
                };
            }
        }

        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // If this is a checkbox click, let the checkbox handle it
            if (_isCheckBoxClick)
            {
                return;
            }

            // For clicks on the row (not on the checkbox), toggle the selection
            if (sender is WPFControls.ListViewItem item && item.DataContext is LDPlayerInstance instance)
            {
                instance.IsSelected = !instance.IsSelected;
                e.Handled = true;
            }
        }

        private void CheckBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isCheckBoxClick = true;
            e.Handled = true;
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            _isCheckBoxClick = false;
        }
    }
} 