using Microsoft.Extensions.AI;

namespace Aria.Harness.Tools;

/// <summary>
/// The <c>fleet_status</c> tool. Registered by the Harness only when the host wires a
/// <see cref="Core.HarnessOptions.FleetStatusProvider"/> — it lets the agent inspect the machine
/// fleet (per bridge node: hardware, live load, available models) before deciding WHERE to run
/// work or which model size a target can plausibly host. Read-only: benign in every governance
/// mode including Plan and Off.
/// </summary>
public static class FleetStatusTools
{
    public static AITool Create(Func<CancellationToken, Task<string>> statusProvider) =>
        AIFunctionFactory.Create(
            async (CancellationToken ct) => await statusProvider(ct),
            name: "fleet_status",
            description:
                "Report the connected machine fleet: for each bridge node — hardware (CPU, total RAM, "
                + "GPU/VRAM), live load (free RAM/VRAM, CPU/GPU utilization), and the model list of each "
                + "channel on that machine. Call this BEFORE splitting work across machines or choosing "
                + "which bridge or model should run a task, and pick a target whose free RAM or VRAM "
                + "plausibly fits the model you intend to use. Model entries are NAMES ONLY (no file "
                + "sizes): infer the size from the name (e.g. '27b', '8x7b', 'q4') and state that "
                + "assumption when you rely on it. Cross-machine execution may require user approval.");
}
