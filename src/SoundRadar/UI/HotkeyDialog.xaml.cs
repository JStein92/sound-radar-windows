using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundRadar.Core;

namespace SoundRadar.UI
{
    /// <summary>A read-only box that records the key combination pressed while it has focus.</summary>
    internal sealed class HotkeyBox : TextBox
    {
        private const string Pending = "+...";
        private string _beforeModifiers = "";

        public HotkeyBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsUndoEnabled = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (key == Key.Tab && modifiers == ModifierKeys.None)
                return; // keep Tab for moving between fields
            e.Handled = true;
            if ((key == Key.Back || key == Key.Delete || key == Key.Escape) && modifiers == ModifierKeys.None)
            {
                Text = "";
                return;
            }
            if (HotkeyText.IsModifier(key))
            {
                // While only modifiers are held, show them as a hint of what's being built.
                if (!Text.EndsWith(Pending))
                    _beforeModifiers = Text;
                Text = HotkeyText.Format(modifiers, Key.None) + Pending;
                return;
            }
            Text = HotkeyText.Format(modifiers, key);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            // Modifiers released without a real key: put back what was there.
            if (Text.EndsWith(Pending) && Keyboard.Modifiers == ModifierKeys.None)
                Text = _beforeModifiers;
            base.OnPreviewKeyUp(e);
        }
    }

    internal partial class HotkeyDialog : Window
    {
        private readonly Dictionary<string, HotkeyBox> _boxes = new Dictionary<string, HotkeyBox>();

        public HotkeyDialog(IReadOnlyDictionary<string, string> current)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => Interop.Native.UseDarkTitleBar(this);
            Rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            Rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            foreach (var pair in HotkeyActions.Labels)
            {
                Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = pair.Value, Margin = new Thickness(0, 3, 0, 3) };
                var box = new HotkeyBox
                {
                    Style = (Style)FindResource(typeof(TextBox)),
                    Text = current.TryGetValue(pair.Key, out var combo) ? combo : "",
                    Margin = new Thickness(0, 3, 0, 3),
                };
                Grid.SetRow(label, row);
                Grid.SetRow(box, row);
                Grid.SetColumn(box, 1);
                Rows.Children.Add(label);
                Rows.Children.Add(box);
                _boxes[pair.Key] = box;
                row++;
            }
        }

        public Dictionary<string, string> Mapping { get; } = new Dictionary<string, string>();

        private void OnClearAll(object sender, RoutedEventArgs e)
        {
            foreach (var box in _boxes.Values)
                box.Text = "";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            foreach (var pair in _boxes)
                Mapping[pair.Key] = pair.Value.Text.EndsWith("...") ? "" : pair.Value.Text.Trim();
            DialogResult = true;
        }
    }
}
