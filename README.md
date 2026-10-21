# TestDB Hub

Nơi lưu **database test** trên **MySQL**, nhưng quản lý theo kiểu **HeliosDB-Nano**: mỗi database có nhánh
`main`, bạn có thể tạo nhánh để thử nghiệm, chụp snapshot, khôi phục, merge, và sao chép bảng qua lại giữa
MySQL và HeliosDB-Nano, tất cả trong một giao diện web.

## Tính năng

| Tính năng | MySQL | HeliosDB-Nano |
|---|---|---|
| Tạo / xoá database test | ✅ nhiều database | Một database (`helios`), dùng nhánh thay thế |
| Nhánh (branch) | ✅ giả lập: sao chép bảng sang schema `<db>__<nhánh>` | ✅ nhánh copy-on-write thật (`CREATE DATABASE BRANCH`) |
| Snapshot + khôi phục | ✅ snapshot `snap_<thời gian>`, khôi phục = ghi đè main | Dùng time-travel thay thế |
| Time-travel (`AS OF TIMESTAMP`) | — | ✅ xem bảng tại một thời điểm, tạo nhánh từ quá khứ |
| Merge nhánh vào main | Ghi đè main bằng nội dung nhánh | `MERGE BRANCH … INTO main` (last-writer-wins) |
| Trình soạn SQL, chạy nhiều câu | ✅ | ✅ |
| Xem bảng, cấu trúc cột | ✅ | ✅ |
| Xuất / nhập file `.sql` | ✅ | ✅ |
| Sao chép bảng MySQL ⇄ HeliosDB | ✅ | ✅ |

## Chạy nhanh bằng Docker Compose

```bash
cp .env.example .env          # sửa mật khẩu nếu muốn
docker compose up -d --build  # lần đầu build HeliosDB-Nano mất 10–20 phút
```

Mở <http://localhost:3000>.

Các cổng chỉ mở trên `127.0.0.1`: MySQL `3306`, HeliosDB-Nano (giao thức MySQL) `3307`, web `3000`.

## Chạy thủ công

Yêu cầu: Node.js ≥ 22.

1. **MySQL**: dùng MySQL có sẵn, hoặc:
   ```bash
   docker run -d --name testdb-mysql -e MYSQL_ROOT_PASSWORD=testpass -p 127.0.0.1:3306:3306 mysql:8.4
   ```
2. **HeliosDB-Nano** (tuỳ chọn): cài bằng Cargo hoặc tải binary ở
   [GitHub Releases](https://github.com/HeliosDatabase/HeliosDB-Nano/releases), rồi chạy:
   ```bash
   cargo install heliosdb-nano --locked
   heliosdb-nano start --data-dir ./data/helios --mysql --mysql-listen 127.0.0.1:3307 --http-port 0
   ```
   Không dùng HeliosDB thì đặt `HELIOS_ENABLED=false` trong `.env`.
3. **App**:
   ```bash
   npm install
   cp .env.example .env
   npm start            # hoặc: npm run dev (tự khởi động lại khi sửa code)
   ```

## Cách dùng

1. Bấm **+ Database** dưới MySQL để tạo database test, ví dụ `shop_test`.
2. Bấm **Dữ liệu mẫu** rồi **▶ Chạy** để tạo bảng `customers` và `orders`.
3. **⑂ Tạo nhánh** `thu_migration`, chạy thử câu lệnh nguy hiểm (`UPDATE`, `ALTER`, `DELETE`…) trên nhánh.
   Nhánh `main` không bị ảnh hưởng.
4. Nếu kết quả ổn, bấm **⤴ Ghi đè main** (MySQL) hoặc **⤴ Merge vào main** (HeliosDB). Nếu không ổn,
   bấm **✕ Xoá nhánh**.
5. Trên `main` của MySQL, bấm **📸 Snapshot** trước khi thay đổi lớn. Muốn quay lại, chọn snapshot rồi bấm
   **↺ Khôi phục vào main**.
6. **⇄ Sao chép sang...** chép bảng từ nhánh hiện tại sang engine, database hoặc nhánh khác, ví dụ đưa dữ liệu
   test từ MySQL sang một nhánh HeliosDB để thử time-travel.
7. Trên HeliosDB, **🕒 Time-travel** xem dữ liệu của một bảng tại một thời điểm trong quá khứ.

Mẹo: bấm vào tên bảng để xem dữ liệu, **Shift + bấm** để xem cấu trúc cột. `Ctrl+Enter` để chạy SQL.

## Giới hạn cần biết

- **Nhánh MySQL là bản sao đầy đủ** (dùng `CREATE TABLE … LIKE` và `INSERT … SELECT`). Khoá ngoại, view,
  trigger và procedure không được sao chép. Phù hợp với dữ liệu test cỡ nhỏ và vừa.
- **Ghi đè main (MySQL)** thay toàn bộ bảng của main bằng bảng của nhánh. Đây không phải merge từng dòng.
- **MERGE BRANCH (HeliosDB)** theo kiểu last-writer-wins, không phát hiện xung đột
  ([tài liệu HeliosDB](https://github.com/HeliosDatabase/HeliosDB-Nano#git-like-branching--fork-test-discard-sandboxes)).
- **Sao chép giữa hai engine** quy đổi kiểu dữ liệu sang kiểu tương đương (INT, BIGINT, DECIMAL, TEXT,
  DATE/TIMESTAMP, JSON…). Khoá chính và chỉ mục không được chép theo. Sao chép trong cùng một engine thì giữ
  nguyên định nghĩa bảng.
- Giao thức MySQL của HeliosDB-Nano **không có xác thực**. Chỉ chạy trên máy cá nhân hoặc mạng nội bộ tin cậy.
- App dành cho môi trường dev/test: trình soạn SQL chạy mọi câu lệnh bạn nhập. Mặc định chỉ lắng nghe trên
  `127.0.0.1`.

## Cấu trúc mã nguồn

```
src/
  server.js          REST API (Express) + phục vụ giao diện
  config.js          đọc cấu hình từ biến môi trường
  util.js            kiểm tra tên, tách câu SQL, quy đổi kiểu dữ liệu
  engines/
    base.js          phần chung: chạy SQL, đọc/ghi bảng, xuất .sql
    mysql.js         MySQL: database, nhánh và snapshot giả lập
    helios.js        HeliosDB-Nano: nhánh thật, merge, time-travel
public/              giao diện web (HTML/CSS/JS thuần)
test/                unit test (npm test)
docker/helios/       Dockerfile build HeliosDB-Nano
```

## API

| Method | Đường dẫn | Mô tả |
|---|---|---|
| GET | `/api/engines` | Danh sách engine và trạng thái kết nối |
| GET / POST | `/api/:engine/databases` | Liệt kê / tạo database (`{name}`) |
| DELETE | `/api/:engine/databases/:db` | Xoá database cùng các nhánh |
| GET / POST | `/api/:engine/databases/:db/branches` | Liệt kê / tạo nhánh (`{name, from, asOf}`) |
| POST | `/api/:engine/databases/:db/snapshots` | Chụp snapshot main (MySQL) |
| DELETE | `/api/:engine/databases/:db/branches/:branch` | Xoá nhánh |
| POST | `…/branches/:branch/merge` | Merge / ghi đè vào main |
| GET | `…/branches/:branch/tables[/:table]` | Liệt kê bảng / cấu trúc bảng |
| POST | `…/branches/:branch/query` | Chạy SQL (`{sql}`) |
| GET | `…/branches/:branch/export` | Tải file `.sql` |
| POST | `/api/copy` | Sao chép bảng (`{from, to, tables}`) |
