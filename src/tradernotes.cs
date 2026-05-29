//v1.0.7
using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.GameContent; 
using Newtonsoft.Json;

namespace TraderMapTooltip
{
    public class TraderNotesConfig {
        public string Icon = "trader";
        public int IconSize = 28;
        public string IconColor = "#d4d87f";
        public string CurrencyName = "";
        public bool LiveUpdate = false;
        public int UpdateIntervalSeconds = 15;

        public string ColorTraderFunds = "#deffa1"; 
        public string ColorSelling = "#40a746"; 
        public string ColorBuying = "#deffa1";  
        public string ColorDemand = "#9d9d9d";    
        public string ColorItemName = "#e5e6de";  
        public string ColorItemStack = "#9d9d9d"; 
        public string ColorPrice = "#deebc7";
        public string ColorDistance = "#7fb3d8";
        public int IconRenderZIndex = 100; 
    }

    public class CachedTradeItem {
        public string Name { get; set; }
        public int Stock { get; set; }     
        public int StackSize { get; set; } 
        public int Price { get; set; }
        public bool IsSoldOut { get; set; }
    }

    public class SavedTrader {
        public string Name { get; set; }
        public string TraderType { get; set; }
        public long EntityId { get; set; }
        public int Money { get; set; }
        public bool IsDiscovered { get; set; } = false;
        public double LastUpdatedTotalDays { get; set; }
        public double NextRefreshTotalDays { get; set; }
        public List<CachedTradeItem> Sells { get; set; } = new List<CachedTradeItem>();
        public List<CachedTradeItem> Wants { get; set; } = new List<CachedTradeItem>();
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class TraderMapMod : ModSystem {
        private ICoreClientAPI capi;
        public static Dictionary<long, SavedTrader> Cache = new Dictionary<long, SavedTrader>();
        public static TraderNotesConfig Config;
        public static long LatestLayerId = 0;
        private string savePath;
        private bool wasTraderInventoryOpen = false;
        private bool isMapLayerRegistered = false;
        private long lastCleanupCheckMs = 0;
        private const int CleanupIntervalMs = 10000;
        private const double CleanupRangeBlocks = 32.0;
        public static long HoveredTraderId = 0;
        private const string DeleteHotkeyCode = "tradernotes-delete-marker";

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api) {
            this.capi = api;
            try {
                Config = api.LoadModConfig<TraderNotesConfig>("TraderNotesConfig.json") ?? new TraderNotesConfig();
                api.StoreModConfig(Config, "TraderNotesConfig.json");
            } catch { Config = new TraderNotesConfig(); }

            api.Event.LevelFinalize += OnLevelFinalize;
            api.Event.LeaveWorld += SaveCache;
            api.Event.BlockTexturesLoaded += OnBlockTexturesLoaded;
            api.Event.RegisterGameTickListener(OnClientTick, 500);

            try {
                api.Input.RegisterHotKey(DeleteHotkeyCode, Lang.Get("tradernotes:delete-marker-hotkey"), GlKeys.Delete, HotkeyType.GUIOrOtherControls);
                api.Input.SetHotKeyHandler(DeleteHotkeyCode, OnDeleteMarkerHotkey);
            } catch { }
        }

        private bool OnDeleteMarkerHotkey(KeyCombination comb) {
            try {
                if (HoveredTraderId == 0) return false;
                if (!Cache.ContainsKey(HoveredTraderId)) return false;
                long removedId = HoveredTraderId;
                Cache.Remove(removedId);
                HoveredTraderId = 0;
                SaveCache();
                capi?.TriggerIngameError(this, "tradernotes-marker-removed", Lang.Get("tradernotes:marker-removed"));
                return true;
            } catch { return false; }
        }

        private void OnLevelFinalize() {
            if (capi?.World == null) return;
            string worldId = capi.World.SavegameIdentifier;
            if (string.IsNullOrEmpty(worldId)) worldId = "default";
            string basePath = capi.DataBasePath;
            if (string.IsNullOrEmpty(basePath)) basePath = "moddata";
            this.savePath = Path.Combine(basePath, "ModData", "TraderNotes", $"tradernotes_cache.{worldId}.json");
            Cache.Clear();
            LoadCache();
        }

        private void OnBlockTexturesLoaded() {
            try {
                if (capi?.World != null && !isMapLayerRegistered) {
                    EnsureMapLayer();
                }
            } catch { }
        }

