using System.Windows;

namespace MangaManager
{
    public partial class EmailPromptWindow : Window
    {
        public string? Email { get; private set; }

        public EmailPromptWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => EmailBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Email = EmailBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void EmailBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) Ok_Click(sender, e);
        }
    }
}
