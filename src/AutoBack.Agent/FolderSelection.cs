namespace AutoBack.Agent;

internal static class FolderSelection
{
    public static string? ChooseRestoreArchive() => SelectOnSta(() =>
    {
        using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose an AutoBack backup ZIP", Filter = "AutoBack ZIP backups|*.zip", CheckFileExists = true };
        return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? picker.FileName : null;
    });

    public static string? ChooseRestoreDestination(bool verify) => SelectOnSta(() =>
    {
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = verify ? "AutoBack: choose the restored folder containing restore-report.json" : "AutoBack: choose a parent folder for a NEW recovery folder",
            UseDescriptionForTitle = true
        };
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

    public static (string Source, string Destination)? Choose()
    {
        (string Source, string Destination)? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new System.Windows.Forms.FolderBrowserDialog { Description = "AutoBack: choose the SOURCE folder to back up", UseDescriptionForTitle = true, ShowNewFolderButton = false };
                if (source.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                using var destination = new System.Windows.Forms.FolderBrowserDialog { Description = "AutoBack: choose a SEPARATE local DESTINATION folder", UseDescriptionForTitle = true };
                if (destination.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                result = (source.SelectedPath, destination.SelectedPath);
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