        private void EnsureMapLayer() {
            if (isMapLayerRegistered || capi?.World == null) return;
            
            try {
                var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
                if (mapManager?.MapLayers == null) return;
                
                bool hasTraderLayer = mapManager.MapLayers.Any(l => l.LayerGroupCode == "traders");
                
                if (!hasTraderLayer) {
                    LatestLayerId = DateTime.Now.Ticks;
                    try {
                        var newLayer = new TraderMapLayer(capi, mapManager, LatestLayerId);
                        mapManager.MapLayers.Add(newLayer);
                        isMapLayerRegistered = true;
                        capi.Logger.Debug("[TraderNotes] MapLayer 'traders' registered, looking for language key 'maplayer-traders'");
                    } catch { 
                        capi.Event.RegisterGameTickListener((dt) => {
                            EnsureMapLayer();
                        }, 1000);
                    }
                } else {
                    isMapLayerRegistered = true;
                    capi.Logger.Debug("[TraderNotes] MapLayer 'traders' already exists");
                }
            } catch { }
        }

        private string DetectTraderType(Entity entity) {
            string tag = (entity.WatchedAttributes.GetString("traderTag") ?? entity.Attributes.GetString("traderTag") ?? "").ToLower();
            string code = entity.Code?.Path?.ToLower() ?? "";
            string combined = (tag + " " + code).ToLower();
            if (combined.Contains("furniture")) return Lang.Get("tradernotes:type-furniture");
            if (combined.Contains("clothing")) return Lang.Get("tradernotes:type-clothing");
            if (combined.Contains("general") || combined.Contains("commodities")) return Lang.Get("tradernotes:type-general");
            if (combined.Contains("artisan")) return Lang.Get("tradernotes:type-artisan");
            if (combined.Contains("agriculture")) return Lang.Get("tradernotes:type-agriculture");
            if (combined.Contains("survival")) return Lang.Get("tradernotes:type-survival");
            if (combined.Contains("build")) return Lang.Get("tradernotes:type-building");
            if (combined.Contains("luxuries")) return Lang.Get("tradernotes:type-luxuries");
            if (combined.Contains("treasure")) return Lang.Get("tradernotes:type-treasure");
            return Lang.Get("tradernotes:type-unknown");
        }

        private void OnClientTick(float dt) {
            if (capi?.World?.Player == null) return;

            try {
                var player = capi.World.Player;
                if (player?.InventoryManager == null) return;
                
                var openedInvs = player.InventoryManager.OpenedInventories;
                if (openedInvs == null) return;
                
                var currentTraderInv = openedInvs.FirstOrDefault(i => i is InventoryTrader) as InventoryTrader;
                bool isCurrentlyOpen = currentTraderInv != null;
                if (isCurrentlyOpen || wasTraderInventoryOpen) UpdateActiveTrader(currentTraderInv);
                wasTraderInventoryOpen = isCurrentlyOpen;

                long nowMs = capi.World.ElapsedMilliseconds;
                if (nowMs - lastCleanupCheckMs > CleanupIntervalMs) {
                    lastCleanupCheckMs = nowMs;
                    CleanupRemovedTraders();
                }

                if (Config?.LiveUpdate == true) {
                    int interval = Config.UpdateIntervalSeconds > 0 ? Config.UpdateIntervalSeconds : 15;
                    if (DateTime.Now.Second % interval == 0) {
                        var traderEntities = capi.World.LoadedEntities.Values
                            .Where(e => e is EntityTradingHumanoid)
                            .ToList();
                            
                        foreach (var entity in traderEntities) {
                            var trader = entity as EntityTradingHumanoid;
                            if (Cache.TryGetValue(trader.EntityId, out var entry)) {
                                entry.X = trader.Pos.X; entry.Y = trader.Pos.Y; entry.Z = trader.Pos.Z;
                                UpdateTraderData(trader, trader.Inventory);
                            }
                        }
                    }
                }
            } catch { }
        }

        private void UpdateActiveTrader(InventoryTrader traderInv) {
            try {
                var player = capi?.World?.Player;
                if (player?.Entity == null) return;
                
                var nearestTrader = capi.World.GetNearestEntity(player.Entity.Pos.XYZ, 10f, 10f, (e) => e is EntityTradingHumanoid) as EntityTradingHumanoid;
                if (nearestTrader != null) UpdateTraderData(nearestTrader, traderInv ?? nearestTrader.Inventory);
            } catch { }
        }

