using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustManager", "RustManager", "0.2.0")]
    [Description("Command library for RustManager.")]
    public class RustManager : RustPlugin
    {
        private const int MaxNestedItemDepth = 4;
        private const int BackpackSlot = 7;
        private const string ContainerInventory = "inventory";
        private const string ContainerBelt = "belt";
        private const string ContainerWear = "wear";
        private const string ContainerBackpack = "backpack";
        private const string PluginVersion = "0.2.0";
        private const string WhitelistDataFile = "RustManagerWhitelist";
        private const string WhitelistRejectMessage = "You are not whitelisted on this server.";
        private const string MapDataFile = "RustManagerMap";
        private const string MapCacheLogPrefix = "[Rust.MapCache-Images] Image uploaded to backend:";
        private const string HarmonyId = "rustmanager.rendermap.patch";
        private const string TrapDataFile = "RustManagerTrap";
        private const int TrapWaterTopologyMask = 128 | 16384;
        private const int MaxTrapItemsPerNight = 10;
        private const int MaxTrapPositionAttempts = 160;
        private const int MaxTrapDetections = 500;
        private const int MaxActiveTraps = 50;
        private const float TrapTickSeconds = 30f;
        private const float TrapNightStartHour = 20f;
        private const float TrapNightEndHour = 6f;
        private const int MaxArgLength = 256;
        private const int MaxWhitelistNameLength = 64;
        private const int MaxWhitelistEntries = 10000;
        private const int MaxItemAmount = 1000000;

        private WhitelistData whitelistData;
        private HashSet<ulong> whitelistSteamIds = new HashSet<ulong>();
        private TrapData trapData;
        private bool trapSpawnedThisNight;
        private MapData mapData;
        private PluginConfiguration pluginConfig;
        private Harmony harmony;
        private static RustManager Instance;
        private static readonly HttpClient HttpClient = new HttpClient();

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None
        };

        private static readonly string[] TrapWeaponShortnames =
        {
            "pistol.revolver",
            "pistol.semiauto",
            "pistol.python",
            "smg.2",
            "smg.thompson",
            "rifle.semiauto",
            "rifle.ak",
            "rifle.lr300",
            "shotgun.pump",
            "crossbow"
        };

        protected override void LoadDefaultConfig()
        {
            pluginConfig = PluginConfiguration.Default();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                pluginConfig = Config.ReadObject<PluginConfiguration>();
            }
            catch
            {
                pluginConfig = null;
            }

            EnsurePluginConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            EnsurePluginConfig();
            Config.WriteObject(pluginConfig, true);
        }

        [ConsoleCommand("rustmanager.status")]
        private void CommandStatus(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.status";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            Reply(arg, new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                plugin = "RustManager",
                version = PluginVersion,
                features = new[]
                {
                    "tc.auth",
                    "teams",
                    "players",
                    "player.items",
                    "player.item.remove",
                    "player.item.move",
                    "player.item.give",
                    "player.item.update",
                    "whitelist.status",
                    "whitelist.enable",
                    "whitelist.add",
                    "whitelist.remove",
                    "whitelist.clear",
                    "map.status",
                    "map.render",
                    "trap.status",
                    "trap.enable",
                    "trap.logs",
                    "trap.logs.clear",
                    "trap.cleanup"
                }
            });
        }

        private void Init()
        {
            Instance = this;
            InstallHarmonyPatches();
            LoadWhitelistData();
            LoadMapData();
            LoadTrapData();
            timer.Every(TrapTickSeconds, TrapTick);
        }

        private void Unload()
        {
            UninstallHarmonyPatches();
            CleanupTrapWorldItems();
            SaveMapData();
            SaveTrapData();
            Instance = null;
        }

        private void OnServerMessage(string message, string stackTrace, LogType type)
        {
            CaptureMapImageUrl(message);
        }

        private void EnsurePluginConfig()
        {
            if (pluginConfig == null)
            {
                pluginConfig = PluginConfiguration.Default();
            }

            if (pluginConfig.mapUpload == null)
            {
                pluginConfig.mapUpload = new MapUploadConfiguration();
            }
        }

        private void InstallHarmonyPatches()
        {
            try
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(RustManager).Assembly);
            }
            catch (Exception ex)
            {
                PrintError("Failed to install RustManager Harmony patches: " + ex);
            }
        }

        private void UninstallHarmonyPatches()
        {
            try
            {
                harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                PrintError("Failed to uninstall RustManager Harmony patches: " + ex);
            }
            finally
            {
                harmony = null;
            }
        }

        private static bool HandleRenderMapCommand(ConsoleSystem.Arg arg)
        {
            var plugin = Instance;
            return plugin == null || plugin.RenderAndUploadMap(arg);
        }

        private bool RenderAndUploadMap(ConsoleSystem.Arg arg)
        {
            try
            {
                var scale = arg.GetFloat(0, 1f);
                int imageWidth;
                int imageHeight;
                Color background;
                var image = MapImageRenderer.Render(out imageWidth, out imageHeight, out background, scale, lossy: false);
                if (image == null)
                {
                    arg.ReplyWith("Failed to render the map (is a map loaded now?)");
                    return false;
                }

                var worldSize = (int)global::World.Size;
                var worldSeed = global::World.Seed;
                var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, $"map_{worldSize}_{worldSeed}.png"));
                File.WriteAllBytes(fullPath, image);
                arg.ReplyWith("Saved map render to: " + fullPath);

                UploadMapImageToRustManager(image, worldSize);
                return false;
            }
            catch (Exception ex)
            {
                PrintError("RustManager patched world.rendermap failed: " + ex);
                return true;
            }
        }

        private async void UploadMapImageToRustManager(byte[] image, int worldSize)
        {
            EnsurePluginConfig();
            var mapUpload = pluginConfig.mapUpload;
            if (mapUpload?.enabled != true)
            {
                return;
            }

            var uploadUrl = (mapUpload.uploadUrl ?? string.Empty).Trim();
            var serverKey = (mapUpload.serverKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(serverKey))
            {
                PrintWarning("RustManager map upload is enabled but uploadUrl or serverKey is empty.");
                return;
            }

            try
            {
                using (var content = new ByteArrayContent(image))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    using (var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl))
                    {
                        request.Content = content;
                        request.Headers.Add("X-RustManager-Key", serverKey);
                        request.Headers.Add("X-RustManager-World-Size", worldSize.ToString());

                        using (var response = await HttpClient.SendAsync(request))
                        {
                            var text = await response.Content.ReadAsStringAsync();
                            if (!response.IsSuccessStatusCode)
                            {
                                PrintWarning($"RustManager map upload failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                                return;
                            }

                            var result = JsonConvert.DeserializeObject<MapUploadResponse>(text);
                            if (IsValidMapImageUrl(result?.imageUrl))
                            {
                                NextTick(() => StoreMapImageUrl(result.imageUrl));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PrintWarning("RustManager map upload failed: " + ex.Message);
            }
        }

        private object CanUserLogin(string name, string id, string ipAddress)
        {
            if (whitelistData == null)
            {
                LoadWhitelistData();
            }

            if (whitelistData?.enabled != true)
            {
                return null;
            }

            if (!TryParseStrictUlong(id, out var steamId))
            {
                return WhitelistRejectMessage;
            }

            if (ServerUsers.Is(steamId, ServerUsers.UserGroup.Owner) || ServerUsers.Is(steamId, ServerUsers.UserGroup.Moderator))
            {
                return null;
            }

            return whitelistSteamIds.Contains(steamId) ? null : WhitelistRejectMessage;
        }

        [ConsoleCommand("rustmanager.whitelist.status")]
        private void CommandWhitelistStatus(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.whitelist.status";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            Reply(arg, WhitelistPayload(command));
        }

        [ConsoleCommand("rustmanager.whitelist.enable")]
        private void CommandWhitelistEnable(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.whitelist.enable";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var enabledText = GetArg(arg, 0);
            if (!TryParseStrictBool(enabledText, out var enabled))
            {
                Reply(arg, Error(command, "invalid_enabled"));
                return;
            }

            EnsureWhitelistData();
            NormalizeWhitelistEntries();
            if (enabled && whitelistData.entries.Count == 0)
            {
                Reply(arg, Error(command, "empty_whitelist", new { enabled = false }));
                return;
            }

            whitelistData.enabled = enabled;
            SaveWhitelistData();

            Reply(arg, WhitelistPayload(command));
        }

        [ConsoleCommand("rustmanager.whitelist.add")]
        private void CommandWhitelistAdd(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.whitelist.add";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            EnsureWhitelistData();
            NormalizeWhitelistEntries();

            var existing = whitelistData.entries.FirstOrDefault(entry => entry != null && entry.steamId == steamId.ToString());
            if (existing == null)
            {
                if (whitelistData.entries.Count >= MaxWhitelistEntries)
                {
                    Reply(arg, Error(command, "whitelist_limit_reached", new { max = MaxWhitelistEntries }));
                    return;
                }

                existing = new WhitelistEntry
                {
                    steamId = steamId.ToString(),
                    addedAt = GeneratedAt()
                };
                whitelistData.entries.Add(existing);
            }

            var name = GetArgsText(arg, 1);
            existing.name = string.IsNullOrWhiteSpace(name) ? ResolvePlayerName(steamId, null) : SanitizeWhitelistName(name);
            if (string.IsNullOrWhiteSpace(existing.addedAt))
            {
                existing.addedAt = GeneratedAt();
            }

            SaveWhitelistData();

            Reply(arg, WhitelistPayload(command, new
            {
                type = "add",
                steamId = steamId.ToString()
            }));
        }

        [ConsoleCommand("rustmanager.whitelist.remove")]
        private void CommandWhitelistRemove(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.whitelist.remove";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            EnsureWhitelistData();
            NormalizeWhitelistEntries();

            if (whitelistData.enabled && whitelistData.entries.Count(entry => entry != null && entry.steamId != steamId.ToString()) == 0)
            {
                Reply(arg, Error(command, "would_empty_enabled_whitelist", new { steamId = steamId.ToString() }));
                return;
            }

            var removed = whitelistData.entries.RemoveAll(entry => entry != null && entry.steamId == steamId.ToString());
            if (removed < 1)
            {
                Reply(arg, Error(command, "entry_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            SaveWhitelistData();

            Reply(arg, WhitelistPayload(command, new
            {
                type = "remove",
                steamId = steamId.ToString()
            }));
        }

        [ConsoleCommand("rustmanager.whitelist.clear")]
        private void CommandWhitelistClear(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.whitelist.clear";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            EnsureWhitelistData();
            if (whitelistData.enabled)
            {
                Reply(arg, Error(command, "whitelist_enabled"));
                return;
            }

            whitelistData.entries.Clear();
            SaveWhitelistData();

            Reply(arg, WhitelistPayload(command, new
            {
                type = "clear"
            }));
        }

        [ConsoleCommand("rustmanager.map.status")]
        private void CommandMapStatus(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.map.status";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            Reply(arg, MapPayload(command));
        }

        [ConsoleCommand("rustmanager.map.render")]
        private void CommandMapRender(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.map.render";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            ConsoleSystem.Run(ConsoleSystem.Option.Server, "world.rendermap");

            Reply(arg, MapPayload(command, new
            {
                type = "render_requested"
            }));
        }

        [ConsoleCommand("rustmanager.trap.status")]
        private void CommandTrapStatus(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.trap.status";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            Reply(arg, TrapStatusPayload(command));
        }

        [ConsoleCommand("rustmanager.trap.enable")]
        private void CommandTrapEnable(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.trap.enable";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var enabledText = GetArg(arg, 0);
            if (!TryParseStrictBool(enabledText, out var enabled))
            {
                Reply(arg, Error(command, "invalid_enabled"));
                return;
            }

            EnsureTrapData();
            trapData.enabled = enabled;
            if (!enabled)
            {
                CleanupTrapWorldItems();
                trapSpawnedThisNight = false;
            }
            SaveTrapData();

            Reply(arg, TrapStatusPayload(command));
        }

        [ConsoleCommand("rustmanager.trap.logs")]
        private void CommandTrapLogs(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.trap.logs";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            Reply(arg, TrapLogsPayload(command));
        }

        [ConsoleCommand("rustmanager.trap.logs.clear")]
        private void CommandTrapLogsClear(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.trap.logs.clear";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            EnsureTrapData();
            trapData.detections.Clear();
            SaveTrapData();

            Reply(arg, TrapLogsPayload(command, new
            {
                type = "clear"
            }));
        }

        [ConsoleCommand("rustmanager.trap.cleanup")]
        private void CommandTrapCleanup(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.trap.cleanup";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var removed = CleanupTrapWorldItems();
            SaveTrapData();

            Reply(arg, TrapStatusPayload(command, new
            {
                type = "cleanup",
                removed
            }));
        }

        [ConsoleCommand("rustmanager.tc.auth")]
        private void CommandTcAuth(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.tc.auth";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var entityIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(entityIdText, out var entityId))
            {
                Reply(arg, Error(command, "invalid_entity_id"));
                return;
            }

            var cupboard = FindToolCupboard(entityId);
            if (cupboard == null)
            {
                Reply(arg, Error(command, "tc_not_found", new { entityId = entityId.ToString() }));
                return;
            }

            var nameCache = new Dictionary<ulong, string>();
            var authorizedPlayers = cupboard.authorizedPlayers
                .OrderBy(steamId => steamId)
                .Select(steamId => new
                {
                    steamId = steamId.ToString(),
                    name = ResolvePlayerName(steamId, nameCache)
                })
                .ToList();

            Reply(arg, new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                entityId = entityId.ToString(),
                position = Position(cupboard.transform.position),
                authorizedCount = authorizedPlayers.Count,
                authorizedPlayers
            });
        }

        [ConsoleCommand("rustmanager.teams")]
        private void CommandTeams(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.teams";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var relationshipManager = RelationshipManager.ServerInstance;
            if (relationshipManager?.teams == null)
            {
                Reply(arg, Error(command, "teams_unavailable"));
                return;
            }

            var teamIdText = GetArg(arg, 0);
            var cupboardsBySteamId = FindToolCupboardsByAuthorizedPlayer();
            var nameCache = new Dictionary<ulong, string>();
            var onlineIds = GetOnlinePlayerIds();
            var teams = new List<object>();

            if (!string.IsNullOrWhiteSpace(teamIdText))
            {
                if (!TryParseStrictUlong(teamIdText, out var teamId))
                {
                    Reply(arg, Error(command, "invalid_team_id"));
                    return;
                }

                var team = relationshipManager.FindTeam(teamId);
                if (team == null)
                {
                    Reply(arg, Error(command, "team_not_found", new { teamId = teamId.ToString() }));
                    return;
                }

                teams.Add(SerializeTeam(team, cupboardsBySteamId, nameCache, onlineIds));
            }
            else
            {
                teams = relationshipManager.teams
                    .OrderBy(pair => pair.Key)
                    .Select(pair => SerializeTeam(pair.Value, cupboardsBySteamId, nameCache, onlineIds))
                    .ToList();
            }

            Reply(arg, new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                teamCount = teams.Count,
                teams
            });
        }

        [ConsoleCommand("rustmanager.players")]
        private void CommandPlayers(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.players";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var nameCache = new Dictionary<ulong, string>();
            var onlineIds = GetOnlinePlayerIds();
            var allPlayers = BasePlayer.allPlayerList
                .Where(player => player != null && player.userID != 0)
                .GroupBy(player => (ulong)player.userID)
                .Select(group => group.First())
                .OrderByDescending(player => onlineIds.Contains((ulong)player.userID))
                .ThenBy(player => ResolvePlayerName((ulong)player.userID, nameCache) ?? player.userID.ToString())
                .ToList();
            var players = allPlayers.Select(player => SerializePlayer(player, nameCache, onlineIds)).ToList();

            Reply(arg, new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                playerCount = players.Count,
                onlineCount = allPlayers.Count(player => onlineIds.Contains((ulong)player.userID)),
                sleepingCount = BasePlayer.sleepingPlayerList.Count,
                players
            });
        }

        [ConsoleCommand("rustmanager.player.items")]
        private void CommandPlayerItems(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.player.items";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            var player = BasePlayer.FindAwakeOrSleeping(steamIdText);
            if (player == null)
            {
                Reply(arg, Error(command, "player_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            Reply(arg, PlayerItemsPayload(command, player));
        }

        [ConsoleCommand("rustmanager.player.item.remove")]
        private void CommandPlayerItemRemove(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.player.item.remove";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            var uidText = GetArg(arg, 1);
            if (!TryParseItemId(uidText, out var uid))
            {
                Reply(arg, Error(command, "invalid_item_uid"));
                return;
            }

            var player = BasePlayer.FindAwakeOrSleeping(steamIdText);
            if (player == null)
            {
                Reply(arg, Error(command, "player_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            var item = FindPlayerInventoryItem(player, uid);
            if (item == null)
            {
                Reply(arg, Error(command, "item_not_found", new { steamId = steamId.ToString(), uid = uidText }));
                return;
            }

            item.Remove();
            ItemManager.DoRemoves();
            RefreshPlayerInventory(player);

            Reply(arg, PlayerItemsPayload(command, player, new
            {
                type = "remove",
                uid = uidText
            }));
        }

        [ConsoleCommand("rustmanager.player.item.move")]
        private void CommandPlayerItemMove(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.player.item.move";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            var uidText = GetArg(arg, 1);
            if (!TryParseItemId(uidText, out var uid))
            {
                Reply(arg, Error(command, "invalid_item_uid"));
                return;
            }

            var containerName = GetArg(arg, 2)?.ToLowerInvariant();
            if (!IsSupportedContainer(containerName))
            {
                Reply(arg, Error(command, "invalid_container"));
                return;
            }

            var slotText = GetArg(arg, 3);
            if (!TryParseStrictInt(slotText, out var slot))
            {
                Reply(arg, Error(command, "invalid_slot"));
                return;
            }

            var player = BasePlayer.FindAwakeOrSleeping(steamIdText);
            if (player == null)
            {
                Reply(arg, Error(command, "player_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            var targetContainer = GetPlayerContainer(player, containerName);
            if (targetContainer == null)
            {
                Reply(arg, Error(command, MissingContainerError(containerName), new { steamId = steamId.ToString(), container = containerName }));
                return;
            }

            if (slot < 0 || slot >= targetContainer.capacity)
            {
                Reply(arg, Error(command, "slot_out_of_range", new { container = containerName, slot, capacity = targetContainer.capacity }));
                return;
            }

            var item = FindPlayerInventoryItem(player, uid);
            if (item == null)
            {
                Reply(arg, Error(command, "item_not_found", new { steamId = steamId.ToString(), uid = uidText }));
                return;
            }

            if (!item.MoveToContainer(targetContainer, slot, true, false, player, true))
            {
                Reply(arg, Error(command, "move_failed", new { uid = uidText, container = containerName, slot }));
                return;
            }

            RefreshPlayerInventory(player);

            Reply(arg, PlayerItemsPayload(command, player, new
            {
                type = "move",
                uid = uidText,
                container = containerName,
                slot
            }));
        }

        [ConsoleCommand("rustmanager.player.item.give")]
        private void CommandPlayerItemGive(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.player.item.give";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            var shortname = GetArg(arg, 1);
            if (!IsSafeItemShortname(shortname))
            {
                Reply(arg, Error(command, "invalid_shortname"));
                return;
            }

            var amountText = GetArg(arg, 2);
            if (!TryParseStrictInt(amountText, out var amount) || amount < 1 || amount > MaxItemAmount)
            {
                Reply(arg, Error(command, "invalid_amount"));
                return;
            }

            var containerName = GetArg(arg, 3)?.ToLowerInvariant();
            if (!IsSupportedContainer(containerName))
            {
                Reply(arg, Error(command, "invalid_container"));
                return;
            }

            var slotText = GetArg(arg, 4);
            if (!TryParseStrictInt(slotText, out var slot))
            {
                Reply(arg, Error(command, "invalid_slot"));
                return;
            }

            var player = BasePlayer.FindAwakeOrSleeping(steamIdText);
            if (player == null)
            {
                Reply(arg, Error(command, "player_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            var targetContainer = GetPlayerContainer(player, containerName);
            if (targetContainer == null)
            {
                Reply(arg, Error(command, MissingContainerError(containerName), new { steamId = steamId.ToString(), container = containerName }));
                return;
            }

            if (slot < 0 || slot >= targetContainer.capacity)
            {
                Reply(arg, Error(command, "slot_out_of_range", new { container = containerName, slot, capacity = targetContainer.capacity }));
                return;
            }

            var definition = ItemManager.FindItemDefinition(shortname);
            if (definition == null)
            {
                Reply(arg, Error(command, "item_definition_not_found", new { shortname }));
                return;
            }

            var item = ItemManager.Create(definition, amount);
            if (item == null)
            {
                Reply(arg, Error(command, "item_create_failed", new { shortname, amount }));
                return;
            }

            var replacedItem = targetContainer.itemList?.FirstOrDefault(existing => existing != null && existing.position == slot);
            var replacedUid = replacedItem?.uid.ToString();
            if (replacedItem != null)
            {
                replacedItem.Remove();
                ItemManager.DoRemoves();
            }

            if (!item.MoveToContainer(targetContainer, slot, true, false, player, true))
            {
                item.Remove();
                ItemManager.DoRemoves();
                Reply(arg, Error(command, "give_failed", new { shortname, amount, container = containerName, slot }));
                return;
            }

            RefreshPlayerInventory(player);

            Reply(arg, PlayerItemsPayload(command, player, new
            {
                type = "give",
                uid = item.uid.ToString(),
                replacedUid,
                shortname,
                amount,
                container = containerName,
                slot
            }));
        }

        [ConsoleCommand("rustmanager.player.item.update")]
        private void CommandPlayerItemUpdate(ConsoleSystem.Arg arg)
        {
            const string command = "rustmanager.player.item.update";

            if (!IsAllowed(arg))
            {
                Reply(arg, Error(command, "unauthorized"));
                return;
            }

            var steamIdText = GetArg(arg, 0);
            if (!TryParseStrictUlong(steamIdText, out var steamId))
            {
                Reply(arg, Error(command, "invalid_steam_id"));
                return;
            }

            var uidText = GetArg(arg, 1);
            if (!TryParseItemId(uidText, out var uid))
            {
                Reply(arg, Error(command, "invalid_item_uid"));
                return;
            }

            var field = GetArg(arg, 2)?.ToLowerInvariant();
            var valueText = GetArg(arg, 3);
            if (string.IsNullOrWhiteSpace(field))
            {
                Reply(arg, Error(command, "invalid_field"));
                return;
            }

            var player = BasePlayer.FindAwakeOrSleeping(steamIdText);
            if (player == null)
            {
                Reply(arg, Error(command, "player_not_found", new { steamId = steamId.ToString() }));
                return;
            }

            var item = FindPlayerInventoryItem(player, uid);
            if (item == null)
            {
                Reply(arg, Error(command, "item_not_found", new { steamId = steamId.ToString(), uid = uidText }));
                return;
            }

            object updatedValue;
            switch (field)
            {
                case "amount":
                    if (!TryParseStrictInt(valueText, out var amount) || amount < 1 || amount > MaxItemAmount)
                    {
                        Reply(arg, Error(command, "invalid_amount"));
                        return;
                    }

                    item.amount = amount;
                    updatedValue = amount;
                    break;

                case "condition":
                    if (!item.hasCondition)
                    {
                        Reply(arg, Error(command, "item_has_no_condition", new { uid = uidText }));
                        return;
                    }

                    if (!TryParseStrictFloat(valueText, out var condition) || condition < 0f)
                    {
                        Reply(arg, Error(command, "invalid_condition"));
                        return;
                    }

                    item.condition = condition;
                    updatedValue = item.condition;
                    break;

                case "maxcondition":
                case "max_condition":
                    if (!item.hasCondition)
                    {
                        Reply(arg, Error(command, "item_has_no_condition", new { uid = uidText }));
                        return;
                    }

                    if (!TryParseStrictFloat(valueText, out var maxCondition) || maxCondition < 0f)
                    {
                        Reply(arg, Error(command, "invalid_max_condition"));
                        return;
                    }

                    item.maxCondition = maxCondition;
                    if (item.condition > item.maxCondition)
                    {
                        item.condition = item.maxCondition;
                    }
                    updatedValue = item.maxCondition;
                    field = "maxcondition";
                    break;

                case "ammo":
                    if (!TryParseStrictInt(valueText, out var ammo) || ammo < 0)
                    {
                        Reply(arg, Error(command, "invalid_ammo"));
                        return;
                    }

                    var projectile = item.GetHeldEntity() as BaseProjectile;
                    var magazine = projectile?.primaryMagazine;
                    if (magazine == null)
                    {
                        Reply(arg, Error(command, "item_has_no_ammo", new { uid = uidText }));
                        return;
                    }

                    if (ammo > magazine.capacity)
                    {
                        Reply(arg, Error(command, "ammo_out_of_range", new { uid = uidText, ammo, capacity = magazine.capacity }));
                        return;
                    }

                    magazine.contents = ammo;
                    updatedValue = ammo;
                    break;

                default:
                    Reply(arg, Error(command, "invalid_field", new { field }));
                    return;
            }

            item.MarkDirty();
            RefreshPlayerInventory(player);

            Reply(arg, PlayerItemsPayload(command, player, new
            {
                type = "update",
                uid = uidText,
                field,
                value = updatedValue
            }));
        }

        private static bool IsAllowed(ConsoleSystem.Arg arg)
        {
            return arg?.Connection == null || arg.Connection.authLevel >= 2;
        }

        private static string GetArg(ConsoleSystem.Arg arg, int index)
        {
            if (arg?.Args == null || arg.Args.Length <= index || arg.Args[index] == null)
            {
                return null;
            }

            var value = arg.Args[index].ToString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxArgLength)
            {
                return null;
            }

            return value.Trim();
        }

        private static string GetArgsText(ConsoleSystem.Arg arg, int startIndex)
        {
            if (arg?.Args == null || arg.Args.Length <= startIndex)
            {
                return null;
            }

            var parts = new List<string>();
            for (var i = startIndex; i < arg.Args.Length; i++)
            {
                var part = arg.Args[i].ToString();
                if (!string.IsNullOrWhiteSpace(part))
                {
                    parts.Add(part.Trim());
                }
            }

            var value = parts.Count == 0 ? null : string.Join(" ", parts.ToArray());
            return value != null && value.Length <= MaxArgLength ? value : null;
        }

        private static bool TryParseStrictUlong(string value, out ulong parsed)
        {
            parsed = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return ulong.TryParse(value, out parsed) && parsed > 0;
        }

        private static bool TryParseStrictInt(string value, out int parsed)
        {
            parsed = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return int.TryParse(value, out parsed);
        }

        private static bool TryParseStrictBool(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "on":
                case "yes":
                case "enable":
                case "enabled":
                    parsed = true;
                    return true;
                case "false":
                case "0":
                case "off":
                case "no":
                case "disable":
                case "disabled":
                    parsed = false;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseStrictFloat(string value, out float parsed)
        {
            parsed = 0f;
            return !string.IsNullOrWhiteSpace(value)
                && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed)
                && !float.IsNaN(parsed)
                && !float.IsInfinity(parsed);
        }

        private static bool TryParseItemId(string value, out ItemId itemId)
        {
            itemId = default(ItemId);
            if (!TryParseStrictUlong(value, out var raw))
            {
                return false;
            }

            itemId = new ItemId(raw);
            return itemId.IsValid;
        }

        private static bool IsSafeItemShortname(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        private static string SanitizeWhitelistName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var chars = new List<char>();
            var trimmed = value.Trim();
            for (var i = 0; i < trimmed.Length && chars.Count < MaxWhitelistNameLength; i++)
            {
                var c = trimmed[i];
                if (!char.IsControl(c))
                {
                    chars.Add(c);
                }
            }

            return chars.Count == 0 ? null : new string(chars.ToArray());
        }

        private static bool IsSupportedContainer(string value)
        {
            return value == ContainerInventory || value == ContainerBelt || value == ContainerWear || value == ContainerBackpack;
        }

        private static string MissingContainerError(string value)
        {
            return value == ContainerBackpack ? "backpack_not_equipped" : "container_not_found";
        }

        private void LoadWhitelistData()
        {
            try
            {
                whitelistData = Interface.Oxide.DataFileSystem.ReadObject<WhitelistData>(WhitelistDataFile);
            }
            catch
            {
                whitelistData = null;
            }

            EnsureWhitelistData();
            NormalizeWhitelistEntries();
            RebuildWhitelistLookup();
        }

        private void SaveWhitelistData()
        {
            EnsureWhitelistData();
            NormalizeWhitelistEntries();
            Interface.Oxide.DataFileSystem.WriteObject(WhitelistDataFile, whitelistData);
            RebuildWhitelistLookup();
        }

        private void EnsureWhitelistData()
        {
            if (whitelistData == null)
            {
                whitelistData = new WhitelistData();
            }

            if (whitelistData.entries == null)
            {
                whitelistData.entries = new List<WhitelistEntry>();
            }

            if (whitelistSteamIds == null)
            {
                whitelistSteamIds = new HashSet<ulong>();
            }
        }

        private void NormalizeWhitelistEntries()
        {
            EnsureWhitelistData();
            var bySteamId = new Dictionary<ulong, WhitelistEntry>();
            foreach (var entry in whitelistData.entries)
            {
                if (entry == null || !TryParseStrictUlong(entry.steamId, out var steamId))
                {
                    continue;
                }

                entry.steamId = steamId.ToString();
                entry.name = SanitizeWhitelistName(entry.name);
                if (!bySteamId.ContainsKey(steamId))
                {
                    bySteamId[steamId] = entry;
                }
            }

            whitelistData.entries = bySteamId
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToList();
        }

        private void RebuildWhitelistLookup()
        {
            EnsureWhitelistData();
            whitelistSteamIds.Clear();
            foreach (var entry in whitelistData.entries)
            {
                if (entry != null && TryParseStrictUlong(entry.steamId, out var steamId))
                {
                    whitelistSteamIds.Add(steamId);
                }
            }
        }

        private object WhitelistPayload(string command, object action = null)
        {
            EnsureWhitelistData();
            var nameCache = new Dictionary<ulong, string>();
            var entries = whitelistData.entries
                .Where(entry => entry != null && TryParseStrictUlong(entry.steamId, out var _))
                .OrderBy(entry => entry.steamId)
                .Select(entry => new
                {
                    steamId = entry.steamId,
                    name = string.IsNullOrWhiteSpace(entry.name) && TryParseStrictUlong(entry.steamId, out var steamId)
                        ? ResolvePlayerName(steamId, nameCache)
                        : entry.name,
                    addedAt = string.IsNullOrWhiteSpace(entry.addedAt) ? null : entry.addedAt
                })
                .ToList();

            return new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                enabled = whitelistData.enabled,
                count = entries.Count,
                action,
                entries
            };
        }

        private void LoadMapData()
        {
            try
            {
                mapData = Interface.Oxide.DataFileSystem.ReadObject<MapData>(MapDataFile);
            }
            catch
            {
                mapData = null;
            }

            EnsureMapData();
        }

        private void SaveMapData()
        {
            EnsureMapData();
            Interface.Oxide.DataFileSystem.WriteObject(MapDataFile, mapData);
        }

        private void EnsureMapData()
        {
            if (mapData == null)
            {
                mapData = new MapData();
            }

            if (!IsValidMapImageUrl(mapData.imageUrl))
            {
                mapData.imageUrl = null;
                mapData.worldSize = null;
                mapData.updatedAt = null;
            }
        }

        private void CaptureMapImageUrl(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var prefixIndex = message.IndexOf(MapCacheLogPrefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
            {
                return;
            }

            var urlStart = message.IndexOf("https://", prefixIndex, StringComparison.OrdinalIgnoreCase);
            if (urlStart < 0)
            {
                return;
            }

            var url = message.Substring(urlStart).Trim().TrimEnd(',', ';', ')', ']', '}', '>', '\'', '"');
            if (!IsValidMapImageUrl(url))
            {
                return;
            }

            StoreMapImageUrl(url);
        }

        private void StoreMapImageUrl(string url)
        {
            if (!IsValidMapImageUrl(url))
            {
                return;
            }

            EnsureMapData();
            if (mapData.imageUrl == url)
            {
                return;
            }

            mapData.imageUrl = url;
            mapData.worldSize = ParseWorldSizeFromMapUrl(url);
            mapData.updatedAt = GeneratedAt();
            SaveMapData();
        }

        private object MapPayload(string command, object action = null)
        {
            EnsureMapData();
            EnsurePluginConfig();
            var upload = pluginConfig.mapUpload;
            return new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                imageUrl = mapData.imageUrl,
                worldSize = mapData.worldSize,
                updatedAt = mapData.updatedAt,
                uploadEnabled = upload?.enabled == true,
                uploadConfigured = upload?.enabled == true
                    && !string.IsNullOrWhiteSpace(upload.uploadUrl)
                    && !string.IsNullOrWhiteSpace(upload.serverKey),
                action
            };
        }

        private static bool IsValidMapImageUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && url.Length <= 2048
                && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        }

        private static int? ParseWorldSizeFromMapUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var marker = "proceduralmap.";
            var start = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = start;
            while (end < url.Length && char.IsDigit(url[end]))
            {
                end++;
            }

            if (end <= start)
            {
                return null;
            }

            if (!int.TryParse(url.Substring(start, end - start), out var worldSize))
            {
                return null;
            }

            return worldSize > 0 ? (int?)worldSize : null;
        }

        private void LoadTrapData()
        {
            try
            {
                trapData = Interface.Oxide.DataFileSystem.ReadObject<TrapData>(TrapDataFile);
            }
            catch
            {
                trapData = null;
            }

            EnsureTrapData();
            NormalizeTrapData();
        }

        private void SaveTrapData()
        {
            EnsureTrapData();
            NormalizeTrapData();
            Interface.Oxide.DataFileSystem.WriteObject(TrapDataFile, trapData);
        }

        private void EnsureTrapData()
        {
            if (trapData == null)
            {
                trapData = new TrapData();
            }

            if (trapData.activeTraps == null)
            {
                trapData.activeTraps = new List<TrapItem>();
            }

            if (trapData.detections == null)
            {
                trapData.detections = new List<TrapDetection>();
            }
        }

        private void NormalizeTrapData()
        {
            EnsureTrapData();
            trapData.activeTraps = trapData.activeTraps
                .Where(trap => trap != null && TryParseItemId(trap.itemUid, out var _))
                .GroupBy(trap => trap.itemUid)
                .Select(group => group.First())
                .Take(MaxActiveTraps)
                .ToList();
            trapData.detections = trapData.detections
                .Where(detection => detection != null && TryParseStrictUlong(detection.steamId, out var _) && TryParseItemId(detection.itemUid, out var _))
                .GroupBy(detection => detection.itemUid)
                .Select(group => group.First())
                .OrderByDescending(detection => detection.detectedAt)
                .Take(MaxTrapDetections)
                .ToList();
        }

        private void TrapTick()
        {
            EnsureTrapData();
            if (!trapData.enabled)
            {
                return;
            }

            var isNight = IsTrapNight();
            if (!isNight)
            {
                if (trapSpawnedThisNight || trapData.activeTraps.Count > 0)
                {
                    CleanupTrapWorldItems();
                    trapSpawnedThisNight = false;
                    SaveTrapData();
                }
                return;
            }

            if (!trapSpawnedThisNight && trapData.activeTraps.Count == 0)
            {
                SpawnTrapItemsForNight();
                trapSpawnedThisNight = true;
                SaveTrapData();
            }

            DetectTrapItemsInInventories();
        }

        private static bool IsTrapNight()
        {
            var hour = ConVar.Env.time;
            return hour >= TrapNightStartHour || hour < TrapNightEndHour;
        }

        private void SpawnTrapItemsForNight()
        {
            EnsureTrapData();
            CleanupTrapWorldItems();

            var spawned = 0;
            for (var i = 0; i < MaxTrapItemsPerNight; i++)
            {
                if (!TryFindTrapPosition(out var position))
                {
                    continue;
                }

                var shortname = TrapWeaponShortnames[UnityEngine.Random.Range(0, TrapWeaponShortnames.Length)];
                var item = ItemManager.CreateByName(shortname, 1);
                if (item == null)
                {
                    continue;
                }

                if (item.hasCondition)
                {
                    item.conditionNormalized = UnityEngine.Random.Range(0.08f, 0.35f);
                }

                item.text = "rustmanager.trap";
                item.MarkDirty();

                var entity = item.Drop(position, Vector3.zero) as DroppedItem;
                if (entity == null)
                {
                    item.Remove();
                    ItemManager.DoRemoves();
                    continue;
                }

                entity.NeverCombine = true;
                trapData.activeTraps.Add(new TrapItem
                {
                    itemUid = item.uid.ToString(),
                    entityId = entity.net.ID.ToString(),
                    shortname = shortname,
                    position = PositionData(position),
                    spawnedAt = GeneratedAt()
                });
                spawned++;
            }

            if (spawned == 0)
            {
                trapSpawnedThisNight = false;
            }
        }

        private static bool TryFindTrapPosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (TerrainMeta.HeightMap == null || TerrainMeta.TopologyMap == null)
            {
                return false;
            }

            var halfX = TerrainMeta.Size.x * 0.5f;
            var halfZ = TerrainMeta.Size.z * 0.5f;
            for (var i = 0; i < MaxTrapPositionAttempts; i++)
            {
                var x = UnityEngine.Random.Range(-halfX, halfX);
                var z = UnityEngine.Random.Range(-halfZ, halfZ);
                var terrainPosition = new Vector3(x, 0f, z);
                terrainPosition.y = TerrainMeta.HeightMap.GetHeight(terrainPosition);

                if (!TerrainMeta.TopologyMap.GetTopology(terrainPosition, TrapWaterTopologyMask))
                {
                    continue;
                }

                var waterInfo = WaterLevel.GetWaterInfo(terrainPosition, true, true);
                if (!waterInfo.isValid || waterInfo.overallDepth < 0.8f || waterInfo.overallDepth > 8f)
                {
                    continue;
                }

                var underwaterOffset = UnityEngine.Random.Range(0.45f, Mathf.Min(2.5f, waterInfo.overallDepth - 0.1f));
                position = new Vector3(x, waterInfo.surfaceLevel - underwaterOffset, z);
                return true;
            }

            return false;
        }

        private void DetectTrapItemsInInventories()
        {
            EnsureTrapData();
            if (trapData.activeTraps.Count == 0)
            {
                return;
            }

            var nameCache = new Dictionary<ulong, string>();
            var remaining = new List<TrapItem>();
            var changed = false;
            var players = BasePlayer.allPlayerList
                .Where(player => player != null && player.userID != 0)
                .GroupBy(player => (ulong)player.userID)
                .Select(group => group.First())
                .ToList();

            foreach (var trap in trapData.activeTraps)
            {
                if (trap == null || !TryParseItemId(trap.itemUid, out var itemUid))
                {
                    changed = true;
                    continue;
                }

                var detected = false;
                foreach (var player in players)
                {
                    var item = FindPlayerInventoryItem(player, itemUid);
                    if (item == null)
                    {
                        continue;
                    }

                    var steamId = (ulong)player.userID;
                    if (!trapData.detections.Any(detection => detection != null && detection.itemUid == trap.itemUid))
                    {
                        trapData.detections.Insert(0, new TrapDetection
                        {
                            steamId = steamId.ToString(),
                            name = string.IsNullOrWhiteSpace(player.displayName) ? ResolvePlayerName(steamId, nameCache) : player.displayName,
                            itemUid = trap.itemUid,
                            shortname = trap.shortname,
                            position = trap.position,
                            detectedAt = GeneratedAt()
                        });
                    }

                    detected = true;
                    changed = true;
                    break;
                }

                if (!detected)
                {
                    remaining.Add(trap);
                }
            }

            if (changed)
            {
                trapData.activeTraps = remaining;
                SaveTrapData();
            }
        }

        private int CleanupTrapWorldItems()
        {
            EnsureTrapData();
            var removed = 0;
            foreach (var trap in trapData.activeTraps)
            {
                if (trap == null || !TryParseStrictUlong(trap.entityId, out var entityId))
                {
                    continue;
                }

                var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as DroppedItem;
                if (entity == null || entity.IsDestroyed)
                {
                    continue;
                }

                entity.Kill();
                removed++;
            }

            trapData.activeTraps.Clear();
            return removed;
        }

        private object TrapStatusPayload(string command, object action = null)
        {
            EnsureTrapData();
            return new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                enabled = trapData.enabled,
                isNight = IsTrapNight(),
                activeCount = trapData.activeTraps.Count,
                detectionCount = trapData.detections.Count,
                lastDetectionAt = trapData.detections.Count > 0 ? trapData.detections[0].detectedAt : null,
                action,
                activeTraps = trapData.activeTraps
            };
        }

        private object TrapLogsPayload(string command, object action = null)
        {
            EnsureTrapData();
            return new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                enabled = trapData.enabled,
                activeCount = trapData.activeTraps.Count,
                detectionCount = trapData.detections.Count,
                action,
                detections = trapData.detections
            };
        }

        private static ItemContainer GetPlayerContainer(BasePlayer player, string value)
        {
            if (player?.inventory == null)
            {
                return null;
            }

            switch (value)
            {
                case ContainerInventory:
                    return player.inventory.containerMain;
                case ContainerBelt:
                    return player.inventory.containerBelt;
                case ContainerWear:
                    return player.inventory.containerWear;
                case ContainerBackpack:
                    return player.inventory.GetContainer(PlayerInventory.Type.BackpackContents);
                default:
                    return null;
            }
        }

        private static Item FindPlayerInventoryItem(BasePlayer player, ItemId uid)
        {
            if (player?.inventory == null || !uid.IsValid)
            {
                return null;
            }

            var item = player.inventory.containerMain?.FindItemByUID(uid);
            if (item != null && item.IsValid())
            {
                return item;
            }

            item = player.inventory.containerBelt?.FindItemByUID(uid);
            if (item != null && item.IsValid())
            {
                return item;
            }

            item = player.inventory.containerWear?.FindItemByUID(uid);
            if (item != null && item.IsValid())
            {
                return item;
            }

            item = player.inventory.GetContainer(PlayerInventory.Type.BackpackContents)?.FindItemByUID(uid);
            return item != null && item.IsValid() ? item : null;
        }

        private static void RefreshPlayerInventory(BasePlayer player)
        {
            if (player?.inventory == null)
            {
                return;
            }

            player.inventory.ServerUpdate(0f);
            if (BasePlayer.FindByID((ulong)player.userID) != null)
            {
                player.inventory.SendSnapshot();
            }
        }

        private static BuildingPrivlidge FindToolCupboard(ulong entityId)
        {
            return BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as BuildingPrivlidge;
        }

        private static Dictionary<ulong, List<BuildingPrivlidge>> FindToolCupboardsByAuthorizedPlayer()
        {
            var result = new Dictionary<ulong, List<BuildingPrivlidge>>();
            var cupboards = BaseNetworkable.serverEntities
                .OfType<BuildingPrivlidge>()
                .Where(cupboard => cupboard != null && !cupboard.IsDestroyed)
                .OrderBy(cupboard => cupboard.net.ID.ToString())
                .ToList();

            foreach (var cupboard in cupboards)
            {
                if (cupboard.authorizedPlayers == null)
                {
                    continue;
                }

                foreach (var steamId in cupboard.authorizedPlayers)
                {
                    if (!result.TryGetValue(steamId, out var playerCupboards))
                    {
                        playerCupboards = new List<BuildingPrivlidge>();
                        result[steamId] = playerCupboards;
                    }

                    playerCupboards.Add(cupboard);
                }
            }

            return result;
        }

        private static object Position(Vector3 position)
        {
            return new
            {
                x = Math.Round(position.x, 3),
                y = Math.Round(position.y, 3),
                z = Math.Round(position.z, 3)
            };
        }

        private static PositionSnapshot PositionData(Vector3 position)
        {
            return new PositionSnapshot
            {
                x = (float)Math.Round(position.x, 3),
                y = (float)Math.Round(position.y, 3),
                z = (float)Math.Round(position.z, 3)
            };
        }

        private static object SerializeTeam(RelationshipManager.PlayerTeam team, Dictionary<ulong, List<BuildingPrivlidge>> cupboardsBySteamId, Dictionary<ulong, string> nameCache, HashSet<ulong> onlineIds)
        {
            var memberIds = team.members ?? new List<ulong>();
            var members = memberIds
                .OrderBy(steamId => steamId)
                .Select(steamId => SerializeTeamMember(team, steamId, cupboardsBySteamId, nameCache, onlineIds))
                .ToList();

            return new
            {
                teamId = team.teamID.ToString(),
                teamName = string.IsNullOrWhiteSpace(team.teamName) ? null : team.teamName,
                leaderSteamId = team.teamLeader.ToString(),
                leaderName = ResolvePlayerName(team.teamLeader, nameCache),
                memberCount = members.Count,
                members
            };
        }

        private static object SerializeTeamMember(RelationshipManager.PlayerTeam team, ulong steamId, Dictionary<ulong, List<BuildingPrivlidge>> cupboardsBySteamId, Dictionary<ulong, string> nameCache, HashSet<ulong> onlineIds)
        {
            var player = RelationshipManager.FindByID(steamId) ?? BasePlayer.FindAwakeOrSleeping(steamId.ToString());
            cupboardsBySteamId.TryGetValue(steamId, out var playerCupboards);
            var bases = (playerCupboards ?? new List<BuildingPrivlidge>())
                .Select(cupboard => SerializeToolCupboard(cupboard, nameCache))
                .ToList();

            return new
            {
                steamId = steamId.ToString(),
                name = !string.IsNullOrWhiteSpace(player?.displayName) ? player.displayName : ResolvePlayerName(steamId, nameCache),
                isLeader = team.teamLeader == steamId,
                online = onlineIds.Contains(steamId),
                sleeping = player != null && player.IsSleeping(),
                baseCount = bases.Count,
                bases
            };
        }

        private static object SerializeToolCupboard(BuildingPrivlidge cupboard, Dictionary<ulong, string> nameCache)
        {
            var authorizedPlayers = cupboard.authorizedPlayers == null
                ? new List<object>()
                : cupboard.authorizedPlayers
                    .OrderBy(steamId => steamId)
                    .Select(steamId => new
                    {
                        steamId = steamId.ToString(),
                        name = ResolvePlayerName(steamId, nameCache)
                    })
                    .Cast<object>()
                    .ToList();

            return new
            {
                entityId = cupboard.net.ID.ToString(),
                position = Position(cupboard.transform.position),
                authorizedCount = authorizedPlayers.Count,
                authorizedPlayers
            };
        }

        private static HashSet<ulong> GetOnlinePlayerIds()
        {
            return new HashSet<ulong>(BasePlayer.activePlayerList
                .Where(player => player != null)
                .Select(player => (ulong)player.userID));
        }

        private static object SerializePlayer(BasePlayer player, Dictionary<ulong, string> nameCache, HashSet<ulong> onlineIds)
        {
            var steamId = (ulong)player.userID;
            var online = onlineIds.Contains(steamId);
            var position = player.transform == null ? Vector3.zero : player.transform.position;

            return new
            {
                SteamID = steamId.ToString(),
                DisplayName = string.IsNullOrWhiteSpace(player.displayName) ? ResolvePlayerName(steamId, nameCache) : player.displayName,
                IsOnline = online,
                IsSleeping = player.IsSleeping(),
                Health = player.health,
                Ping = 0,
                LastIP = string.Empty,
                LastConnected = 0,
                FirstConnected = 0,
                TotalConnections = 0,
                X = Math.Round(position.x, 3),
                Y = Math.Round(position.y, 3),
                Z = Math.Round(position.z, 3),
                CurrentTeam = player.currentTeam.ToString()
            };
        }

        private static List<object> SerializeContainer(ItemContainer container, int depth, Func<Item, bool> include = null)
        {
            var items = new List<object>();
            if (container?.itemList == null)
            {
                return items;
            }

            var sourceItems = include == null
                ? container.itemList.Where(item => item != null).ToList()
                : container.itemList.Where(item => item != null && include(item)).ToList();
            sourceItems.Sort((left, right) => left.position.CompareTo(right.position));

            foreach (var item in sourceItems)
            {
                items.Add(SerializeItem(item, depth));
            }

            return items;
        }

        private static object PlayerItemsPayload(string command, BasePlayer player, object action = null)
        {
            var steamId = (ulong)player.userID;
            return new
            {
                ok = true,
                command,
                generatedAt = GeneratedAt(),
                action,
                player = new
                {
                    steamId = steamId.ToString(),
                    name = string.IsNullOrWhiteSpace(player.displayName) ? ResolvePlayerName(steamId, null) : player.displayName,
                    online = BasePlayer.FindByID(steamId) != null,
                    sleeping = player.IsSleeping()
                },
                containers = new
                {
                    inventory = SerializeContainer(player.inventory?.containerMain, 0),
                    belt = SerializeContainer(player.inventory?.containerBelt, 0),
                    wear = SerializeContainer(player.inventory?.containerWear, 0, item => item.position != BackpackSlot),
                    backpack = SerializeItem(player.inventory?.GetAnyBackpack(), 0)
                }
            };
        }

        private static object SerializeItem(Item item, int depth)
        {
            if (item == null)
            {
                return null;
            }

            var definition = item.info;
            var hasCondition = item.hasCondition;

            return new
            {
                shortname = definition?.shortname,
                displayName = definition?.displayName?.english,
                itemId = definition?.itemid ?? 0,
                uid = item.uid.ToString(),
                amount = item.amount,
                slot = item.position,
                skin = item.skin.ToString(),
                condition = hasCondition ? (float?)item.condition : null,
                maxCondition = hasCondition ? (float?)item.maxCondition : null,
                conditionNormalized = hasCondition ? (float?)item.conditionNormalized : null,
                ammo = SerializeAmmo(item),
                contentsCapacity = item.contents?.capacity ?? 0,
                contents = depth >= MaxNestedItemDepth ? new List<object>() : SerializeContainer(item.contents, depth + 1)
            };
        }

        private static object SerializeAmmo(Item item)
        {
            var projectile = item.GetHeldEntity() as BaseProjectile;
            var magazine = projectile?.primaryMagazine;
            if (magazine == null)
            {
                return null;
            }

            return new
            {
                amount = magazine.contents,
                capacity = magazine.capacity,
                shortname = magazine.ammoType?.shortname,
                itemId = magazine.ammoType?.itemid ?? 0
            };
        }

        private static string ResolvePlayerName(ulong steamId, Dictionary<ulong, string> cache)
        {
            if (cache != null && cache.TryGetValue(steamId, out var cachedName))
            {
                return cachedName;
            }

            var player = BasePlayer.FindByID(steamId);
            if (!string.IsNullOrWhiteSpace(player?.displayName))
            {
                if (cache != null)
                {
                    cache[steamId] = player.displayName;
                }
                return player.displayName;
            }

            try
            {
                var name = SingletonComponent<ServerMgr>.Instance?.persistance?.GetPlayerName(steamId);
                name = string.IsNullOrWhiteSpace(name) ? null : name;
                if (cache != null)
                {
                    cache[steamId] = name;
                }
                return name;
            }
            catch
            {
                if (cache != null)
                {
                    cache[steamId] = null;
                }
                return null;
            }
        }

        private static object Error(string command, string error, object details = null)
        {
            return new
            {
                ok = false,
                command,
                generatedAt = GeneratedAt(),
                error,
                details
            };
        }

        private static string GeneratedAt()
        {
            return DateTime.UtcNow.ToString("o");
        }

        private static void Reply(ConsoleSystem.Arg arg, object payload)
        {
            arg.ReplyWith(JsonConvert.SerializeObject(payload, JsonSettings));
        }

        [HarmonyPatch(typeof(ConVar.World), nameof(ConVar.World.rendermap))]
        private static class WorldRenderMapPatch
        {
            private static bool Prefix(ConsoleSystem.Arg arg)
            {
                return HandleRenderMapCommand(arg);
            }
        }

        private class WhitelistData
        {
            public bool enabled = false;
            public List<WhitelistEntry> entries = new List<WhitelistEntry>();
        }

        private class WhitelistEntry
        {
            public string steamId;
            public string name;
            public string addedAt;
        }

        private class PluginConfiguration
        {
            public MapUploadConfiguration mapUpload = new MapUploadConfiguration();

            public static PluginConfiguration Default()
            {
                return new PluginConfiguration
                {
                    mapUpload = new MapUploadConfiguration
                    {
                        enabled = false,
                        uploadUrl = "https://rustmanager.io/api/rust-map/upload",
                        serverKey = ""
                    }
                };
            }
        }

        private class MapUploadConfiguration
        {
            public bool enabled = false;
            public string uploadUrl = "";
            public string serverKey = "";
        }

        private class MapData
        {
            public string imageUrl;
            public int? worldSize;
            public string updatedAt;
        }

        private class MapUploadResponse
        {
            public bool ok;
            public string imageUrl;
        }

        private class TrapData
        {
            public bool enabled = false;
            public List<TrapItem> activeTraps = new List<TrapItem>();
            public List<TrapDetection> detections = new List<TrapDetection>();
        }

        private class TrapItem
        {
            public string itemUid;
            public string entityId;
            public string shortname;
            public PositionSnapshot position;
            public string spawnedAt;
        }

        private class TrapDetection
        {
            public string steamId;
            public string name;
            public string itemUid;
            public string shortname;
            public PositionSnapshot position;
            public string detectedAt;
        }

        private class PositionSnapshot
        {
            public float x;
            public float y;
            public float z;
        }
    }
}
