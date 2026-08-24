using System.Collections.Immutable;

namespace OpenMCAD.Interaction.Navigation;

/// <summary>A mouse button, as far as navigation is concerned.</summary>
/// <remarks>
/// Deliberately not <c>System.Windows.Input.MouseButton</c>. ADR-0007 keeps the UI framework out
/// of everything below the shell, and navigation is arithmetic that should be testable — and
/// eventually drivable from a 3D mouse or a touchpad — without a window in sight.
/// </remarks>
public enum PointerButton
{
    /// <summary>No button.</summary>
    None = 0,

    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The wheel button.</summary>
    Middle,

    /// <summary>The secondary button.</summary>
    Right,
}

/// <summary>Modifier keys held during a gesture.</summary>
[Flags]
public enum NavigationModifiers
{
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control.</summary>
    Control = 2,

    /// <summary>Alt.</summary>
    Alt = 4,
}

/// <summary>What a drag does.</summary>
public enum NavigationGesture
{
    /// <summary>Nothing. The drag belongs to something else — selection, a sketch tool.</summary>
    None = 0,

    /// <summary>Turn the camera about its target.</summary>
    Orbit,

    /// <summary>Slide the camera and target across the view plane.</summary>
    Pan,

    /// <summary>Move towards or away from the target.</summary>
    Zoom,
}

/// <summary>One button-and-modifier combination, and what it does.</summary>
/// <param name="Button">The button held.</param>
/// <param name="Modifiers">The modifiers held, matched exactly.</param>
/// <param name="Gesture">What the drag performs.</param>
public readonly record struct NavigationBinding(
    PointerButton Button,
    NavigationModifiers Modifiers,
    NavigationGesture Gesture);

/// <summary>
/// Which mouse gestures navigate the view (P2-T08).
/// </summary>
/// <remarks>
/// <para>
/// Every CAD package binds these differently, and users arrive with a decade of muscle memory from
/// whichever one they came from. Someone who orbits with the middle button in SolidWorks will pan
/// by accident in Fusion for weeks. Making this a table rather than a set of hard-coded
/// <c>if</c> statements is what lets that be a setting instead of a complaint.
/// </para>
/// <para>
/// The presets are <b>modelled on</b> the packages they name rather than claimed to be identical
/// to them; each has corners and per-version differences. They are starting points, and the whole
/// point of the type is that a user can rebind.
/// </para>
/// <para>
/// <b>Modifiers match exactly.</b> Control+middle is not middle-with-something-extra: it resolves
/// only to whatever Control+middle is bound to, and to nothing if that is unbound. Matching
/// loosely would make a modified drag silently fall back to the unmodified gesture, which is how
/// you pan when you meant to zoom.
/// </para>
/// </remarks>
public sealed record MouseProfile(string Name, ImmutableArray<NavigationBinding> Bindings)
{
    /// <summary>
    /// Middle to orbit, Control+middle to pan, Shift+middle to zoom.
    /// </summary>
    /// <remarks>
    /// Modelled on SolidWorks, and the default here because it is the binding the largest number
    /// of mechanical engineers already have in their hands.
    /// </remarks>
    public static MouseProfile SolidWorks { get; } = new(
        "SolidWorks",
        [
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.None, NavigationGesture.Orbit),
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.Control, NavigationGesture.Pan),
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.Shift, NavigationGesture.Zoom),
        ]);

    /// <summary>Middle to pan, Shift+middle to orbit. Modelled on Fusion 360 and Inventor.</summary>
    public static MouseProfile Fusion { get; } = new(
        "Fusion",
        [
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.None, NavigationGesture.Pan),
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.Shift, NavigationGesture.Orbit),
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.Control, NavigationGesture.Zoom),
        ]);

    /// <summary>Right to orbit, Control+right to pan. Modelled on Onshape.</summary>
    public static MouseProfile Onshape { get; } = new(
        "Onshape",
        [
            new NavigationBinding(PointerButton.Right, NavigationModifiers.None, NavigationGesture.Orbit),
            new NavigationBinding(PointerButton.Right, NavigationModifiers.Control, NavigationGesture.Pan),
            new NavigationBinding(PointerButton.Middle, NavigationModifiers.None, NavigationGesture.Pan),
        ]);

    /// <summary>Gets the profile used when nothing has been chosen.</summary>
    public static MouseProfile Default => SolidWorks;

    /// <summary>Gets every preset, for offering a choice in settings.</summary>
    public static ImmutableArray<MouseProfile> Presets { get; } = [SolidWorks, Fusion, Onshape];

    /// <summary>
    /// Finds what a button-and-modifier combination does.
    /// </summary>
    /// <param name="button">The button pressed.</param>
    /// <param name="modifiers">The modifiers held.</param>
    /// <returns>
    /// The gesture, or <see cref="NavigationGesture.None"/> when the combination is unbound — which
    /// leaves the drag for selection or a tool to claim.
    /// </returns>
    public NavigationGesture Resolve(PointerButton button, NavigationModifiers modifiers)
    {
        foreach (NavigationBinding binding in Bindings)
        {
            if (binding.Button == button && binding.Modifiers == modifiers)
            {
                return binding.Gesture;
            }
        }

        return NavigationGesture.None;
    }

    /// <summary>Returns this profile with one combination rebound.</summary>
    /// <param name="button">The button to bind.</param>
    /// <param name="modifiers">The modifiers to bind.</param>
    /// <param name="gesture">What it should do, or <see cref="NavigationGesture.None"/> to unbind.</param>
    /// <returns>A new profile. The original is unchanged.</returns>
    public MouseProfile With(
        PointerButton button, NavigationModifiers modifiers, NavigationGesture gesture)
    {
        ImmutableArray<NavigationBinding>.Builder kept =
            ImmutableArray.CreateBuilder<NavigationBinding>(Bindings.Length + 1);

        foreach (NavigationBinding binding in Bindings)
        {
            if (binding.Button != button || binding.Modifiers != modifiers)
            {
                kept.Add(binding);
            }
        }

        if (gesture != NavigationGesture.None)
        {
            kept.Add(new NavigationBinding(button, modifiers, gesture));
        }

        return this with { Name = $"{Name} (modified)", Bindings = kept.ToImmutable() };
    }
}
