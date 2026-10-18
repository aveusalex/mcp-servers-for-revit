using System.Windows;
using System.Windows.Controls;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Settings.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private CommandSetSettingsPage commandSetPage;
        private ApprovalModePage approvalModePage;
        private bool isInitialized = false;

        public SettingsWindow()
        {
            InitializeComponent();

            // 初始化页面
            commandSetPage = new CommandSetSettingsPage();
            approvalModePage = new ApprovalModePage();

            // 加载默认页面
            ContentFrame.Navigate(approvalModePage);

            isInitialized = true;
        }

        private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isInitialized) return;

            if (NavListBox.SelectedItem == CommandSetItem)
            {
                ContentFrame.Navigate(commandSetPage);
            }
            else if (NavListBox.SelectedItem == ApprovalModeItem)
            {
                ContentFrame.Navigate(approvalModePage);
            }
        }
    }
}
