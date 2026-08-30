using JET.Domain.Enums;

namespace JET.Infrastructure.Configuration
{
    public sealed class JetAppOptions
    {
        public HostOptions Host { get; set; } = new();
        public DatabaseOptions Database { get; set; } = new();
        public DemoOptions Demo { get; set; } = new();
    }

    public sealed class HostOptions
    {
        public string Title { get; set; } = "JET - Journal Entry Testing";
        public string VirtualHostName { get; set; } = "jet.app.local";
        public string StartPage { get; set; } = "index.html";

        public string StartPageUrl => $"https://{VirtualHostName}/{StartPage.TrimStart('/')}";
    }

    public sealed class DatabaseOptions
    {
        public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
        public string SqliteConnectionString { get; set; } = "Data Source=%LOCALAPPDATA%\\JET\\project.db";
        public string SqlServerConnectionString { get; set; } = string.Empty;
    }

    public sealed class DemoOptions
    {
        public bool Enabled { get; set; } = false;
    }
}
