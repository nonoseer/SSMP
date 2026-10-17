using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SSMP.Api.Client;
using UnityEngine;

namespace SSMP.TestBot;

/// <summary>
/// Entry point of the test bot. Inactive unless the test launcher set SSMP_TEST_DIR.
/// </summary>
[BepInPlugin("ssmp.testbot", "SSMP TestBot", "0.1.0")]
[BepInDependency("ssmp")]
public class TestBotPlugin : BaseUnityPlugin {
    internal static ManualLogSource? LogSource;

    private void Awake() {
        LogSource = Logger;

        TestConfig? config;
        try {
            config = TestConfig.FromEnvironment();
        } catch (Exception e) {
            Logger.LogError($"Invalid test environment, quitting: {e.Message}");
            Application.Quit();
            return;
        }

        if (config == null) {
            Logger.LogInfo("SSMP_TEST_DIR not set, test bot inactive");
            return;
        }

        Bot.Init(config);
        Bot.Log($"Test bot active: role {config.Role}, slot {config.SlotDir}");

        var harmony = new Harmony("ssmp.testbot");
        SaveIsolation.Apply(config, harmony);
        ApplyBackgroundPatches(harmony);
        Mutations.Apply(config, harmony);

        ClientAddon.RegisterAddon(new TestBotAddon());

        gameObject.AddComponent<Bot>();
    }

    /// <summary>
    /// Keep every client running, silent and free of input prompts while its window is not focused.
    /// </summary>
    private static void ApplyBackgroundPatches(Harmony harmony) {
        Application.runInBackground = true;
        AudioListener.volume = 0f;

        harmony.Patch(
            AccessTools.Method(typeof(UIManager), "OnApplicationFocus"),
            prefix: new HarmonyMethod(typeof(TestBotPlugin), nameof(SkipPrefix))
        );

        // Test copies start at the first-launch language picker, which waits for input
        harmony.Patch(
            AccessTools.Method(typeof(StartManager), "CheckIsLanguageSet"),
            prefix: new HarmonyMethod(typeof(TestBotPlugin), nameof(LanguageSetPrefix))
        );
    }

    private static bool SkipPrefix() => false;

    private static bool LanguageSetPrefix(ref bool __result) {
        __result = true;
        return false;
    }
}

/// <summary>
/// Client addon that hands the SSMP client API to the bot. Not networked, so servers don't need it.
/// </summary>
internal class TestBotAddon : ClientAddon {
    protected override string Name => "TestBot";
    protected override string Version => "0.1.0";
    public override bool NeedsNetwork => false;
    public override uint ApiVersion => 1;

    public override void Initialize(IClientApi clientApi) {
        Bot.ClientApi = clientApi;
        clientApi.ClientManager.ConnectEvent += () => {
            Bot.IsConnected = true;
            Bot.Log("SSMP connect event received");
        };
        clientApi.ClientManager.DisconnectEvent += () => {
            Bot.IsConnected = false;
            Bot.Log("SSMP disconnect event received");
        };
        Bot.Log("SSMP client API received");
    }
}
