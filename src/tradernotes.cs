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
//v1.0.3
namespace TraderMapTooltip
{
    public class TraderNotesConfig {
        public string Icon = "trader";
        public int IconSize = 28;
        public string IconColor = "#d4d87f";
        public string CurrencyName = "";
        public bool LiveUpdate = false; 

        public string ColorTraderFunds = "#deffa1"; 
        public string ColorSelling = "#40a746"; 
        public string ColorBuying = "#deffa1";  
        public string ColorDemand = "#9d9d9d";    
        public string ColorItemName = "#e5e6de";  
        public string ColorItemStack = "#9d9d9d"; 
        public string ColorPrice = "#deebc7";
        public string ColorDistance = "#7fb3d8"; 
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

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api) {
            this.capi = api;
            try {
                Config = api.LoadModConfig<TraderNotesConfig>("TraderNotesConfig.json") ?? new TraderNotesConfig();
                api.StoreModConfig(Config, "TraderNotesConfig.json");
            } catch { Config = new TraderNotesConfig(); }

            api.Event.LevelFinalize += OnLevelFinalize;
            api.Event.LeaveWorld += SaveCache;
            api.Event.RegisterGameTickListener(OnClientTick, 500);
        }

        private void OnLevelFinalize() {
            string worldId = capi.World.SavegameIdentifier;
            if (string.IsNullOrEmpty(worldId)) worldId = "default";
            this.savePath = Path.Combine(capi.DataBasePath, "ModData", $"tradernotes_cache.{worldId}.json");
            Cache.Clear();
            LoadCache();
        }

        private void EnsureMapLayer() {
            if (isMapLayerRegistered || capi?.World == null) return;
            var mapManager = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager == null) return;
            if (!mapManager.MapLayers.Any(l => l is TraderMapLayer)) {
                LatestLayerId = DateTime.Now.Ticks;
                mapManager.MapLayers.Add(new TraderMapLayer(capi, mapManager, LatestLayerId));
            }
            isMapLayerRegistered = true;
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
            if (!isMapLayerRegistered) EnsureMapLayer();
            if (capi?.World?.Player == null) return;

            var currentTraderInv = capi.World.Player.InventoryManager.OpenedInventories.FirstOrDefault(i => i is InventoryTrader) as InventoryTrader;
            bool isCurrentlyOpen = currentTraderInv != null;
            if (isCurrentlyOpen || wasTraderInventoryOpen) UpdateActiveTrader(currentTraderInv);
            wasTraderInventoryOpen = isCurrentlyOpen;

