# TestDB Hub (WPF)

Ứng dụng desktop **WPF (C# / .NET 8)** để lưu **database test** trên **MySQL**, nhưng quản lý theo kiểu
**HeliosDB-Nano**: mỗi database có nhánh `main`. Bạn có thể tạo nhánh để thử nghiệm, chụp snapshot, khôi phục,
merge, chạy SQL, và sao chép bảng qua lại giữa MySQL và HeliosDB-Nano.

## Tính năng

| Tính năng | MySQL | HeliosDB-Nano |
|---|---|---|
| Tạo / xoá database test | ✅ nhiều database | Một database (`heliosdb`), dùng nhánh thay thế |
| Nhánh (branch) | ✅ giả lập: sao chép bảng sang schema `<db>__<nhánh>` | ✅ nhánh thật (`CREATE DATABASE BRANCH`) |
| Snapshot + khôi phục | ✅ snapshot `snap_<thời gian>`, khôi phục = ghi đè main | Dùng time-travel thay thế |
| Time-travel (`AS OF TIMESTAMP`) | — | ✅ xem bảng tại một thời điểm, tạo nhánh từ quá khứ |
| Merge nhánh vào main | Ghi đè main bằng nội dung nhánh | `MERGE BRANCH … INTO main` |
| Trình soạn SQL, chạy nhiều câu, kết quả dạng bảng | ✅ | ✅ |
| Xem bảng, cấu trúc cột | ✅ | ✅ |
| Xuất / mở file `.sql` | ✅ | ✅ |
| Sao chép bảng MySQL ⇄ HeliosDB | ✅ | ✅ |

## Tải và chạy

### Cách 1: tải file `.exe` có sẵn
Mỗi lần đẩy code, GitHub Actions build sẵn `TestDbHub.exe`. Vào tab **Actions** → lần chạy **build** mới nhất →
mục **Artifacts** → tải **TestDbHub-win-x64**. File đã kèm .NET nên chạy ngay trên Windows 10/11 x64.

### Cách 2: build từ mã nguồn
Cần [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) trên Windows (hoặc Visual Studio 2022):

```powershell
git clone https://github.com/nmthanh55/thanh.git
cd thanh
dotnet run --project src/TestDbHub.Wpf
```

Hoặc mở `TestDbHub.sln` bằng Visual Studio rồi nhấn F5.

## Chuẩn bị máy chủ database

Cách nhanh nhất là dùng Docker:

```bash
docker compose up -d     # lần đầu build HeliosDB-Nano mất 10–20 phút
```

Lệnh này mở MySQL tại `127.0.0.1:3306` (root / `testpass`) và HeliosDB-Nano tại `127.0.0.1:5432` (giao thức
PostgreSQL). Không dùng Docker thì:

- **MySQL**: dùng MySQL 8 có sẵn.
- **HeliosDB-Nano**: `cargo install heliosdb-nano --locked`, hoặc tải binary ở
  [GitHub Releases](https://github.com/HeliosDatabase/HeliosDB-Nano/releases), rồi chạy
  `heliosdb-nano start --data-dir ./helios-data --http-port 0`.

Mở app, bấm **⚙ Kết nối** để nhập máy chủ, cổng, tài khoản và mật khẩu. Cấu hình lưu ở
`%AppData%\TestDbHub\settings.json`; mật khẩu được mã hoá bằng Windows DPAPI.

## Cách dùng

1. Bấm **+ Database** để tạo database test trên MySQL, ví dụ `shop_test`.
2. Bấm **Dữ liệu mẫu** rồi **▶ Chạy** (Ctrl+Enter hoặc F5) để tạo bảng `customers` và `orders`.
3. Bấm **⑂ Tạo nhánh** `thu_migration` rồi chạy thử câu lệnh nguy hiểm (`UPDATE`, `ALTER`, `DELETE`…) trên
   nhánh. Nhánh `main` không bị ảnh hưởng.
4. Nếu kết quả ổn, bấm **⤴ Ghi đè main** (MySQL) hoặc **⤴ Merge vào main** (HeliosDB). Nếu không ổn, bấm
   **✕ Xoá nhánh**.
5. Trên `main` của MySQL, bấm **📸 Snapshot** trước khi thay đổi lớn. Muốn quay lại, chọn snapshot rồi bấm
   **↺ Khôi phục vào main**.
6. **⇄ Sao chép sang...** chép bảng sang engine, database hoặc nhánh khác, ví dụ đưa dữ liệu test từ MySQL sang
   một nhánh HeliosDB.
7. Trên HeliosDB, **🕒 Time-travel** xem dữ liệu của một bảng tại một thời điểm trong quá khứ.

Mẹo: bấm vào tên bảng để xem dữ liệu, **Shift + bấm** để xem cấu trúc cột.

## Giới hạn cần biết

**MySQL**
- Nhánh là bản sao đầy đủ (dùng `CREATE TABLE … LIKE` và `INSERT … SELECT`). Khoá ngoại, view, trigger và
  procedure không được sao chép. Phù hợp với dữ liệu test cỡ nhỏ và vừa.
- "Ghi đè main" thay toàn bộ bảng của main bằng bảng của nhánh. Đây không phải merge từng dòng.

**HeliosDB-Nano 4.41** (đã kiểm tra thực tế khi viết app)
- App kết nối qua giao thức PostgreSQL bằng một client nhỏ tự viết, dùng *simple query protocol*. Lý do: lệnh
  nhánh chỉ chạy ở chế độ này, còn giao thức MySQL của HeliosDB làm treo driver .NET.
- `USE BRANCH` có tác dụng trên **toàn server** chứ không riêng từng kết nối. App luôn chọn nhánh ở đầu mỗi
  thao tác rồi trả về `main`. Tránh để hai chương trình cùng dùng một HeliosDB trên hai nhánh khác nhau cùng lúc.
- Định nghĩa bảng dùng chung giữa các nhánh: bảng tạo trong nhánh cũng xuất hiện (rỗng) ở `main`. Dữ liệu từng
  dòng vẫn tách riêng theo nhánh.
- `MERGE BRANCH` theo kiểu last-writer-wins và không phát hiện xung đột.
- Time-travel hiện giá trị cũ của dòng bị `UPDATE` và ẩn dòng thêm sau thời điểm chọn, nhưng **không** hiện lại
  dòng đã `DELETE`.

**Chung**
- Sao chép giữa hai engine quy đổi kiểu dữ liệu sang kiểu tương đương (INT, BIGINT, DECIMAL, TEXT,
  DATE/TIMESTAMP, JSON…). Khoá chính và chỉ mục không được chép theo. Sao chép trong cùng một engine thì giữ
  nguyên định nghĩa bảng.
- App dành cho môi trường dev/test: trình soạn SQL chạy mọi câu lệnh bạn nhập.

## Cấu trúc mã nguồn

```
TestDbHub.sln
src/
  TestDbHub.Core/             thư viện dùng chung (net8.0), không phụ thuộc giao diện
    Engines/MySqlEngine.cs    MySQL: database, nhánh và snapshot giả lập (MySqlConnector)
    Engines/HeliosEngine.cs   HeliosDB-Nano: nhánh thật, merge, time-travel
    Engines/DbEngine.cs       phần chung: chạy SQL, đọc/ghi bảng, xuất .sql
    Sessions/PgSimpleSession.cs  client PostgreSQL simple-query (trust/MD5/SCRAM)
    CopyService.cs            sao chép bảng giữa các engine
    PortableSql.cs            quy đổi kiểu dữ liệu và literal SQL
    SqlSplitter.cs            tách script thành từng câu lệnh
  TestDbHub.Wpf/              ứng dụng WPF (net8.0-windows, MVVM với CommunityToolkit.Mvvm)
    Views/MainWindow.xaml     cửa sổ chính
    Views/FormDialog.cs       hộp thoại nhập liệu dùng chung
    ViewModels/               MainViewModel, cây engine/database/nhánh, kết quả truy vấn
    Services/SettingsStore.cs lưu cấu hình kết nối
tests/TestDbHub.Core.Tests/   xUnit: unit test và test tích hợp với MySQL/HeliosDB thật
docker-compose.yml            MySQL + HeliosDB-Nano
```

## Chạy test

```bash
dotnet test                                    # unit test

# Test tích hợp chạy với máy chủ thật khi đặt biến môi trường:
export TESTDB_MYSQL_HOST=127.0.0.1 TESTDB_MYSQL_PASSWORD=testpass
export TESTDB_HELIOS_HOST=127.0.0.1 TESTDB_HELIOS_PORT=5432
dotnet test
```

Phần Core và test chạy được trên cả Linux/macOS. Ứng dụng WPF build được ở mọi nơi nhưng chỉ chạy trên Windows.
