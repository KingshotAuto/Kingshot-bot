using System.Windows;
using System.Windows.Controls;
using Bot.GUI.ViewModels;
using WPFMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WPFMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Bot.GUI.Views
{
    public partial class DevToolsWindow : Window
    {
        private readonly DevToolsViewModel _viewModel;

        public DevToolsWindow(DevToolsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        private void Canvas_MouseLeftButtonDown(object sender, WPFMouseButtonEventArgs e)
        {
            var canvas = (Canvas)sender;
            var position = e.GetPosition(canvas);

            if (!_viewModel.FirstPoint.HasValue)
            {
                _viewModel.FirstPoint = position;
                SelectionRect.SetValue(Canvas.LeftProperty, position.X);
                SelectionRect.SetValue(Canvas.TopProperty, position.Y);
                SelectionRect.Width = 0;
                SelectionRect.Height = 0;

                // Add mouse move and up handlers
                canvas.MouseMove += Canvas_MouseMove;
                canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            }
            else if (!_viewModel.SecondPoint.HasValue)
            {
                _viewModel.SecondPoint = position;
                canvas.MouseMove -= Canvas_MouseMove;
                canvas.MouseLeftButtonUp -= Canvas_MouseLeftButtonUp;
            }
        }

        private void Canvas_MouseMove(object sender, WPFMouseEventArgs e)
        {
            if (_viewModel.FirstPoint.HasValue && !_viewModel.SecondPoint.HasValue)
            {
                var canvas = (Canvas)sender;
                var currentPos = e.GetPosition(canvas);
                var startPoint = _viewModel.FirstPoint.Value;

                // Calculate rectangle dimensions
                double left = System.Math.Min(startPoint.X, currentPos.X);
                double top = System.Math.Min(startPoint.Y, currentPos.Y);
                double width = System.Math.Abs(currentPos.X - startPoint.X);
                double height = System.Math.Abs(currentPos.Y - startPoint.Y);

                // Update rectangle position and size
                SelectionRect.SetValue(Canvas.LeftProperty, left);
                SelectionRect.SetValue(Canvas.TopProperty, top);
                SelectionRect.Width = width;
                SelectionRect.Height = height;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, WPFMouseButtonEventArgs e)
        {
            var canvas = (Canvas)sender;
            var position = e.GetPosition(canvas);
            _viewModel.SecondPoint = position;

            canvas.MouseMove -= Canvas_MouseMove;
            canvas.MouseLeftButtonUp -= Canvas_MouseLeftButtonUp;
        }
    }
} 