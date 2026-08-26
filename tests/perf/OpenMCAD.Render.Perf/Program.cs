using System.Globalization;

using OpenMCAD.Render;
using OpenMCAD.Render.Direct3D12;
using OpenMCAD.Render.Perf;

// The viewport performance harness (P2-T13).
//
// PLAN.md's budget is "viewport frame time, 2M triangles, rotating: under 16 ms". This measures
// against that, and against the shape of the problem either side of it: the same triangle count
// spread across few large bodies and across many small ones, because a CAD assembly is the second
// and a single imported mesh is the first, and they fail for different reasons.

int width = 1920;
int height = 1080;
int frames = 60;
bool software = false;
bool debugLayer = false;

foreach (string argument in args)
{
    if (argument == "--software")
    {
        software = true;
    }
    else if (argument == "--debug")
    {
        // Off by default: the debug layer costs enough to distort the very numbers this exists to
        // produce. On when something has gone wrong, which is the only time its cost is worth it.
        debugLayer = true;
    }
    else if (argument.StartsWith("--frames=", StringComparison.Ordinal))
    {
        frames = int.Parse(argument["--frames=".Length..], CultureInfo.InvariantCulture);
    }
    else if (argument.StartsWith("--size=", StringComparison.Ordinal))
    {
        string[] parts = argument["--size=".Length..].Split('x');
        width = int.Parse(parts[0], CultureInfo.InvariantCulture);
        height = int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
    else
    {
        Console.Error.WriteLine($"unknown argument '{argument}'");
        Console.Error.WriteLine("usage: [--software] [--debug] [--frames=N] [--size=WxH]");
        return 2;
    }
}

using D3D12RenderDevice device = new(new RenderDeviceOptions(
    EnableDebugLayer: debugLayer, ForceSoftware: software));

Console.WriteLine($"device    : {device.Info}");
Console.WriteLine($"target    : {width}x{height}, {frames} frames each, every frame fenced");

using FrameTimer timer = new(device, width, height, MsaaTarget.DefaultSampleCount);

Console.WriteLine($"sampling  : {timer.SampleCount}x");
Console.WriteLine($"budget    : 16.00 ms median (PLAN.md, 2M triangles rotating)");
Console.WriteLine();

// Triangle counts either side of the budget, and body counts either side of what instancing would
// be for. The pairs at 2M are the interesting comparison: the same geometry as few large bodies
// and as many small ones.
(string Label, int Triangles, int Bodies)[] scenes =
[
    ("100k, 1 body", 100_000, 1),
    ("100k, 1k bodies", 100_000, 1_000),
    ("1M, 16 bodies", 1_000_000, 16),
    ("1M, 1k bodies", 1_000_000, 1_000),
    ("2M, 16 bodies", 2_000_000, 16),
    ("2M, 1k bodies", 2_000_000, 1_000),
    ("2M, 10k bodies", 2_000_000, 10_000),
    ("5M, 64 bodies", 5_000_000, 64),
    ("5M, 10k bodies", 5_000_000, 10_000),
];

// Wake the device before the first scene is timed.
//
// On a laptop the GPU idles at a low clock and takes a second or so of sustained load to ramp.
// Without this the first scene measured absorbs the ramp and reports several times its true cost
// -- 100k triangles across 1k bodies came out at 30 ms median with a 75 ms p95, four times slower
// than the same body count at twenty times the triangles. That is the power governor, not the
// renderer, and a harness that reports it as the renderer is worse than no harness.
Console.Write("warming    : ");
DisplaySnapshot warmupScene = SyntheticScene.Build(1_000_000, 100);
timer.Measure("warmup", warmupScene, frames: 90, warmup: 0);
Console.WriteLine("done");
Console.WriteLine();

List<FrameMeasurement> results = [];

foreach ((string label, int triangles, int bodies) in scenes)
{
    DisplaySnapshot snapshot = SyntheticScene.Build(triangles, bodies);
    FrameMeasurement measurement = timer.Measure(label, snapshot, frames);

    results.Add(measurement);
    Console.WriteLine(measurement);

    // Off-screen rendering has no present to report a lost device, and submission stays silent, so
    // a removed device would produce a full set of plausible-looking timings for work that never
    // happened.
    if (device.IsRemoved)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "the graphics device was lost during this run; every figure after it is meaningless");

        return 1;
    }
}

Console.WriteLine();

FrameMeasurement? headline = results.FirstOrDefault(r => r.Label == "2M, 16 bodies");

if (headline is { } budget)
{
    Console.WriteLine(budget.WithinBudget
        ? $"PASS  the 2M budget is met at {budget.MedianMs:0.00} ms median"
        : $"OVER  the 2M budget is missed at {budget.MedianMs:0.00} ms median, against 16.00");
}

// A non-zero exit would make this look like a test, and it is not one: a slow machine or a busy
// one is not a defect, and a harness that failed the build on a timing would be turned off within
// a week. The numbers are the output.
return 0;
