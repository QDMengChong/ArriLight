namespace ArriLight;

// The page and OTA service are production sources; only MAUI platform boundaries
// are replaced. These connection tests never open native pickers or transfer OTA.
public interface IQueryAttributable
{
    void ApplyQueryAttributes(IDictionary<string, object> query);
}

public sealed class Keyboard
{
    public static Keyboard Url { get; } = new();
}

public sealed class PickOptions
{
    public string? PickerTitle { get; set; }
}

public sealed class FilePicker
{
    public static FilePicker Default { get; } = new();
    public Task<FileResult?> PickAsync(PickOptions options) =>
        throw new NotSupportedException("Native file pickers are not available in host tests.");
}

public sealed class FileResult
{
    public string FileName { get; init; } = string.Empty;
    public Task<Stream> OpenReadAsync() =>
        throw new NotSupportedException("Native file providers are not available in host tests.");
}

public static class FileSystem
{
    public static string CacheDirectory =>
        throw new NotSupportedException("OTA downloads are not used in connection tests.");
}

public partial class FirmwareUpdatePage
{
    private void InitializeComponent() { }
    internal void AppearForTest() => OnAppearing();
    internal void DisappearForTest() => OnDisappearing();
}
