using TestDbHub.Core.Engines;

namespace TestDbHub.Core;

public static class EngineFactory
{
    public static IReadOnlyList<DbEngine> Create(ConnectionSettings s)
    {
        var engines = new List<DbEngine>();
        if (s.LocalEnabled) engines.Add(new SqliteEngine(s.LocalFolder));
        if (s.MySqlEnabled) engines.Add(new MySqlEngine(s.MySqlHost, s.MySqlPort, s.MySqlUser, s.MySqlPassword));
        if (s.HeliosEnabled) engines.Add(new HeliosEngine(s.HeliosHost, s.HeliosPort, s.HeliosUser, s.HeliosPassword, s.HeliosDatabase));
        return engines;
    }
}
