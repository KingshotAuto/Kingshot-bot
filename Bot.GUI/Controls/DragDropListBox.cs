using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataFormats = System.Windows.DataFormats;
using WpfCursors = System.Windows.Input.Cursors;
using WpfPanel = System.Windows.Controls.Panel;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Bot.GUI.Controls
{
    public class DragDropListBox : WpfListBox
    {
        private WpfPoint _startPoint;
        private bool _isDragging;
        private ListBoxItem? _draggedItem;
        private DropIndicatorAdorner? _dropIndicator;

        public static readonly DependencyProperty ItemReorderedCommandProperty =
            DependencyProperty.Register("ItemReorderedCommand", typeof(ICommand), typeof(DragDropListBox));

        public ICommand ItemReorderedCommand
        {
            get { return (ICommand)GetValue(ItemReorderedCommandProperty); }
            set { SetValue(ItemReorderedCommandProperty, value); }
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(WpfMouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                WpfPoint mousePos = e.GetPosition(null);
                Vector diff = _startPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var listBoxItem = GetListBoxItemUnderMouse(e.GetPosition(this));
                    if (listBoxItem != null)
                    {
                        _isDragging = true;
                        _draggedItem = listBoxItem;
                        var draggedItem = listBoxItem.DataContext;
                        
                        // Change cursor and add visual feedback
                        Mouse.SetCursor(WpfCursors.Hand);
                        listBoxItem.Opacity = 0.5;
                        
                        WpfDragDropEffects result = DragDrop.DoDragDrop(this, draggedItem, WpfDragDropEffects.Move);
                        
                        // Reset visual state
                        listBoxItem.Opacity = 1.0;
                        Mouse.SetCursor(WpfCursors.Arrow);
                        HideDropIndicator();
                        ResetItemBackgrounds();
                        _isDragging = false;
                        _draggedItem = null;
                    }
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnDrop(WpfDragEventArgs e)
        {
            if (e.Data.GetDataPresent(WpfDataFormats.StringFormat) || e.Data.GetData(e.Data.GetFormats()[0]) != null)
            {
                var draggedItem = e.Data.GetData(e.Data.GetFormats()[0]);
                var targetListBoxItem = GetListBoxItemUnderMouse(e.GetPosition(this));

                if (targetListBoxItem != null && draggedItem != null)
                {
                    var targetItem = targetListBoxItem.DataContext;
                    if (draggedItem != targetItem && ItemsSource is IList collection)
                    {
                        int draggedIndex = collection.IndexOf(draggedItem);
                        int targetIndex = collection.IndexOf(targetItem);

                        if (draggedIndex >= 0 && targetIndex >= 0 && draggedIndex != targetIndex)
                        {
                            // Use ObservableCollection.Move if available, otherwise remove and insert
                            if (collection is ObservableCollection<Bot.Core.Models.AccountSettings> obsCollection)
                            {
                                obsCollection.Move(draggedIndex, targetIndex);
                            }
                            else
                            {
                                collection.Remove(draggedItem);
                                collection.Insert(targetIndex, draggedItem);
                            }
                            
                            SelectedItem = draggedItem; // Maintain selection
                            
                            // Execute the command if provided
                            ItemReorderedCommand?.Execute(null);
                        }
                    }
                }
            }
            base.OnDrop(e);
        }

        protected override void OnDragOver(WpfDragEventArgs e)
        {
            e.Effects = WpfDragDropEffects.Move;
            e.Handled = true;
            
            // Reset all item backgrounds first
            ResetItemBackgrounds();
            
            // Show drop indicator and highlight target
            var targetItem = GetListBoxItemUnderMouse(e.GetPosition(this));
            if (targetItem != null && targetItem != _draggedItem)
            {
                ShowDropIndicator(targetItem, e.GetPosition(this));
                
                // Highlight the target item
                var originalBrush = targetItem.Background;
                targetItem.Background = new SolidColorBrush(WpfColor.FromArgb(50, 0, 122, 204)); // Semi-transparent blue
            }
            else
            {
                HideDropIndicator();
            }
            
            base.OnDragOver(e);
        }

        private void ResetItemBackgrounds()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item && item != _draggedItem)
                {
                    item.Background = WpfBrushes.Transparent;
                }
            }
        }

        private void ShowDropIndicator(ListBoxItem targetItem, WpfPoint mousePosition)
        {
            HideDropIndicator(); // Remove any existing indicator
            
            var adornerLayer = AdornerLayer.GetAdornerLayer(this);
            if (adornerLayer != null && targetItem != null)
            {
                var targetRect = targetItem.TransformToAncestor(this).TransformBounds(new Rect(targetItem.RenderSize));
                var isAboveCenter = mousePosition.Y < targetRect.Top + targetRect.Height / 2;
                
                _dropIndicator = new DropIndicatorAdorner(this, targetRect, isAboveCenter);
                adornerLayer.Add(_dropIndicator);
            }
        }

        private void HideDropIndicator()
        {
            if (_dropIndicator != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(this);
                if (adornerLayer != null)
                {
                    adornerLayer.Remove(_dropIndicator);
                }
                _dropIndicator = null;
            }
        }

        private ListBoxItem GetListBoxItemUnderMouse(WpfPoint position)
        {
            HitTestResult hitTestResult = VisualTreeHelper.HitTest(this, position);
            if (hitTestResult?.VisualHit != null)
            {
                var element = hitTestResult.VisualHit as FrameworkElement;
                while (element != null)
                {
                    if (element is ListBoxItem listBoxItem)
                        return listBoxItem;
                    element = VisualTreeHelper.GetParent(element) as FrameworkElement;
                }
            }
            return null;
        }

        public DragDropListBox()
        {
            AllowDrop = true;
            
            // Add mouse enter/leave for cursor feedback
            MouseEnter += OnMouseEnterListBox;
            MouseLeave += OnMouseLeaveListBox;
        }

        private void OnMouseEnterListBox(object sender, WpfMouseEventArgs e)
        {
            if (!_isDragging)
                Mouse.SetCursor(WpfCursors.SizeAll); // Indicates draggable
        }

        private void OnMouseLeaveListBox(object sender, WpfMouseEventArgs e)
        {
            if (!_isDragging)
                Mouse.SetCursor(WpfCursors.Arrow);
        }
    }

    public class DropIndicatorAdorner : Adorner
    {
        private readonly Rect _targetRect;
        private readonly bool _isAbove;
        private readonly WpfPen _indicatorPen;

        public DropIndicatorAdorner(UIElement adornedElement, Rect targetRect, bool isAbove) : base(adornedElement)
        {
            _targetRect = targetRect;
            _isAbove = isAbove;
            _indicatorPen = new WpfPen(new SolidColorBrush(WpfColor.FromRgb(0, 122, 204)), 3)
            {
                DashStyle = DashStyles.Solid
            };
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var y = _isAbove ? _targetRect.Top : _targetRect.Bottom;
            var startPoint = new WpfPoint(_targetRect.Left - 5, y);
            var endPoint = new WpfPoint(_targetRect.Right + 5, y);

            // Draw the main line
            drawingContext.DrawLine(_indicatorPen, startPoint, endPoint);

            // Draw arrow indicators at both ends
            DrawArrow(drawingContext, startPoint, true);  // Left arrow
            DrawArrow(drawingContext, endPoint, false);   // Right arrow
        }

        private void DrawArrow(DrawingContext drawingContext, WpfPoint point, bool pointsRight)
        {
            var arrowSize = 4;
            var direction = pointsRight ? 1 : -1;
            
            var triangle = new[]
            {
                point,
                new WpfPoint(point.X - direction * arrowSize, point.Y - arrowSize),
                new WpfPoint(point.X - direction * arrowSize, point.Y + arrowSize)
            };

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = triangle[0] };
            figure.Segments.Add(new LineSegment(triangle[1], true));
            figure.Segments.Add(new LineSegment(triangle[2], true));
            figure.IsClosed = true;
            geometry.Figures.Add(figure);

            drawingContext.DrawGeometry(_indicatorPen.Brush, null, geometry);
        }
    }
}