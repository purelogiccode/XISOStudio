using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace XISOStudio.Models;

/// <summary>
///     Represents a selectable file in the conversion or test file lists.
///     Implements <see cref="INotifyPropertyChanged" /> for data binding support.
/// </summary>
public class FileItem : INotifyPropertyChanged
{
    private string _displaySize = string.Empty;

    /// <summary>
    ///     Gets or sets whether this file is selected for processing.
    /// </summary>
    public bool IsSelected
    {
        get;
        set
        {
            if (field == value) return;

            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>
    ///     Gets or sets the file name (relative to the selected input folder).
    /// </summary>
    public string FileName
    {
        get;
        set
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;

            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the full path to the file.
    /// </summary>
    public string FullPath
    {
        get;
        set
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;

            field = value;
            OnPropertyChanged();
        }
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the file size in bytes.
    /// </summary>
    public long FileSize
    {
        get;
        set
        {
            if (field == value)
            {
                // A fresh item has size 0 but no formatted display yet; keep it consistent.
                if (string.IsNullOrEmpty(_displaySize)) DisplaySize = FormatSize(value);
                return;
            }

            field = value;
            OnPropertyChanged();
            DisplaySize = FormatSize(value);
        }
    }

    /// <summary>
    ///     Gets or sets the formatted display size (e.g., "1.5 GB").
    /// </summary>
    public string DisplaySize
    {
        get => _displaySize;
        set
        {
            if (string.Equals(_displaySize, value, StringComparison.Ordinal)) return;

            _displaySize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Raises the <see cref="PropertyChanged" /> event.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed; defaults to the caller member name.</param>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatSize(long bytes)
    {
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffix.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {suffix[i]}");
    }
}