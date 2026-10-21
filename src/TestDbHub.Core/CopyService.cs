using TestDbHub.Core.Engines;

namespace TestDbHub.Core;

public static class CopyService
{
    /// <summary>
    /// Copy tables between any two locations, e.g. from a MySQL test database
    /// into a HeliosDB branch or back. Tables with the same name at the
    /// destination are replaced. A copy within one engine keeps the exact
    /// table definition; across engines column types are mapped.
    /// </summary>
    public static async Task<IReadOnlyList<CopiedTable>> CopyTablesAsync(
        DbEngine source, Location from, DbEngine target, Location to, IReadOnlyList<string>? tables = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (source == target && from == to) throw new TestDbException("Nguồn và đích trùng nhau");
        var names = tables is { Count: > 0 }
            ? tables
            : (await source.ListTablesAsync(from.Database, from.Branch, ct)).Where(t => !t.IsView).Select(t => t.Name).ToList();
        var copied = new List<CopiedTable>();
        foreach (var table in names)
        {
            Names.Validate(table, "Tên bảng");
            progress?.Report($"Đang sao chép {table}...");
            var data = await source.ReadTableAsync(from.Database, from.Branch, table, ct);
            var ddl = source == target ? await source.GetTableDdlAsync(from.Database, from.Branch, table, data.Columns, ct) : null;
            await target.WriteTableAsync(to.Database, to.Branch, table, data, ddl, ct);
            copied.Add(new CopiedTable(table, data.Rows.Count));
        }
        return copied;
    }
}
