using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RimPulse.App.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base class. RimPulse intentionally avoids a MVVM
/// toolkit dependency: there are no source generators, no service locator and no hidden behaviour to
/// learn, which keeps the desktop layer easy to audit.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));

    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