        private void UpdateTraderData(EntityTradingHumanoid trader, IInventory inv) {
            long id = trader.EntityId;
            if (!Cache.ContainsKey(id)) {
                Cache[id] = new SavedTrader {
                    Name = trader.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? Lang.Get("tradernotes:trader-defaultname"),
                    EntityId = id,
                    TraderType = DetectTraderType(trader)
                };
            }
            var entry = Cache[id];
            bool changed = false;
            if (entry.TraderType != DetectTraderType(trader)) { entry.TraderType = DetectTraderType(trader); changed = true; }
            int currentMoney = trader.Inventory?.MoneySlot?.Empty == false ? trader.Inventory.MoneySlot.StackSize : 0;
            if (entry.Money != currentMoney) { entry.Money = currentMoney; changed = true; }
            double targetDate = capi.World.Calendar.TotalDays + trader.NextRefreshTotalDays();
            if (Math.Abs(entry.NextRefreshTotalDays - targetDate) > 0.01) { entry.NextRefreshTotalDays = targetDate; changed = true; }

            if (inv != null) {
                List<CachedTradeItem> sells = new List<CachedTradeItem>();
                List<CachedTradeItem> wants = new List<CachedTradeItem>();
                for (int i = 0; i < inv.Count; i++) {
                    var slot = inv[i];
                    if (slot?.Itemstack == null || slot.Itemstack.Collectible?.Code.Path.Contains("gear-rusty") == true) continue;
                    bool isSoldOut = slot.DrawUnavailable;
                    int availableTrades = slot is ItemSlotTrade tradeSlot && tradeSlot.TradeItem != null ? tradeSlot.TradeItem.Stock : slot.Itemstack.StackSize;
                    int price = slot is ItemSlotTrade ts && ts.TradeItem != null ? ts.TradeItem.Price : 0;
                    string itemName = slot.Itemstack.GetName();
                    try {
                        var tradeStack = slot.Itemstack.Attributes?.GetTreeAttribute("tradeprops")?.GetItemstack("stack");
                        if (tradeStack != null) itemName = tradeStack.GetName();
                    } catch { }
                    var itemInfo = new CachedTradeItem { Name = itemName, Stock = isSoldOut ? 0 : availableTrades, StackSize = slot.Itemstack.StackSize, Price = price, IsSoldOut = isSoldOut };
                    if (i < 16) sells.Add(itemInfo); else wants.Add(itemInfo);
                }
                
                bool sellsChanged = !ListsEqual(sells, entry.Sells);
                bool wantsChanged = !ListsEqual(wants, entry.Wants);
                
                if (sellsChanged || wantsChanged) {
                    entry.Sells = sells; entry.Wants = wants;
                    entry.IsDiscovered = true;
                    entry.LastUpdatedTotalDays = capi.World.Calendar.TotalDays;
                    changed = true;
                }
            }
            if (changed) { entry.X = trader.Pos.X; entry.Y = trader.Pos.Y; entry.Z = trader.Pos.Z; SaveCache(); }
        }

