namespace TestDbHub.Core;

public static class SampleData
{
    /// <summary>Sample script that runs on SQLite, MySQL and HeliosDB-Nano.</summary>
    public const string Sql = """
        -- Dữ liệu mẫu: chạy được trên SQLite (tích hợp), MySQL và HeliosDB-Nano
        CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(100), email VARCHAR(120), city VARCHAR(60));
        CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT, total DECIMAL(10,2), created_at DATE);

        INSERT INTO customers VALUES
          (1, 'Nguyễn Văn An', 'an@example.com', 'Hà Nội'),
          (2, 'Trần Thị Bình', 'binh@example.com', 'TP.HCM'),
          (3, 'Lê Minh Châu', 'chau@example.com', 'Đà Nẵng'),
          (4, 'Phạm Quốc Dũng', 'dung@example.com', 'Hà Nội');

        INSERT INTO orders VALUES
          (1, 1, 250000.00, '2026-01-05'),
          (2, 1, 120000.00, '2026-02-11'),
          (3, 2, 990000.00, '2026-02-14'),
          (4, 3, 75000.00, '2026-03-01'),
          (5, 4, 430000.00, '2026-03-09');

        SELECT c.name, COUNT(o.id) AS so_don, SUM(o.total) AS tong_tien
        FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
        GROUP BY c.name
        ORDER BY tong_tien DESC;
        """;
}
