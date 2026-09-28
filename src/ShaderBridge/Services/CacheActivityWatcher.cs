namespace ShaderBridge.Services;

public sealed class CacheActivityWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    public event Action<string>? Activity;

    public void Start(IEnumerable<string> roots)
    {
        Stop();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, e) => Activity?.Invoke($"CREATE  {e.FullPath}");
                watcher.Deleted += (_, e) => Activity?.Invoke($"DELETE  {e.FullPath}");
                watcher.Changed += (_, e) => Activity?.Invoke($"CHANGE  {e.FullPath}");
                watcher.Renamed += (_, e) => Activity?.Invoke($"RENAME  {e.OldFullPath} -> {e.FullPath}");
                watcher.Error += (_, e) => Activity?.Invoke($"WATCHER ERROR  {root}: {e.GetException().Message}");
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Activity?.Invoke($"WATCHER FAILED  {root}: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    public void Dispose() => Stop();
}
