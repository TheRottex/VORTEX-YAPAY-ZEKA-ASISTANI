using System.Text;

namespace Vortex.Desktop.Services;

public sealed class TokenStorageService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path;

    public TokenStorageService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        var dir = Path.Combine(root, "VortexAI");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "desktop-session.dat");
    }

    public async Task SaveAsync(string token, CancellationToken cancellationToken)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                File.SetAttributes(_path, FileAttributes.Normal);
            }

            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, data, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            try
            {
                File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
            }
            catch (Exception ex)
            {
                DesktopLogService.Error("Masaüstü oturum dosyası gizli olarak işaretlenemedi.", ex);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var data = await File.ReadAllTextAsync(_path, cancellationToken);
            return Encoding.UTF8.GetString(Convert.FromBase64String(data));
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Kaydedilmiş masaüstü oturumu okunamadı.", ex);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    public void Clear()
    {
        Gate.Wait();
        try
        {
            if (File.Exists(_path))
            {
                File.SetAttributes(_path, FileAttributes.Normal);
                File.Delete(_path);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

