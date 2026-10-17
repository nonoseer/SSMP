using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SSMP.TestBot;

/// <summary>
/// Redirects the game's save directory into the test slot, so a test can never read or write the owner's saves.
/// The desktop platform keeps its save path in <c>DesktopPlatform.saveDirPath</c> and passes it on to the save
/// restore handler and the shared data objects when it awakes. We rewrite every one of those paths.
/// </summary>
internal static class SaveIsolation {
    private static TestConfig _config = null!;

    /// <summary>
    /// Whether the redirect was applied and verified.
    /// </summary>
    public static bool IsVerified { get; private set; }

    public static void Apply(TestConfig config, Harmony harmony) {
        _config = config;
        Directory.CreateDirectory(config.SaveDir);

        harmony.Patch(
            AccessTools.Method(typeof(DesktopPlatform), "Awake"),
            postfix: new HarmonyMethod(typeof(SaveIsolation), nameof(AwakePostfix))
        );

        // The platform may already exist when plugins load
        if (Platform.Current is DesktopPlatform desktopPlatform) {
            Redirect(desktopPlatform);
        }
    }

    private static void AwakePostfix(DesktopPlatform __instance) => Redirect(__instance);

    private static void Redirect(DesktopPlatform platform) {
        var realRoot = Path.GetFullPath(Application.persistentDataPath);
        var testRoot = _config.SaveDir;

        var rewritten = RewriteStrings(platform, realRoot, testRoot);
        foreach (var field in typeof(DesktopPlatform).GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                                                                BindingFlags.Public | BindingFlags.DeclaredOnly)) {
            if (field.FieldType == typeof(string) || field.FieldType.IsValueType) continue;

            var value = field.GetValue(platform);
            if (value != null) {
                rewritten += RewriteStrings(value, realRoot, testRoot);
            }
        }

        var saveDir = (string) AccessTools.Field(typeof(DesktopPlatform), "saveDirPath").GetValue(platform);
        Directory.CreateDirectory(saveDir);

        var slotPath = platform.GetSaveSlotPath(1, default);
        var restorePath = platform.SaveRestoreHandler is DesktopSaveRestoreHandler restoreHandler
            ? restoreHandler.GetDirectoryPath(1)
            : null;
        IsVerified = IsUnder(saveDir, testRoot) && IsUnder(slotPath, testRoot) &&
                     (restorePath == null || IsUnder(restorePath, testRoot));

        Bot.Log($"Save isolation: rewrote {rewritten} path(s), save dir {saveDir}, slot 1 path {slotPath}, " +
                $"restore path {restorePath ?? "none"}");
        if (!IsVerified) {
            Bot.Fatal($"Save isolation failed: saves would still go to {slotPath}");
        }
    }

    /// <summary>
    /// Rewrite every string field of the given object (and its base types) that points into the real save root.
    /// </summary>
    private static int RewriteStrings(object target, string realRoot, string testRoot) {
        var count = 0;
        for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType) {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                                                 BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                // readonly instance fields are rewritten too: the restore handler keeps its path in one
                if (field.FieldType != typeof(string) || field.IsLiteral) continue;

                if (field.GetValue(target) is not string value || !IsUnder(value, realRoot)) continue;

                var replaced = testRoot + Path.GetFullPath(value).Substring(realRoot.Length);
                field.SetValue(target, replaced);
                Bot.Log($"  {type.Name}.{field.Name}: {value} -> {replaced}");
                count++;
            }
        }

        return count;
    }

    private static bool IsUnder(string path, string root) {
        if (string.IsNullOrEmpty(path)) return false;

        var full = Path.GetFullPath(path).TrimEnd('/');
        root = root.TrimEnd('/');
        return full == root || full.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
