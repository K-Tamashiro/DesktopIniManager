using System.Collections.Generic;
using System.ComponentModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Services
{
    internal sealed class ElevationService : INotifyPropertyChanged
    {
        internal static readonly ElevationService Shared = new ElevationService();
        private readonly HashSet<object> workers = new HashSet<object>();
        private bool enabled, initialized;
        public bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; Changed(nameof(Enabled)); } }
        public bool CanChange => workers.Count == 0;
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        internal static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        internal void Initialize(bool requested)
        { if (initialized) return; initialized = true; Enabled = requested || IsAdministrator(); }
        internal void SetBusy(object owner, bool busy)
        { if (busy) workers.Add(owner); else workers.Remove(owner); Changed(nameof(CanChange)); }
        internal Border CreateToggle(Window window, string target)
        {
            var box = new CheckBox { VerticalContentAlignment = VerticalAlignment.Center };
            var text = new StackPanel { Margin = new Thickness(5, -1, 0, 0) };
            var title = new TextBlock { Text = Strings.Main_FastNtfs, FontWeight = FontWeights.SemiBold };
            var note = new TextBlock { Text = Strings.Main_ElevationHint, Margin = new Thickness(0, 2, 0, 0), FontSize = 10 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(title);
            text.Children.Add(note);
            box.Content = text;

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 12, 0),
                Child = box
            };
            border.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            border.SetResourceReference(Border.BorderBrushProperty, "Line");
            Bind(box, window, target);
            return border;
        }

        internal void Bind(CheckBox box, Window window, string target)
        {
            Initialize(false);
            box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Enabled)) { Source = this, Mode = BindingMode.TwoWay });
            box.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CanChange)) { Source = this });
            box.ToolTip = Strings.Main_ElevationTooltip;
            box.Click += (sender, args) =>
            {
                if (!Enabled || IsAdministrator()) return;
                var main = window as MainWindow ?? window.Owner as MainWindow;
                if (main == null || !main.RestartElevated(target)) Enabled = false;
            };
            window.Closed += (sender, args) => SetBusy(window, false);
        }
    }
}