            foreach (var entity in capi.World.LoadedEntities.Values) {
                if (entity is EntityTradingHumanoid trader) {
                    if (Cache.TryGetValue(entity.EntityId, out var entry)) {
                        entry.X = trader.Pos.X; entry.Y = trader.Pos.Y; entry.Z = trader.Pos.Z;
                        if (Config.LiveUpdate && !isCurrentlyOpen) {
                            UpdateTraderData(trader, trader.Inventory);
                        }
                    }
                }
            }
        }

        private void UpdateActiveTrader(InventoryTrader traderInv) {
            EntityTradingHumanoid nearestTrader = capi.World.GetNearestEntity(capi.World.Player.Entity.Pos.XYZ, 10f, 10f, (e) => e is EntityTradingHumanoid) as EntityTradingHumanoid;
            if (nearestTrader != null) UpdateTraderData(nearestTrader, traderInv ?? nearestTrader.Inventory);
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
                if (JsonConvert.SerializeObject(sells) != JsonConvert.SerializeObject(entry.Sells) || JsonConvert.SerializeObject(wants) != JsonConvert.SerializeObject(entry.Wants)) {
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
            try { Cache = JsonConvert.DeserializeObject<Dictionary<long, SavedTrader>>(File.ReadAllText(savePath)) ?? new Dictionary<long, SavedTrader>(); } catch { }
        }

        public void SaveCache() {
            if (string.IsNullOrEmpty(savePath)) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(savePath)); File.WriteAllText(savePath, JsonConvert.SerializeObject(Cache, Formatting.Indented)); } catch { }
        }
    }

    public class TraderMapLayer : MapLayer {
        private ICoreClientAPI capi;
        private long myId;
        private LoadedTexture iconTexture;
        private string loadedIconName;
        private string loadedIconColor;
        private int loadedIconSize;

        public TraderMapLayer(ICoreClientAPI api, IWorldMapManager mapSink, long id) : base(api, mapSink) { this.capi = api; this.myId = id; }
        public override string LayerGroupCode => "traders";
        public override string Title => Lang.Get("tradernotes:layer-title");
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        public override void Render(GuiElementMap map, float dt) {
            if (!Active || myId != TraderMapMod.LatestLayerId || TraderMapMod.Config == null) return;
            if (iconTexture == null || loadedIconName != TraderMapMod.Config.Icon || loadedIconColor != TraderMapMod.Config.IconColor || loadedIconSize != TraderMapMod.Config.IconSize) {
                iconTexture?.Dispose(); iconTexture = null;
                loadedIconName = TraderMapMod.Config.Icon ?? "trader";
                loadedIconColor = TraderMapMod.Config.IconColor ?? "#d4d87f";
                loadedIconSize = TraderMapMod.Config.IconSize > 0 ? TraderMapMod.Config.IconSize : 28;
                try {
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

            foreach (var trader in TraderMapMod.Cache.Values) {
                if (trader == null || !trader.IsDiscovered) continue;
                Vec2f viewPos = new Vec2f();
                map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                if (viewPos.X < 0 || viewPos.Y < 0 || viewPos.X > map.Bounds.OuterWidth || viewPos.Y > map.Bounds.OuterHeight) continue;
                float drawX = (float)(viewPos.X + map.Bounds.renderX);
                float drawY = (float)(viewPos.Y + map.Bounds.renderY);
                float halfSize = loadedIconSize / 2f;
                if (iconTexture != null && iconTexture.TextureId != 0) {
                    capi.Render.Render2DTexture(iconTexture.TextureId, drawX - halfSize, drawY - halfSize, loadedIconSize, loadedIconSize, 50f);
                } else {
                    string hexFallback = (TraderMapMod.Config.IconColor ?? "#d4d87f").Replace("#", "");
                    if (hexFallback.Length == 6) hexFallback = "FF" + hexFallback;
                    int fbColor = (int)uint.Parse(hexFallback, NumberStyles.HexNumber);
                    float rectSize = loadedIconSize / 4f;
                    capi.Render.RenderRectangle(drawX - rectSize, drawY - rectSize, 50f, rectSize * 2, rectSize * 2, fbColor);
                }
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText) {
            if (!Active || myId != TraderMapMod.LatestLayerId || TraderMapMod.Config == null) return;
            float halfSize = (TraderMapMod.Config.IconSize > 0 ? TraderMapMod.Config.IconSize : 28) / 2f;
            foreach (var trader in TraderMapMod.Cache.Values) {
                if (trader == null || !trader.IsDiscovered) continue;
                Vec2f viewPos = new Vec2f();
                map.TranslateWorldPosToViewPos(new Vec3d(trader.X, trader.Y, trader.Z), ref viewPos);
                if (Math.Abs(viewPos.X - (args.X - map.Bounds.renderX)) < halfSize && Math.Abs(viewPos.Y - (args.Y - map.Bounds.renderY)) < halfSize) {
                    var cfg = TraderMapMod.Config;
                    string cur = cfg.CurrencyName ?? "";
                    string so = Lang.Get("tradernotes:soldout");
                    
                    hoverText.AppendLine($"<font color='#F5E6B5'><b>{trader.Name}</b></font>");
                    hoverText.AppendLine($"<font color='#BBBBBB'><i>{trader.TraderType}</i></font>");

                    if (capi.World.Player != null) {
                        double dist = Math.Sqrt(capi.World.Player.Entity.Pos.SquareDistanceTo(trader.X, trader.Y, trader.Z));
                        hoverText.AppendLine($"<font color='{cfg.ColorDistance}'>{Lang.Get("tradernotes:distance-label", "Distance:")} {dist.ToString("0")}m</font>");
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
                    
                    double days = trader.NextRefreshTotalDays - capi.World.Calendar.TotalDays;
                    if (days > 0.01) hoverText.AppendLine($"\n<font color='#AAAAAA'>{Lang.Get("tradernotes:refresh-in", days.ToString("0.0"))}</font>");
                    else hoverText.AppendLine($"\n<font color='#FF6666'><i>{Lang.Get("tradernotes:outdated")}</i></font>");

                    if (cfg.LiveUpdate) {
                        bool inRange = capi.World.LoadedEntities.ContainsKey(trader.EntityId);
                        if (!inRange) {
                            hoverText.AppendLine($"<font color='#ff6666'>{Lang.Get("tradernotes:out-of-range", "Trader out of range, cannot live-update.")}</font>");
                        }
                    }
                    return;
                }
            }
        }

        private void BuildItemString(StringBuilder sb, CachedTradeItem item, TraderNotesConfig cfg, string currency, string soldOutTxt) {
            string so = item.IsSoldOut ? $" <font color='#FF6666'>({soldOutTxt})</font>" : "";
            // Fix: Leerzeichen und Doppelpunkt-Formatierung beibehalten
            sb.AppendLine($" • <font color='{cfg.ColorDemand}'>{item.Stock}x</font> <font color='{cfg.ColorItemName}'>{item.Name} </font><font color='{cfg.ColorItemStack}'>[{item.StackSize}]</font>:<font color='{cfg.ColorPrice}'>{item.Price}{currency} </font>{so}");
        }

        public override void Dispose() { base.Dispose(); iconTexture?.Dispose(); }
    }
}
