using System;
using System.Windows;
using System.Windows.Controls;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.UI
{
    /// <summary>Settings page where the person picks the approval mode (saved at once, applied by the broker live).</summary>
    public partial class ApprovalModePage : Page
    {
        private bool _loading = true;

        public ApprovalModePage()
        {
            InitializeComponent();
            var mode = ApprovalModeStore.Read();
            AskFirstOption.IsChecked = mode == ApprovalModeStore.AskFirst;
            AutoOption.IsChecked = mode == ApprovalModeStore.Auto;
            LookOnlyOption.IsChecked = mode == ApprovalModeStore.LookOnly;
            ShowStatus(mode, saved: false);
            _loading = false;
        }

        private void Option_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var mode = sender == AutoOption ? ApprovalModeStore.Auto
                : sender == LookOnlyOption ? ApprovalModeStore.LookOnly
                : ApprovalModeStore.AskFirst;
            try
            {
                ApprovalModeStore.Write(mode);
                ShowStatus(mode, saved: true);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Não foi possível salvar: " + ex.Message;
            }
        }

        private void ShowStatus(string mode, bool saved)
        {
            string name = mode == ApprovalModeStore.Auto ? "Automático (não perguntar)"
                : mode == ApprovalModeStore.LookOnly ? "Somente leitura"
                : "Perguntar antes de alterações importantes";
            StatusText.Text = (saved ? "Salvo. " : string.Empty) + "Modo atual: " + name + ".";
        }
    }
}
