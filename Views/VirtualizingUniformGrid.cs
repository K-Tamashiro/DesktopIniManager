using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopIniManager.Views
{
    internal sealed class VirtualizingUniformGrid : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        private Size _extent = new Size(0, 0);
        private Size _viewport = new Size(0, 0);
        private Point _offset = new Point(0, 0);
        private bool _measuring;

        public bool CanVerticallyScroll { get; set; } = true;
        public bool CanHorizontallyScroll { get; set; }
        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => _offset.X;
        public double VerticalOffset => _offset.Y;
        public ScrollViewer ScrollOwner { get; set; }

        public VirtualizingUniformGrid()
        {
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(InvalidateMeasure), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (sizeInfo.WidthChanged || sizeInfo.HeightChanged) InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_measuring) return availableSize.Width > 0 && !double.IsInfinity(availableSize.Width)
                ? new Size(availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height)
                : new Size(0, 0);
            _measuring = true;
            try
            {
                int columns = Math.Max(1, Columns);
                int count = ItemCount();
                double width = double.IsInfinity(availableSize.Width) ? 240 : Math.Max(1, availableSize.Width);
                double height = double.IsInfinity(availableSize.Height) ? 240 : Math.Max(1, availableSize.Height);
                Size slot = SlotSize(width, columns);
                int rows = count == 0 ? 0 : (count + columns - 1) / columns;
                _extent = new Size(width, rows * slot.Height);
                _viewport = new Size(width, height);
                ClampOffset();

                int firstRow = (int)(_offset.Y / Math.Max(1, slot.Height));
                int visibleRows = (int)Math.Ceiling(height / Math.Max(1, slot.Height)) + 1;
                if (visibleRows < 1) visibleRows = 1;
                if (visibleRows > 40) visibleRows = 40;
                int start = Math.Max(0, firstRow * columns);
                int end = Math.Min(count, start + visibleRows * columns);

                IItemContainerGenerator generator = ItemContainerGenerator;
                if (generator == null || count == 0) return _viewport;

                GeneratorPosition position = generator.GeneratorPositionFromIndex(start);
                using (generator.StartAt(position, GeneratorDirection.Forward, true))
                {
                    int childIndex = (position.Offset == 0) ? position.Index : position.Index + 1;
                    if (childIndex < 0) childIndex = 0;
                    for (int itemIndex = start; itemIndex < end; itemIndex++, childIndex++)
                    {
                        bool newly;
                        var child = generator.GenerateNext(out newly) as UIElement;
                        if (child == null) break;
                        if (newly)
                        {
                            if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                            else InsertInternalChild(Math.Min(childIndex, InternalChildren.Count), child);
                            generator.PrepareItemContainer(child);
                        }
                        child.Measure(slot);
                    }
                }
                return _viewport;
            }
            catch
            {
                return _viewport.Width > 0 ? _viewport : new Size(240, 240);
            }
            finally
            {
                _measuring = false;
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            try
            {
                int columns = Math.Max(1, Columns);
                Size slot = SlotSize(finalSize.Width, columns);
                IItemContainerGenerator generator = ItemContainerGenerator;
                int n = InternalChildren.Count;
                for (int i = 0; i < n; i++)
                {
                    UIElement child = InternalChildren[i];
                    int itemIndex = generator == null ? i : generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                    if (itemIndex < 0) { child.Arrange(new Rect()); continue; }
                    int row = itemIndex / columns;
                    int column = itemIndex % columns;
                    child.Arrange(new Rect(column * slot.Width, row * slot.Height - _offset.Y, slot.Width, slot.Height));
                }
                _viewport = finalSize;
            }
            catch { }
            return finalSize;
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            try
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    RemoveInternalChildRange(0, InternalChildren.Count);
            }
            catch { }
            InvalidateMeasure();
        }

        private Size SlotSize(double width, int columns)
        {
            return new Size(Math.Max(1, width / columns), Columns >= 3 ? 78 : 118);
        }

        private int ItemCount()
        {
            var owner = ItemsControl.GetItemsOwner(this);
            return owner != null && owner.HasItems ? owner.Items.Count : 0;
        }

        private void ClampOffset()
        {
            double maxY = Math.Max(0, _extent.Height - _viewport.Height);
            if (_offset.Y < 0) _offset.Y = 0;
            if (_offset.Y > maxY) _offset.Y = maxY;
        }

        public void LineUp() => SetVerticalOffset(_offset.Y - 16);
        public void LineDown() => SetVerticalOffset(_offset.Y + 16);
        public void LineLeft() { }
        public void LineRight() { }
        public void PageUp() => SetVerticalOffset(_offset.Y - Math.Max(16, _viewport.Height));
        public void PageDown() => SetVerticalOffset(_offset.Y + Math.Max(16, _viewport.Height));
        public void PageLeft() { }
        public void PageRight() { }
        public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 48);
        public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 48);
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }
        public void SetHorizontalOffset(double offset) { }

        public void SetVerticalOffset(double offset)
        {
            if (_measuring) return;
            _offset.Y = offset;
            ClampOffset();
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }

        public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
    }
}
