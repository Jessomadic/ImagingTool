using System.Windows.Forms;

namespace ImagingTool.Helpers
{
    public static class DialogHelper
    {
        public static Task<string?> ShowSaveDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            var uiThread = new Thread(() =>
            {
                try
                {
                    using var dialog = new SaveFileDialog
                    {
                        Title = "Select Backup Location and Filename",
                        Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*",
                        DefaultExt = "wim",
                        FileName = $"SystemBackup_{DateTime.Now:yyyyMMdd_HHmmss}.wim",
                        RestoreDirectory = true
                    };

                    DialogResult result = dialog.ShowDialog();
                    tcs.TrySetResult(result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName)
                        ? dialog.FileName
                        : null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();
            return tcs.Task;
        }

        public static Task<string?> ShowOpenDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            var uiThread = new Thread(() =>
            {
                try
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = "Select WIM Image File to Restore",
                        Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*",
                        DefaultExt = "wim",
                        CheckFileExists = true,
                        RestoreDirectory = true
                    };

                    DialogResult result = dialog.ShowDialog();
                    tcs.TrySetResult(result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName)
                        ? dialog.FileName
                        : null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();
            return tcs.Task;
        }
    }
}
