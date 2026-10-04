using System.Text.Json;
using System.Text.Json.Serialization;
using Dianbo.Core.Models;

namespace Dianbo.Infrastructure.Storage;

public sealed class QueueStateRecord
{
    public List<Song> Songs { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public string PlayMode { get; set; } = "RepeatAll";
}

public sealed class QueueStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Location { get; set; }
    private readonly object _gate = new();

    public QueueStateStore(string path) => Location = path;

    public QueueStateRecord Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Location)) return new QueueStateRecord();
                var json = File.ReadAllText(Location);
                return JsonSerializer.Deserialize<QueueStateRecord>(json, Options) ?? new QueueStateRecord();
            }
            catch (Exception)
            {
                return new QueueStateRecord();
            }
        }
    }

    public void Save(QueueStateRecord state)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(Location);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temporary = Location + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
                File.Move(temporary, Location, overwrite: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
