#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Dls24Editor
{
    public sealed class PlayerSummary
    {
        public int Pid { get; internal set; }
        public string Name { get; internal set; } = "";
        public string DisplayName { get; internal set; } = "";
        public string Surname { get; internal set; } = "";
        public int PrimaryPosition { get; internal set; }
        public int Rating { get; internal set; }
    }

    public sealed class PlayerDetails
    {
        public int Pid { get; internal set; }
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Nationality { get; set; }
        public int Height { get; set; }
        public int Weight { get; set; }
        public int PrimaryPosition { get; set; }
        public int SecondaryPosition { get; set; }
        public int PreferredFoot { get; set; }
        public int Flags { get; set; }
        // DB order, with tenths preserved. This array belongs to this editor snapshot.
        public int[] Stats { get; set; } = Array.Empty<int>();
        public string AppearanceHex { get; set; } = "";
        public string RecordHex { get; internal set; } = "";
        public bool HasMarket { get; internal set; }
        public string? MarketJson { get; internal set; }
        public int Rating { get; internal set; }
        public int InitialRating { get; internal set; }
        public int Rarity { get; internal set; }
        public int? RawPrice { get; internal set; }
        public int? Price { get; internal set; }
    }

    public sealed class DatOutput
    {
        internal DatOutput(byte[] bootstrap, byte[] market)
        {
            Bootstrap = bootstrap;
            Market = market;
        }

        public byte[] Bootstrap { get; }
        public byte[] Market { get; }
    }

    public sealed class GameData
    {
        public const string BootstrapFileName = "dls24-local-bootstrap.dat";
        public const string MarketFileName = "dls24-local-market.dat";
        public const string BootstrapEntry = "assets/droicer/" + BootstrapFileName;
        public const string MarketEntry = "assets/droicer/" + MarketFileName;

        private const int DbHeaderSize = 12;
        private const int RecordSize = 196;
        private const int MaxDbSize = DbHeaderSize + 65536 * RecordSize;
        private const int MaxDatSize = 32 * 1024 * 1024;
        private const int MaxJsonSize = 96 * 1024 * 1024;
        private const int PidOffset = 112;
        private const int AppearanceOffset = 116;
        private const int AppearanceSize = 24;
        private const int StatsOffset = 150;
        private const int FlagsOffset = 172;
        private const int RarityOffset = 178;
        private const int StatCount = 10;

        // This asset format uses a fixed game data key documented in the workspace guide.
        private static readonly byte[] DataKey = Convert.FromHexString("744eff868a3e94a07b3df16916c82d7e763b6211e61eea88cfba27c0c17e7765");
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DRJ1");
        private static readonly string[] VersionNames = { "DBv", "PrDBv", "NDBv", "PRv", "TRv", "LRv" };
        private static readonly int[] NativeStatOrder = { 3, 2, 1, 0, 5, 7, 6, 4, 8, 9 };
        private static readonly int[] LegacyStatOrder = { 3, 2, 1, 0, 6, 5, 7, 4, 8, 9 };
        private static readonly int[][] DefaultWeights =
        {
            new[] { 500, 500, 500, 1000, 500, 1000, 500, 500, 49400, 45600 },
            new[] { 15288, 5415, 15732, 12607, 21109, 10067, 9913, 9869, 0, 0 }
        };
        private static readonly (int Start, int Min, int Max)[] TariffCurve =
        {
            (51, 50, 3300), (51, 75, 3600), (52, 150, 4100), (52, 150, 4400)
        };
        private static readonly UnicodeEncoding StrictUtf16 = new UnicodeEncoding(false, false, true);
        private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        private static readonly JsonSerializerOptions PrettyJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        private readonly JsonObject bootstrap;
        private readonly JsonObject market;
        private readonly JsonObject marketPlayers;
        private readonly byte[] sourceBootstrap;
        private readonly byte[] sourceMarket;
        private readonly byte[] database;
        private readonly Dictionary<int, int> offsets = new Dictionary<int, int>();
        private readonly Dictionary<int, string[]> normalizedNames = new Dictionary<int, string[]>();
        private readonly Dictionary<(int Group, int Rating), (int Raw, int Rounded)?> tariffs =
            new Dictionary<(int Group, int Rating), (int Raw, int Rounded)?>();
        private readonly int[][] ratingWeights;
        private readonly int ratingBias;
        private readonly int commonBelow;
        private readonly int legendaryFrom;
        private readonly bool canDeriveTariffs;
        private long editRevision;
        private long lastExportedRevision;
        private int exportNumber;

        private GameData(byte[] bootstrapDat, byte[] marketDat, string? sourceArchive)
        {
            SourceArchive = sourceArchive;
            sourceBootstrap = bootstrapDat;
            sourceMarket = marketDat;
            bootstrap = DecodeDat(bootstrapDat, BootstrapFileName);
            market = DecodeDat(marketDat, MarketFileName);
            marketPlayers = market["players"] as JsonObject
                ?? throw new InvalidDataException("Market thiếu bảng players.");

            foreach (var version in VersionNames)
            {
                if (ReadInt(bootstrap[version]) is not int value || value < 0)
                    throw new InvalidDataException($"Bootstrap có phiên bản {version} không hợp lệ.");
            }

            if (bootstrap["DBp"] is not JsonValue dbValue || !dbValue.TryGetValue<string>(out var dbText))
                throw new InvalidDataException("Bootstrap thiếu trường DBp.");
            database = Inflate(Convert.FromBase64String(dbText), MaxDbSize);
            if (database.Length < DbHeaderSize)
                throw new InvalidDataException("Phần đầu DBp bị thiếu.");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(8, 4));
            if (count == 0 || DbHeaderSize + (long)count * RecordSize != database.Length)
                throw new InvalidDataException("Kích thước DBp không khớp với định dạng bản ghi 196 byte.");

            int lastPid = -1;
            for (int offset = DbHeaderSize; offset < database.Length; offset += RecordSize)
            {
                int pid = ReadU16(database, offset + PidOffset);
                if (pid <= lastPid)
                    throw new InvalidDataException($"DBp có PID {pid} bị trùng hoặc sai thứ tự.");
                offsets.Add(pid, offset);
                lastPid = pid;
            }

            ratingWeights = ReadWeights(market["rating_weights"]);
            ratingBias = ReadInt(market["rating_bias"]) ?? 4165000;
            var rarityPolicy = market["card_rarity_policy"] as JsonObject;
            commonBelow = ReadInt(rarityPolicy?["common_below"]) ?? 7000;
            legendaryFrom = ReadInt(rarityPolicy?["legendary_from"]) ?? 8000;
            if (commonBelow > legendaryFrom)
                throw new InvalidDataException("Ngưỡng phân loại thẻ trong market không hợp lệ.");

            var verifiedCurveGroups = new HashSet<int>();
            int verifiedCurveRows = 0;
            bool curveMatches = true;
            foreach (var entry in marketPlayers)
            {
                if (entry.Value is not JsonObject player || player["profile"] is not JsonObject profile)
                    continue;
                var group = ReadInt(profile["PGP"]) ?? ReadInt(player["position_group"]);
                var rating = ReadInt(player["rating"]);
                var raw = ReadInt(player["raw_price"]);
                var rounded = ReadInt(player["price"]);
                if (group is not int groupValue || rating is not int ratingValue ||
                    raw is not int rawValue || rounded is not int roundedValue || rawValue < 0 ||
                    roundedValue != ((long)rawValue + 2) / 5 * 5)
                {
                    curveMatches = false;
                    continue;
                }

                var key = (groupValue, ratingValue);
                var candidate = (rawValue, roundedValue);
                if (tariffs.TryGetValue(key, out var existing) && existing != candidate)
                    tariffs[key] = null; // Conflicting examples cannot establish a tariff.
                else
                    tariffs[key] = candidate;

                if (groupValue >= 0 && groupValue < TariffCurve.Length && ratingValue >= 0 && ratingValue <= 100 &&
                    CurvePrice(groupValue, ratingValue) == candidate)
                {
                    verifiedCurveRows++;
                    if (ratingValue > TariffCurve[groupValue].Start)
                        verifiedCurveGroups.Add(groupValue);
                }
                else
                    curveMatches = false;
            }
            // Use the inferred curve for unobserved ratings only when the input itself verifies it.
            canDeriveTariffs = curveMatches && verifiedCurveRows >= 100 && verifiedCurveGroups.Count == TariffCurve.Length;

            foreach (var player in offsets)
                IndexName(player.Key, player.Value);
        }

        public string? SourceArchive { get; }
        public int Count => offsets.Count;
        public bool HasChanges => editRevision != 0;

        public static GameData LoadArchive(string path)
        {
            string source = Path.GetFullPath(path);
            using var zip = ZipFile.OpenRead(source);
            return new GameData(ReadEntry(zip, BootstrapEntry), ReadEntry(zip, MarketEntry), source);
        }

        public static GameData LoadFiles(string bootstrapPath, string marketPath)
        {
            using var bootstrapFile = File.OpenRead(bootstrapPath);
            using var marketFile = File.OpenRead(marketPath);
            return new GameData(ReadLimited(bootstrapFile, MaxDatSize), ReadLimited(marketFile, MaxDatSize), null);
        }

        public void ValidateBaseArchive(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            if (!sourceBootstrap.AsSpan().SequenceEqual(ReadEntry(zip, BootstrapEntry)) ||
                !sourceMarket.AsSpan().SequenceEqual(ReadEntry(zip, MarketEntry)))
                throw new InvalidDataException("Hai tệp DAT gốc của phiên chỉnh sửa không khớp APK/ZIP đã chọn.");
        }

        public PlayerSummary[] Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<PlayerSummary>();

            var trimmed = query.Trim();
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return offsets.TryGetValue(pid, out int offset)
                    ? new[] { Summarize(pid, offset) }
                    : Array.Empty<PlayerSummary>();
            }

            var needle = Normalize(trimmed);
            if (needle.Length == 0)
                return Array.Empty<PlayerSummary>();

            var matches = new List<(int Pid, int Rank)>();
            foreach (var entry in normalizedNames)
            {
                var rank = MatchRank(entry.Value, needle);
                if (rank != 3)
                    matches.Add((entry.Key, rank));
            }
            return matches.OrderBy(item => item.Rank).ThenBy(item => item.Pid)
                .Select(item => Summarize(item.Pid, offsets[item.Pid])).ToArray();
        }

        public PlayerDetails GetPlayer(int pid)
        {
            int offset = FindOffset(pid);
            var result = ReadRecord(database, offset);
            var player = GetMarketPlayer(pid);
            result.HasMarket = player != null;
            result.MarketJson = player?.ToJsonString(PrettyJson);
            result.InitialRating = ReadInt((player?["profile"] as JsonObject)?["IRa"])
                ?? CalculateRating(result.Stats, GroupForPosition(result.PrimaryPosition));
            result.Rating = ReadInt(player?["rating"]) ?? result.InitialRating / 100;
            result.RawPrice = ReadInt(player?["raw_price"]);
            result.Price = ReadInt(player?["price"]);
            result.RecordHex = Convert.ToHexString(database.AsSpan(offset, RecordSize));
            return result;
        }

        public int PreviewRating(PlayerDetails player)
        {
            ArgumentNullException.ThrowIfNull(player);
            ValidateStats(player.Stats, ReadRecord(database, FindOffset(player.Pid)).Stats);
            return CalculateRating(player.Stats, GroupForPosition(player.PrimaryPosition));
        }

        public int[] GetSupportedRatings(int primaryPosition)
        {
            int group = GroupForPosition(primaryPosition);
            if (canDeriveTariffs)
                return Enumerable.Range(0, 101).ToArray();
            return tariffs.Where(item => item.Key.Group == group && item.Value.HasValue)
                .Select(item => item.Key.Rating).OrderBy(value => value).ToArray();
        }

        public int[] AdjustStatsForOvr(PlayerDetails edit, int targetOvr)
        {
            ArgumentNullException.ThrowIfNull(edit);
            var original = ReadRecord(database, FindOffset(edit.Pid));
            ValidateStats(edit.Stats, original.Stats);
            int group = GroupForPosition(edit.PrimaryPosition);
            var marketPlayer = GetMarketPlayer(edit.Pid);
            int minimum = tariffs.Where(item => item.Key.Group == group && item.Value.HasValue)
                .Select(item => item.Key.Rating).DefaultIfEmpty(0).Min();
            if (targetOvr < minimum || targetOvr > 100)
                throw new ArgumentException($"Nhập OVR từ {minimum} đến 100 cho nhóm {GroupName(group)}.");

            if (marketPlayer != null)
            {
                var profile = marketPlayer["profile"] as JsonObject;
                int ratingFromStats = CalculateRating(original.Stats, GroupForPosition(original.PrimaryPosition));
                if (ReadInt(profile?["IRa"]) != ratingFromStats || ReadInt(marketPlayer["rating"]) != ratingFromStats / 100)
                    throw new InvalidDataException("OVR trong hồ sơ chợ hiện không khớp với chỉ số DBp. Kiểm tra gói DLS24 nguồn.");
                FindTariff(group, targetOvr, edit.PrimaryPosition);
            }

            var weights = ratingWeights[group == 0 ? 0 : 1];
            var weightsInDbOrder = new int[StatCount];
            for (int index = 0; index < StatCount; index++)
                weightsInDbOrder[NativeStatOrder[index]] = weights[index];

            if (!OvrAdjustment.TryGetStats(edit.Stats, weightsInDbOrder, targetOvr, values => CalculateRating(values, group), out var adjusted))
                throw new InvalidOperationException($"Không thể đạt OVR {targetOvr} với vị trí này và giới hạn chỉ số 0–100.");
            return adjusted;
        }

        public string[] SavePlayer(PlayerDetails edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            int offset = FindOffset(edit.Pid);
            var current = ReadRecord(database, offset);
            ValidateEdit(edit, current);

            bool nameChanged = edit.Name != current.Name || edit.Surname != current.Surname;
            bool displayChanged = edit.DisplayName != current.DisplayName;
            bool positionChanged = edit.PrimaryPosition != current.PrimaryPosition;
            int group = GroupForPosition(edit.PrimaryPosition);
            bool groupChanged = positionChanged && group != GroupForPosition(current.PrimaryPosition);
            bool statsChanged = !edit.Stats.SequenceEqual(current.Stats);
            bool ratingChanged = groupChanged || statsChanged;
            int nativeRating = ratingChanged ? CalculateRating(edit.Stats, group) : 0;
            int rarity = ratingChanged ? GetRarity(nativeRating) : current.Rarity;
            int flags = edit.Flags;
            if (displayChanged)
                flags = edit.DisplayName.Length == 0 ? flags & ~6 : flags | 4;

            byte[] record = database.AsSpan(offset, RecordSize).ToArray();
            WriteName(record, 0, 32, edit.Name, current.Name, "Tên");
            WriteName(record, 32, 48, edit.Surname, current.Surname, "Họ");
            WriteName(record, 80, 32, edit.DisplayName, current.DisplayName, "Tên hiển thị");
            WriteU16(record, 114, edit.Nationality);
            if (!edit.AppearanceHex.Equals(current.AppearanceHex, StringComparison.OrdinalIgnoreCase))
                DecodeAppearance(edit.AppearanceHex).CopyTo(record, AppearanceOffset);
            WriteU16(record, 140, edit.Height);
            WriteU16(record, 142, edit.Weight);
            record[144] = (byte)edit.PrimaryPosition;
            record[145] = (byte)edit.SecondaryPosition;
            WriteU16(record, 148, edit.PreferredFoot);
            for (int index = 0; index < StatCount; index++)
                WriteU16(record, StatsOffset + index * 2, edit.Stats[index]);
            WriteU16(record, FlagsOffset, flags);
            record[RarityOffset] = (byte)rarity;
            if (database.AsSpan(offset, RecordSize).SequenceEqual(record))
                return Array.Empty<string>();

            string[] warnings = Array.Empty<string>();
            var player = GetMarketPlayer(edit.Pid);
            JsonObject? updatedPlayer = null;
            JsonObject? updatedRebalance = null;
            if (player == null)
            {
                warnings = new[] { $"PID {edit.Pid} chưa có mục trong market. Đã lưu DBp; công cụ không tạo mục thị trường mới." };
            }
            else
            {
                updatedPlayer = (JsonObject)player.DeepClone();
                var profile = updatedPlayer["profile"] as JsonObject
                    ?? throw new InvalidDataException($"Mục market của PID {edit.Pid} thiếu profile.");
                int? oldGroup = ReadInt(profile["PGP"]) ?? ReadInt(player["position_group"]);
                if (ReadInt(profile["PID"]) is int marketPid && marketPid != edit.Pid)
                    throw new InvalidDataException($"Mục market của PID {edit.Pid} có profile.PID không khớp.");

                if (nameChanged)
                    updatedPlayer["name"] = FullName(edit.Name, edit.Surname);
                if (nameChanged || displayChanged || (flags & 7) != (current.Flags & 7))
                    SynchronizeDisplayName(updatedPlayer, edit, displayChanged, flags, current.Flags);
                if (edit.PreferredFoot != current.PreferredFoot)
                    updatedPlayer["preferred_foot"] = edit.PreferredFoot;

                if (positionChanged)
                {
                    profile["PGP"] = group;
                    updatedPlayer["position_group"] = group;
                }

                if (statsChanged)
                {
                    profile["PSt"] = ReorderStats(edit.Stats, NativeStatOrder);
                    updatedPlayer["native_attributes"] = ReorderStats(edit.Stats, NativeStatOrder);
                    if (updatedPlayer.ContainsKey("legacy_profile_stats"))
                        updatedPlayer["legacy_profile_stats"] = ReorderStats(edit.Stats, LegacyStatOrder);
                }

                if (ratingChanged)
                {
                    int rating = nativeRating / 100;
                    int? oldRating = ReadInt(player["rating"]);
                    if (groupChanged || oldRating != rating || oldGroup != group)
                    {
                        var tariff = FindTariff(group, rating, edit.PrimaryPosition);
                        updatedPlayer["raw_price"] = tariff.Raw;
                        updatedPlayer["price"] = tariff.Rounded;
                    }
                    profile["IRa"] = nativeRating;
                    updatedPlayer["rating"] = rating;
                    updatedPlayer["initial_rating"] = nativeRating;
                    updatedPlayer["rarity"] = rarity;

                    if (market["custom_player_rebalance"] is JsonObject rebalance &&
                        ReadInt(rebalance["player_id"]) == edit.Pid)
                    {
                        updatedRebalance = (JsonObject)rebalance.DeepClone();
                        updatedRebalance["native_attributes"] = ReorderStats(edit.Stats, NativeStatOrder);
                        updatedRebalance["displayed_overall"] = rating;
                    }
                }

                if (updatedPlayer["custom_player"] is JsonObject custom)
                {
                    UpdateCustomField(custom, "height_cm", edit.Height, current.Height);
                    UpdateCustomField(custom, "weight_kg", edit.Weight, current.Weight);
                    UpdateCustomField(custom, "position", edit.PrimaryPosition, current.PrimaryPosition);
                    UpdateCustomField(custom, "preferred_foot", edit.PreferredFoot, current.PreferredFoot);
                    UpdateCustomNationality(custom, edit.Nationality, current.Nationality);
                }
            }

            // Every validation and tariff lookup above finishes before either structure changes.
            if (updatedPlayer != null)
                marketPlayers[PidText(edit.Pid)] = updatedPlayer;
            if (updatedRebalance != null)
                market["custom_player_rebalance"] = updatedRebalance;
            record.CopyTo(database, offset);
            editRevision++;
            IndexName(edit.Pid, offset);
            return warnings;
        }

        public DatOutput BuildDatFiles()
        {
            if (!HasChanges)
                return new DatOutput((byte[])sourceBootstrap.Clone(), (byte[])sourceMarket.Clone());

            int outputVersion = editRevision == lastExportedRevision ? exportNumber : checked(exportNumber + 1);
            var updatedBootstrap = (JsonObject)bootstrap.DeepClone();
            foreach (var version in VersionNames)
                updatedBootstrap[version] = checked(bootstrap[version]!.GetValue<int>() + outputVersion);
            updatedBootstrap["DBp"] = Convert.ToBase64String(Deflate(database));

            var output = new DatOutput(EncodeDat(updatedBootstrap, BootstrapFileName), EncodeDat(market, MarketFileName));
            exportNumber = outputVersion;
            lastExportedRevision = editRevision;
            return output;
        }

        private static int MatchRank(string[] names, string query)
        {
            int rank = 3;
            foreach (string name in names)
            {
                if (name == query)
                    return 0;
                if (name.StartsWith(query, StringComparison.Ordinal))
                    rank = 1;
                else if (rank == 3 && name.Contains(query, StringComparison.Ordinal))
                    rank = 2;
            }
            return rank;
        }

        private void IndexName(int pid, int offset)
        {
            string name = ReadName(database, offset, 32);
            string surname = ReadName(database, offset + 32, 48);
            string display = ReadName(database, offset + 80, 32);
            var player = GetMarketPlayer(pid);
            string[] names =
            {
                FullName(name, surname), FullName(surname, name), display,
                ReadText(player?["name"]), ReadText(player?["display_name"]), ReadText(player?["nickname"])
            };
            normalizedNames[pid] = names.Select(Normalize).Where(value => value.Length != 0).Distinct().ToArray();
        }

        private PlayerSummary Summarize(int pid, int offset)
        {
            var marketPlayer = GetMarketPlayer(pid);
            var surname = ReadName(database, offset + 32, 48);
            var name = FullName(ReadName(database, offset, 32), surname);
            var display = ReadText(marketPlayer?["display_name"]);
            if (display.Length == 0)
                display = ReadText(marketPlayer?["name"]);
            if (display.Length == 0)
                display = ReadName(database, offset + 80, 32);
            int primaryPosition = database[offset + 144];
            return new PlayerSummary
            {
                Pid = pid,
                Name = name,
                Surname = surname,
                DisplayName = display.Length == 0 ? name : display,
                PrimaryPosition = primaryPosition,
                Rating = ReadInt(marketPlayer?["rating"]) ?? CalculateRating(ReadStats(database, offset), GroupForPosition(primaryPosition)) / 100
            };
        }

        private static PlayerDetails ReadRecord(byte[] bytes, int offset)
        {
            return new PlayerDetails
            {
                Pid = ReadU16(bytes, offset + PidOffset),
                Name = ReadName(bytes, offset, 32),
                Surname = ReadName(bytes, offset + 32, 48),
                DisplayName = ReadName(bytes, offset + 80, 32),
                Nationality = ReadU16(bytes, offset + 114),
                AppearanceHex = Convert.ToHexString(bytes.AsSpan(offset + AppearanceOffset, AppearanceSize)),
                Height = ReadU16(bytes, offset + 140),
                Weight = ReadU16(bytes, offset + 142),
                PrimaryPosition = bytes[offset + 144],
                SecondaryPosition = bytes[offset + 145],
                PreferredFoot = ReadU16(bytes, offset + 148),
                Stats = ReadStats(bytes, offset),
                Flags = ReadU16(bytes, offset + FlagsOffset),
                Rarity = bytes[offset + RarityOffset]
            };
        }

        private static int[] ReadStats(byte[] bytes, int offset)
        {
            var stats = new int[StatCount];
            for (int index = 0; index < StatCount; index++)
                stats[index] = ReadU16(bytes, offset + StatsOffset + index * 2);
            return stats;
        }

        private static void ValidateEdit(PlayerDetails edit, PlayerDetails current)
        {
            ValidateStats(edit.Stats, current.Stats);
            ValidateRange(edit.Nationality, 0, ushort.MaxValue, "Mã quốc tịch");
            if (edit.Height != current.Height)
                ValidateRange(edit.Height, 1, 300, "Chiều cao");
            if (edit.Weight != current.Weight)
                ValidateRange(edit.Weight, 1, 500, "Cân nặng");
            if (edit.PrimaryPosition != current.PrimaryPosition)
                GroupForPosition(edit.PrimaryPosition);
            if (edit.SecondaryPosition != current.SecondaryPosition && edit.SecondaryPosition != 255)
                GroupForPosition(edit.SecondaryPosition);
            if (edit.PreferredFoot != current.PreferredFoot)
                ValidateRange(edit.PreferredFoot, 0, 3, "Mã chân thuận");
            ValidateRange(edit.Flags, 0, ushort.MaxValue, "Cờ cầu thủ");
            if (edit.Name == null || edit.Surname == null || edit.DisplayName == null || edit.AppearanceHex == null)
                throw new ArgumentException("Các trường văn bản không được null.");
            if (string.IsNullOrWhiteSpace(edit.Name) && string.IsNullOrWhiteSpace(edit.Surname) &&
                (edit.Name != current.Name || edit.Surname != current.Surname))
                throw new ArgumentException("Cần giữ ít nhất một phần tên cầu thủ.");
        }

        private static void ValidateStats(int[] stats, int[] original)
        {
            if (stats == null || stats.Length != StatCount)
                throw new ArgumentException("Cần cung cấp đủ 10 chỉ số.");
            for (int index = 0; index < StatCount; index++)
                if (stats[index] != original[index])
                    ValidateRange(stats[index], 0, 1000, $"Chỉ số thứ {index + 1} theo thang ×10");
        }

        private static void ValidateRange(int value, int min, int max, string label)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(label, $"{label} phải từ {min} đến {max}.");
        }

        private static byte[] DecodeAppearance(string value)
        {
            if (value.Length != AppearanceSize * 2 || !value.All(Uri.IsHexDigit))
                throw new ArgumentException("Mã ngoại hình phải có đúng 48 ký tự hex (24 byte).");
            return Convert.FromHexString(value);
        }

        private static void WriteName(byte[] record, int offset, int byteCount, string value, string previous, string label)
        {
            if (value == previous)
                return; // Keep the entire original field, including padding after the terminator.
            if (value.Length > byteCount / 2 || value.Any(char.IsControl))
                throw new ArgumentException($"{label} phải vừa trong {byteCount / 2} đơn vị UTF-16 và không có ký tự điều khiển.");
            byte[] text = StrictUtf16.GetBytes(value);
            Array.Clear(record, offset, byteCount);
            text.CopyTo(record, offset);
        }

        private static string ReadName(byte[] record, int offset, int byteCount)
        {
            for (int size = 0; size < byteCount; size += 2)
                if (record[offset + size] == 0 && record[offset + size + 1] == 0)
                    return Encoding.Unicode.GetString(record, offset, size);
            return Encoding.Unicode.GetString(record, offset, byteCount);
        }

        private static string Normalize(string value)
        {
            var normalized = value.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            var result = new StringBuilder();
            bool spacePending = false;
            foreach (char character in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
                    continue;
                if (char.IsWhiteSpace(character))
                {
                    spacePending = result.Length > 0;
                    continue;
                }
                if (spacePending)
                {
                    result.Append(' ');
                    spacePending = false;
                }
                result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString();
        }

        private static string FullName(string first, string last) => (first.Trim() + " " + last.Trim()).Trim();

        private static void SynchronizeDisplayName(JsonObject player, PlayerDetails edit, bool displayChanged, int flags, int previousFlags)
        {
            var fullName = FullName(edit.Name, edit.Surname);
            int nameFlags = (flags & 7) == (previousFlags & 7)
                ? ReadInt(player["name_flags"]) ?? (flags & 7)
                : ((ReadInt(player["name_flags"]) ?? 0) & ~7) | (flags & 7);
            if (displayChanged)
                player["nickname"] = edit.DisplayName;
            var nickname = ReadText(player["nickname"]);
            if ((flags & 7) != (previousFlags & 7))
                player["name_flags"] = nameFlags;
            if (displayChanged || player.ContainsKey("display_name") || nickname.Length != 0)
                player["display_name"] = (nameFlags & 4) != 0 && nickname.Length != 0 ? nickname : fullName;
        }

        private static void UpdateCustomField(JsonObject custom, string name, int next, int previous)
        {
            if (next != previous && custom.ContainsKey(name))
                custom[name] = next;
        }

        private void UpdateCustomNationality(JsonObject custom, int next, int previous)
        {
            if (next == previous || !custom.ContainsKey("nationality"))
                return;
            custom["nationality"] = next;
            if (!custom.ContainsKey("country"))
                return;

            foreach (var item in marketPlayers)
            {
                if (item.Value is JsonObject player && player["custom_player"] is JsonObject source &&
                    ReadInt(source["nationality"]) == next && ReadText(source["country"]).Length != 0)
                {
                    custom["country"] = ReadText(source["country"]);
                    return;
                }
            }
            // This optional label cannot be resolved for an unknown country code from the source.
            custom.Remove("country");
        }

        private static int GroupForPosition(int position)
        {
            return position switch
            {
                0 => 0,
                1 or 2 or 3 or 4 or 5 => 1,
                8 or 9 or 11 or 12 or 13 or 14 or 16 or 17 or 18 => 2,
                19 or 20 or 21 or 22 => 3,
                _ => throw new ArgumentException($"Mã vị trí {position} không có nhóm PGP được xác thực cho bản DLS24 này.")
            };
        }

        private static string GroupName(int group)
        {
            return group switch { 0 => "GK", 1 => "DEF", 2 => "MID", 3 => "FWD", _ => group.ToString() };
        }

        private (int Raw, int Rounded) FindTariff(int group, int rating, int position)
        {
            if (tariffs.TryGetValue((group, rating), out var tariff) && tariff.HasValue)
                return tariff.Value;
            if (canDeriveTariffs && rating >= 0 && rating <= 100)
                return CurvePrice(group, rating);

            string options = canDeriveTariffs
                ? "Chọn chỉ số cho OVR tối đa 100."
                : $"OVR có giá trong nguồn: {string.Join(", ", GetSupportedRatings(position))}.";
            throw new InvalidOperationException($"Không có giá gốc xác thực cho nhóm {GroupName(group)} với OVR {rating}. {options}");
        }

        private static (int Raw, int Rounded) CurvePrice(int group, int rating)
        {
            var (start, min, max) = TariffCurve[group];
            double position = Math.Max(0, rating - start) / (double)(100 - start);
            int raw = min + (int)Math.Floor((max - min) * Math.Pow(position, 2.3));
            return (raw, (raw + 2) / 5 * 5);
        }

        private int CalculateRating(int[] stats, int group)
        {
            long sum = ratingBias;
            var weights = ratingWeights[group == 0 ? 0 : 1];
            for (int index = 0; index < StatCount; index++)
                sum += (long)weights[index] * stats[NativeStatOrder[index]];
            return checked((int)(sum / 10000L));
        }

        private int GetRarity(int rating) => rating < commonBelow ? 0 : rating < legendaryFrom ? 1 : 2;

        private static int[][] ReadWeights(JsonNode? node)
        {
            if (node == null)
                return DefaultWeights;
            if (node is not JsonArray matrix || matrix.Count != 2)
                throw new InvalidDataException("Bảng rating_weights trong market không hợp lệ.");
            return matrix.Select(row =>
            {
                if (row is not JsonArray array || array.Count != StatCount)
                    throw new InvalidDataException("Một hàng rating_weights không chứa 10 chỉ số.");
                return array.Select(cell => ReadInt(cell) ?? throw new InvalidDataException("rating_weights chứa giá trị không phải số nguyên.")).ToArray();
            }).ToArray();
        }

        private static JsonArray ReorderStats(int[] stats, int[] order)
        {
            var array = new JsonArray();
            foreach (var index in order)
                array.Add(stats[index]);
            return array;
        }

        private int FindOffset(int pid)
        {
            return offsets.TryGetValue(pid, out int offset)
                ? offset
                : throw new KeyNotFoundException($"Không có cầu thủ với PID {pid}.");
        }

        private JsonObject? GetMarketPlayer(int pid) => marketPlayers[PidText(pid)] as JsonObject;
        private static string PidText(int pid) => pid.ToString(CultureInfo.InvariantCulture);
        private static int? ReadInt(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
        private static string ReadText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";
        private static int ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        private static void WriteU16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), checked((ushort)value));

        private static byte[] ReadEntry(ZipArchive zip, string name)
        {
            var entries = zip.Entries.Where(entry => entry.FullName == name).ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException($"APK/ZIP phải chứa đúng một mục {name}.");
            if (entries[0].Length > MaxDatSize)
                throw new InvalidDataException($"Mục {name} quá lớn.");
            using var stream = entries[0].Open();
            return ReadLimited(stream, MaxDatSize);
        }

        private static JsonObject DecodeDat(byte[] data, string name)
        {
            if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidDataException($"{name} không đúng định dạng DRJ1.");
            byte[] compressed = new byte[data.Length - 32];
            using var cipher = new AesGcm(DataKey, 16);
            try
            {
                cipher.Decrypt(data.AsSpan(4, 12), data.AsSpan(16, compressed.Length),
                    data.AsSpan(data.Length - 16), compressed, Aad(name));
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException($"Không xác thực được {name}. Hãy chọn đúng tệp của bản DLS24 Droicer.", exception);
            }

            return JsonNode.Parse(Inflate(compressed, MaxJsonSize)) as JsonObject
                ?? throw new InvalidDataException($"Dữ liệu JSON của {name} phải là một đối tượng.");
        }

        private static byte[] EncodeDat(JsonObject json, string name)
        {
            var compressed = Deflate(Encoding.UTF8.GetBytes(json.ToJsonString(CompactJson)));
            var output = new byte[32 + compressed.Length];
            Magic.CopyTo(output, 0);
            RandomNumberGenerator.Fill(output.AsSpan(4, 12));
            using var cipher = new AesGcm(DataKey, 16);
            cipher.Encrypt(output.AsSpan(4, 12), compressed, output.AsSpan(16, compressed.Length),
                output.AsSpan(output.Length - 16), Aad(name));
            return output;
        }

        private static byte[] Aad(string name) => Encoding.ASCII.GetBytes("droicer/" + Path.GetFileNameWithoutExtension(name) + ".json");

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var compress = new ZLibStream(output, CompressionLevel.Optimal, true))
                compress.Write(data);
            return output.ToArray();
        }

        private static byte[] Inflate(byte[] data, int limit)
        {
            using var source = new MemoryStream(data, false);
            using var input = new ZLibStream(source, CompressionMode.Decompress);
            return ReadLimited(input, limit);
        }

        private static byte[] ReadLimited(Stream input, int limit)
        {
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int size;
            while ((size = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + size > limit)
                    throw new InvalidDataException("Tệp dữ liệu vượt kích thước tối đa của định dạng được hỗ trợ.");
                output.Write(buffer, 0, size);
            }
            return output.ToArray();
        }
    }
}
