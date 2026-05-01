using System;
using System.Windows;

namespace ByeWhiteList.Windows
{
    public partial class SettingsMenuWindow : Window
    {
        public SettingsMenuAction? SelectedAction { get; private set; }

        public SettingsMenuWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Overlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Close_Click(sender, new RoutedEventArgs());
            }
            catch { }
        }

        private void Card_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // prevent overlay click-to-close
            e.Handled = true;
        }

        private void Select(SettingsMenuAction action)
        {
            SelectedAction = action;
            DialogResult = true;
            Close();
        }

        private void News_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.News);
        private void Routing_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.RoutingRules);
        private void Speedtest_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.Speedtest);
        private void AppUpdate_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.AppUpdate);
        private void Feedback_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.Feedback);
        private void StartTutorial_Click(object sender, RoutedEventArgs e) => Select(SettingsMenuAction.StartTutorial);
    }

    public enum SettingsMenuAction
    {
        News,
        RoutingRules,
        Speedtest,
        AppUpdate,
        Feedback,
        StartTutorial

    }

}
