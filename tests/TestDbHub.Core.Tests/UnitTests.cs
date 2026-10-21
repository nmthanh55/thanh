using TestDbHub.Core;
using TestDbHub.Core.Sessions;

namespace TestDbHub.Core.Tests;

public class SqlSplitterTests
{
    [Fact]
    public void SplitsOnSemicolonsOutsideQuotesAndComments()
    {
        const string sql = """
            -- comment; not a statement
            INSERT INTO t VALUES ('a;b', "c;d", 'it''s');
            /* block; comment */ SELECT `x;y` FROM t # trailing; comment
            ;
            SELECT 'back\'slash;'
            """;
        Assert.Equal(
            ["INSERT INTO t VALUES ('a;b', \"c;d\", 'it''s')", "SELECT `x;y` FROM t", "SELECT 'back\\'slash;'"],
            SqlSplitter.Split(sql));
    }

    [Fact]
    public void IgnoresEmptyStatements() => Assert.Empty(SqlSplitter.Split(" ; ;\n"));

    [Fact]
    public void KeepsUnterminatedStringAsLastStatement() =>
        Assert.Equal(["SELECT 'abc"], SqlSplitter.Split("SELECT 'abc"));
}

public class NamesTests
{
    [Theory]
    [InlineData("shop_test1")]
    [InlineData("A")]
    public void AcceptsSimpleIdentifiers(string name) => Assert.Equal(name, Names.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("a`b")]
    [InlineData("a\"b")]
    [InlineData(null)]
    public void RejectsOtherNames(string? name) => Assert.Throws<TestDbException>(() => Names.Validate(name));

    [Fact]
    public void RejectsLongNames() => Assert.Throws<TestDbException>(() => Names.Validate(new string('x', 41)));
}

public class PortableSqlTests
{
    [Theory]
    [InlineData(PortableType.Int, SqlDialect.MySql, "INT")]
    [InlineData(PortableType.DateTime, SqlDialect.MySql, "DATETIME(6)")]
    [InlineData(PortableType.DateTime, SqlDialect.Helios, "TIMESTAMP")]
    [InlineData(PortableType.Text, SqlDialect.MySql, "LONGTEXT")]
    [InlineData(PortableType.Text, SqlDialect.Helios, "TEXT")]
    [InlineData(PortableType.Binary, SqlDialect.Helios, "BYTEA")]
    public void MapsTypesPerDialect(PortableType type, SqlDialect dialect, string expected) =>
        Assert.Equal(expected, PortableSql.ColumnType(new PortableColumn("c", type), dialect));

    [Fact]
    public void KeepsReportedDecimalPrecision() =>
        Assert.Equal("DECIMAL(10,2)", PortableSql.ColumnType(new PortableColumn("c", PortableType.Decimal, 10, 2), SqlDialect.MySql));

    [Fact]
    public void InfersDecimalPrecisionFromData()
    {
        var cols = PortableSql.InferDecimals([new PortableColumn("c", PortableType.Decimal)], [[12345.678m], [1.5m], [null]]);
        Assert.Equal("DECIMAL(18,3)", PortableSql.ColumnType(cols[0], SqlDialect.MySql));
    }

    [Theory]
    [InlineData("it's", SqlDialect.MySql, @"'it''s'")]
    [InlineData(@"a\b", SqlDialect.MySql, @"'a\\b'")]
    [InlineData(@"a\b", SqlDialect.Helios, @"'a\b'")]
    public void QuotesStrings(string value, SqlDialect dialect, string expected) =>
        Assert.Equal(expected, PortableSql.Literal(value, null, dialect));

    [Fact]
    public void FormatsOtherLiterals()
    {
        Assert.Equal("NULL", PortableSql.Literal(null, null, SqlDialect.MySql));
        Assert.Equal("1", PortableSql.Literal(true, null, SqlDialect.MySql));
        Assert.Equal("TRUE", PortableSql.Literal(true, null, SqlDialect.Helios));
        Assert.Equal("9.99", PortableSql.Literal(9.99m, null, SqlDialect.MySql));
        Assert.Equal("X'DEAD'", PortableSql.Literal(new byte[] { 0xDE, 0xAD }, null, SqlDialect.MySql));
        Assert.Equal(@"'\xDEAD'", PortableSql.Literal(new byte[] { 0xDE, 0xAD }, null, SqlDialect.Helios));
        Assert.Equal("'2024-01-02'", PortableSql.Literal(new DateTime(2024, 1, 2), new PortableColumn("d", PortableType.Date), SqlDialect.MySql));
        Assert.Equal("'2024-01-02 03:04:05.000000'", PortableSql.Literal(new DateTime(2024, 1, 2, 3, 4, 5), null, SqlDialect.Helios));
        Assert.Equal("'-27:00:01'", PortableSql.Literal(-new TimeSpan(1, 3, 0, 1), null, SqlDialect.MySql));
    }
}

public class PgSimpleSessionTests
{
    [Fact]
    public void ConvertsTextValuesByTypeOid()
    {
        Assert.Equal(true, PgSimpleSession.ConvertText("t", 16));
        Assert.Equal(42, PgSimpleSession.ConvertText("42", 23));
        Assert.Equal(9007199254740993L, PgSimpleSession.ConvertText("9007199254740993", 20));
        Assert.Equal(9.99m, PgSimpleSession.ConvertText("9.99", 1700));
        Assert.Equal(new byte[] { 0xDE, 0xAD }, PgSimpleSession.ConvertText(@"\xdead", 17));
        Assert.Equal("2024-01-02", PgSimpleSession.ConvertText("2024-01-02", 1082));
    }

    [Theory]
    [InlineData("INSERT 0 3", 3)]
    [InlineData("UPDATE 2", 2)]
    [InlineData("OK 0", 0)]
    [InlineData("CREATE TABLE", 0)]
    public void ParsesCommandTags(string tag, long expected) => Assert.Equal(expected, PgSimpleSession.ParseAffected(tag));
}
