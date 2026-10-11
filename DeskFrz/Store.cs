using System.Text.Json;

namespace DeskFrz;

public sealed class Store(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public Library Read()
    {
        var file = Path.Combine(DirectoryPath, "library.json");
        if (!File.Exists(file)) return new Library();
        var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("布局记录为空。请保留原文件并检查。");
        if (library.SchemaVersion != 1 || library.Current is null || library.History is null || library.Settings is null)
            throw new InvalidDataException("布局记录格式不受支持。");
        if (library.Current.Select(s => s.ConfigKey).Distinct().Count() != library.Current.Count)
            throw new InvalidDataException("布局记录包含重复配置。");
        Renumber(library);
        return library;
    }
    static void Renumber(Library library)
    {
        var numbers = library.Records.GroupBy(s => s.IsSingleScreen).SelectMany(group =>
            group.OrderBy(s => s.SavedAt).ThenBy(s => s.Number).ThenBy(s => s.Id)
                .Select((s, index) => (s.Id, Number: index + 1))).ToDictionary(s => s.Id, s => s.Number);
        library.Current = library.Current.Select(s => s with { Number = numbers[s.Id] }).ToList();
        library.History = library.History.Select(s => s with { Number = numbers[s.Id] }).ToList();
        library.NextRecordNumber = numbers.Count + 1;
    }
    public void Write(Library library)
    {
        Renumber(library);
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, "library.json");
        var temporary = Path.Combine(DirectoryPath, $"library.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, library, Json);
                stream.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Save(Snapshot snapshot, bool select = true)
    {
        var library = Read();
        var old = library.Current.SingleOrDefault(s => s.ConfigKey == snapshot.ConfigKey);
        snapshot = snapshot with { Number = library.NextRecordNumber++ };
        if (old is not null) { library.History.Add(old); library.Current.Remove(old); }
        library.Current.Add(snapshot);
        if (select) {
            library.ActiveId = snapshot.Id;
            library.SelectionInitialized = true;
            library.ActiveBaseline = snapshot;
        }
        Write(library);
    }
    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60) throw new ArgumentException("名称应为 1–60 个字符。");
        var library = Read();
        var list = library.Current.Any(s => s.Id == id) ? library.Current : library.History;
        var index = list.FindIndex(s => s.Id == id);
        if (index < 0) throw new InvalidOperationException("未找到该布局记录。");
        list[index] = list[index] with { Name = name };
        Write(library);
    }
    public void Delete(string id)
    {
        var library = Read();
        if (library.DefaultSingleId == id)
            throw new InvalidOperationException("请先将另一套单屏布局设为默认，再删除这一套。");
        if (library.Current.RemoveAll(s => s.Id == id) + library.History.RemoveAll(s => s.Id == id) == 0)
            throw new InvalidOperationException("未找到该布局记录。");
        if (library.ActiveId == id) { library.ActiveId = null; library.ActiveBaseline = null; }
        library.SelectionInitialized = true;
        if (library.UndoActiveId == id) { library.UndoActiveId = null; library.UndoActiveBaseline = null; }
        Write(library);
    }
    public void SetDefaultSingle(string id)
    {
        var library = Read();
        var saved = library.Records.SingleOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("未找到该布局记录。");
        if (!saved.IsSingleScreen) throw new InvalidOperationException("请选择单屏布局。");
        library.DefaultSingleId = saved.Id;
        Write(library);
    }
}
