namespace OpenMCAD.IdlGen;

/// <summary>How one IDL type crosses the boundary.</summary>
/// <param name="CParameters">The C parameter declarations, given the parameter name.</param>
/// <param name="CSharpParameters">The C# parameter declarations.</param>
/// <param name="OpsParameter">
/// The declaration the hand-written implementation sees, in the shim's own C++ vocabulary.
/// </param>
/// <param name="OpsArgument">The expression the dispatch layer passes to the implementation.</param>
/// <param name="IsOutput">Whether this parameter carries a result outwards.</param>
/// <param name="NeedsNullCheck">Whether the dispatch layer must reject a null pointer.</param>
public sealed record Marshalling(
    Func<string, string> CParameters,
    Func<string, string> CSharpParameters,
    Func<string, string> OpsParameter,
    Func<string, string> OpsArgument,
    bool IsOutput = false,
    bool NeedsNullCheck = false);

/// <summary>
/// The marshalling rules, one per IDL type.
/// </summary>
/// <remarks>
/// <para>
/// This table is the entire boundary contract in one place, which is the point of generating rather
/// than hand-writing: a rule stated here is applied identically to all forty-nine operations, and a
/// mistake here is one mistake rather than forty-nine.
/// </para>
/// <para>
/// The rules follow ADR-0003 without exception. Handles are opaque <c>uint64</c>. Nothing carries a
/// pointer to kernel-owned memory outwards. Bulk data uses the two-call pattern — a null buffer
/// asks for the required size — because the alternative, returning shim-allocated memory, needs a
/// matching free on every path including the error paths, and that is how boundaries leak.
/// </para>
/// <para>
/// The C++ vocabulary the implementation sees (<c>openmcad::ShapeRef</c>,
/// <c>openmcad::OutBuffer</c>, and friends) is declared in the hand-written
/// <c>native/openmcad_occt/include/openmcad_types.h</c>. Deliberately no OCCT types appear here:
/// the generator must not know what kernel is underneath.
/// </para>
/// </remarks>
public static class TypeTable
{
    private static readonly Dictionary<string, Marshalling> Rules = new(StringComparer.Ordinal)
    {
        // --- scalars in -------------------------------------------------------------------------
        ["f64"] = new(
            n => $"double {n}",
            n => $"double {n}",
            n => $"double {n}",
            n => n),

        ["i32"] = new(
            n => $"int32_t {n}",
            n => $"int {n}",
            n => $"int32_t {n}",
            n => n),

        // A C ABI has no bool of agreed width. int32_t is unambiguous; the implementation sees the
        // C++ type it wants.
        ["bool"] = new(
            n => $"int32_t {n}",
            n => $"int {n}",
            n => $"bool {n}",
            n => $"{n} != 0"),

        ["utf8"] = new(
            n => $"const char* {n}",
            n => $"string {n}",
            n => $"const char* {n}",
            n => n,
            NeedsNullCheck: true),

        // --- handles in --------------------------------------------------------------------------
        ["shape"] = new(
            n => $"uint64_t {n}",
            n => $"ulong {n}",
            n => $"openmcad::ShapeRef {n}",
            n => $"openmcad::ShapeRef{{{n}}}"),

        ["entity"] = new(
            n => $"uint64_t {n}",
            n => $"ulong {n}",
            n => $"openmcad::EntityRef {n}",
            n => $"openmcad::EntityRef{{{n}}}"),

        ["history"] = new(
            n => $"uint64_t {n}",
            n => $"ulong {n}",
            n => $"openmcad::HistoryRef {n}",
            n => $"openmcad::HistoryRef{{{n}}}"),

        ["mesh"] = new(
            n => $"uint64_t {n}",
            n => $"ulong {n}",
            n => $"openmcad::MeshRef {n}",
            n => $"openmcad::MeshRef{{{n}}}"),

        // --- fixed-size value blocks in ------------------------------------------------------------
        // Passed as a caller-allocated pointer rather than a struct by value: struct ABI rules for
        // aggregates differ between calling conventions, and a plain double array does not.
        ["transform"] = new(
            n => $"const double* {n}",
            n => $"ReadOnlySpan<double> {n}",
            n => $"const openmcad::Transform& {n}",
            n => $"openmcad::Transform::from({n})",
            NeedsNullCheck: true),

        ["vec3"] = new(
            n => $"const double* {n}",
            n => $"ReadOnlySpan<double> {n}",
            n => $"const openmcad::Vec3& {n}",
            n => $"openmcad::Vec3::from({n})",
            NeedsNullCheck: true),

        // --- arrays in -----------------------------------------------------------------------------
        ["f64_array"] = new(
            n => $"const double* {n}, int32_t {n}_count",
            n => $"ReadOnlySpan<double> {n}, int {n}Count",
            n => $"std::span<const double> {n}",
            n => $"openmcad::make_span({n}, {n}_count)"),

        ["vec2_array"] = new(
            n => $"const double* {n}, int32_t {n}_count",
            n => $"ReadOnlySpan<double> {n}, int {n}Count",
            n => $"std::span<const openmcad::Vec2> {n}",
            n => $"openmcad::make_vec2_span({n}, {n}_count)"),

        ["entity_array"] = new(
            n => $"const uint64_t* {n}, int32_t {n}_count",
            n => $"ReadOnlySpan<ulong> {n}, int {n}Count",
            n => $"std::span<const uint64_t> {n}",
            n => $"openmcad::make_span({n}, {n}_count)"),

        ["shape_array"] = new(
            n => $"const uint64_t* {n}, int32_t {n}_count",
            n => $"ReadOnlySpan<ulong> {n}, int {n}Count",
            n => $"std::span<const uint64_t> {n}",
            n => $"openmcad::make_span({n}, {n}_count)"),

        ["byte_array"] = new(
            n => $"const uint8_t* {n}, int32_t {n}_count",
            n => $"ReadOnlySpan<byte> {n}, int {n}Count",
            n => $"std::span<const uint8_t> {n}",
            n => $"openmcad::make_span({n}, {n}_count)"),

        // --- scalars out ------------------------------------------------------------------------------
        ["i32_out"] = new(
            n => $"int32_t* {n}",
            n => $"out int {n}",
            n => $"int32_t& {n}",
            n => $"*{n}",
            IsOutput: true,
            NeedsNullCheck: true),

        ["u64_out"] = new(
            n => $"uint64_t* {n}",
            n => $"out ulong {n}",
            n => $"uint64_t& {n}",
            n => $"*{n}",
            IsOutput: true,
            NeedsNullCheck: true),

        ["f64_out"] = new(
            n => $"double* {n}",
            n => $"out double {n}",
            n => $"double& {n}",
            n => $"*{n}",
            IsOutput: true,
            NeedsNullCheck: true),

        // --- handles out --------------------------------------------------------------------------------
        ["shape_out"] = new(
            n => $"uint64_t* {n}",
            n => $"out ulong {n}",
            n => $"openmcad::ShapeOut {n}",
            n => $"openmcad::ShapeOut{{{n}}}",
            IsOutput: true,
            NeedsNullCheck: true),

        ["history_out"] = new(
            n => $"uint64_t* {n}",
            n => $"out ulong {n}",
            n => $"openmcad::HistoryOut {n}",
            n => $"openmcad::HistoryOut{{{n}}}",
            IsOutput: true,
            NeedsNullCheck: true),

        ["mesh_out"] = new(
            n => $"uint64_t* {n}",
            n => $"out ulong {n}",
            n => $"openmcad::MeshOut {n}",
            n => $"openmcad::MeshOut{{{n}}}",
            IsOutput: true,
            NeedsNullCheck: true),

        // --- buffers out, the two-call pattern -------------------------------------------------------------
        // A null buffer with a non-null required pointer asks for the size. That is the whole
        // protocol, and it is identical for every buffer type so callers never have to check.
        ["f64_array_out"] = new(
            n => $"double* {n}, int32_t {n}_capacity, int32_t* {n}_required",
            n => $"Span<double> {n}, int {n}Capacity, out int {n}Required",
            n => $"openmcad::OutBuffer<double> {n}",
            n => $"openmcad::OutBuffer<double>{{{n}, {n}_capacity, {n}_required}}",
            IsOutput: true),

        ["i32_array_out"] = new(
            n => $"int32_t* {n}, int32_t {n}_capacity, int32_t* {n}_required",
            n => $"Span<int> {n}, int {n}Capacity, out int {n}Required",
            n => $"openmcad::OutBuffer<int32_t> {n}",
            n => $"openmcad::OutBuffer<int32_t>{{{n}, {n}_capacity, {n}_required}}",
            IsOutput: true),

        ["u64_array_out"] = new(
            n => $"uint64_t* {n}, int32_t {n}_capacity, int32_t* {n}_required",
            n => $"Span<ulong> {n}, int {n}Capacity, out int {n}Required",
            n => $"openmcad::OutBuffer<uint64_t> {n}",
            n => $"openmcad::OutBuffer<uint64_t>{{{n}, {n}_capacity, {n}_required}}",
            IsOutput: true),

        ["byte_array_out"] = new(
            n => $"uint8_t* {n}, int32_t {n}_capacity, int32_t* {n}_required",
            n => $"Span<byte> {n}, int {n}Capacity, out int {n}Required",
            n => $"openmcad::OutBuffer<uint8_t> {n}",
            n => $"openmcad::OutBuffer<uint8_t>{{{n}, {n}_capacity, {n}_required}}",
            IsOutput: true),

        ["utf8_out"] = new(
            n => $"char* {n}, int32_t {n}_capacity, int32_t* {n}_required",
            n => $"Span<byte> {n}, int {n}Capacity, out int {n}Required",
            n => $"openmcad::OutBuffer<char> {n}",
            n => $"openmcad::OutBuffer<char>{{{n}, {n}_capacity, {n}_required}}",
            IsOutput: true),
    };

    /// <summary>Gets every known IDL type name.</summary>
    public static IEnumerable<string> KnownTypes => Rules.Keys.Order(StringComparer.Ordinal);

    /// <summary>Looks up a marshalling rule.</summary>
    /// <param name="type">The IDL type name.</param>
    /// <exception cref="InvalidOperationException">The type is not in the table.</exception>
    public static Marshalling For(string type)
        => Rules.TryGetValue(type, out Marshalling? rule)
            ? rule
            : throw new InvalidOperationException(
                $"Unknown IDL type '{type}'. Known types: {string.Join(", ", KnownTypes)}. "
                + "Add a marshalling rule to TypeTable before using a new type.");
}
