using System.ComponentModel;

namespace Asterisk.NetAot.Live;

/// <summary>
/// Base interface for all live domain objects with property change notification.
/// </summary>
public interface ILiveObject : INotifyPropertyChanged
{
    /// <summary>Unique identifier for this live object.</summary>
    string Id { get; }
}

/// <summary>
/// Base implementation with PropertyChanged support.
/// </summary>
public abstract class LiveObjectBase : ILiveObject
{
    public abstract string Id { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
