using ServoBench.Core.Drive;
using ServoBench.Core.Modbus;

namespace ServoBench.Presentation;

/// <summary>Creates a drive client for an endpoint. Swapped for a fake in tests.</summary>
public interface IDriveClientFactory
{
    IDriveClient Create(string host, int port);
}

public sealed class ModbusDriveClientFactory : IDriveClientFactory
{
    public IDriveClient Create(string host, int port) => new ModbusDriveClient(new ModbusTcpClient(host, port));
}

/// <summary>File dialogs and file I/O for profiles. The WPF app implements this with the Win32 dialogs.</summary>
public interface IProfileFileService
{
    /// <summary>Returns the chosen path, or null if the user cancelled.</summary>
    string? ChooseSavePath(string suggestedFileName);

    /// <summary>Returns the chosen path, or null if the user cancelled.</summary>
    string? ChooseOpenPath();

    Task WriteAllTextAsync(string path, string contents);

    Task<string> ReadAllTextAsync(string path);
}
