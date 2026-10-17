using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSMP.Api.Client;
using SSMP.Networking.Transport.Common;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMP.TestBot;

/// <summary>
/// Runs the steps of a test suite for this client's role and writes PASS or FAIL to the slot's result file.
/// Steps act on the game the way the game itself does (loading a save, moving the hero), never through faked input.
/// </summary>
internal class Bot : MonoBehaviour {
    /// <summary>
    /// The client API, set once SSMP initializes its addons at the main menu.
    /// </summary>
    public static IClientApi? ClientApi { get; set; }

    /// <summary>
    /// Whether the client has received the server info after connecting.
    /// </summary>
    public static bool IsConnected { get; set; }

    private static TestConfig _config = null!;
    private static bool _finished;
    private static StreamWriter? _log;

    public static void Init(TestConfig config) {
        _config = config;
        Directory.CreateDirectory(config.SlotDir);
        _log = new StreamWriter(Path.Combine(config.SlotDir, "testbot.log"), append: false) { AutoFlush = true };
        File.Delete(ResultPath);
    }

    private static string ResultPath => Path.Combine(_config.SlotDir, "result.txt");

    private string Username => "Bot" + _config.Role;

    public static void Log(string message) {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{_config?.Role}] {message}";
        _log?.WriteLine(line);
        TestBotPlugin.LogSource?.LogInfo(message);
    }

    /// <summary>
    /// Fail the test immediately and quit the game.
    /// </summary>
    public static void Fatal(string reason) => Finish(false, reason);

    private static void Finish(bool pass, string reason) {
        if (_finished) return;
        _finished = true;

        var result = pass ? "PASS" : $"FAIL: {reason}";
        Log($"RESULT: {result}");
        if (!pass) {
            Capture("fail");
        }
        File.WriteAllText(ResultPath, result + "\n");

        try {
            ClientApi?.ClientManager.Disconnect();
        } catch (Exception e) {
            Log($"Disconnect on finish threw: {e.Message}");
        }

        // Give the failure screenshot a few frames to be written before quitting
        if (_instance != null) {
            _instance.StartCoroutine(QuitSoon());
        } else {
            Application.Quit();
        }
    }

    private static IEnumerator QuitSoon() {
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
    }

    private static Bot? _instance;

    private void Start() {
        _instance = this;
        StartCoroutine(Run());
    }

    private IEnumerator Run() {
        JObject suite;
        JArray steps;
        try {
            suite = JObject.Parse(File.ReadAllText(_config.SuitePath));
            steps = (JArray?) suite["steps"]?[_config.Role]
                    ?? throw new InvalidDataException($"Suite has no steps for role {_config.Role}");
        } catch (Exception e) {
            Fatal($"Could not read suite {_config.SuitePath}: {e.Message}");
            yield break;
        }

        Log($"Suite '{suite["name"]}', role {_config.Role}, {steps.Count} steps, " +
            $"mutations [{string.Join(",", _config.Mutations)}]");

        for (var i = 0; i < steps.Count; i++) {
            var step = (JObject) steps[i];
            var op = (string?) step["op"] ?? "";
            Log($"Step {i + 1}/{steps.Count}: {step.ToString(Formatting.None)}");

            var routine = op switch {
                "wait_menu" => WaitMenu(step),
                "load_slot" => LoadSlot(step),
                "connect" => Connect(step),
                "barrier" => Barrier(step),
                "wait" => Wait(step),
                "move" => Move(step),
                "publish_position" => PublishPosition(step),
                "assert_players_in_scene" => AssertPlayersInScene(step),
                "assert_remote_at" => AssertRemoteAt(step),
                _ => null
            };

            if (routine == null) {
                Fatal($"Unknown op '{op}' in step {i + 1}");
                yield break;
            }

            yield return routine;
            if (_finished) yield break;
        }

        Finish(true, "");
    }

    #region Steps

    private IEnumerator WaitMenu(JObject step) {
        yield return Until(
            () => SaveIsolation.IsVerified && ClientApi != null && GameManager.instance != null &&
                  GameManager.instance.GameState == GameState.MAIN_MENU,
            Timeout(step, 120),
            "main menu with SSMP initialized and save isolation verified"
        );
        yield return new WaitForSecondsRealtime(3f);
    }

    private IEnumerator LoadSlot(JObject step) {
        var slot = (int?) step["slot"] ?? 1;
        Log($"Loading save slot {slot}");
        GameManager.instance.LoadGameFromUI(slot);

        yield return Until(
            () => GameManager.instance.GameState == GameState.PLAYING && HeroController.instance != null &&
                  HeroController.instance.acceptingInput,
            Timeout(step, 120),
            $"hero in game from slot {slot}"
        );
        yield return new WaitForSecondsRealtime(2f);
        Log($"In scene {SceneManager.GetActiveScene().name} at {HeroPosition}");
    }

    private IEnumerator Connect(JObject step) {
        var address = (string?) step["address"] ?? "127.0.0.1";
        var port = (int?) step["port"] ?? 26960;

        // SSMP has no public connect API yet, so raise the same event the connect menu raises. Like a player, the
        // bot connects from the main menu; SSMP then opens save slot selection, which the load_slot step answers.
        // (Connecting from in-game is not supported by SSMP: the server never learns the client's scene.)
        if (GameManager.instance.GameState != GameState.MAIN_MENU) {
            Fatal("connect must run at the main menu, before load_slot");
            yield break;
        }

        var uiManager = ClientApi!.UiManager;
        var field = uiManager.GetType().GetField(
            "RequestClientConnectEvent", BindingFlags.Instance | BindingFlags.NonPublic
        );
        if (field?.GetValue(uiManager) is not Delegate connectEvent) {
            Fatal("Could not find SSMP's RequestClientConnectEvent");
            yield break;
        }

        Log($"Connecting to {address}:{port} as {Username}");
        connectEvent.DynamicInvoke(address, port, Username, TransportType.Udp, false, null);

        yield return Until(() => IsConnected, Timeout(step, 30), $"connected to {address}:{port}");
    }

    private IEnumerator Barrier(JObject step) {
        var name = (string) step["name"]!;
        var dir = Path.Combine(_config.RunDir, "barriers");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.{_config.Role}"), DateTime.Now.ToString("O"));

        yield return Until(
            () => Directory.GetFiles(dir, $"{name}.*").Length >= _config.Clients,
            Timeout(step, 180),
            $"all {_config.Clients} clients at barrier '{name}'"
        );
    }

    private IEnumerator Wait(JObject step) {
        yield return new WaitForSecondsRealtime((float?) step["seconds"] ?? 1f);
    }

    private IEnumerator Move(JObject step) {
        var dx = (float?) step["dx"] ?? 0f;
        var dy = (float?) step["dy"] ?? 0f;
        var hero = HeroController.instance;
        var start = hero.transform.position;
        var target = start + new Vector3(dx, dy);

        hero.transform.position = target;
        yield return new WaitForSecondsRealtime((float?) step["settle"] ?? 2f);

        // Precondition: the hero really moved. A teleport into a wall silently fails and makes later asserts lie.
        var end = hero.transform.position;
        Log($"Moved hero from {start} to {end} (target {target})");
        if (Mathf.Abs(end.x - target.x) > 0.5f) {
            Fatal($"Move failed: hero at {end}, expected x {target.x}");
        }
    }

    private IEnumerator PublishPosition(JObject step) {
        var key = (string) step["key"]!;
        var dir = Path.Combine(_config.RunDir, "positions");
        Directory.CreateDirectory(dir);

        var position = HeroPosition;
        var data = new JObject {
            ["x"] = position.x,
            ["y"] = position.y,
            ["scene"] = SceneManager.GetActiveScene().name
        };
        File.WriteAllText(Path.Combine(dir, $"{key}.json"), data.ToString());
        Log($"Published position '{key}': {data.ToString(Formatting.None)}");
        yield break;
    }

    private IEnumerator AssertPlayersInScene(JObject step) {
        var count = (int?) step["count"] ?? 1;
        yield return Until(
            () => ClientApi!.ClientManager.Players.Count(p => p.IsInLocalScene) >= count,
            Timeout(step, 30),
            $"{count} remote player(s) in local scene"
        );
    }

    private IEnumerator AssertRemoteAt(JObject step) {
        var username = "Bot" + (string) step["player"]!;
        var key = (string) step["key"]!;
        var tolerance = (float?) step["tolerance"] ?? 1f;
        var timeout = Timeout(step, 20);
        var file = Path.Combine(_config.RunDir, "positions", $"{key}.json");

        yield return Until(() => File.Exists(file), timeout, $"position '{key}' published");
        if (_finished) yield break;

        var expected = JObject.Parse(File.ReadAllText(file));
        var expectedPosition = new Vector2((float) expected["x"]!, (float) expected["y"]!);

        Vector2? last = null;
        yield return Until(() => {
                last = RemotePosition(username);
                return last.HasValue && Vector2.Distance(last.Value, expectedPosition) <= tolerance;
            },
            timeout,
            () => $"{username} seen at {expectedPosition} (+-{tolerance}); last seen at " +
                  (last.HasValue ? last.Value.ToString() : "nowhere")
        );
        if (!_finished) {
            Log($"Saw {username} at {last} (expected {expectedPosition})");
        }
    }

    #endregion

    #region Helpers

    private static Vector3 HeroPosition => HeroController.instance.transform.position;

    private static Vector2? RemotePosition(string username) {
        var player = ClientApi!.ClientManager.Players.FirstOrDefault(p => p.Username == username);
        if (player is not { IsInLocalScene: true }) return null;

        var obj = player.PlayerContainer != null ? player.PlayerContainer : player.PlayerObject;
        if (obj == null || !obj.activeInHierarchy) return null;

        return obj.transform.position;
    }

    /// <summary>
    /// Save an in-game screenshot to the slot folder (the game's own frame, never the desktop).
    /// </summary>
    private static void Capture(string label) {
        try {
            var path = Path.Combine(_config.SlotDir, $"{DateTime.Now:HHmmss}-{label}.png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"Screenshot {path}");
        } catch (Exception e) {
            Log($"Screenshot failed: {e.Message}");
        }
    }

    private static string GameStateDescription() {
        var gm = GameManager.instance;
        return $"scene {SceneManager.GetActiveScene().name}, game state {(gm != null ? gm.GameState.ToString() : "no GameManager")}, " +
               $"hero {(HeroController.instance != null ? "present" : "none")}, ssmp {(ClientApi != null ? "ready" : "not ready")}";
    }

    private static float Timeout(JObject step, float fallback) => (float?) step["timeout"] ?? fallback;

    private static IEnumerator Until(Func<bool> condition, float timeout, string description) =>
        Until(condition, timeout, () => description);

    /// <summary>
    /// Poll a condition every frame until it holds, failing the test when the timeout passes.
    /// </summary>
    private static IEnumerator Until(Func<bool> condition, float timeout, Func<string> description) {
        var deadline = Time.realtimeSinceStartup + timeout;
        var nextReport = Time.realtimeSinceStartup + 15f;
        while (true) {
            bool holds;
            try {
                holds = condition();
            } catch (Exception e) {
                Fatal($"Exception while waiting for {description()}: {e}");
                yield break;
            }

            if (holds) yield break;

            if (Time.realtimeSinceStartup > nextReport) {
                nextReport += 15f;
                Log($"Still waiting for {description()}: {GameStateDescription()}");
                Capture("waiting");
            }

            if (Time.realtimeSinceStartup > deadline) {
                Fatal($"Timed out after {timeout}s waiting for {description()}");
                yield break;
            }

            yield return null;
        }
    }

    #endregion
}
