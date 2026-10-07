namespace ArriLight;

/// <summary>A selected firmware file whose stream can be reopened for validation and transfer.</summary>
public interface IFirmwareFile
{
    string FileName { get; }
    Task<Stream> OpenReadAsync();
}

public interface IFirmwareFilePicker
{
    Task<IFirmwareFile?> PickAsync(string title);
}

public sealed class SelectedFirmwareFile(FileResult file) : IFirmwareFile
{
    public string FileName => file.FileName;
    public Task<Stream> OpenReadAsync() => file.OpenReadAsync();
}
