using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermDeck.Core.Ai;

/// <summary>
/// The surface the running app exposes to the in-app AI agent (through the loopback control server and the
/// MCP server). <see cref="Views.MainWindow"/> implements it; read methods return data, "mutating" methods
/// (run a tool, edit tools, change autorun) are gated behind a UI confirmation unless auto-approve is on.
/// </summary>
public interface IAppControl
{
    /// <summary>Dispatches one tool call by name. <paramref name="args"/> is the MCP tool arguments object.
    /// Returns any JSON-serializable object; throw <see cref="ControlException"/> for a clean error to the agent.</summary>
    Task<object?> CallAsync(string method, JsonElement args, CancellationToken ct);
}

/// <summary>An error meant to be shown to the agent as a tool error (not a crash).</summary>
public sealed class ControlException(string message) : System.Exception(message);
