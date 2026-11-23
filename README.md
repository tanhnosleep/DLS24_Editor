# DLS24 Player Editor

Công cụ Windows dành cho gói DLS24 Droicer có các tệp `assets/droicer/dls24-local-bootstrap.dat` và `assets/droicer/dls24-local-market.dat`.

Repo này chứa mã nguồn và `DLS24-Editor.exe` để chạy trên Windows x64. APK, ZIP game và khóa ký không được lưu trong repo.

## Chạy sau khi clone

1. Cài [.NET 8 Desktop Runtime cho Windows x64](https://dotnet.microsoft.com/download/dotnet/8.0) nếu máy chưa có.
2. Nhấp đúp `DLS24-Editor.exe` ngay trong thư mục vừa clone.
3. Chọn **Mở APK / ZIP** để nạp gói game của bạn. Muốn xuất APK mới, cần thêm Java, Android SDK Build Tools và keystore ký riêng (xem phần **Yêu cầu** bên dưới).

## Build từ mã nguồn

Cài .NET 8 SDK trên Windows, rồi chạy trong thư mục này:

```powershell
dotnet publish .\DLS24-Editor.csproj -c Release -r win-x64 --self-contained false
```

File mới nằm tại `bin\Release\net8.0-windows\win-x64\publish\DLS24-Editor.exe`. Cần .NET 8 Desktop Runtime trên máy chạy ứng dụng. Gói game không đi kèm repo.

## Tìm và sửa cầu thủ

1. Mở `DLS24-Editor.exe` trong repo hoặc bản vừa build. Chọn **Mở APK / ZIP** để dùng bản mới nhất đang chơi hoặc chọn **Mở cặp .dat** nếu đã tách hai tệp dữ liệu.
2. Tìm theo tên, biệt danh, hoặc PID. Tìm tên có dấu hoặc không dấu đều được. Nếu trùng tên, chọn theo PID. Giao diện hiện **Không có cầu thủ** khi không tìm thấy.
3. Chọn cầu thủ. Tab **Chỉ số** nhận giá trị hiển thị trong game, ví dụ nhập `93` cho tốc độ 93. Tab **Hồ sơ** cho phép sửa các trường đã nhận diện. Tab **Dữ liệu chợ** hiện đầy đủ JSON tương ứng, còn **Bản ghi DBp** hiện đầy đủ 196 byte của bản ghi.
4. Bấm **Lưu cầu thủ** hoặc **Ctrl+S**. Công cụ cập nhật bản ghi DBp và các trường liên quan trong chợ, gồm chỉ số, OVR, độ hiếm và giá khi các giá trị đó thay đổi.
5. Chọn **Build APK** để mã hóa dữ liệu, tăng phiên bản DB, đóng gói và ký APK. Nếu chỉ cần hai tệp đã mã hóa, chọn **Xuất 2 tệp .dat**. Khi mở từ cặp DAT, hãy chọn gói gốc có đúng cặp đó khi build.

Giá chợ cho OVR tối đa 100 được tính theo bảng và công thức đã đối chiếu với gói game hỗ trợ. Công cụ báo lỗi nếu thay đổi cần một mức giá mà dữ liệu nguồn không xác định được.

## Chỉnh OVR

Chọn cầu thủ, nhập **OVR mục tiêu** ở tab **Chỉ số** rồi bấm **Áp dụng OVR**. Ví dụ, tìm Cristiano Ronaldo theo PID `382` và nhập `90` để tăng OVR từ 83 lên 90. Xem lại các chỉ số công cụ đề xuất, rồi bấm **Lưu cầu thủ**.

Game tính OVR từ chỉ số theo công thức riêng cho thủ môn và cầu thủ sân. Công cụ điều chỉnh các chỉ số được tính vào OVR trong khoảng 0–100 và kiểm tra kết quả trước khi áp dụng. Khi lưu, công cụ đồng bộ OVR, chỉ số, độ hiếm và giá chợ. OVR mục tiêu tối đa là 100 và phải có giá chợ xác thực cho nhóm vị trí đó.

## Ký APK và cài

- Để cài đè và giữ save, chọn keystore đã ký bản DLS24 đang cài, nhập alias và mật khẩu của khóa đó. Nếu bản hiện tại đã có dữ liệu mới hơn gói ZIP kèm theo, hãy mở chính APK của bản hiện tại làm nguồn.
- Nếu muốn cài riêng, nhập alias và mật khẩu, chọn **Tạo keystore mới** rồi build. Hãy giữ lại keystore và mật khẩu cho các lần cập nhật tiếp theo. Android có thể yêu cầu gỡ bản cùng tên gói nếu chữ ký khác, thao tác này có thể xóa save.
- Sau khi nhận thông báo APK đã ký, cài tệp trên thiết bị hoặc kéo thả vào BlueStacks. Công cụ xuất APK mới và giữ nguyên gói nguồn.

## Yêu cầu

- Windows x64 và .NET 8 Desktop Runtime. Ứng dụng mở trực tiếp bằng tệp `.exe`.
- Java để ký APK. Dùng Java sẵn có hoặc đặt `JAVA_HOME` đến JDK nếu cần. Hộp thoại ký tự chọn Android SDK Build Tools đã cài; có thể chọn thư mục Build Tools khác trong hộp thoại. [Android SDK Build Tools](https://developer.android.com/tools/releases/build-tools).
- Chừa khoảng 2 GB tại nơi xuất APK để tạo các tệp tạm khi đóng gói.
