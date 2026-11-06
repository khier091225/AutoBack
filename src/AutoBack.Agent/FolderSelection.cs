namespace AutoBack.Agent;

internal static class FolderSelection
{
    internal static string ChooseSetupAction(AutoBack.Connection.AgentCredentials saved, string status)
    {
        return SelectOnSta(() =>
        {
            using var form = new PairingSetupForm(saved.Name, saved.Host, status);
            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? form.SelectedAction : null;
        }) ?? "cancel";
    }
    internal static System.Windows.Forms.FolderBrowserDialog CreateFolderPicker(string description)
    {
        var picker = new System.Windows.Forms.FolderBrowserDialog { Description = description };
#if !NETFRAMEWORK
        picker.UseDescriptionForTitle = true;
#endif
        return picker;
    }
    public static string? ChooseInvitation() => SelectOnSta(() =>
    {
        using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose a fresh pairing invitation from the host", Filter = "AutoBack invitation|*.autoback-invite", CheckFileExists = true };
        return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? picker.FileName : null;
    });
    public static bool AskDriveSetup() => SelectOnSta(() => System.Windows.Forms.MessageBox.Show("Connect this client's Google Drive account now? Choose No to configure it later with drive-connect.",
        "AutoBack client setup", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes ? "yes" : null) is not null;
    public static string? ChooseRestoreArchive() => SelectOnSta(() =>
    {
        using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose an AutoBack backup", Filter = "AutoBack backups|*.zip;*.abk", CheckFileExists = true };
        return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? picker.FileName : null;
    });

    public static string? ChooseRestoreDestination(bool verify) => SelectOnSta(() =>
    {
        using var picker = CreateFolderPicker(verify ? "AutoBack: choose the restored folder containing restore-report.json" : "AutoBack: choose a parent folder for a NEW recovery folder");
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        return verify ? picker.SelectedPath : Path.Combine(picker.SelectedPath, $"AutoBack-Restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
    });

    private static string? SelectOnSta(Func<string?> select)
    {
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() => { try { result = select(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Restore selection failed. Supply --archive and/or --destination explicitly.", error);
        return result;
    }

    public static string? ChooseOAuthFile()
    {
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose Google OAuth Desktop client JSON", Filter = "JSON files|*.json", CheckFileExists = true };
                if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = picker.FileName;
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("OAuth file selection failed. Use drive-connect --client-secrets <file> instead.", error);
        return result;
    }

    public static AutoBack.Backup.BackupPlan? ChoosePlan(AutoBack.Backup.BackupPlan? existing, string dataDirectory, bool serviceMode, AutoBack.Connection.ProtectedStore store)
    {
        AutoBack.Backup.BackupPlan? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                using var form = new BackupFoldersForm(existing, dataDirectory, serviceMode, store);
                if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = form.SelectedPlan;
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Folder selection failed. Use configure --source <folder> --destination <folder> instead.", error);
        return result;
    }
}
