using System.Windows;

namespace SoundRadar.UI
{
    internal partial class RenameDialog : Window
    {
        private readonly string _defaultName;

        public RenameDialog(string currentName, string defaultName, int maxLength)
        {
            InitializeComponent();
            SourceInitialized += (s, e) => Interop.Native.UseDarkTitleBar(this);
            _defaultName = defaultName;
            NameBox.MaxLength = maxLength;
            NameBox.Text = currentName;
            HintText.Text = $"Leave blank to go back to \"{defaultName}\".";
            Loaded += (s, e) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        public string NewName { get; private set; }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            NewName = name.Length == 0 ? _defaultName : name;
            DialogResult = true;
        }
    }
}
