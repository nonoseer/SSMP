using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SSMP.TestBot;

/// <summary>
/// Test run configuration, read from environment variables set by Testing/run-coop-test.sh.
/// </summary>
internal class TestConfig {
    /// <summary>
    /// Directory of this client's test slot. Saves, the bot log and the result file go here.
    /// </summary>
    public string SlotDir { get; private set; } = null!;

    /// <summary>
    /// Directory the game's saves are redirected to.
    /// </summary>
    public string SaveDir => Path.Combine(SlotDir, "saves");

    /// <summary>
    /// Directory shared by all clients of one run, used for barriers and position exchange.
    /// </summary>
    public string RunDir { get; private set; } = null!;

    /// <summary>
    /// The role of this client in the suite (e.g. "A" or "B").
    /// </summary>
    public string Role { get; private set; } = null!;

    /// <summary>
    /// Path to the suite JSON file.
    /// </summary>
    public string SuitePath { get; private set; } = null!;

    /// <summary>
    /// Number of clients in this run.
    /// </summary>
    public int Clients { get; private set; }

    /// <summary>
    /// Names of the mutations to apply (deliberately broken behaviour for mutation twin runs).
    /// </summary>
    public HashSet<string> Mutations { get; private set; } = new();

    /// <summary>
    /// Read the configuration from the environment.
    /// </summary>
    /// <returns>The config, or null if the test environment is not set and the bot must stay inactive.</returns>
    public static TestConfig? FromEnvironment() {
        var slotDir = Environment.GetEnvironmentVariable("SSMP_TEST_DIR");
        if (string.IsNullOrEmpty(slotDir)) {
            return null;
        }

        return new TestConfig {
            SlotDir = Path.GetFullPath(slotDir),
            RunDir = Path.GetFullPath(Require("SSMP_TEST_RUN_DIR")),
            Role = Require("SSMP_TEST_ROLE"),
            SuitePath = Require("SSMP_TEST_SUITE"),
            Clients = int.Parse(Require("SSMP_TEST_CLIENTS")),
            Mutations = new HashSet<string>(
                (Environment.GetEnvironmentVariable("SSMP_TEST_MUTATE") ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
            )
        };
    }

    private static string Require(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)) {
            throw new InvalidOperationException($"SSMP_TEST_DIR is set but {name} is missing");
        }

        return value!;
    }
}
