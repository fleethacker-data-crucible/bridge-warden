// Where the app's files live. Start-BridgeWarden.ps1 sets Paths.Root to the app folder before anything else runs.
// Config, scenarios, skills and fixtures are shared with the Python backend while the port is in progress.
using System.IO;

namespace BridgeWarden
{
    public static class Paths
    {
        public static string Root = Directory.GetCurrentDirectory();

        public static string Backend { get { return Path.Combine(Root, "backend"); } }
        public static string Config { get { return Path.Combine(Backend, "config"); } }        // agents.json, policies.json
        public static string Scenarios { get { return Path.Combine(Backend, "scenarios"); } }  // <name>.json
        public static string Skills { get { return Path.Combine(Backend, "skills"); } }        // <role>.md, lessons\<role>.md
        public static string Fixtures { get { return Path.Combine(Backend, "fixtures"); } }    // llm_cache.json
        public static string Data { get { return Path.Combine(Backend, "data"); } }            // ledger.db (gitignored)
        public static string EnvFile { get { return Path.Combine(Backend, ".env"); } }         // settings (gitignored)
        public static string Frontend { get { return Path.Combine(Root, "frontend"); } }

        public static string ReadText(string path) { return File.ReadAllText(path, System.Text.Encoding.UTF8); }
    }
}
