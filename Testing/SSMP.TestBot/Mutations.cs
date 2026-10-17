using HarmonyLib;

namespace SSMP.TestBot;

/// <summary>
/// Deliberate breakages for mutation twin runs: every test must FAIL with the matching mutation applied,
/// otherwise the test proves nothing.
/// </summary>
internal static class Mutations {
    public static void Apply(TestConfig config, Harmony harmony) {
        foreach (var mutation in config.Mutations) {
            switch (mutation) {
                case "no_position_updates":
                    // The local player never sends its position, so remote clients can't see it move
                    harmony.Patch(
                        AccessTools.Method("SSMP.Networking.Client.ClientUpdateManager:UpdatePlayerPosition"),
                        prefix: new HarmonyMethod(typeof(Mutations), nameof(SkipPrefix))
                    );
                    break;
                default:
                    // An unknown mutation would make a twin run pass for the wrong reason
                    Bot.Fatal($"Unknown mutation '{mutation}'");
                    return;
            }

            Bot.Log($"Mutation applied: {mutation}");
        }
    }

    private static bool SkipPrefix() => false;
}
