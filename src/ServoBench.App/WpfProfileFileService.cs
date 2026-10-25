using System.IO;
using Microsoft.Win32;
using ServoBench.Presentation;

namespace ServoBench.App;

/// <summary>Profile dialogs and file I/O using the standard Windows file dialogs.</summary>
public sealed class WpfProfileFileService : IProfileFileService
{
    private const string Filter = "Drive profile (*.json)|*.json|All files (*.*)|*.*";

    public string? ChooseSavePath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save drive profile",
            Filter = Filter,
            DefaultExt = ".json",
            AddExtension = true,
            FileName = suggestedFileName,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseOpenPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load drive profile",
            Filter = Filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public Task WriteAllTextAsync(string path, string contents) => File.WriteAllTextAsync(path, contents);

    public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path);
}
