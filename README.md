# TestDB Hub (WPF)

Ứng dụng desktop **WPF (C# / .NET 10)** để lưu **database test** và quản lý theo kiểu **HeliosDB-Nano**: mỗi
database có nhánh `main`. Bạn có thể tạo nhánh để thử nghiệm, chụp snapshot, khôi phục, merge, chạy SQL, xuất
`.sql`, và sao chép bảng qua lại giữa các nơi lưu.

**Không cần cài MySQL.** App có sẵn **database tích hợp (SQLite)**: dữ liệu lưu thành file ngay trên máy, không
cần cài đặt, không cần quyền admin. MySQL và HeliosDB-Nano chỉ là tuỳ chọn khi bạn có máy chủ để kết nối.

## Tính năng

| Tính năng | Tích hợp (SQLite) | MySQL (tuỳ chọn) | HeliosDB-Nano (tuỳ chọn) |
|---|---|---|---|
| Cần cài đặt | **Không** | Máy chủ MySQL | Máy chủ HeliosDB-Nano |
| Tạo / xoá database test | ✅ | ✅ | Một database, dùng nhánh thay thế |
| Nhánh (branch) | ✅ mỗi nhánh là một file | ✅ giả lập bằng schema `<db>__<nhánh>` | ✅ nhánh thật |
| Snapshot + khôi phục | ✅ | ✅ | Dùng time-travel thay thế |
| Merge nhánh vào main | Ghi đè main | Ghi đè main | `MERGE BRANCH … INTO main` |
| Time-travel (`AS OF TIMESTAMP`) | — | — | ✅ |
| Trình soạn SQL, kết quả dạng bảng | ✅ | ✅ | ✅ |
| Xem bảng, cấu trúc cột | ✅ | ✅ | ✅ |
| Xuất / mở file `.sql` | ✅ | ✅ | ✅ |
| Sao chép bảng giữa các nơi lưu | ✅ | ✅ | ✅ |
| Chạy ngay trên máy, không cài đặt | ✅ | ✅ qua **🐬 MySQL nội bộ** (bản ZIP) | — |

Database tích hợp lưu ở `%LocalAppData%\TestDbHub\databases\<tên database>\` (đổi được trong **⚙ Kết nối**).
Mỗi nhánh là một file `.sqlite`, nên có thể sao lưu bằng cách chép thư mục, hoặc mở bằng DB Browser for SQLite.

## Tải và chạy

### Cách 1: tải file `.exe` có sẵn
Mỗi lần đẩy code, GitHub Actions build sẵn `TestDbHub.exe`. Vào tab **Actions** → lần chạy **build** mới nhất →
mục **Artifacts** → tải **TestDbHub-win-x64** (file zip, giải nén ra `TestDbHub.exe`). File đã kèm .NET 10 và
SQLite nên chạy ngay trên Windows 10/11 x64, không cần cài gì. Lần đầu mở, Windows SmartScreen có thể cảnh báo vì
file chưa ký số: bấm **More info → Run anyway**. Máy công ty có chính sách chặn file `.exe` lạ thì cần IT cho phép.

### Cách 2: build từ mã nguồn
Cần [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) trên Windows (hoặc Visual Studio 2026):

```powershell
git clone https://github.com/nmthanh55/thanh.git
cd thanh
dotnet run --project src/TestDbHub.Wpf
```

Hoặc mở `TestDbHub.sln` bằng Visual Studio rồi nhấn F5.

## MySQL nội bộ: chạy web ở máy local mà không cần cài MySQL

Web viết cho MySQL (ví dụ web portal) không chạy được trên database tích hợp SQLite. Nút **🐬 MySQL nội bộ** chạy
**MySQL thật** từ bản ZIP chính thức: không cài đặt, không cần quyền admin, chỉ nhận kết nối từ chính máy này.

1. Tải MySQL tại https://dev.mysql.com/downloads/mysql/ → *Microsoft Windows* → **Windows (x86, 64-bit), ZIP
   Archive**. Nên chọn **cùng bản với server** (ví dụ 8.4) để code chạy y hệt khi đưa lên server.
2. Trong TestDB Hub bấm **🐬 MySQL nội bộ** → **Chọn file ZIP MySQL...** → **▶ Bật**. Lần đầu app tự khởi tạo dữ
   liệu (tài khoản `root`, không mật khẩu) và tự trỏ kết nối MySQL của TestDB Hub vào đây.
3. **Nạp file .sql...** để đưa bản backup (file `mysqldump`) vào.
4. Chép **chuỗi kết nối** trong cửa sổ đó vào `appsettings.json` của web (đổi `ten_database` thành tên thật).
5. Trước mỗi đợt test bấm **📸 Snapshot** database đó; test xong bấm **↺ Khôi phục vào main** để về dữ liệu cũ.

MySQL nội bộ vẫn chạy khi đóng TestDB Hub (để web dùng tiếp); bấm **■ Tắt** trong cửa sổ đó để dừng. Dữ liệu nằm ở
`%LocalAppData%\TestDbHub\mysql\`. Nếu báo thiếu `VCRUNTIME140.dll`: chép `vcruntime140.dll`, `vcruntime140_1.dll`,
`msvcp140.dll` từ `C:\Windows\System32` của máy khác vào `%LocalAppData%\TestDbHub\mysql\server\bin`, hoặc nhờ IT cài
*Visual C++ Redistributable x64*.

**Quy trình test local rồi đưa lên server:**

```
sửa code ──> chạy web ở máy local, nối MySQL nội bộ (dữ liệu test) ──> chạy ổn
         ──> git push lên GitHub ──> server cập nhật như hiện nay (nối MySQL thật của server)
```

`appsettings.json` của mỗi nơi giữ riêng (không đưa lên GitHub), nên máy local nối MySQL nội bộ còn server nối MySQL
của server mà không phải sửa code.

## Kết nối MySQL / HeliosDB-Nano (tuỳ chọn)

Chỉ cần nếu bạn muốn dùng thêm máy chủ. Bấm **⚙ Kết nối**, tick **Dùng MySQL** hoặc **Dùng HeliosDB-Nano**, rồi
nhập máy chủ, cổng, tài khoản và mật khẩu. Cấu hình lưu ở `%AppData%\TestDbHub\settings.json`; mật khẩu được mã
hoá bằng Windows DPAPI.

Muốn chạy thử nhanh hai máy chủ này bằng Docker:

```bash
docker compose up -d     # MySQL 127.0.0.1:3306 (root / testpass), HeliosDB-Nano 127.0.0.1:5432
```

## Cách dùng

1. Bấm **+ Database**, đặt tên (ví dụ `shop_test`), chọn nơi lưu **Tích hợp (SQLite)**.
2. Bấm **Dữ liệu mẫu** rồi **▶ Chạy** (Ctrl+Enter hoặc F5) để tạo bảng `customers` và `orders`.
3. Bấm **⑂ Tạo nhánh** `thu_migration` rồi chạy thử câu lệnh nguy hiểm (`UPDATE`, `ALTER`, `DELETE`…) trên
   nhánh. Nhánh `main` không bị ảnh hưởng.
4. Nếu kết quả ổn, bấm **⤴ Ghi đè main** (tích hợp / MySQL) hoặc **⤴ Merge vào main** (HeliosDB). Nếu không
   ổn, bấm **✕ Xoá nhánh**.
5. Trên `main`, bấm **📸 Snapshot** trước khi thay đổi lớn. Muốn quay lại, chọn snapshot rồi bấm
   **↺ Khôi phục vào main**.
6. **⇄ Sao chép sang...** chép bảng sang database hoặc nhánh khác, kể cả sang MySQL / HeliosDB nếu có.
7. Trên HeliosDB, **🕒 Time-travel** xem dữ liệu của một bảng tại một thời điểm trong quá khứ.

Mẹo: bấm vào tên bảng để xem dữ liệu, **Shift + bấm** để xem cấu trúc cột.

## Giới hạn cần biết

**Database tích hợp (SQLite)**
- Cú pháp SQL là của SQLite, gần giống MySQL nhưng không giống hệt. Ví dụ: dùng `INTEGER PRIMARY KEY` thay cho
  `AUTO_INCREMENT`, không có `ENUM`, kiểu dữ liệu linh hoạt hơn.
- Nhánh và snapshot là bản sao đầy đủ của file database. Phù hợp với dữ liệu test cỡ nhỏ và vừa.

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
  TestDbHub.Core/             thư viện dùng chung (net10.0), không phụ thuộc giao diện
    Engines/SqliteEngine.cs   database tích hợp: mỗi nhánh/snapshot là một file SQLite
    Engines/MySqlEngine.cs    MySQL: database, nhánh và snapshot giả lập (MySqlConnector)
    Engines/HeliosEngine.cs   HeliosDB-Nano: nhánh thật, merge, time-travel
    Engines/DbEngine.cs       phần chung: chạy SQL, đọc/ghi bảng, xuất .sql
    Sessions/PgSimpleSession.cs  client PostgreSQL simple-query (trust/MD5/SCRAM)
    CopyService.cs            sao chép bảng giữa các engine
    LocalMySql.cs             MySQL nội bộ: giải nén ZIP, bật/tắt mysqld, nạp file .sql
    PortableSql.cs            quy đổi kiểu dữ liệu và literal SQL
    SqlSplitter.cs            tách script thành từng câu lệnh
  TestDbHub.Wpf/              ứng dụng WPF (net10.0-windows, MVVM với CommunityToolkit.Mvvm)
    Views/MainWindow.xaml     cửa sổ chính
    Views/FormDialog.cs       hộp thoại nhập liệu dùng chung
    Views/LocalMySqlWindow.cs cửa sổ 🐬 MySQL nội bộ
    ViewModels/               MainViewModel, cây engine/database/nhánh, kết quả truy vấn
    Services/SettingsStore.cs lưu cấu hình kết nối
tests/TestDbHub.Core.Tests/   xUnit: unit test, test SQLite, test với MySQL/HeliosDB thật
docker-compose.yml            MySQL + HeliosDB-Nano (tuỳ chọn)
```

## Chạy test

```bash
dotnet test                                    # unit test + database tích hợp

# Test tích hợp chạy với máy chủ thật khi đặt biến môi trường:
export TESTDB_MYSQL_HOST=127.0.0.1 TESTDB_MYSQL_PASSWORD=testpass
export TESTDB_HELIOS_HOST=127.0.0.1 TESTDB_HELIOS_PORT=5432
export TESTDB_MYSQLD_BIN=/usr/bin              # thư mục có mysqld + mysql để test MySQL nội bộ
dotnet test
```

Phần Core và test chạy được trên cả Linux/macOS. Ứng dụng WPF build được ở mọi nơi nhưng chỉ chạy trên Windows.
