using System.Text.RegularExpressions;

namespace TestDbHub.Core;

public static partial class Names
{
    // Database, branch and table names: letters, digits, underscore. Keeping
    // them this strict lets us embed them in SQL for both MySQL and HeliosDB
    // without worrying about engine-specific quoting rules.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex NameRegex();

    public static bool IsValid(string? name) => name is not null && NameRegex().IsMatch(name);

    public static string Validate(string? name, string what = "Tên")
    {
        if (!IsValid(name))
            throw new TestDbException($"{what} không hợp lệ: chỉ dùng chữ, số, dấu _ (bắt đầu bằng chữ, tối đa 40 ký tự)");
        return name!;
    }

    public static string Timestamp(DateTime now) => now.ToString("yyyyMMdd_HHmmss");
}
