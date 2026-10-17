using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PocketCake.ViewModels;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for the view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);

        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>; an empty name refreshes every binding.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