        private void LoadCache() {
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath)) return;
            try { 
                string content = File.ReadAllText(savePath);
                if (!string.IsNullOrWhiteSpace(content)) {
                    Cache = JsonConvert.DeserializeObject<Dictionary<long, SavedTrader>>(content) ?? new Dictionary<long, SavedTrader>();
                }
            } catch { Cache = new Dictionary<long, SavedTrader>(); }
        }

        private void CleanupRemovedTraders() {
            try {
                if (capi?.World?.BlockAccessor == null) return;
                var loaded = capi.World.LoadedEntities;
                if (loaded == null) return;
                var playerEntity = capi.World.Player?.Entity;
                if (playerEntity == null) return;
                var ppos = playerEntity.Pos;
                double rangeSq = CleanupRangeBlocks * CleanupRangeBlocks;

                List<long> toRemove = null;
                foreach (var kvp in Cache) {
                    var t = kvp.Value;
                    if (t == null) continue;
                    double distSq = ppos.SquareDistanceTo(t.X, t.Y, t.Z);
                    if (distSq > rangeSq) continue;
                    var pos = new BlockPos((int)t.X, (int)t.Y, (int)t.Z, 0);
                    var chunk = capi.World.BlockAccessor.GetChunkAtBlockPos(pos);
                    if (chunk == null) continue;
                    if (!loaded.ContainsKey(kvp.Key)) {
                        if (toRemove == null) toRemove = new List<long>();
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove != null && toRemove.Count > 0) {
                    foreach (var id in toRemove) Cache.Remove(id);
                    SaveCache();
                }
            } catch { }
        }

        public static string GetDeleteHotkeyDisplay(ICoreClientAPI api) {
            try {
                var hk = api?.Input?.GetHotKeyByCode(DeleteHotkeyCode);
                var mapping = hk?.CurrentMapping;
                if (mapping != null) {
                    string s = mapping.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            } catch { }
            return "Delete";
        }

        public void SaveCache() {
            if (string.IsNullOrEmpty(savePath)) return;
            try { 
                Directory.CreateDirectory(Path.GetDirectoryName(savePath)); 
                File.WriteAllText(savePath, JsonConvert.SerializeObject(Cache, Formatting.Indented)); 
            } catch { }
        }
        
        private bool ListsEqual<T>(List<T> list1, List<T> list2) {
            if (list1 == null && list2 == null) return true;
            if (list1 == null || list2 == null) return false;
            if (list1.Count != list2.Count) return false;
            
            for (int i = 0; i < list1.Count; i++) {
                if (!Equals(list1[i], list2[i])) return false;
            }
            return true;
        }
    }

    public class TraderMapLayer : MapLayer {
        private ICoreClientAPI capi;
        private long myId;
        private LoadedTexture iconTexture;
        private string loadedIconName;
        private string loadedIconColor;
        private int loadedIconSize;

        public TraderMapLayer(ICoreClientAPI api, IWorldMapManager mapSink, long id) : base(api, mapSink) { 
            this.capi = api; 
            this.myId = id;
        }
        public override string LayerGroupCode => "traders";
        public override string Title => "Trader Notes";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        public override void Render(GuiElementMap map, float dt) {
            try {
                if (!Active || myId != TraderMapMod.LatestLayerId || TraderMapMod.Config == null) return;
                if (map == null || map.Bounds == null) return;
                
                if (iconTexture == null || loadedIconName != TraderMapMod.Config.Icon || loadedIconColor != TraderMapMod.Config.IconColor || loadedIconSize != TraderMapMod.Config.IconSize) {
                    iconTexture?.Dispose(); iconTexture = null;
                    loadedIconName = TraderMapMod.Config.Icon ?? "trader";
                    loadedIconColor = TraderMapMod.Config.IconColor ?? "#d4d87f";
                    loadedIconSize = TraderMapMod.Config.IconSize > 0 ? TraderMapMod.Config.IconSize : 28;
                    try {
                        if (capi?.Assets == null) return;
                        AssetLocation loc = new AssetLocation("survival", "textures/icons/worldmap/" + loadedIconName + ".svg");
                        if (!capi.Assets.Exists(loc)) loc = new AssetLocation("game", "textures/icons/worldmap/" + loadedIconName + ".svg");
                        if (capi.Assets.Exists(loc)) {
                            string hex = loadedIconColor.Replace("#", "");
                            if (hex.Length == 6) hex = "FF" + hex;
                            uint colorUint = uint.Parse(hex, NumberStyles.HexNumber);
                            iconTexture = capi.Gui.LoadSvgWithPadding(loc, loadedIconSize, loadedIconSize, 2, (int)colorUint);
                        }
                    } catch { }
                }

                var tradersCopy = TraderMapMod.Cache.Values.ToList();
                foreach (var trader in tradersCopy) {
                    if (trader == null || !trader.IsDiscovered) continue;
                    
                    try {
                        Vec2f viewPos = new Vec2f();
                        map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                        if (viewPos.X < 0 || viewPos.Y < 0 || viewPos.X > map.Bounds.OuterWidth || viewPos.Y > map.Bounds.OuterHeight) continue;
                        
                        float drawX = (float)(viewPos.X + map.Bounds.renderX);
                        float drawY = (float)(viewPos.Y + map.Bounds.renderY);
                        float halfSize = loadedIconSize / 2f;
                        float zPosition = (float)(TraderMapMod.Config?.IconRenderZIndex ?? 100);
                        
                        if (iconTexture != null && iconTexture.TextureId != 0) {
                            capi.Render.Render2DTexture(iconTexture.TextureId, drawX - halfSize, drawY - halfSize, loadedIconSize, loadedIconSize, zPosition);
                        } else {
                            string hexFallback = (TraderMapMod.Config.IconColor ?? "#d4d87f").Replace("#", "");
                            if (hexFallback.Length == 6) hexFallback = "FF" + hexFallback;
                            int fbColor = (int)uint.Parse(hexFallback, NumberStyles.HexNumber);
                            float rectSize = loadedIconSize / 4f;
                            capi.Render.RenderRectangle(drawX - rectSize, drawY - rectSize, zPosition, rectSize * 2, rectSize * 2, fbColor);
                        }
                    } catch { }
                }
            } catch { }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText) {
            if (!Active || myId != TraderMapMod.LatestLayerId || TraderMapMod.Config == null) {
                TraderMapMod.HoveredTraderId = 0;
                return;
            }
            float halfSize = (TraderMapMod.Config.IconSize > 0 ? TraderMapMod.Config.IconSize : 28) / 2f;
            var tradersCopy = TraderMapMod.Cache.Values.ToList();
            TraderMapMod.HoveredTraderId = 0;
            foreach (var trader in tradersCopy) {
                if (trader == null || !trader.IsDiscovered) continue;
                Vec2f viewPos = new Vec2f();
                map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                if (Math.Abs(viewPos.X - (args.X - map.Bounds.renderX)) < halfSize && Math.Abs(viewPos.Y - (args.Y - map.Bounds.renderY)) < halfSize) {
                    TraderMapMod.HoveredTraderId = trader.EntityId;
                    var cfg = TraderMapMod.Config;
                    string cur = cfg.CurrencyName ?? "";
                    string so = Lang.Get("tradernotes:soldout");
                    
                    hoverText.AppendLine($"<font color='#F5E6B5'><b>{trader.Name}</b></font>");
                    hoverText.AppendLine($"<font color='#BBBBBB'><i>{trader.TraderType}</i></font>");

                    if (capi.World?.Player != null && capi.World.Player.Entity != null) {
                        try {
                            double dist = Math.Sqrt(capi.World.Player.Entity.Pos.SquareDistanceTo(trader.X, trader.Y, trader.Z));
                            hoverText.AppendLine($"<font color='{cfg.ColorDistance}'>{Lang.Get("tradernotes:distance-label")}: {dist.ToString("0")}m</font>");
                        } catch { }
                    }

                    hoverText.AppendLine($"<font color='{cfg.ColorTraderFunds}'>{Lang.Get("tradernotes:money-label")} {trader.Money}{cur}</font>");

                    if (trader.Sells.Count > 0) {
                        hoverText.AppendLine($"\n<font color='{cfg.ColorSelling}'>{Lang.Get("tradernotes:offers-label")}</font>");
                        foreach (var item in trader.Sells) BuildItemString(hoverText, item, cfg, cur, so);
                    }
                    if (trader.Wants.Count > 0) {
                        hoverText.AppendLine($"\n<font color='{cfg.ColorBuying}'>{Lang.Get("tradernotes:wants-label")}</font>");
                        foreach (var item in trader.Wants) BuildItemString(hoverText, item, cfg, cur, so);
                    }
                    
                    try {
                        double days = trader.NextRefreshTotalDays - (capi.World?.Calendar?.TotalDays ?? 0);
                        if (days > 0.01) hoverText.AppendLine($"\n<font color='#AAAAAA'>{Lang.Get("tradernotes:refresh-in", days.ToString("0.0"))}</font>");
                        else hoverText.AppendLine($"\n<font color='#FF6666'><i>{Lang.Get("tradernotes:outdated")}</i></font>");
                    } catch { }

                    if (cfg.LiveUpdate) {
                        bool inRange = capi.World.LoadedEntities.ContainsKey(trader.EntityId);
                        if (!inRange) {
                            hoverText.AppendLine($"<font color='#ff6666'>{Lang.Get("tradernotes:out-of-range")}</font>");
                        }
                    }
                    string hkLabel = TraderMapMod.GetDeleteHotkeyDisplay(capi);
                    hoverText.AppendLine($"\n<font color='#888888'><i>{Lang.Get("tradernotes:delete-hint", hkLabel)}</i></font>");
                    return;
                }
            }
        }

        private void BuildItemString(StringBuilder sb, CachedTradeItem item, TraderNotesConfig cfg, string currency, string soldOutTxt) {
            string so = item.IsSoldOut ? $" <font color='#FF6666'>({soldOutTxt})</font>" : "";
            sb.AppendLine($" • <font color='{cfg.ColorDemand}'>{item.Stock}x</font> <font color='{cfg.ColorItemName}'>{item.Name} </font><font color='{cfg.ColorItemStack}'>[{item.StackSize}]</font>:<font color='{cfg.ColorPrice}'>{item.Price}{currency} </font>{so}");
        }

        public override void OnMapClosedClient() {
            base.OnMapClosedClient();
            TraderMapMod.HoveredTraderId = 0;
        }

        public override void Dispose() { base.Dispose(); iconTexture?.Dispose(); TraderMapMod.HoveredTraderId = 0; }
    }
}