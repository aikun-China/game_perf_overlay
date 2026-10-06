using System.Collections.Generic;
using System.Windows;

namespace PerfMonitor
{
    // Lightweight one-shot picker: shows a prompt and a list of Label/Value options.
    public partial class ScreenPickerDialog : Window
    {
        public sealed class PickerOption
        {
            public PickerOption()
            {
                Label = "";
                Value = "";
            }
            public string Label { get; set; }
            public string Value { get; set; }
        }

        private string _result = "";
        public string Result { get { return _result; } }

        public ScreenPickerDialog(string title, string prompt, List<PickerOption> options, string defaultValue)
        {
            InitializeComponent();
            Title = title;
            Prompt.Text = prompt;
            Combo.ItemsSource = options;
            if (!string.IsNullOrEmpty(defaultValue))
            {
                for (int i = 0; i < options.Count; i++)
                {
                    if (options[i].Value == defaultValue) { Combo.SelectedIndex = i; break; }
                }
            }
            else if (options.Count > 0)
            {
                Combo.SelectedIndex = 0;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            object sel = Combo.SelectedValue;
            _result = sel as string ?? "";
            DialogResult = true;
        }
    }
}
