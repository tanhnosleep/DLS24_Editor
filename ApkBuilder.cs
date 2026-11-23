using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Dls24Editor
{
    public static class ApkBuilder
    {
        private const string BootstrapEntry = "assets/droicer/dls24-local-bootstrap.dat";
        private const string MarketEntry = "assets/droicer/dls24-local-market.dat";
        private const string StorePasswordVariable = "DLS24_EDITOR_STORE_PASSWORD";
        private const string KeyPasswordVariable = "DLS24_EDITOR_KEY_PASSWORD";
        private const ushort HasDescriptor = 8;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static string FindBuildTools()
        {
            string[] sdkRoots =
            {
                Environment.GetEnvironmentVariable("ANDROID_HOME"),
                Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
                @"C:\Android"
            };
            foreach (string sdkRoot in sdkRoots)
            {
                string tools = ResolveBuildTools(sdkRoot);
                if (tools != null)
                    return tools;
            }

            return PathDirectories().FirstOrDefault(IsBuildTools);
        }

        public static string FindKeytool() => FindJavaProgram("keytool.exe");

        public static void CreateKeystore(string path, string alias, string password, string keytoolPath = null)
        {
            string destination = NewOutputPath(path);
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("Nhập bí danh khóa (alias).");
            if (password == null || password.Length < 6)
                throw new ArgumentException("Mật khẩu keystore phải có ít nhất 6 ký tự.");

            string keytool = string.IsNullOrWhiteSpace(keytoolPath) ? FindKeytool() : keytoolPath;
            if (keytool == null || !File.Exists(keytool))
                throw new InvalidOperationException("Không tìm thấy keytool. Cài JDK 17 và đặt JAVA_HOME hoặc thêm thư mục bin vào PATH.");

            string staging = CreateStagingDirectory(destination);
            string temporary = Path.Combine(staging, "new.keystore");
            try
            {
                Run(keytool, "Tạo keystore", new[]
                {
                    "-genkeypair", "-keystore", temporary, "-storetype", "PKCS12", "-alias", alias,
                    "-storepass:env", StorePasswordVariable, "-keypass:env", KeyPasswordVariable,
                    "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
                    "-dname", "CN=DLS24 Mod", "-noprompt"
                }, Passwords(password, password));
                File.Move(temporary, destination);
            }
            finally
            {
                CleanStaging(staging, temporary);
            }
        }

        public static string Build(string basePath, string outputPath, byte[] bootstrap, byte[] market,
            string buildToolsPath, string keystorePath, string alias, string storePassword,
            string keyPassword, Action<string> progress)
        {
            string output = NewOutputPath(outputPath);
            if (!output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Tệp đầu ra phải có đuôi .apk.");

            string tools = string.IsNullOrWhiteSpace(buildToolsPath) ? FindBuildTools() : ResolveBuildTools(buildToolsPath);
            if (tools == null)
                throw new InvalidOperationException("Không tìm thấy Android SDK Build Tools. Chọn thư mục chứa zipalign.exe và lib\\apksigner.jar, ví dụ C:\\Android\\build-tools\\34.0.0.");
            string java = FindJavaProgram("java.exe");
            if (java == null)
                throw new InvalidOperationException("Không tìm thấy Java. Cài JDK 17 và đặt JAVA_HOME hoặc thêm thư mục bin vào PATH.");
            if (string.IsNullOrWhiteSpace(keystorePath) || !File.Exists(keystorePath))
                throw new ArgumentException("Chọn tệp keystore dùng để ký APK.");
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("Nhập bí danh khóa (alias) trong keystore.");

            string zipalign = Path.Combine(tools, "zipalign.exe");
            string signer = Path.Combine(tools, "lib", "apksigner.jar");
            string staging = CreateStagingDirectory(output);
            string unsigned = Path.Combine(staging, "unsigned.apk");
            string aligned = Path.Combine(staging, "aligned.apk");
            string signed = Path.Combine(staging, "signed.apk");
            try
            {
                progress?.Invoke("Đang đóng gói dữ liệu vào APK…");
                PrepareUnsigned(basePath, unsigned, bootstrap, market);

                progress?.Invoke("Đang căn chỉnh APK…");
                string help = RunProcess(zipalign, new[] { "-h" }, null).Output;
                string[] pageSize = help.Contains("-P ", StringComparison.Ordinal)
                    ? new[] { "-P", "16" }
                    : new[] { "-p" };
                Run(zipalign, "Căn chỉnh APK",
                    new[] { "-f" }.Concat(pageSize).Concat(new[] { "4", unsigned, aligned }));
                File.Delete(unsigned);

                progress?.Invoke("Đang ký APK…");
                Run(java, "Ký APK", new[]
                {
                    "-jar", signer, "sign", "--ks", Path.GetFullPath(keystorePath), "--ks-key-alias", alias,
                    "--ks-pass", "env:" + StorePasswordVariable, "--key-pass", "env:" + KeyPasswordVariable,
                    "--v4-signing-enabled", "false", "--out", signed, aligned
                }, Passwords(storePassword ?? "", string.IsNullOrEmpty(keyPassword) ? storePassword ?? "" : keyPassword));
                File.Delete(aligned);

                progress?.Invoke("Đang kiểm tra căn chỉnh và chữ ký…");
                Run(zipalign, "Kiểm tra căn chỉnh APK",
                    new[] { "-c" }.Concat(pageSize).Concat(new[] { "4", signed }));
                Run(java, "Kiểm tra chữ ký APK", new[] { "-jar", signer, "verify", signed });
                File.Move(signed, output);
                return output;
            }
            finally
            {
                CleanStaging(staging, unsigned, aligned, signed, signed + ".idsig");
            }
        }

        public static void PrepareUnsigned(string basePath, string outputPath, byte[] bootstrap, byte[] market)
        {
            if (bootstrap == null || bootstrap.Length == 0 || market == null || market.Length == 0)
                throw new ArgumentException("Cần cả hai tệp dữ liệu .dat đã mã hóa.");
            string output = NewOutputPath(outputPath);
            bool created = false;
            try
            {
                using var source = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
                byte[] end = ReadEndRecord(source, out long endOffset);
                List<ZipMember> entries = ReadCentralDirectory(source, end, endOffset);
                if (!entries.Any(entry => entry.Name == "AndroidManifest.xml") ||
                    entries.Count(entry => entry.Name == BootstrapEntry) != 1 ||
                    entries.Count(entry => entry.Name == MarketEntry) != 1)
                {
                    throw new InvalidDataException("Gói gốc phải chứa AndroidManifest.xml và đúng một bản của mỗi tệp .dat ở assets/droicer/.");
                }

                using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
                created = true;
                byte[] copyBuffer = new byte[1024 * 1024];
                long memberBoundary = U32(end, 16);
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    ZipMember entry = entries[index];
                    entry.EndBoundary = memberBoundary;
                    memberBoundary = entry.Offset;
                }

                // Copy only ZIP members; the old APK signing block is outside all members.
                foreach (ZipMember entry in entries)
                {
                    if (!entry.IsSignature)
                    {
                        byte[] replacement = entry.Name == BootstrapEntry ? bootstrap :
                            entry.Name == MarketEntry ? market : null;
                        CopyMember(source, destination, entry, replacement, copyBuffer);
                    }
                }
                uint centralOffset = Zip32(destination.Position);
                ushort count = 0;
                foreach (ZipMember entry in entries)
                {
                    if (entry.IsSignature)
                        continue;
                    destination.Write(entry.Central);
                    count++;
                }
                Put16(end, 8, count);
                Put16(end, 10, count);
                Put32(end, 12, Zip32(destination.Position - centralOffset));
                Put32(end, 16, centralOffset);
                destination.Write(end);
                Zip32(destination.Position);
            }
            catch
            {
                if (created)
                    TryDeleteFile(output);
                throw;
            }
        }

        private static byte[] ReadEndRecord(FileStream source, out long endOffset)
        {
            if (source.Length >= uint.MaxValue)
                throw new InvalidDataException("Công cụ hỗ trợ APK/ZIP ZIP32 dưới 4 GiB.");
            int tailLength = (int)Math.Min(source.Length, 22 + ushort.MaxValue);
            source.Position = source.Length - tailLength;
            byte[] tail = Read(source, tailLength);
            for (int index = tail.Length - 22; index >= 0; index--)
            {
                if (U32(tail, index) == 0x06054b50 && index + 22 + U16(tail, index + 20) == tail.Length)
                {
                    endOffset = source.Length - tailLength + index;
                    return tail.AsSpan(index).ToArray();
                }
            }
            throw new InvalidDataException("Không đọc được cấu trúc ZIP của gói gốc.");
        }

        private static List<ZipMember> ReadCentralDirectory(FileStream source, byte[] end, long endOffset)
        {
            ushort count = U16(end, 10);
            uint centralOffset = U32(end, 16);
            if (U16(end, 4) != 0 || U16(end, 6) != 0 || U16(end, 8) != count ||
                count == ushort.MaxValue || (long)centralOffset + U32(end, 12) != endOffset)
                throw new InvalidDataException("Gói gốc phải là ZIP32 thông thường, không chia nhiều phần.");

            source.Position = centralOffset;
            var entries = new List<ZipMember>(count);
            for (int i = 0; i < count; i++)
            {
                byte[] header = Read(source, 46);
                if (U32(header, 0) != 0x02014b50 || U16(header, 34) != 0 ||
                    U32(header, 20) == uint.MaxValue || U32(header, 24) == uint.MaxValue ||
                    U32(header, 42) >= centralOffset)
                    throw new InvalidDataException("Chỉ mục ZIP của gói gốc không hợp lệ hoặc sử dụng ZIP64.");
                ushort nameLength = U16(header, 28);
                byte[] entry = new byte[46 + nameLength + U16(header, 30) + U16(header, 32)];
                header.CopyTo(entry, 0);
                source.ReadExactly(entry.AsSpan(46));
                entries.Add(new ZipMember(entry, Encoding.UTF8.GetString(entry, 46, nameLength)));
            }
            if (source.Position != endOffset)
                throw new InvalidDataException("Kích thước chỉ mục ZIP không khớp.");

            // The output central directory and local records both follow physical entry order.
            entries.Sort((left, right) => left.Offset.CompareTo(right.Offset));
            return entries;
        }

        private static void CopyMember(FileStream source, FileStream destination, ZipMember entry,
            byte[] replacement, byte[] copyBuffer)
        {
            source.Position = entry.Offset;
            byte[] local = Read(source, 30);
            ushort flags = U16(local, 6);
            ushort method = U16(local, 8);
            if (U32(local, 0) != 0x04034b50 || flags != U16(entry.Central, 8) ||
                method != U16(entry.Central, 10) || (flags & 1) != 0)
                throw new InvalidDataException("Mục ZIP không hợp lệ hoặc được mã hóa ở cấp ZIP: " + entry.Name);
            byte[] fields = Read(source, U16(local, 26) + U16(local, 28));
            if (!fields.AsSpan(0, U16(local, 26)).SequenceEqual(entry.Central.AsSpan(46, U16(entry.Central, 28))))
                throw new InvalidDataException("Tên mục ZIP không khớp: " + entry.Name);
            long compressedLength = U32(entry.Central, 20);
            if (source.Position + compressedLength > entry.EndBoundary)
                throw new InvalidDataException("Kích thước mục ZIP không hợp lệ: " + entry.Name);

            Put32(entry.Central, 42, Zip32(destination.Position));
            if (replacement == null)
            {
                destination.Write(local);
                destination.Write(fields);
                CopyBytes(source, destination, compressedLength, copyBuffer);
                if ((flags & HasDescriptor) != 0)
                    destination.Write(ReadDescriptor(source, entry));
                return;
            }

            byte[] payload = Compress(replacement, method);
            uint crc = Crc32(replacement);
            ushort newFlags = (ushort)(flags & ~HasDescriptor);
            Put16(local, 6, newFlags);
            Put16(entry.Central, 8, newFlags);
            Put32(local, 14, crc);
            Put32(entry.Central, 16, crc);
            Put32(local, 18, (uint)payload.Length);
            Put32(entry.Central, 20, (uint)payload.Length);
            Put32(local, 22, (uint)replacement.Length);
            Put32(entry.Central, 24, (uint)replacement.Length);
            destination.Write(local);
            destination.Write(fields);
            destination.Write(payload);
        }

        private static byte[] ReadDescriptor(FileStream source, ZipMember entry)
        {
            int maximum = (int)Math.Min(entry.EndBoundary - source.Position, 16);
            byte[] descriptor = Read(source, maximum);
            ReadOnlySpan<byte> expected = entry.Central.AsSpan(16, 12);
            if (maximum >= 16 && U32(descriptor, 0) == 0x08074b50 && descriptor.AsSpan(4, 12).SequenceEqual(expected))
                return descriptor;
            if (maximum >= 12 && descriptor.AsSpan(0, 12).SequenceEqual(expected))
                return descriptor.AsSpan(0, 12).ToArray();
            throw new InvalidDataException("Không đọc được ZIP data descriptor: " + entry.Name);
        }

        private static byte[] Compress(byte[] contents, ushort method)
        {
            if (method == 0)
                return contents;
            if (method != 8)
                throw new InvalidDataException("Tệp dữ liệu sử dụng kiểu nén ZIP chưa được hỗ trợ: " + method);
            using var buffer = new MemoryStream();
            using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(contents);
            return buffer.ToArray();
        }

        private static void CopyBytes(Stream source, Stream destination, long count, byte[] buffer)
        {
            while (count > 0)
            {
                int chunk = (int)Math.Min(count, buffer.Length);
                source.ReadExactly(buffer.AsSpan(0, chunk));
                destination.Write(buffer, 0, chunk);
                count -= chunk;
            }
        }

        private static string ResolveBuildTools(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;
            string path = candidate.Trim().Trim('"');
            if (File.Exists(path) && Path.GetFileName(path).Equals("zipalign.exe", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(Path.GetFullPath(path));
            if (IsBuildTools(path))
                return Path.GetFullPath(path);
            string subfolder = Path.Combine(path, "build-tools");
            string directory = Directory.Exists(subfolder) ? subfolder : path;
            if (!Directory.Exists(directory))
                return null;
            return Directory.EnumerateDirectories(directory)
                .Where(IsBuildTools)
                .OrderByDescending(folder => Version.TryParse(Path.GetFileName(folder).Split('-')[0], out Version version)
                    ? version : new Version(0, 0))
                .FirstOrDefault();
        }

        private static bool IsBuildTools(string path) => !string.IsNullOrEmpty(path) &&
            File.Exists(Path.Combine(path, "zipalign.exe")) && File.Exists(Path.Combine(path, "lib", "apksigner.jar"));

        private static string FindJavaProgram(string name)
        {
            var javaHomes = new List<string>
            {
                Environment.GetEnvironmentVariable("JAVA_HOME"),
                Environment.GetEnvironmentVariable("JDK_HOME"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr")
            };
            return javaHomes.Where(home => !string.IsNullOrWhiteSpace(home))
                .Select(home => Path.Combine(home.Trim('"'), "bin"))
                .Concat(PathDirectories())
                .Select(path => Path.Combine(path, name))
                .FirstOrDefault(File.Exists);
        }

        private static IEnumerable<string> PathDirectories() =>
            (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Trim('"')).Where(path => Path.IsPathFullyQualified(path));

        private static Dictionary<string, string> Passwords(string store, string key) => new Dictionary<string, string>
        {
            [StorePasswordVariable] = store,
            [KeyPasswordVariable] = key
        };

        private static void Run(string program, string operation, IEnumerable<string> arguments,
            Dictionary<string, string> environment = null)
        {
            var result = RunProcess(program, arguments, environment);
            if (result.ExitCode == 0)
                return;
            string output = result.Output.Trim();
            if (output.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
                output = "Phiên bản Java hiện tại quá cũ. Đặt JAVA_HOME trỏ đến JDK 17 hoặc mới hơn.\n" + output;
            if (output.Length > 8000)
                output = output.Substring(output.Length - 8000);
            throw new InvalidOperationException($"{operation} thất bại (mã {result.ExitCode}).\n{output}");
        }

        private static (int ExitCode, string Output) RunProcess(string program, IEnumerable<string> arguments,
            Dictionary<string, string> environment)
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            if (environment != null)
            {
                foreach (var variable in environment)
                    info.Environment[variable.Key] = variable.Value;
            }
            using Process process = Process.Start(info);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult());
        }

        private static string NewOutputPath(string path)
        {
            string output = Path.GetFullPath(path);
            if (File.Exists(output) || Directory.Exists(output))
                throw new IOException("Tệp đích đã tồn tại. Hãy chọn tên khác: " + output);
            if (!Directory.Exists(Path.GetDirectoryName(output)))
                throw new DirectoryNotFoundException("Thư mục lưu không tồn tại: " + Path.GetDirectoryName(output));
            return output;
        }

        private static string CreateStagingDirectory(string destination) =>
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(destination),
                ".dls24-" + Guid.NewGuid().ToString("N"))).FullName;

        private static void CleanStaging(string directory, params string[] files)
        {
            foreach (string file in files)
                TryDeleteFile(file);
            try { Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static uint Zip32(long value)
        {
            if (value >= uint.MaxValue)
                throw new InvalidDataException("APK kết quả vượt quá giới hạn ZIP32.");
            return (uint)value;
        }

        private static byte[] Read(Stream source, int length)
        {
            byte[] result = new byte[length];
            source.ReadExactly(result);
            return result;
        }

        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
        private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
                table[i] = crc;
            }
            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in data)
                crc = (crc >> 8) ^ CrcTable[(byte)(crc ^ value)];
            return ~crc;
        }

        private sealed class ZipMember
        {
            public readonly byte[] Central;
            public readonly string Name;
            public readonly uint Offset;
            public readonly bool IsSignature;
            public long EndBoundary;

            public ZipMember(byte[] central, string name)
            {
                Central = central;
                Name = name;
                Offset = U32(central, 42);
                if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                    return;
                string fileName = name.Substring("META-INF/".Length).ToUpperInvariant();
                IsSignature = !fileName.Contains('/') && (fileName == "MANIFEST.MF" ||
                    fileName.EndsWith(".SF", StringComparison.Ordinal) || fileName.EndsWith(".RSA", StringComparison.Ordinal) ||
                    fileName.EndsWith(".DSA", StringComparison.Ordinal) || fileName.EndsWith(".EC", StringComparison.Ordinal) ||
                    fileName.StartsWith("SIG-", StringComparison.Ordinal));
            }
        }
    }
}
