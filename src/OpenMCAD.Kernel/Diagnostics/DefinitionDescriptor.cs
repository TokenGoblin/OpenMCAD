using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using OpenMCAD.Kernel.Operations;

namespace OpenMCAD.Kernel.Diagnostics;

/// <summary>
/// Renders an operation definition as text, for the manifest and for the failure fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// Written because <c>record.ToString()</c> is wrong for both jobs, in two ways that are easy to
/// miss and were:
/// </para>
/// <list type="number">
/// <item><description>
/// The compiler-generated <c>PrintMembers</c> renders an <see cref="ImmutableArray{T}"/> as its
/// type name. A fillet's radii and edge selection — the entire content of the operation — came out
/// as <c>System.Collections.Immutable.ImmutableArray`1[...]</c>, so the manifest described a
/// failure nobody could reproduce, and two fillets differing only in radius produced identical
/// fingerprints. The second, genuinely different failure was discarded as a duplicate.
/// </description></item>
/// <item><description>
/// It embeds <see cref="KernelShape"/> tags, which are slot indices with a generation counter and
/// change on every rebuild. Fingerprinting them meant the same failure hashed differently each time
/// — so the deduplication this class exists to provide did not work for any operation that consumes
/// a shape, which is to say all the ones that actually fail.
/// </description></item>
/// </list>
/// <para>
/// Hence two renderings. <see cref="ForManifest"/> is complete and includes tags, because a human
/// reading the bundle wants them. <see cref="ForFingerprint"/> replaces each distinct tag with a
/// positional slot, so it is stable across rebuilds while still distinguishing "the same edge
/// twice" from "two different edges".
/// </para>
/// <para>
/// Reflection rather than a method on each definition. There are ten definitions today and ADR-0002
/// projects 200 to 300; a per-definition method would be 250 chances to forget, and forgetting
/// produces a silently unreproducible bundle rather than a compile error.
/// </para>
/// </remarks>
internal static class DefinitionDescriptor
{
    private const int MaxDepth = 5;

    /// <summary>Renders a definition in full, including handle tags.</summary>
    /// <param name="definition">The definition, or <see langword="null"/>.</param>
    internal static string ForManifest(IOperationDefinition? definition)
        => Render(definition, anonymiseHandles: false);

    /// <summary>
    /// Renders a definition with handle tags replaced by positional slots, so that the same
    /// failure produces the same text on a later rebuild.
    /// </summary>
    /// <param name="definition">The definition, or <see langword="null"/>.</param>
    internal static string ForFingerprint(IOperationDefinition? definition)
        => Render(definition, anonymiseHandles: true);

    private static string Render(IOperationDefinition? definition, bool anonymiseHandles)
    {
        if (definition is null)
        {
            return "(none)";
        }

        StringBuilder text = new();
        text.Append(definition.OperationName);
        text.Append('(');

        Dictionary<ulong, int> slots = [];
        bool first = true;

        foreach (PropertyInfo property in PropertiesOf(definition.GetType()))
        {
            if (!first)
            {
                text.Append(", ");
            }

            first = false;
            text.Append(property.Name);
            text.Append('=');
            AppendValue(text, ReadOrNull(property, definition), anonymiseHandles, slots, 0);
        }

        text.Append(')');
        return text.ToString();
    }

    private static IEnumerable<PropertyInfo> PropertiesOf(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.CanRead)

            // OperationName is the prefix already, and EqualityContract is record machinery.
            .Where(p => p.Name is not ("OperationName" or "EqualityContract"))
            .OrderBy(p => p.MetadataToken);

    private static object? ReadOrNull(PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (TargetInvocationException)
        {
            // A computed property that throws must not take the bundle down with it.
            return "(threw)";
        }
    }

    private static void AppendValue(
        StringBuilder text,
        object? value,
        bool anonymiseHandles,
        Dictionary<ulong, int> slots,
        int depth)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;

            case KernelShape shape:
                AppendHandle(text, "shape", shape.Tag, anonymiseHandles, slots);
                return;

            case SubEntity entity:
                text.Append(entity.Kind);
                text.Append(':');
                AppendHandle(text, "entity", entity.Tag, anonymiseHandles, slots);
                return;

            case double number:
                // G17 round-trips exactly. A radius that differed in its last bit would otherwise
                // fingerprint as the same failure.
                text.Append(number.ToString("G17", CultureInfo.InvariantCulture));
                return;

            case float number:
                text.Append(number.ToString("G9", CultureInfo.InvariantCulture));
                return;

            case string s:
                text.Append('"').Append(s).Append('"');
                return;

            case bool b:
                text.Append(b ? "true" : "false");
                return;

            case Enum e:
                text.Append(e.ToString());
                return;

            case IFormattable formattable:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        if (depth >= MaxDepth)
        {
            text.Append("...");
            return;
        }

        if (value is IEnumerable sequence)
        {
            text.Append('[');
            bool first = true;
            foreach (object? element in sequence)
            {
                if (!first)
                {
                    text.Append(", ");
                }

                first = false;
                AppendValue(text, element, anonymiseHandles, slots, depth + 1);
            }

            text.Append(']');
            return;
        }

        Type type = value.GetType();

        // A nested record or struct such as FilletEdge, Transform, or Vec3d. Recurse so its
        // contents reach the text rather than its type name.
        if (type.IsValueType || type.Namespace?.StartsWith("OpenMCAD", StringComparison.Ordinal) == true)
        {
            text.Append('{');
            bool first = true;
            foreach (PropertyInfo property in PropertiesOf(type))
            {
                if (!first)
                {
                    text.Append(", ");
                }

                first = false;
                text.Append(property.Name).Append('=');
                AppendValue(text, ReadOrNull(property, value), anonymiseHandles, slots, depth + 1);
            }

            text.Append('}');
            return;
        }

        text.Append(value.ToString());
    }

    private static void AppendHandle(
        StringBuilder text,
        string kind,
        ulong tag,
        bool anonymiseHandles,
        Dictionary<ulong, int> slots)
    {
        if (!anonymiseHandles)
        {
            text.Append(kind).Append("(0x").Append(tag.ToString("X", CultureInfo.InvariantCulture)).Append(')');
            return;
        }

        // Positional, in first-encountered order. Two references to the same tag get the same slot,
        // so "fillet these two distinct edges" and "fillet this edge twice" stay distinguishable.
        if (!slots.TryGetValue(tag, out int slot))
        {
            slot = slots.Count;
            slots[tag] = slot;
        }

        text.Append(kind).Append('#').Append(slot.ToString(CultureInfo.InvariantCulture));
    }
}
