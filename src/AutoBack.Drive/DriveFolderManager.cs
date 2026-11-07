using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record DriveDevice(Guid Id);

// The visible PC name is a label. Private app properties and the protected installation ID prove ownership.
public sealed class DriveFolderManager(ProtectedStore store, DriveApi api, Guid agentId, string agentName)
{
    public string DeviceId
    {
        get
        {
            var device = store.Load<DriveDevice>("drive-device.dat");
            if (device is null) { device = new(Guid.NewGuid()); store.Save("drive-device.dat", device); }
            if (device.Id == Guid.Empty) throw new InvalidDataException("The saved Drive device identity is invalid.");
            return device.Id.ToString("N");
        }
    }

    public async Task<DriveFolders> EnsureAsync(string accountKey, CancellationToken token)
    {
        if (!Protocol.ValidName(agentName)) throw new InvalidDataException("The paired PC name is invalid.");
        var device = DeviceId;
        var cache = store.Load<Dictionary<string, DriveFolders>>("drive-folders.dat") ?? [];
        cache.TryGetValue(accountKey, out var saved);
        var root = saved is null ? null : await api.GetFileAsync(saved.RootId, token);
        string rootId;
        if (root is not null && !root.Trashed) rootId = root.Id;
        else if (root is null && saved is { RootConfirmed: false }) rootId = saved.RootId;
        else rootId = await api.FindRootAsync(token) ?? await api.GenerateIdAsync(token);
        var client = saved is null || saved.RootId != rootId ? null : await api.GetFileAsync(saved.ClientId, token);
        string clientId;
        if (client is not null && !client.Trashed) clientId = client.Id;
        else if (client is null && saved is { ClientConfirmed: false } && saved.RootId == rootId) clientId = saved.ClientId;
        else
        {
            var matches = await api.ListAsync($"trashed = false and '{rootId}' in parents and mimeType = 'application/vnd.google-apps.folder' and appProperties has {{ key='autobackRole' and value='client' }} and (appProperties has {{ key='autobackDevice' and value='{device}' }} or appProperties has {{ key='autobackAgent' and value='{agentId:N}' }})", token);
            if (matches.Count > 1) throw new DriveException(DriveFailure.Permanent, "Several AutoBack folders claim this PC. Resolve the duplicate folders before retrying.");
            clientId = matches.SingleOrDefault()?.Id ?? await api.GenerateIdAsync(token);
        }
        // Persist allocated IDs before creation: a lost response must never create a second PC folder.
        Save(new(rootId, clientId, false, false));
        await api.EnsureFolderAsync(rootId, "AutoBack Backups", null, "root", agentId, token);
        Save(new(rootId, clientId, true, false));
        await api.EnsureFolderAsync(clientId, agentName, rootId, "client", agentId, token, device);
        var folders = new DriveFolders(rootId, clientId);
        Save(folders);
        return folders;

        void Save(DriveFolders folders) { cache[accountKey] = folders; store.Save("drive-folders.dat", cache); }
    }
}
