using System.Text.Json.Serialization;

namespace OpenMCAD.IdlGen;

/// <summary>The parsed contents of <c>native/kernel.api.json</c>.</summary>
public sealed class ApiDocument
{
    /// <summary>Gets or sets the IDL format version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Gets or sets the prefix every exported C symbol carries.</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "openmcad";

    /// <summary>Gets or sets the native library name the bindings load.</summary>
    [JsonPropertyName("library")]
    public string Library { get; set; } = "openmcad_occt";

    /// <summary>Gets or sets the operations.</summary>
    [JsonPropertyName("operations")]
    public List<Operation> Operations { get; set; } = [];
}

/// <summary>One entry point in the C ABI.</summary>
public sealed class Operation
{
    /// <summary>Gets or sets the snake-case name, without the prefix.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the PascalCase name used on the C# side.</summary>
    [JsonPropertyName("csharp")]
    public string CSharp { get; set; } = string.Empty;

    /// <summary>Gets or sets the group this operation belongs to, used only for section headers.</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the documentation summary.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this operation escalates through the retry ladder.
    /// </summary>
    /// <remarks>
    /// PLAN.md 5.2.4. Marks the operations where OCCT is known to be fragile — booleans and blends
    /// — so the generated dispatch threads tolerance and rung reporting through them, and so the
    /// generated documentation says which they are.
    /// </remarks>
    [JsonPropertyName("fragile")]
    public bool Fragile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this operation has a hand-written body in every
    /// build, rather than a generated not-implemented fallback.
    /// </summary>
    /// <remarks>
    /// Four entry points must work before any geometry does: initialise, shut down, report the
    /// version, and report the last error. A build whose error reporting returned
    /// "not implemented" would be unable to tell you why anything else failed.
    /// </remarks>
    [JsonPropertyName("handwritten")]
    public bool Handwritten { get; set; }

    /// <summary>Gets or sets the parameters, in declaration order.</summary>
    [JsonPropertyName("parameters")]
    public List<Parameter> Parameters { get; set; } = [];

    /// <summary>Gets the exported C symbol.</summary>
    /// <param name="prefix">The prefix from the document.</param>
    public string CSymbol(string prefix) => $"{prefix}_{Name}";
}

/// <summary>One parameter of an operation.</summary>
public sealed class Parameter
{
    /// <summary>Gets or sets the snake-case parameter name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the marshalling kind, from <see cref="TypeTable"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the documentation summary.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fixed element count for an output buffer whose size is known in advance.
    /// </summary>
    /// <remarks>
    /// Where a buffer has a fixed size — mass properties is always eleven doubles — the caller can
    /// skip the size query and allocate directly. Recording it here lets the generated
    /// documentation say so, and lets the C# wrapper stack-allocate.
    /// </remarks>
    [JsonPropertyName("fixed")]
    public int? Fixed { get; set; }

    /// <summary>Gets the camelCase form of the name, for C#.</summary>
    public string CamelCase
    {
        get
        {
            string[] parts = Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return Name;
            }

            return parts[0] + string.Concat(parts.Skip(1).Select(Capitalise));
        }
    }

    private static string Capitalise(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
