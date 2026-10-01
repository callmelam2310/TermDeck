using System;
using System.Threading;
using TermDeck.Core.Ai;

namespace TermDeck;

/// <summary>
/// Entry point. Normally starts the WPF app, but <c>TermDeck.exe --mcp</c> runs the headless MCP server
/// (stdio JSON-RPC) that the in-app AI agent spawns and that forwards tool calls to the running app's
/// loopback control server. Keeping it in the same exe means the agent needs no Python or extra install.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--mcp")
            return McpServer.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
