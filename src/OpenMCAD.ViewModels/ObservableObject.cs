using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenMCAD.ViewModels;

/// <summary>
/// Minimal change-notification base for view models.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than taken from an MVVM toolkit. PLAN.md 12 asks for an ADR before every
/// new dependency, and an MVVM framework is a decision worth making when there is a real view
/// model to inform it, not in Phase 0 on the strength of forty lines of boilerplate. Revisit at
/// P6-T04, when the schema-driven property manager exists and the requirements are known.
/// </para>
/// <para>
/// <see cref="INotifyPropertyChanged"/> lives in <c>System.ComponentModel</c>, which is part of
/// the base library and not of WPF, so using it here does not violate ADR-0007.
/// </para>
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the named property.</summary>
    /// <param name="propertyName">
    /// The property name, supplied automatically by the compiler at the call site.
    /// </param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises
    /// <see cref="PropertyChanged"/> if the value actually changed.
    /// </summary>
    /// <typeparam name="T">The property type.</typeparam>
    /// <param name="field">A reference to the backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">
    /// The property name, supplied automatically by the compiler at the call site.
    /// </param>
    /// <returns><see langword="true"/> if the value changed.</returns>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
