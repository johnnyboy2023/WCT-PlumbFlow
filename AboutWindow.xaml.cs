using System.Windows;

namespace RevitMCPApplication
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
