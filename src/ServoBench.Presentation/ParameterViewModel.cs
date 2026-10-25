using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServoBench.Core.Drive;

namespace ServoBench.Presentation;

/// <summary>
/// One row in the parameter grid. Tracks the value last read from the drive separately from the
/// text the user is editing, so a pending edit is never overwritten by live polling.
/// </summary>
public sealed class ParameterViewModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly IFormatProvider _culture;
    private double? _deviceValue;
    private string _editText = string.Empty;
    private string? _error;
    private double? _pendingValue;

    public ParameterViewModel(ParameterDefinition definition, IFormatProvider culture)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _culture = culture ?? throw new ArgumentNullException(nameof(culture));
    }

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public ParameterDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Group => Definition.Group;

    public string Unit => Definition.Unit;

    public string Description => Definition.Description;

    public bool IsWritable => Definition.IsWritable;

    public bool IsReadOnly => !Definition.IsWritable;

    public string Range => IsWritable
        ? $"{Definition.Format(Definition.Minimum, _culture)} … {Definition.Format(Definition.Maximum, _culture)}"
        : string.Empty;

    public double? DeviceValue
    {
        get => _deviceValue;
        private set
        {
            if (SetProperty(ref _deviceValue, value))
            {
                OnPropertyChanged(nameof(DeviceText));
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string DeviceText => _deviceValue is { } v ? Definition.Format(v, _culture) : "–";

    /// <summary>The text in the edit cell. Validated on every change.</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (SetProperty(ref _editText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    /// <summary>The parsed edit, or null while the text is invalid.</summary>
    public double? PendingValue => _pendingValue;

    public string? Error => _error;

    public bool HasErrors => _error is not null;

    /// <summary>True when there is a valid edit that differs from what the drive holds.</summary>
    public bool IsDirty =>
        IsWritable
        && _pendingValue is { } pending
        && (_deviceValue is not { } device || !SameRegisterValue(pending, device));

    public IEnumerable GetErrors(string? propertyName) =>
        (string.IsNullOrEmpty(propertyName) || propertyName == nameof(EditText)) && _error is not null
            ? new[] { _error }
            : Array.Empty<string>();

    /// <summary>Records a value read from the drive. Follows the drive unless the user has a pending edit.</summary>
    public void UpdateFromDevice(double value)
    {
        var followDevice = !IsDirty && !HasErrors;
        DeviceValue = value;

        // Leave the text alone if it already means this value ("6" vs "6.0"), so live polling
        // never rewrites what the user is typing or moves the caret.
        if (followDevice && !(_pendingValue is { } pending && SameRegisterValue(pending, value)))
        {
            EditText = DeviceText;
        }
    }

    /// <summary>Discards any edit and shows the drive's value again.</summary>
    public void Revert()
    {
        if (_deviceValue is not null)
        {
            EditText = DeviceText;
        }
    }

    /// <summary>Stages a value (e.g. from a profile) as an edit, without writing it.</summary>
    public void Stage(double value) => EditText = Definition.Format(value, _culture);

    /// <summary>Marks the pending edit as accepted by the drive.</summary>
    public void MarkWritten()
    {
        if (_pendingValue is { } pending)
        {
            DeviceValue = pending;
        }
    }

    /// <summary>Shows an error reported by the drive for this parameter.</summary>
    public void SetDeviceError(string message) => SetError(message);

    private bool SameRegisterValue(double a, double b) =>
        Math.Round(a / Definition.Scale) == Math.Round(b / Definition.Scale);

    private void Validate()
    {
        if (Definition.TryParse(_editText, _culture, out var value, out var error))
        {
            _pendingValue = value;
            SetError(null);
        }
        else
        {
            _pendingValue = null;
            SetError(error);
        }

        OnPropertyChanged(nameof(PendingValue));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void SetError(string? error)
    {
        if (_error == error)
        {
            return;
        }

        _error = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(EditText)));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasErrors));
    }
}
